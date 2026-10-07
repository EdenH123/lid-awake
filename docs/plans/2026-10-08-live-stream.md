# Live Stream (WebSocket) Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Push screen frames to the phone over one WebSocket (with Sharp/Balanced/Fast presets) and carry phone input back over the same socket — step 1 of `docs/specs/2026-10-08-phone-upgrades-design.md`.

**Architecture:** A second localhost listener (`127.0.0.1:8788`, WebSocket-only — Network.framework's WebSocket listener can't also answer plain HTTP, verified by probe) validates Host, Origin and the session cookie in the handshake. Tailscale Serve maps `https://<mac>.ts.net/live` → `127.0.0.1:8788`. `FrameStream` gains subscribers and quality presets; each socket gets latest-wins frame delivery. The page prefers the socket and falls back to the existing `/screen.jpg` long-poll and `POST /input`.

**Tech Stack:** Swift (AppKit, Network.framework `NWProtocolWebSocket`, ScreenCaptureKit, CoreImage), vanilla JS in `Dashboard.swift`, Tailscale Serve, Node (only for local test scripts).

**Repo:** `~/Developer/LidAwake` (git). Build+install: `./build.sh`. Never edit files inside the .app bundle.

---

## File map

| File | Change | Responsibility |
|---|---|---|
| `FrameStream.swift` | rewrite | SCStream lifecycle, presets, long-poll waiters **and** push subscribers, display-wake helper |
| `LiveServer.swift` | create | WebSocket listener on 8788: handshake checks, per-socket latest-wins sender, input/quality messages |
| `Auth.swift` | modify | post `Auth.sessionsChanged` notification on password change / logout |
| `Server.swift` | modify | `ensureTailscaleServe` manages both `/` → 8787 and `/live` → 8788 |
| `main.swift` | modify | start `LiveServer`; `--selftest` runs a live-socket check per preset |
| `Dashboard.swift` | modify | socket client with reconnect + fallback, quality control, input over socket |
| `tests/live_checks.sh` | create | handshake rejection checks (no cookie / bad host / bad origin) + selftest |
| `tests/ws_handshake.js` | create | raw WebSocket handshake helper used by the checks |

---

### Task 1: Test harness for the live socket (failing first)

**Files:**
- Create: `tests/ws_handshake.js`
- Create: `tests/live_checks.sh`

- [ ] **Step 1: Write the handshake helper**

`tests/ws_handshake.js`:
```js
// Usage: node ws_handshake.js <port> <host-header> <origin> [cookie]
// Prints the HTTP status line the server answers the WebSocket handshake with.
const net = require("net");
const [port, host, origin, cookie] = process.argv.slice(2);
const s = net.connect(Number(port), "127.0.0.1", () => s.write(
  `GET /live HTTP/1.1\r\nHost: ${host}\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n` +
  `Sec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: ${origin}\r\n` +
  (cookie ? `Cookie: ${cookie}\r\n` : "") + "\r\n"));
let buf = "";
s.on("data", d => { buf += d.toString("latin1"); });
s.on("error", e => { console.log("ERROR " + e.message); process.exit(0); });
setTimeout(() => { console.log((buf.split("\r\n")[0] || "NO-REPLY").trim()); s.destroy(); }, 1200);
```

- [ ] **Step 2: Write the check script**

