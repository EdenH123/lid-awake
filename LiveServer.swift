import Foundation
import Network

let livePort: UInt16 = 8788

// WebSocket endpoint for the Screen tab (Tailscale Serve maps https://<mac>.ts.net/live here).
// The handshake is refused unless the Host is this Mac, the Origin is this page, the
// session cookie is valid, lid-awake is on and screen access is allowed. Frames go out as
// binary messages (latest-wins per socket); input and quality choices come in as JSON text.
final class LiveServer: @unchecked Sendable {
    struct Checks {
        var sessionValid: (String?) -> Bool
        var awake: () -> Bool
        var remoteAllowed: () -> Bool
        var controlAllowed: () -> Bool
        var screenPermission: () -> Bool
        var controlPermission: () -> Bool
        var cameraAllowed: () -> Bool = { false }
        var allowLoopback = false  // the selftest's private server only
    }

    // For the menu bar: someone is watching the screen, or controlled it in the last few seconds.
    private let activityLock = NSLock()
    private var viewers = 0
    private var lastInput = Date.distantPast
    var activity: (viewers: Int, controlling: Bool) {
        activityLock.lock(); defer { activityLock.unlock() }
        return (viewers, Date().timeIntervalSince(lastInput) < 5)
    }
    // Phones watching the Mac's camera right now.
    private var cameraViewers = 0
    var cameraWatched: Bool { activityLock.lock(); defer { activityLock.unlock() }; return cameraViewers > 0 }

    private let listener: NWListener
    private let queue: DispatchQueue
    private let checks: Checks
    private var sockets: [ObjectIdentifier: NWConnection] = [:]  // every connection, from its first byte
    private var handshakeTokens: [(token: String, device: String)] = []  // accepted handshakes, oldest first
    private static let maxSockets = 8
    private var observer: NSObjectProtocol?
    private var timer: DispatchSourceTimer?

    init?(port: UInt16 = livePort, checks: Checks) {
        let queue = DispatchQueue(label: "lidawake.live.\(port)")
        let ws = NWProtocolWebSocket.Options()
        ws.autoReplyPing = true
        ws.maximumMessageSize = 64 * 1024
        // The handshake check must be in place before the listener starts.
        // (Each accepted handshake is followed by that connection becoming ready, in the same
        // order on this queue, so the session it used can be tied to the socket.)
        var tokens: ((String, String)) -> Void = { _ in }
        ws.setClientRequestHandler(queue) { _, headers in
            let token = LiveServer.handshakeToken(headers, checks)
            if let token {
                let agent = headers.first { $0.name.lowercased() == "user-agent" }?.value
                tokens((token, Auth.deviceName(userAgent: agent)))
            }
            return NWProtocolWebSocket.Response(status: token != nil ? .accept : .reject, subprotocol: nil)
        }
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.defaultProtocolStack.applicationProtocols.insert(ws, at: 0)
        params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!)
        guard let listener = try? NWListener(using: params) else { return nil }
        self.listener = listener
        self.queue = queue
        self.checks = checks

        tokens = { [weak self] in self?.handshakeTokens.append($0) }
        listener.newConnectionHandler = { [weak self] connection in self?.open(connection) }
        listener.start(queue: queue)

