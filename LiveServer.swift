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
        var screenPermission: () -> Bool
        var controlPermission: () -> Bool
    }

    private let listener: NWListener
    private let queue: DispatchQueue
    private let checks: Checks
    private var sockets: [UUID: NWConnection] = [:]
    private var observer: NSObjectProtocol?
    private var timer: DispatchSourceTimer?

    init?(port: UInt16 = livePort, checks: Checks) {
        let queue = DispatchQueue(label: "lidawake.live.\(port)")
        let ws = NWProtocolWebSocket.Options()
        ws.autoReplyPing = true
        ws.maximumMessageSize = 64 * 1024
        // The handshake check must be in place before the listener starts.
        ws.setClientRequestHandler(queue) { _, headers in
            let ok = LiveServer.handshakeAllowed(headers, checks)
            return NWProtocolWebSocket.Response(status: ok ? .accept : .reject, subprotocol: nil)
        }
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.defaultProtocolStack.applicationProtocols.insert(ws, at: 0)
        params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!)
        guard let listener = try? NWListener(using: params) else { return nil }
        self.listener = listener
        self.queue = queue
        self.checks = checks

        listener.newConnectionHandler = { [weak self] connection in self?.open(connection) }
        listener.start(queue: queue)

        // Revoked sessions (new password, logout): drop every open socket; the page reconnects
        // and is asked to log in again.
        observer = NotificationCenter.default.addObserver(forName: Auth.sessionsChanged, object: nil, queue: nil) { [weak self] _ in
            self?.queue.async { self?.closeAll() }
        }
        // Lid-awake turned off or screen access withdrawn: close sockets within 5 s.
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 5, repeating: 5)
        timer.setEventHandler { [weak self] in
            guard let self, !self.sockets.isEmpty else { return }
            if !self.checks.awake() || !self.checks.remoteAllowed() { self.closeAll() }
        }
        timer.resume()
        self.timer = timer
    }

    private static func handshakeAllowed(_ headers: [(name: String, value: String)], _ checks: Checks) -> Bool {
        func header(_ name: String) -> String? { headers.first { $0.name.lowercased() == name }?.value }
        let host = (header("host") ?? "").split(separator: ":").first.map(String.init) ?? ""
        let hostOK = host == "127.0.0.1" || host == "localhost" || host.hasSuffix(".ts.net")
        // Origin must be this page: the Tailscale name itself, or the local page on 8787.
        let origin = header("origin") ?? ""
        let originOK = origin == "https://\(host)"
            || origin == "http://127.0.0.1:\(statusPort)" || origin == "http://localhost:\(statusPort)"
        let token = (header("cookie") ?? "").split(separator: ";").lazy
            .map { $0.trimmingCharacters(in: .whitespaces) }
            .first { $0.hasPrefix(Auth.cookieName + "=") }
            .map { String($0.dropFirst(Auth.cookieName.count + 1)) }
        return hostOK && originOK && checks.sessionValid(token)
            && checks.awake() && checks.remoteAllowed() && checks.screenPermission()
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
        // Read from FrameStream's queue too (VideoSink.ready), hence the lock.
        let lock = NSLock()
        var unconfirmed = 0
        var waitingSince = Date()
        var sentAt: [(at: Date, bytes: Int, key: Bool)] = []  // unconfirmed frames
        var minDelay = 0.3                          // usual send → confirm time on this link
        init(_ id: UUID, _ connection: NWConnection) { self.id = id; self.connection = connection }

        // Video: may the next screen frame be encoded? Only while little is unconfirmed and the
        // oldest unconfirmed frame isn't stuck, so frames never queue up on a slow link.
        func videoReady() -> Bool {
            lock.lock(); defer { lock.unlock() }
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
        pump(peer)
    }

    private func open(_ connection: NWConnection) {
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
                self.sockets[id] = connection
                self.peers[ObjectIdentifier(connection)] = peer
                FrameStream.shared.subscribe(id,
                    frame: { jpeg in self.queue.async { sendFrame(jpeg) } },
                    unavailable: { self.queue.async { self.sendText(connection, ["error": "Waking the Mac's screen… If this stays, the lid may be closed with no monitor attached, so there's no screen to show."]) } })
                self.receive(connection)
            case .failed, .cancelled:
                self.sockets[id] = nil
                self.peers[ObjectIdentifier(connection)] = nil
                FrameStream.shared.unsubscribe(id)
            default:
                break
            }
        }
        connection.start(queue: queue)
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
        if let peer = peers[ObjectIdentifier(connection)] {
            if message["acks"] as? Bool == true { peer.flowControl = true }
            // The page can play H.264 (true), or its decoder failed and it wants JPEGs again (false).
            if let wantsVideo = message["video"] as? Bool {
                StreamStats.shared.note("phone video: \(wantsVideo ? "on" : "off")" + ((message["why"] as? String).map { " (\($0))" } ?? ""))
            }
            if let wantsVideo = message["video"] as? Bool, wantsVideo != peer.video {
                peer.video = wantsVideo
                FrameStream.shared.setVideo(peer.id, wantsVideo ? FrameStream.VideoSink(
                    send: { [weak self] packet in self?.queue.async { self?.enqueue(peer, packet) } },
                    ready: { peer.videoReady() }) : nil)
                if !wantsVideo { peer.outbox.removeAll() }
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
        if message["video"] != nil || message["acks"] != nil { return }
        guard checks.awake(), checks.remoteAllowed() else {
            return sendText(connection, ["error": "Screen access is off."])
        }
        guard checks.controlPermission() else {
            return sendText(connection, ["error": "On your Mac, allow Lid Awake in System Settings → Privacy & Security → Accessibility."])
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

    private func closeAll() {
        sockets.values.forEach { $0.cancel() }
        sockets.removeAll()
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