`tests/live_checks.sh`:
```sh
#!/bin/sh
# Checks the live socket refuses anything it shouldn't, then runs the in-app selftest.
cd "$(dirname "$0")"
APP="$HOME/Applications/Lid Awake.app"
fail=0
expect() {  # expect <label> <expected-substring> <actual>
  case "$3" in *"$2"*) echo "PASS $1 ($3)";; *) echo "FAIL $1: expected '$2', got '$3'"; fail=1;; esac
}
expect "no cookie"   "400" "$(node ws_handshake.js 8788 127.0.0.1:8788 http://127.0.0.1:8787)"
expect "fake cookie" "400" "$(node ws_handshake.js 8788 127.0.0.1:8788 http://127.0.0.1:8787 la_session=deadbeef)"
expect "bad host"    "400" "$(node ws_handshake.js 8788 evil.example.com http://127.0.0.1:8787 la_session=deadbeef)"
expect "bad origin"  "400" "$(node ws_handshake.js 8788 127.0.0.1:8788 https://evil.example.com la_session=deadbeef)"

LOG="$HOME/Library/Logs/LidAwake-selftest.log"; rm -f "$LOG"
launchctl submit -l local.lidawake.selftest -- "$APP/Contents/MacOS/LidAwake" --selftest
sleep 16; launchctl remove local.lidawake.selftest 2>/dev/null
cat "$LOG"
for p in sharp balanced fast; do
  line=$(grep "^live $p:" "$LOG")
  n=$(echo "$line" | sed -n 's/.* \([0-9][0-9]*\) frames.*/\1/p')
  if [ -n "$n" ] && [ "$n" -ge 3 ]; then echo "PASS live $p ($line)"; else echo "FAIL live $p: '$line'"; fail=1; fi
done
grep -q "^live input echo: ok" "$LOG" && echo "PASS live input" || { echo "FAIL live input"; fail=1; }
exit $fail
```

- [ ] **Step 3: Run to verify it fails**

Run: `chmod +x tests/live_checks.sh && tests/live_checks.sh`
Expected: FAIL lines (`ERROR connect ECONNREFUSED` — nothing listens on 8788 yet; no `live …` lines in the selftest log). Exit code 1.

- [ ] **Step 4: Commit**

```bash
git add tests && git commit -m "test: live socket checks (failing)"
```

---

### Task 2: FrameStream — presets, subscribers, display wake

**Files:**
- Modify (rewrite): `FrameStream.swift`

- [ ] **Step 1: Replace `FrameStream.swift` with:**