        // Revoked sessions (new password, logout, expired): close the sockets they opened; the
        // page reconnects and is asked to sign in again.
        observer = NotificationCenter.default.addObserver(forName: Auth.sessionsChanged, object: nil, queue: nil) { [weak self] _ in
            self?.queue.async { self?.closeRevoked() }
        }
        // Lid-awake turned off or screen access withdrawn: close sockets within 2 s (the menu
        // also closes them right away). Expired sessions and stuck sockets go too.
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 2, repeating: 2)
        timer.setEventHandler { [weak self] in
            guard let self, !self.sockets.isEmpty else { return }
            if !self.checks.awake() || !self.checks.remoteAllowed() { return self.closeAll() }
            self.closeRevoked()
            // The camera closes by itself after 10 minutes, or as soon as the menu stops allowing it.
            for peer in self.peers.values where peer.camera {
                if !self.checks.cameraAllowed() {
                    self.stopCamera(peer, telling: "The camera was turned off on the Mac.")
                } else if Date().timeIntervalSince(peer.cameraSince) > Self.cameraMinutes * 60 {
                    self.stopCamera(peer, telling: "The camera closed by itself after \(Int(Self.cameraMinutes)) minutes. Open it again to keep watching.")
                }
            }
            for peer in self.peers.values where peer.sending && Date().timeIntervalSince(peer.lastProgress) > 15 {
                peer.connection.cancel()  // the phone stopped reading
            }
        }
        timer.resume()
        self.timer = timer
    }

    // The session token of an allowed handshake, or nil to refuse it.
    private static func handshakeToken(_ headers: [(name: String, value: String)], _ checks: Checks) -> String? {
        func header(_ name: String) -> String? { headers.first { $0.name.lowercased() == name }?.value }
        let host = ((header("host") ?? "").split(separator: ":").first.map(String.init) ?? "").lowercased()
        // Same rule as the page: this Mac's Tailscale name from the owner's own device (Tailscale
        // says who), with the page itself as the Origin. Plain 127.0.0.1 is only used by the selftest.
        let hostOK = (checks.allowLoopback && Tailnet.isLoopback(host))
            || Tailnet.isOwner(host: host, login: header("tailscale-user-login"))
        let origin = header("origin") ?? ""
        let originOK = Tailnet.isLoopback(host)
            ? origin == "http://127.0.0.1:\(statusPort)" || origin == "http://localhost:\(statusPort)"
            : origin == "https://\(host)"
        let siteOK = header("sec-fetch-site").map { $0 == "same-origin" } ?? true
        guard header("tailscale-funnel-request") == nil, siteOK else { return nil }
        let token = Auth.token(fromCookies: header("cookie"))
        guard hostOK && originOK && checks.sessionValid(token)
            && checks.awake() && checks.remoteAllowed() && checks.screenPermission() else { return nil }
        return token
    }

    // One phone connection. A send "completes" once the frame is handed to the network, not
    // when the phone has it, so on a slow link frames would pile up in buffers along the way and
    // the picture would fall seconds behind. Pages that confirm each frame ("acks") get at most
    // two unconfirmed frames on the way: one being shown, one travelling. Newer frames replace
    // the waiting one, so the phone always gets the latest picture, at whatever rate the link allows.
    private final class Peer {
        let id: UUID
        let connection: NWConnection
        var sending = false
        var pending: Data?                          // JPEG: the newest frame waiting its turn
        var outbox: [(data: Data, text: Bool)] = [] // video: setup and frames, all in order
        var flowControl = false
        var video = false
        var token: String?
        var device = "phone"
        var lastProgress = Date()                   // last send that completed
        var inputs = 0, inputWindow = Date()        // input messages this second
        var usedInput = false
        var watch: (() -> Void)?                    // starts the screen frames for this phone
        var camera = false                          // watching the Mac's camera instead of the screen
        var cameraSince = Date()
        // Read from FrameStream's queue too (VideoSink.ready), hence the lock.
        let lock = NSLock()
        var unconfirmed = 0
        var waitingSince = Date()
        var sentAt: [(at: Date, bytes: Int, key: Bool)] = []  // unconfirmed frames
        var minDelay = 0.3                          // usual send → confirm time on this link
        var backlog = 0                             // video messages queued or being sent
        init(_ id: UUID, _ connection: NWConnection) { self.id = id; self.connection = connection }

        // Video: may the next screen frame be encoded? Only while little is unconfirmed and the
        // oldest unconfirmed frame isn't stuck, so frames never queue up on a slow link.
        func videoReady() -> Bool {
            lock.lock(); defer { lock.unlock() }
            // A phone that stopped reading (locked, backgrounded) gets no new frames to queue up.
            guard backlog < 4 else { return false }
            guard let oldest = sentAt.first else { return true }
            let age = Date().timeIntervalSince(oldest.at)
            if age > 3 { unconfirmed = 0; sentAt.removeAll(); return true }  // page stopped confirming
            return unconfirmed < 6 && age < minDelay + 0.15
        }
    }
    private var peers: [ObjectIdentifier: Peer] = [:]

    private func pump(_ peer: Peer) {
        guard !peer.sending else { return }
        var item: (data: Data, text: Bool, key: Bool)
        if !peer.outbox.isEmpty {
            let next = peer.outbox.removeFirst()
            peer.lock.lock(); peer.backlog = peer.outbox.count + 1; peer.lock.unlock()
            item = (next.data, next.text, next.data.first == 1)
        } else if let next = peer.pending {
            if peer.flowControl {
                peer.lock.lock()
                let full = peer.unconfirmed >= 2
                // A page that stopped confirming (e.g. backgrounded) shouldn't freeze the stream forever.
                let stuck = full && Date().timeIntervalSince(peer.waitingSince) > 3
                if stuck { peer.unconfirmed = 0; peer.sentAt.removeAll() }
                peer.lock.unlock()
                if full && !stuck { return StreamStats.shared.heldBack() }
            }
            peer.pending = nil
            item = (next, false, false)
        } else { return }
        peer.sending = true
        if !item.text {
            peer.lock.lock()
            if peer.unconfirmed == 0 { peer.waitingSince = Date() }
            peer.unconfirmed += 1
            if peer.flowControl { peer.sentAt.append((Date(), item.data.count, item.key)) }
            peer.lock.unlock()
            StreamStats.shared.sent(bytes: item.data.count)
        }
        let meta = NWProtocolWebSocket.Metadata(opcode: item.text ? .text : .binary)
        peer.connection.send(content: item.data, contentContext: .init(identifier: "frame", metadata: [meta]),
                             isComplete: true, completion: .contentProcessed { [weak self] error in
            self?.queue.async {
                peer.sending = false
                peer.lastProgress = Date()
                peer.lock.lock(); peer.backlog = peer.outbox.count; peer.lock.unlock()
                if error == nil { self?.pump(peer) }
            }
        })
    }

    // Video messages: setup as text JSON; each frame as binary [1 = keyframe / 2 = other]
    // [8-byte timestamp, µs, big-endian][H.264 data]. (JPEGs start with 0xFF, so they can't be confused.)
    private func enqueue(_ peer: Peer, _ packet: FrameStream.VideoSink.Packet) {
        switch packet {
        case .config(let c):
            let setup: [String: Any] = ["video": ["codec": c.codec, "description": c.description.base64EncodedString(),
                                                  "width": c.width, "height": c.height]]
            guard let json = try? JSONSerialization.data(withJSONObject: setup) else { return }
            peer.outbox.append((json, true))
        case .chunk(let k):
            var data = Data([k.key ? 1 : 2])
            withUnsafeBytes(of: k.timestamp.bigEndian) { data.append(contentsOf: $0) }
            data.append(k.data)
            peer.outbox.append((data, false))
        }
        // Never let video pile up for a phone that isn't reading: drop it and start over
        // from a keyframe once it catches up.
        if peer.outbox.count > 60 {
            peer.outbox.removeAll()
            FrameStream.shared.restartVideo(peer.id)
        }
        peer.lock.lock(); peer.backlog = peer.outbox.count + (peer.sending ? 1 : 0); peer.lock.unlock()
        pump(peer)
    }

    // ----- Camera: this phone watches the Mac's camera and microphone instead of the screen -----
    static let cameraMinutes = 10.0

    private func startCamera(_ peer: Peer, ticket: String?) {
        guard !peer.camera else { return }
        guard checks.cameraAllowed() else {
            return sendText(peer.connection, ["cameraError": "The camera is off. On your Mac, tick “Allow camera & microphone from phone” in the Lid Awake menu."])
        }
        guard let ticket, CameraTickets.redeem(ticket, token: peer.token) else {
            return sendText(peer.connection, ["cameraError": "Face ID is needed to open the camera. Try again."])
        }
        peer.camera = true
        peer.cameraSince = Date()
        // No screen for this phone meanwhile: only one video uses the link.
        FrameStream.shared.unsubscribe(peer.id)
        peer.video = false
        peer.pending = nil
        peer.outbox.removeAll()
        activityLock.lock(); cameraViewers += 1; activityLock.unlock()
        if !checks.allowLoopback { AccessLog.write("Camera and microphone opened from the phone · \(peer.device)") }
        CameraStream.shared.start(peer.id) { [weak self] packet in self?.queue.async { self?.enqueueCamera(peer, packet) } }
    }

    private func stopCamera(_ peer: Peer, telling reason: String? = nil) {
        guard peer.camera else { return }
        peer.camera = false
        CameraStream.shared.stop(peer.id)
        peer.outbox.removeAll()
        activityLock.lock(); cameraViewers -= 1; activityLock.unlock()
        if !checks.allowLoopback { AccessLog.write("Camera and microphone closed · \(peer.device)") }
        if let reason { sendText(peer.connection, ["cameraStopped": reason]) }
    }

    // Camera messages: setup as text JSON; picture as binary [3 = keyframe / 4 = other]
    // [8-byte timestamp, µs][H.264]; sound as binary [5][4-byte sample rate][μ-law bytes].
    private func enqueueCamera(_ peer: Peer, _ packet: CameraStream.Packet) {
        guard peer.camera else { return }
        switch packet {
        case .config(let c):
            let setup: [String: Any] = ["camera": ["codec": c.codec, "description": c.description.base64EncodedString(),
                                                   "width": c.width, "height": c.height]]
            guard let json = try? JSONSerialization.data(withJSONObject: setup) else { return }
            peer.outbox.append((json, true))
        case .video(let k):
            var data = Data([k.key ? 3 : 4])
            withUnsafeBytes(of: k.timestamp.bigEndian) { data.append(contentsOf: $0) }
            data.append(k.data)
            peer.outbox.append((data, false))
        case .audio(let rate, let samples):
            var data = Data([5])
            withUnsafeBytes(of: UInt32(rate).bigEndian) { data.append(contentsOf: $0) }
            data.append(samples)
            peer.outbox.append((data, false))
        case .problem(let text):
            return sendText(peer.connection, ["cameraError": text])
        }
        // A phone that can't keep up: start over from a fresh keyframe rather than fall behind.
        if peer.outbox.count > 90 {
            peer.outbox.removeAll()
            CameraStream.shared.keyframe()
        }
        pump(peer)
    }

    private func open(_ connection: NWConnection) {
        let key = ObjectIdentifier(connection)
        guard sockets.count < Self.maxSockets else { return connection.cancel() }
        sockets[key] = connection
        let id = UUID()
        let peer = Peer(id, connection)
        func sendFrame(_ jpeg: Data) {
            peer.pending = jpeg
            pump(peer)
        }

        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                if !self.handshakeTokens.isEmpty {
                    let first = self.handshakeTokens.removeFirst()
                    peer.token = first.token
                    peer.device = first.device
                }
                self.peers[key] = peer
                self.activityLock.lock(); self.viewers = self.peers.count; self.activityLock.unlock()
                if !self.checks.allowLoopback { AccessLog.write("Screen viewing started · \(peer.device)") }
                peer.watch = {
                    FrameStream.shared.subscribe(id,
                        frame: { jpeg in self.queue.async { sendFrame(jpeg) } },
                        unavailable: { self.queue.async { self.sendText(connection, ["error": "Waking the Mac's screen… This usually takes a few seconds."]) } })
                }
                peer.watch?()
                self.receive(connection)
            case .failed:
                connection.cancel()
            case .cancelled:
                self.sockets[key] = nil
                peer.watch = nil
                self.stopCamera(peer)
                if self.peers.removeValue(forKey: key) != nil {
                    FrameStream.shared.unsubscribe(id)
                    self.activityLock.lock(); self.viewers = self.peers.count; self.activityLock.unlock()
                    if !self.checks.allowLoopback { AccessLog.write("Screen viewing ended · \(peer.device)") }
                    // Don't leave a mouse button held down if the phone was mid-drag.
                    if peer.usedInput { DispatchQueue.main.async { RemoteInput.releaseButtons(); RemoteInput.releaseModifiers() } }
                }
            default:
                break
            }
        }
        connection.start(queue: queue)
        // The handshake must finish quickly.
        queue.asyncAfter(deadline: .now() + 10) { [weak self] in
            if self?.peers[key] == nil, connection.state != .cancelled { connection.cancel() }
        }
    }

    private func receive(_ connection: NWConnection) {
        connection.receiveMessage { [weak self] data, _, _, error in
            guard let self else { return }
            if let data, !data.isEmpty,
               let message = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] {
                self.handle(message, from: connection)
            }
            if error == nil { self.receive(connection) } else { connection.cancel() }
        }
    }

    private func handle(_ message: [String: Any], from connection: NWConnection) {
        let peer = peers[ObjectIdentifier(connection)]
        if let peer {
            if message["acks"] as? Bool == true { peer.flowControl = true }
            if let wants = message["camera"] as? Bool { return wants ? startCamera(peer, ticket: message["ticket"] as? String) : stopCamera(peer) }
            if message["cameraKey"] != nil { return CameraStream.shared.keyframe() }
            // The page can play H.264 (true), or its decoder failed and it wants JPEGs again (false).
            if let wantsVideo = message["video"] as? Bool {
                StreamStats.shared.note("phone video: \(wantsVideo ? "on" : "off")" + Self.why(message))
            }
            if let wantsVideo = message["video"] as? Bool, wantsVideo != peer.video, !peer.camera {
                peer.video = wantsVideo
                FrameStream.shared.setVideo(peer.id, wantsVideo ? FrameStream.VideoSink(
                    send: { [weak self] packet in self?.queue.async { self?.enqueue(peer, packet) } },
                    ready: { peer.videoReady() }) : nil)
                if !wantsVideo { peer.outbox.removeAll() }
            }
            if message["videoRestart"] != nil {
                StreamStats.shared.note("phone video: restarting decoder" + Self.why(message))
                FrameStream.shared.restartVideo(peer.id)
                return
            }
            if message["ack"] != nil {
                peer.lock.lock()
                peer.unconfirmed = max(0, peer.unconfirmed - 1)
                let frame = peer.sentAt.isEmpty ? nil : peer.sentAt.removeFirst()
                let seconds = frame.map { Date().timeIntervalSince($0.at) }
                if let seconds { peer.minDelay = min(peer.minDelay + 0.002, seconds) }
                peer.waitingSince = Date()
                let usual = peer.minDelay
                peer.lock.unlock()
                if let frame, let seconds {
                    StreamStats.shared.acked(after: seconds)
                    LinkSpeed.record(bytes: frame.bytes, seconds: seconds)
                    // Keyframes are big and slow by nature; judge the link by ordinary frames.
                    if peer.video && !frame.key { FrameStream.shared.videoFeedback(extraDelay: seconds - usual) }
                }
                return pump(peer)
            }
        }
        if let name = message["quality"] as? String, let preset = FrameStream.Preset(rawValue: name) {
            FrameStream.shared.setPreset(preset)
            return
        }
        if message["video"] != nil || message["acks"] != nil || message["camera"] != nil { return }
        guard checks.awake(), checks.remoteAllowed() else {
            return sendText(connection, ["error": "Screen access is off."])
        }
        guard checks.controlAllowed() else {
            return sendText(connection, ["error": "View only: control from the phone is off in the Lid Awake menu on your Mac."])
        }
        guard checks.controlPermission() else {
            return sendText(connection, ["error": "On your Mac, allow Lid Awake in System Settings → Privacy & Security → Accessibility."])
        }
        // Input limits: a burst of messages or a huge paste can't flood the Mac.
        if let peer {
            if Date().timeIntervalSince(peer.inputWindow) >= 1 { peer.inputWindow = Date(); peer.inputs = 0 }
            peer.inputs += 1
            if peer.inputs > 240 { return }
            if !peer.usedInput && !checks.allowLoopback { AccessLog.write("Mac controlled from the phone · \(peer.device)") }
            peer.usedInput = true
        }
        activityLock.lock(); lastInput = Date(); activityLock.unlock()
        if let text = message["text"] as? String, text.count > RemoteInput.maxText {
            return sendText(connection, ["error": "That's too much text to type at once."])
        }
        FrameStream.shared.wakeNow()
        // "ping" isn't an input command (RemoteInput ignores it); it lets the selftest
        // check the input path end-to-end.
        if message["type"] as? String == "ping" { return sendText(connection, ["pong": true]) }
        DispatchQueue.main.async { RemoteInput.perform(message) }
    }

    private func sendText(_ connection: NWConnection, _ object: [String: Any]) {
        guard let data = try? JSONSerialization.data(withJSONObject: object) else { return }
        let meta = NWProtocolWebSocket.Metadata(opcode: .text)
        connection.send(content: data, contentContext: .init(identifier: "text", metadata: [meta]),
                        isComplete: true, completion: .contentProcessed { _ in })
    }

    // The page's reason for a video change, made safe for the log: one short line.
    private static func why(_ message: [String: Any]) -> String {
        guard let why = message["why"] as? String else { return "" }
        let clean = String(why.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }.prefix(100))
        return " (\(clean))"
    }

    // Sockets whose session was signed out, replaced or expired.
    private func closeRevoked() {
        for peer in peers.values where !checks.sessionValid(peer.token) { peer.connection.cancel() }
    }

    func closeAll() {
        queue.async {
            self.sockets.values.forEach { $0.cancel() }
            DispatchQueue.main.async { RemoteInput.releaseButtons() }
        }
    }
}

// How fast frames reach the phone, from "sent" to "confirmed" (bytes per second, smoothed).
// It includes the round trip, so it errs on the slow side. nil when no page confirmed lately.
enum LinkSpeed {
    private static let lock = NSLock()
    private static var rate: Double?
    private static var updated = Date.distantPast

    static func record(bytes: Int, seconds: Double) {
        lock.lock(); defer { lock.unlock() }
        let sample = Double(bytes) / max(seconds, 0.02)
        rate = rate.map { $0 * 0.7 + sample * 0.3 } ?? sample
        updated = Date()
    }

    static var bytesPerSecond: Double? {
        lock.lock(); defer { lock.unlock() }
        return Date().timeIntervalSince(updated) < 30 ? rate : nil
    }
}