```swift
import CoreImage
import Foundation
import IOKit.pwr_mgt
@preconcurrency import ScreenCaptureKit

// Live screen frames for the phone. While someone is watching, a ScreenCaptureKit
// stream runs and delivers a frame only when the screen changes. Frames reach the
// phone two ways: pushed to WebSocket subscribers (LiveServer), or handed to
// long-poll requests (/screen.jpg) that wait for a frame newer than their last.
// The stream stops when nobody has watched for a while.
final class FrameStream: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    static let shared = FrameStream()

    enum Result { case frame(Data, id: Int), unchanged, unavailable }

    enum Preset: String {
        case sharp, balanced, fast
        var maxWidth: Double { switch self { case .sharp: 1600; case .balanced: 1200; case .fast: 800 } }
        var quality: Double { switch self { case .sharp: 0.7; case .balanced: 0.5; case .fast: 0.4 } }
        var fps: Int32 { switch self { case .sharp: 12; case .balanced: 20; case .fast: 25 } }
    }

    private let queue = DispatchQueue(label: "lidawake.frames")
    private let ciContext = CIContext()
    private var stream: SCStream?
    private var display: SCDisplay?
    private var starting = false
    private var preset = Preset.balanced
    private var latest: Data?
    private var latestID = 0
    private var lastRequest = Date.distantPast
    private var lastWake = Date.distantPast
    private var waiters: [(after: Int, done: (Result) -> Void)] = []
    private var subscribers: [UUID: (frame: (Data) -> Void, unavailable: () -> Void)] = [:]
    private var idleTimer: DispatchSourceTimer?

    private override init() {
        super.init()
        let idle = DispatchSource.makeTimerSource(queue: queue)
        idle.schedule(deadline: .now() + 5, repeating: 5)
        idle.setEventHandler { [weak self] in self?.housekeeping() }
        idle.resume()
        idleTimer = idle
    }

    // Every 5 s: keep the display awake and the stream running while someone watches;
    // stop capturing once nobody has for 10 s.
    private func housekeeping() {
        if !subscribers.isEmpty {
            wakeDisplay()
            startIfNeeded()
        } else if stream != nil, Date().timeIntervalSince(lastRequest) > 10 {
            stop()
        }
    }

    // Counts as user activity (like nudging the mouse): wakes the display if it slept
    // and keeps it on. Throttled to every 15 s.
    func wakeDisplay() {
        queue.async {
            guard Date().timeIntervalSince(self.lastWake) > 15 else { return }
            self.lastWake = Date()
            var assertion: IOPMAssertionID = 0
            IOPMAssertionDeclareUserActivity("Lid Awake phone is viewing the screen" as CFString,
                                             kIOPMUserActiveLocal, &assertion)
        }
    }

    // Long-poll: calls back with a frame newer than `after`, waiting up to 2 seconds.
    func frame(after: Int, done: @escaping (Result) -> Void) {
        wakeDisplay()
        queue.async {
            self.lastRequest = Date()
            self.startIfNeeded()
            if let latest = self.latest, self.latestID > after {
                return done(.frame(latest, id: self.latestID))
            }
            self.waiters.append((after, done))
            self.queue.asyncAfter(deadline: .now() + 2) { self.expireWaiters() }
        }
    }

    // Push: `frame` is called with every new frame (and once right away with the latest).
    func subscribe(_ id: UUID, frame: @escaping (Data) -> Void, unavailable: @escaping () -> Void) {
        wakeDisplay()
        queue.async {
            self.subscribers[id] = (frame, unavailable)
            self.lastRequest = Date()
            self.startIfNeeded()
            if let latest = self.latest { frame(latest) }
        }
    }

    func unsubscribe(_ id: UUID) {
        queue.async {
            self.subscribers[id] = nil
            self.lastRequest = Date()
        }
    }

    func setPreset(_ newPreset: Preset) {
        queue.async {
            guard newPreset != self.preset else { return }
            self.preset = newPreset
            if let stream = self.stream, let display = self.display {
                stream.updateConfiguration(self.configuration(for: display)) { _ in }
            }
        }
    }

    private func configuration(for display: SCDisplay) -> SCStreamConfiguration {
        let config = SCStreamConfiguration()
        let scale = min(1, preset.maxWidth / Double(max(display.width, 1)))
        config.width = Int(Double(display.width) * scale)
        config.height = Int(Double(display.height) * scale)
        config.minimumFrameInterval = CMTime(value: 1, timescale: preset.fps)
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.showsCursor = true
        config.queueDepth = 3
        return config
    }

    private func expireWaiters() {
        let now = waiters
        waiters = []
        for waiter in now {
            if stream == nil && !starting { waiter.done(.unavailable) } else { waiter.done(.unchanged) }
        }
    }

    private func startIfNeeded() {
        guard stream == nil, !starting else { return }
        starting = true
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() })
                        ?? content.displays.first else { throw CocoaError(.featureUnsupported) }
                let config = queue.sync { configuration(for: display) }
                let filter = SCContentFilter(display: display, excludingWindows: [])
                let stream = SCStream(filter: filter, configuration: config, delegate: self)
                try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
                try await stream.startCapture()
                queue.async {
                    self.stream = stream
                    self.display = display
                    self.starting = false
                }
            } catch {
                // No display (e.g. lid closed with no monitor, display asleep) or no permission.
                queue.async {
                    self.starting = false
                    let now = self.waiters
                    self.waiters = []
                    now.forEach { $0.done(.unavailable) }
                    self.subscribers.values.forEach { $0.unavailable() }
                }
            }
        }
    }

    private func stop() {
        stream?.stopCapture { _ in }
        stream = nil
        display = nil
        latest = nil
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete,
              let pixels = sampleBuffer.imageBuffer,
              let jpeg = ciContext.jpegRepresentation(
                  of: CIImage(cvPixelBuffer: pixels),
                  colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                  options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: preset.quality])
        else { return }

        latest = jpeg
        latestID += 1
        let ready = waiters.filter { $0.after < latestID }
        waiters.removeAll { $0.after < latestID }
        ready.forEach { $0.done(.frame(jpeg, id: latestID)) }
        subscribers.values.forEach { $0.frame(jpeg) }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async {
            if self.stream === stream { self.stream = nil; self.display = nil }
        }
    }
}
```

- [ ] **Step 2: Build to verify it compiles and long-poll still works**

Run: `./build.sh && sleep 5 && launchctl submit -l local.lidawake.selftest -- "$HOME/Applications/Lid Awake.app/Contents/MacOS/LidAwake" --selftest && sleep 9 && launchctl remove local.lidawake.selftest; grep stream ~/Library/Logs/LidAwake-selftest.log`
Expected: `Lid Awake rebuilt and restarted.` and `stream first frame: … bytes` (the existing selftest still passes).

- [ ] **Step 3: Commit**

```bash
git add FrameStream.swift && git commit -m "FrameStream: quality presets, push subscribers, shared display wake"
```

---

### Task 3: Auth — announce session changes

**Files:**
- Modify: `Auth.swift` (inside `setPassword` and `logout`)

- [ ] **Step 1: Add the notification name and post it**

At the top of `enum Auth`, after `static let cookieName = "la_session"`:
```swift
    // Posted when sessions are revoked (new password, logout) so open live sockets close.
    static let sessionsChanged = Notification.Name("LidAwakeSessionsChanged")
```
At the end of `setPassword(_:)` (after `lockedUntil = .distantPast`) and at the end of `logout(_:)` (after `defaults.set(sessions, forKey: "sessions")`):
```swift
        NotificationCenter.default.post(name: sessionsChanged, object: nil)
```
(Both posts happen while `lock` is held; observers must not call back into `Auth` synchronously — `LiveServer` hops to its own queue.)

- [ ] **Step 2: Build**

Run: `./build.sh`
Expected: `Lid Awake rebuilt and restarted.`

- [ ] **Step 3: Commit**

```bash
git add Auth.swift && git commit -m "Auth: notify when sessions are revoked"
```

---

### Task 4: LiveServer — the WebSocket endpoint

**Files:**
- Create: `LiveServer.swift`

- [ ] **Step 1: Create `LiveServer.swift`:**

```swift
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
    private let queue = DispatchQueue(label: "lidawake.live")
    private let checks: Checks
    private var sockets: [UUID: NWConnection] = [:]
    private var observer: NSObjectProtocol?

    init?(port: UInt16 = livePort, checks: Checks) {
        let ws = NWProtocolWebSocket.Options()
        ws.autoReplyPing = true
        ws.maximumMessageSize = 64 * 1024
        self.checks = checks
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.defaultProtocolStack.applicationProtocols.insert(ws, at: 0)
        params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: port)!)
        guard let listener = try? NWListener(using: params) else { return nil }
        self.listener = listener

        ws.setClientRequestHandler(queue) { [weak self] _, headers in
            let ok = self?.handshakeAllowed(headers) ?? false
            return NWProtocolWebSocket.Response(status: ok ? .accept : .reject, subprotocol: nil)
        }
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
            guard let self else { return }
            if !self.checks.awake() || !self.checks.remoteAllowed() { self.closeAll() }
        }
        timer.resume()
        self.timer = timer
    }
    private var timer: DispatchSourceTimer?

    private func handshakeAllowed(_ headers: [(name: String, value: String)]) -> Bool {
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

    private func open(_ connection: NWConnection) {
        let id = UUID()
        var sending = false
        var pending: Data?

        func sendFrame(_ jpeg: Data) {
            // Latest-wins: while one frame is on its way, keep only the newest next one.
            if sending { pending = jpeg; return }
            sending = true
            let meta = NWProtocolWebSocket.Metadata(opcode: .binary)
            connection.send(content: jpeg, contentContext: .init(identifier: "frame", metadata: [meta]),
                            isComplete: true, completion: .contentProcessed { [weak self] error in
                self?.queue.async {
                    sending = false
                    if error == nil, let next = pending { pending = nil; sendFrame(next) }
                }
            })
        }

        connection.stateUpdateHandler = { [weak self] state in
            guard let self else { return }
            switch state {
            case .ready:
                self.sockets[id] = connection
                FrameStream.shared.subscribe(id,
                    frame: { jpeg in self.queue.async { sendFrame(jpeg) } },
                    unavailable: { self.queue.async { self.sendText(connection, ["error": "Waking the Mac's screen… If this stays, the lid may be closed with no monitor attached, so there's no screen to show."]) } })
                self.receive(connection)
            case .failed, .cancelled:
                self.sockets[id] = nil
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
        if let name = message["quality"] as? String, let preset = FrameStream.Preset(rawValue: name) {
            FrameStream.shared.setPreset(preset)
            return
        }
        guard checks.awake(), checks.remoteAllowed() else {
            return sendText(connection, ["error": "Screen access is off."])
        }
        guard checks.controlPermission() else {
            return sendText(connection, ["error": "On your Mac, allow Lid Awake in System Settings → Privacy & Security → Accessibility."])
        }
        FrameStream.shared.wakeDisplay()
        DispatchQueue.main.async { RemoteInput.perform(message) }
        if message["type"] as? String == "ping" { sendText(connection, ["pong": true]) }
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
```

Note: `{"type":"ping"}` is not a `RemoteInput` command (`perform` ignores unknown types), it only exists so the selftest can verify the input path end-to-end.

- [ ] **Step 2: Build**

Run: `./build.sh`
Expected: `Lid Awake rebuilt and restarted.` (LiveServer not started yet — nothing listens on 8788.)

- [ ] **Step 3: Commit**

```bash
git add LiveServer.swift && git commit -m "LiveServer: WebSocket endpoint with handshake checks"
```

---

### Task 5: Start LiveServer in the app and extend --selftest

**Files:**
- Modify: `main.swift` — `AppDelegate` properties, `applicationDidFinishLaunching` (after `server = StatusServer(...)`), and the `--selftest` block

- [ ] **Step 1: Add the property** next to `var server: StatusServer?`:
```swift
    var liveServer: LiveServer?
```

- [ ] **Step 2: Start it** right after the `server = StatusServer(...)` statement:
```swift
        liveServer = LiveServer(checks: .init(
            sessionValid: { Auth.isValid($0) },
            awake: { DispatchQueue.main.sync { self.isOn } },
            remoteAllowed: { UserDefaults.standard.bool(forKey: "remoteScreen") },
            screenPermission: { screenPermission() },
            controlPermission: { controlPermission() }))
```

- [ ] **Step 3: Add the live check to `--selftest`.** Inside the selftest `Task { … }`, just before the existing `done.signal()` that ends the long-poll check (the one after the `switch next { … }`), replace that `done.signal()` with:
```swift
                // Live socket: a private LiveServer on port 18788 that accepts a test token,
                // so no real login is created. Counts frames per preset for 3 s each.
                let test = LiveServer(port: 18788, checks: .init(
                    sessionValid: { $0 == "selftest" }, awake: { true }, remoteAllowed: { true },
                    screenPermission: { true }, controlPermission: { true }))
                Task {
                    for preset in ["sharp", "balanced", "fast"] {
                        var request = URLRequest(url: URL(string: "ws://127.0.0.1:18788/live")!)
                        request.setValue("http://127.0.0.1:\(statusPort)", forHTTPHeaderField: "Origin")
                        request.setValue("\(Auth.cookieName)=selftest", forHTTPHeaderField: "Cookie")
                        let socket = URLSession.shared.webSocketTask(with: request)
                        socket.resume()
                        try? await socket.send(.string(#"{"quality":"\#(preset)"}"#))
                        var frames = 0, bytes = 0
                        let end = Date().addingTimeInterval(3)
                        while Date() < end {
                            // Keep the screen changing a little so frames keep coming.
                            CGEvent(mouseEventSource: nil, mouseType: .mouseMoved,
                                    mouseCursorPosition: CGEvent(source: nil)!.location, mouseButton: .left)?.post(tap: .cghidEventTap)
                            if case .data(let d)? = try? await socket.receive() { frames += 1; bytes += d.count }
                        }
                        lines.append("live \(preset): \(frames) frames, avg \(frames > 0 ? bytes / frames : 0) bytes")
                        if preset == "fast" {
                            try? await socket.send(.string(#"{"type":"ping"}"#))
                            var echoed = false
                            for _ in 0..<40 {
                                if case .string(let s)? = try? await socket.receive(), s.contains("pong") { echoed = true; break }
                            }
                            lines.append("live input echo: \(echoed ? "ok" : "missing")")
                        }
                        socket.cancel(with: .normalClosure, reason: nil)
                    }
                    _ = test
                    done.signal()
                }
```
(The pointer "move" posts the cursor to where it already is — nothing visibly changes on the Mac.)

- [ ] **Step 4: Build and run the checks**

Run: `./build.sh && sleep 5 && tests/live_checks.sh`
Expected: four `PASS` handshake lines (`HTTP/1.1 400 Bad Request`), `PASS live sharp/balanced/fast (… N frames …)` with N ≥ 3, `PASS live input`. Exit code 0.

If a handshake check prints `NO-REPLY`/`ECONNREFUSED`, the listener didn't start: check `lsof -nP -iTCP:8788 -sTCP:LISTEN`.

- [ ] **Step 5: Commit**

```bash
git add main.swift && git commit -m "Start LiveServer; selftest checks live frames per preset and input echo"
```

---

### Task 6: Tailscale Serve maps /live

**Files:**
- Modify: `Server.swift` — body of `ensureTailscaleServe(enabled:)` after `run(_:)`

- [ ] **Step 1: Replace the last block of `ensureTailscaleServe`** (from `let serving = …` to the end of the function) with:
```swift
    let status = run(["serve", "status", "--json"])
    let servingPage = status.contains("127.0.0.1:\(statusPort)")
    let servingLive = status.contains("127.0.0.1:\(livePort)")
    if enabled {
        if !servingPage { _ = run(["serve", "--bg", "\(statusPort)"]) }
        if !servingLive { _ = run(["serve", "--bg", "--set-path", "/live", "http://127.0.0.1:\(livePort)"]) }
    } else if servingPage || servingLive {
        _ = run(["serve", "--https=443", "off"])
    }
```

- [ ] **Step 2: Build and verify the mapping (lid-awake must be ON)**

Run: `./build.sh && sleep 8 && TAILSCALE_BE_CLI=1 /Applications/Tailscale.app/Contents/MacOS/Tailscale serve status`
Expected:
```
https://s-macbook-pro.tail039789.ts.net (tailnet only)
|-- /     proxy http://127.0.0.1:8787
|-- /live proxy http://127.0.0.1:8788
```

- [ ] **Step 3: Verify through Tailscale that the handshake is refused without a session**

Run: `node -e 'const t=require("tls");const s=t.connect(443,"s-macbook-pro.tail039789.ts.net",{servername:"s-macbook-pro.tail039789.ts.net"},()=>s.write("GET /live HTTP/1.1\r\nHost: s-macbook-pro.tail039789.ts.net\r\nUpgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Key: dGhlIHNhbXBsZSBub25jZQ==\r\nSec-WebSocket-Version: 13\r\nOrigin: https://s-macbook-pro.tail039789.ts.net\r\n\r\n"));let b="";s.on("data",d=>b+=d);setTimeout(()=>{console.log(b.split("\r\n")[0]);s.destroy()},2500)'`
Expected: a non-101 status (e.g. `HTTP/1.1 400 Bad Request` or `502`) — proves `/live` reaches the app and refuses without a cookie. A `404` means the path mapping is wrong.

- [ ] **Step 4: Commit**

```bash
git add Server.swift && git commit -m "Tailscale Serve: map /live to the live socket"
```

---

### Task 7: Page — socket client, fallback, quality control

**Files:**
- Modify: `Dashboard.swift` (CSS block, Screen-tab markup above `#screenWrap`, the `startFrames` function, and `pumpInput`)

- [ ] **Step 1: CSS** — add after the `.zoombar button { … }` rule:
```css
  .quality { display: grid; grid-template-columns: repeat(3, 1fr); background: var(--seg); border-radius: 10px; padding: 2px; }
  .quality button {
    font: inherit; font-size: 13px; font-weight: 600; color: var(--muted); background: none; border: 0;
    padding: 6px 0; border-radius: 8px; cursor: pointer;
  }
  .quality button.active { background: var(--seg-on); color: var(--text); box-shadow: 0 1px 2px rgba(0,0,0,.12); }
```

- [ ] **Step 2: Markup** — insert directly before `<div class="screen-wrap" id="screenWrap">`:
```html
    <nav class="quality" id="quality" aria-label="Picture quality">
      <button data-quality="sharp">Sharp</button>
      <button data-quality="balanced">Balanced</button>
      <button data-quality="fast">Fast</button>
    </nav>
```

- [ ] **Step 3: Replace the whole `startFrames` function** (from `// The Mac holds each request until the screen changes` through its closing `}` and `framesRunning = false;` block) with:
```js
  // ----- Live stream: one WebSocket, frames pushed by the Mac -----
  let ws = null, wsFailures = 0, wsRetryAt = 0;
  let quality = "balanced";
  try { quality = localStorage.getItem("quality") || "balanced"; } catch (e) {}
  function showQuality() {
    document.querySelectorAll("[data-quality]").forEach(b => b.classList.toggle("active", b.dataset.quality === quality));
  }
  document.querySelectorAll("[data-quality]").forEach(b => b.addEventListener("click", () => {
    quality = b.dataset.quality;
    try { localStorage.setItem("quality", quality); } catch (e) {}
    showQuality();
    if (ws && ws.readyState === 1) ws.send(JSON.stringify({ quality }));
  }));
  showQuality();

  function liveURL() {
    return location.protocol === "https:" ? `wss://${location.host}/live` : `ws://${location.hostname}:8788/live`;
  }
  function showFrame(blob) {
    const url = URL.createObjectURL(blob);
    const img = $("screenImg");
    img.onload = () => { if (frameUrl && frameUrl !== url) URL.revokeObjectURL(frameUrl); frameUrl = url; };
    img.src = url;
    $("screenControls").hidden = false;
    setScreenMsg("");
  }
  function watching() { return unlocked && currentTab === "screen" && !document.hidden; }
  function stopLive() { if (ws) { ws.onclose = null; ws.close(); ws = null; } }

  // Opens the socket; on repeated failure falls back to long-polling and retries the socket every 30 s.
  function startFrames() {
    if (!watching()) { stopLive(); return; }
    if (ws || framesRunning) return;
    if (wsFailures >= 3 && Date.now() < wsRetryAt) return startPolling();
    let opened = false;
    const socket = new WebSocket(liveURL());
    socket.binaryType = "blob";
    ws = socket;
    socket.onopen = () => {
      opened = true; wsFailures = 0;
      socket.send(JSON.stringify({ quality }));
      pumpInput();
    };
    socket.onmessage = e => {
      if (typeof e.data !== "string") return showFrame(e.data);
      try { const m = JSON.parse(e.data); if (m.error) setScreenMsg(m.error); } catch (err) {}
    };
    socket.onclose = () => {
      ws = null;
      if (!opened) { wsFailures++; if (wsFailures >= 3) wsRetryAt = Date.now() + 30000; }
      if (!watching()) return;
      // A refused handshake can mean "logged out" or "lid-awake off": let checkAuth sort it out.
      if (!opened) checkAuth();
      const delay = opened ? 500 : Math.min(5000, 500 * 2 ** wsFailures);
      setTimeout(startFrames, delay);
    };
  }

  // Fallback: the Mac holds each request until the screen changes (up to 2 s).
  async function startPolling() {
    if (framesRunning) return;
    framesRunning = true;
    while (watching() && !ws) {
      if (wsFailures >= 3 && Date.now() >= wsRetryAt) { framesRunning = false; return startFrames(); }
      let wait = 0;
      try {
        const res = await fetch("screen.jpg?after=" + frameId, { cache: "no-store" });
        if (res.status === 401 || res.status === 423) { locked(); break; }
        if (res.status === 204) continue;
        if (res.ok) {
          frameId = Number(res.headers.get("X-Frame-Id")) || 0;
          showFrame(await res.blob());
        } else {
          setScreenMsg(await res.text());
          if (res.status === 403) $("screenControls").hidden = true;
          frameId = 0;
          wait = 2000;
        }
      } catch (e) {
        setScreenMsg("Can't reach your Mac.");
        frameId = 0;
        wait = 2000;
      }
      if (wait) await new Promise(r => setTimeout(r, wait));
    }
    framesRunning = false;
  }
```

- [ ] **Step 4: Send input over the socket when it's open** — in `pumpInput()`, at the top of the `while (inputQueue.length) {` loop body, after the `continue` that skips empty moves, insert:
```js
      if (ws && ws.readyState === 1) { ws.send(JSON.stringify(cmd)); continue; }
```

- [ ] **Step 5: Close the socket when leaving the tab or locking** — in `showTab(name)`, after `if (name === "screen") startFrames();` add:
```js
    else stopLive();
```
and in `locked()` before `checkAuth()`:
```js
    stopLive();
```
and in the `visibilitychange` listener, replace `if (document.hidden) return;` with:
```js
    if (document.hidden) { stopLive(); return; }
```

- [ ] **Step 6: Syntax check, build, run the checks**

Run: `python3 -c "s=open('Dashboard.swift').read(); open('/tmp/page.js','w').write(s.split('<script>')[1].split('</script>')[0])" && node --check /tmp/page.js && ./build.sh && sleep 5 && tests/live_checks.sh`
Expected: no syntax error; all `PASS`; exit code 0.

- [ ] **Step 7: Verify the page opens the socket (local browser pane, already logged in)**

In the browser pane at `http://127.0.0.1:8787/`, run JS: `showTab("screen"); await new Promise(r=>setTimeout(r,3000)); ({ws: ws && ws.readyState, img: $("screenImg").src.slice(0,5)})`
Expected: `{ws: 1, img: "blob:"}` (if the pane is hidden, `watching()` is false and `ws` is null — make the pane visible first).

- [ ] **Step 8: Commit**

```bash
git add Dashboard.swift && git commit -m "Page: live socket with reconnect, long-poll fallback, quality presets"
```

---

### Task 8: Hand-off check on the iPhone (user)

- [ ] Reload the page from the home-screen icon, open **Screen**: picture appears; pointer/trackpad feel snappier.
- [ ] Switch **Sharp / Balanced / Fast**: picture sharpness and smoothness change within a second.
- [ ] Turn on Airplane Mode for 5 s, then off: the screen resumes by itself within ~5 s.
- [ ] Tap **Lock** on the page: screen stops; after logging in again it resumes.
- [ ] Switch lid-awake **OFF** from the page: page shows the closed message.
- [ ] Update the memory note `lid-awake-app.md` (live socket on 8788, `/live` Serve mapping, selftest lines).
