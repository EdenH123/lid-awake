import Foundation
import IOKit.ps
import Network

let statusPort: UInt16 = 8787

// Battery details for the live page: level, power source, charging and time estimates.
func powerInfo() -> [String: Any] {
    guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
          let list = IOPSCopyPowerSourcesList(info)?.takeRetainedValue() as? [CFTypeRef] else { return [:] }
    for source in list {
        guard let desc = IOPSGetPowerSourceDescription(info, source)?.takeUnretainedValue() as? [String: Any],
              let current = desc[kIOPSCurrentCapacityKey] as? Int,
              let max = desc[kIOPSMaxCapacityKey] as? Int, max > 0 else { continue }
        return [
            "battery": current * 100 / max,
            "onBattery": (desc[kIOPSPowerSourceStateKey] as? String) == kIOPSBatteryPowerValue,
            "charging": desc[kIOPSIsChargingKey] as? Bool ?? false,
            "charged": desc[kIOPSIsChargedKey] as? Bool ?? false,
            "minutesToEmpty": desc[kIOPSTimeToEmptyKey] as? Int ?? -1,
            "minutesToFull": desc[kIOPSTimeToFullChargeKey] as? Int ?? -1,
        ]
    }
    return [:]
}

// Offer the private HTTPS link only while lid-awake is on. When on, restore the
// Tailscale Serve config if it was lost (e.g. after Tailscale restarts); when off,
// remove it so nothing on the tailnet can reach this Mac's page at all.
func ensureTailscaleServe(enabled: Bool) {
    let tailscale = "/Applications/Tailscale.app/Contents/MacOS/Tailscale"
    guard FileManager.default.isExecutableFile(atPath: tailscale) else { return }

    func run(_ args: [String]) -> String {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: tailscale)
        p.arguments = args
        let out = Pipe()
        p.standardOutput = out
        p.standardError = out
        p.standardInput = FileHandle.nullDevice
        // Launched by launchd, the Tailscale binary otherwise starts in GUI mode and fails.
        var env = ProcessInfo.processInfo.environment
        env["TAILSCALE_BE_CLI"] = "1"
        p.environment = env
        guard (try? p.run()) != nil else { return "" }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return String(data: data, encoding: .utf8) ?? ""
    }

    let status = run(["serve", "status", "--json"])
    let servingPage = status.contains("127.0.0.1:\(statusPort)")
    let servingLive = status.contains("127.0.0.1:\(livePort)")
    // Funnel would put the page on the open internet. Never allow it for this address.
    if let json = (try? JSONSerialization.jsonObject(with: Data(status.utf8))) as? [String: Any],
       let funnel = json["AllowFunnel"] as? [String: Any], funnel.values.contains(where: { $0 as? Bool == true }) {
        _ = run(["funnel", "--https=443", "off"])
        StreamStats.shared.note("Tailscale Funnel was on for this Mac; turned it off")
        notify("Tailscale Funnel was on, which would put your phone page on the open internet. Lid Awake turned it off.")
    }
    if enabled {
        if !servingPage { _ = run(["serve", "--bg", "\(statusPort)"]) }
        if !servingLive { _ = run(["serve", "--bg", "--set-path", "/live", "http://127.0.0.1:\(livePort)"]) }
    } else if servingPage || servingLive {
        _ = run(["serve", "--https=443", "off"])
    }
}

struct HTTPRequest {
    let method: String
    let path: String
    let query: [String: String]
    let headers: [String: String]
    let body: Data

    static let maxHeader = 16 * 1024
    static let maxBody = 64 * 1024

    enum Parse { case incomplete, bad, request(HTTPRequest) }

    // Waits for the whole request; anything malformed or too big is refused outright.
    static func parse(_ data: Data) -> Parse {
        guard let headerEnd = data.range(of: Data("\r\n\r\n".utf8)) else {
            return data.count > maxHeader ? .bad : .incomplete
        }
        guard headerEnd.lowerBound <= maxHeader,
              let head = String(data: data[..<headerEnd.lowerBound], encoding: .utf8) else { return .bad }
        let lines = head.components(separatedBy: "\r\n")
        let parts = lines.first?.split(separator: " ") ?? []
        guard parts.count >= 2 else { return .bad }
        var headers: [String: String] = [:]
        for line in lines.dropFirst() {
            guard let colon = line.firstIndex(of: ":") else { continue }
            headers[line[..<colon].lowercased()] = line[line.index(after: colon)...].trimmingCharacters(in: .whitespaces)
        }
        // Only plain bodies with a sane, non-negative length.
        guard headers["transfer-encoding"] == nil else { return .bad }
        var length = 0
        if let text = headers["content-length"] {
            guard text.allSatisfy({ $0.isASCII && $0.isNumber }), text.count <= 6, let n = Int(text), n <= maxBody else { return .bad }
            length = n
        }
        let body = data[headerEnd.upperBound...]
        guard body.count >= length else { return .incomplete }
        let target = parts[1].split(separator: "?", maxSplits: 1)
        var query: [String: String] = [:]
        for pair in (target.count > 1 ? target[1] : "").split(separator: "&") {
            let kv = pair.split(separator: "=", maxSplits: 1)
            if kv.count == 2 { query[String(kv[0])] = String(kv[1]) }
        }
        return .request(HTTPRequest(method: String(parts[0]), path: target.first.map(String.init) ?? "/",
                                    query: query, headers: headers, body: Data(body.prefix(length))))
    }
}

// A tiny HTTP server for the live page. It listens on 127.0.0.1 only, so nothing
// on the network can reach it directly; Tailscale Serve proxies it over HTTPS to
// the owner's own devices.
final class StatusServer: @unchecked Sendable {
    private let listener: NWListener
    private let queue = DispatchQueue(label: "lidawake.status-server")
    private let status: () -> Data
    private let remoteAllowed: () -> Bool
    private let awake: () -> Bool
    private let turnOff: () -> Void
    private let setTimer: (Int) -> Bool
    private let setLimit: (String, Bool) -> Bool

    init?(status: @escaping () -> Data, remoteAllowed: @escaping () -> Bool, awake: @escaping () -> Bool,
          turnOff: @escaping () -> Void, setLimit: @escaping (String, Bool) -> Bool,
          setTimer: @escaping (Int) -> Bool) {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: statusPort)!)
        guard let listener = try? NWListener(using: params) else { return nil }
        self.listener = listener
        self.status = status
        self.remoteAllowed = remoteAllowed
        self.awake = awake
        self.turnOff = turnOff
        self.setTimer = setTimer
        self.setLimit = setLimit
        listener.newConnectionHandler = { [weak self] connection in self?.accept(connection) }
        listener.start(queue: queue)
    }

    // Each request gets 10 seconds to arrive in full, and only so many may be open at once,
    // so slow or stuck connections can't pile up and starve the app.
    private var live = Set<ObjectIdentifier>()       // open connections
    private var answering = Set<ObjectIdentifier>()  // whose request arrived (a screen request may wait a while)
    private static let maxOpen = 64

    private func accept(_ connection: NWConnection) {
        let id = ObjectIdentifier(connection)
        guard live.count < Self.maxOpen else { return connection.cancel() }
        live.insert(id)
        connection.stateUpdateHandler = { [weak self] state in
            switch state {
            case .failed:
                connection.cancel()
            case .cancelled:
                self?.live.remove(id)
                self?.answering.remove(id)
            default: break
            }
        }
        connection.start(queue: queue)
        queue.asyncAfter(deadline: .now() + 10) { [weak self, weak connection] in
            guard let self, let connection, self.live.contains(id), !self.answering.contains(id) else { return }
            connection.cancel()
        }
        read(connection, Data())
    }

    private func read(_ connection: NWConnection, _ buffer: Data) {
        connection.receive(minimumIncompleteLength: 1, maximumLength: 64 * 1024) { data, _, done, error in
            var buffer = buffer
            if let data { buffer.append(data) }
            switch HTTPRequest.parse(buffer) {
            case .request(let request):
                self.answering.insert(ObjectIdentifier(connection))
                self.route(request, connection)
            case .bad:
                self.text(connection, "400 Bad Request", "Bad request")
            case .incomplete:
                if done || error != nil { connection.cancel() } else { self.read(connection, buffer) }
            }
        }
    }

    private func reply(_ connection: NWConnection, _ code: String, _ type: String, _ body: Data, extraHeaders: [String] = []) {
        let extra = extraHeaders.map { $0 + "\r\n" }.joined()
        let head = "HTTP/1.1 \(code)\r\nContent-Type: \(type)\r\nContent-Length: \(body.count)\r\n"
            + "Cache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nX-Frame-Options: DENY\r\n"
            + extra + "Connection: close\r\n\r\n"
        connection.send(content: Data(head.utf8) + body, completion: .contentProcessed { _ in connection.cancel() })
    }

    private func text(_ connection: NWConnection, _ code: String, _ message: String) {
        reply(connection, code, "text/plain; charset=utf-8", Data(message.utf8))
    }

    private func route(_ req: HTTPRequest, _ c: NWConnection) {
        // Only answer requests addressed to this Mac (the Tailscale name or localhost),
        // so a website using DNS rebinding can't reach the server from a browser here.
        let host = (req.headers["host"] ?? "").split(separator: ":").first.map(String.init) ?? ""
        guard host == "127.0.0.1" || host == "localhost" || host.hasSuffix(".ts.net") else {
            return text(c, "403 Forbidden", "Forbidden")
        }
        // Tailscale marks requests that came from the public internet (Funnel): never answer those.
        guard req.headers["tailscale-funnel-request"] == nil else {
            return text(c, "403 Forbidden", "Forbidden")
        }

        // Home-screen icon and app manifest: static, needed by iOS before any login.
        switch req.path {
        case "/apple-touch-icon.png", "/apple-touch-icon-precomposed.png":
            return reply(c, "200 OK", "image/png", appIcon180)
        case "/icon-512.png":
            return reply(c, "200 OK", "image/png", appIcon512)
        case "/sw.js":
            // Shows the Mac's alerts on the phone (also while the page is closed).
            return reply(c, "200 OK", "text/javascript; charset=utf-8", Data(serviceWorkerJS.utf8))
        case "/manifest.webmanifest":
            let manifest = ##"{"name":"Lid Awake","short_name":"Lid Awake","start_url":"/","display":"standalone","background_color":"#000000","theme_color":"#000000","icons":[{"src":"/apple-touch-icon.png","sizes":"180x180","type":"image/png"},{"src":"/icon-512.png","sizes":"512x512","type":"image/png"}]}"##
            return reply(c, "200 OK", "application/manifest+json", Data(manifest.utf8))
        default:
            break
        }

        // While lid-awake is off the page is closed: only the page itself and a
        // status that says "off" are answered — no login, data, screen or control.
        if !awake() {
            switch req.path {
            case "/": return reply(c, "200 OK", "text/html; charset=utf-8", Data(dashboardHTML.utf8))
            case "/auth": return reply(c, "200 OK", "application/json", Data(#"{"off":true}"#.utf8))
            default: return text(c, "423 Locked", "Lid Awake is off on your Mac.")
            }
        }

        let cookies = Dictionary(
            (req.headers["cookie"] ?? "").split(separator: ";").compactMap { pair -> (String, String)? in
                let kv = pair.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
                return kv.count == 2 ? (kv[0], kv[1]) : nil
            },
            uniquingKeysWith: { first, _ in first })
        let token = cookies[Auth.cookieName]
        // The page and its login calls are open; everything else needs a valid session.
        let isPublic = req.path == "/" || req.path == "/auth" || req.path == "/login" || req.path == "/logout"
            || req.path == "/passkey/login-options" || req.path == "/passkey/login"
        if !isPublic && !Auth.isValid(token) {
            return text(c, "401 Unauthorized", "Locked")
        }
        // Login and logout are POSTs with a custom header, which other websites can't send.
        if req.method == "POST" && req.headers["x-lidawake"] != "1" {
            return text(c, "403 Forbidden", "Forbidden")
        }

        switch (req.method, req.path) {
        case ("GET", "/"):
            reply(c, "200 OK", "text/html; charset=utf-8", Data(dashboardHTML.utf8))

        case ("GET", "/auth"):
            // Face ID needs HTTPS, so it's only offered on the Tailscale address.
            let info: [String: Any] = ["hasPassword": Auth.hasPassword, "loggedIn": Auth.isValid(token),
                                       "faceID": host.hasSuffix(".ts.net") && Passkeys.count > 0]
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: info)) ?? Data())

        case ("POST", "/login"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            switch Auth.login(body?["password"] as? String ?? "") {
            case .ok(let newToken):
                let secure = host.hasSuffix(".ts.net") ? "; Secure" : ""
                let maxAge = Int(Auth.sessionDays * 86400)
                reply(c, "204 No Content", "text/plain", Data(), extraHeaders: [
                    "Set-Cookie: \(Auth.cookieName)=\(newToken); Path=/; Max-Age=\(maxAge); HttpOnly; SameSite=Strict\(secure)",
                ])
            case .wrong:
                text(c, "401 Unauthorized", "Wrong password.")
            case .locked(let seconds):
                text(c, "429 Too Many Requests", "Too many wrong tries. Try again in \(seconds) seconds.")
            case .noPassword:
                text(c, "409 Conflict", "No password is set yet.")
            }

        // Face ID (passkeys). Signing in: a one-time challenge, then the phone's signature.
        case ("GET", "/passkey/login-options"):
            let options: [String: Any] = ["challenge": Passkeys.challenge("get"), "rpId": host,
                                          "ids": Passkeys.credentialIDs]
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: options)) ?? Data())

        case ("POST", "/passkey/login"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            guard Auth.hasPassword,
                  let id = body?["id"] as? String,
                  let clientData = (body?["clientData"] as? String).flatMap(Passkeys.fromBase64url),
                  let authData = (body?["authenticatorData"] as? String).flatMap(Passkeys.fromBase64url),
                  let signature = (body?["signature"] as? String).flatMap(Passkeys.fromBase64url),
                  Passkeys.verify(credentialID: id, clientData: clientData, authenticatorData: authData,
                                  signature: signature, host: host)
            else { return text(c, "401 Unauthorized", "Face ID sign-in didn't work. Use your password.") }
            reply(c, "204 No Content", "text/plain", Data(), extraHeaders: [
                "Set-Cookie: \(Auth.cookieName)=\(Auth.loginWithPasskey()); Path=/; Max-Age=\(Int(Auth.sessionDays * 86400)); HttpOnly; SameSite=Strict; Secure",
            ])

        // Adding Face ID (already signed in with the password).
        case ("GET", "/passkey/register-options"):
            guard host.hasSuffix(".ts.net") else { return text(c, "400 Bad Request", "Face ID works on the Tailscale address only.") }
            let options: [String: Any] = ["challenge": Passkeys.challenge("create"), "rpId": host]
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: options)) ?? Data())

        case ("POST", "/passkey/register"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            guard let clientData = (body?["clientData"] as? String).flatMap(Passkeys.fromBase64url),
                  let attestation = (body?["attestation"] as? String).flatMap(Passkeys.fromBase64url),
                  Passkeys.register(clientData: clientData, attestation: attestation, host: host)
            else { return text(c, "400 Bad Request", "Couldn't turn on Face ID. Try again.") }
            reply(c, "204 No Content", "text/plain", Data())

        // iPhone alerts: the Mac's public key, subscribing, and which alerts.
        case ("GET", "/push/key"):
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: ["key": WebPush.publicKey])) ?? Data())

        case ("POST", "/push/subscribe"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            let keys = body?["keys"] as? [String: Any]
            guard let endpoint = body?["endpoint"] as? String, let p256dh = keys?["p256dh"] as? String,
                  let auth = keys?["auth"] as? String,
                  WebPush.subscribe(endpoint: endpoint, p256dh: p256dh, auth: auth,
                                    off: body?["off"] as? [String] ?? [], pageHost: host)
            else { return text(c, "400 Bad Request", "Couldn't turn on alerts.") }
            reply(c, "204 No Content", "text/plain", Data())

        case ("POST", "/push/settings"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            guard let endpoint = body?["endpoint"] as? String else { return text(c, "400 Bad Request", "Missing phone.") }
            if body?["enabled"] as? Bool == false { WebPush.unsubscribe(endpoint: endpoint) }
            else { WebPush.setOff(endpoint: endpoint, off: body?["off"] as? [String] ?? []) }
            reply(c, "204 No Content", "text/plain", Data())

        case ("POST", "/logout"):
            Auth.logout(token)
            reply(c, "204 No Content", "text/plain", Data(), extraHeaders: [
                "Set-Cookie: \(Auth.cookieName)=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict",
            ])

        case ("POST", "/awake"):
            // The phone can only turn lid-awake off: while it's off this page is closed,
            // so turning it back on is done on the Mac.
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            guard body?["on"] as? Bool == false else {
                return text(c, "400 Bad Request", "Lid-awake can only be turned on from the Mac.")
            }
            DispatchQueue.main.async { self.turnOff() }
            reply(c, "204 No Content", "text/plain", Data())

        case ("GET", "/app-icon.png"):
            // Icons only for apps on the owner's list.
            let png: Data? = DispatchQueue.main.sync {
                PhoneApps.path(for: req.query["id"] ?? "").flatMap { PhoneApps.iconPNG($0) }
            }
            guard let png else { return text(c, "404 Not Found", "No such app.") }
            reply(c, "200 OK", "image/png", png)

        case ("POST", "/open-app"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            let opened: Bool = DispatchQueue.main.sync {
                guard let path = PhoneApps.path(for: body?["id"] as? String ?? "") else { return false }
                PhoneApps.open(path)
                return true
            }
            guard opened else { return text(c, "404 Not Found", "That app isn't on your list any more.") }
            FrameStream.shared.wakeNow()
            reply(c, "204 No Content", "text/plain", Data())

        case ("POST", "/quit-app"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            let result: Bool? = DispatchQueue.main.sync {
                guard let path = PhoneApps.path(for: body?["id"] as? String ?? "") else { return nil }
                return PhoneApps.quit(path)
            }
            switch result {
            case nil: text(c, "404 Not Found", "That app isn't on your list any more.")
            case false: text(c, "409 Conflict", "That app isn't open.")
            default: reply(c, "204 No Content", "text/plain", Data())
            }

        case ("POST", "/apps-order"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            guard let ids = body?["ids"] as? [String] else { return text(c, "400 Bad Request", "Missing app order.") }
            DispatchQueue.main.sync { PhoneApps.reorder(ids) }
            reply(c, "204 No Content", "text/plain", Data())

        // Safety limits (same switches as the Mac menu): "battery" or "heat", on or off.
        case ("POST", "/limits"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            guard let name = body?["name"] as? String, let on = body?["on"] as? Bool, setLimit(name, on)
            else { return text(c, "400 Bad Request", "Unknown limit.") }
            reply(c, "204 No Content", "text/plain", Data())

        case ("POST", "/timer"):
            // Same choices as the menu's "Turn off after"; 0 = never.
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            guard let minutes = body?["minutes"] as? Int, setTimer(minutes) else {
                return text(c, "400 Bad Request", "Unknown timer length.")
            }
            reply(c, "204 No Content", "text/plain", Data())

        case ("GET", "/status"):
            reply(c, "200 OK", "application/json", status())

        case ("GET", "/screen.jpg"):
            guard remoteAllowed() else {
                return text(c, "403 Forbidden", "Screen access is off. Turn on “Allow screen & control from phone” in the Lid Awake menu on your Mac.")
            }
            guard screenPermission() else {
                return text(c, "403 Forbidden", "On your Mac, allow Lid Awake in System Settings → Privacy & Security → Screen & System Audio Recording.")
            }
            // Waits until there's a frame newer than the phone's last one (or 2 s pass).
            let after = Int(req.query["after"] ?? "") ?? 0
            FrameStream.shared.frame(after: after) { result in
                self.queue.async {
                    switch result {
                    case .frame(let jpeg, let id):
                        StreamStats.shared.polled(bytes: jpeg.count)
                        self.reply(c, "200 OK", "image/jpeg", jpeg, extraHeaders: ["X-Frame-Id: \(id)"])
                    case .unchanged:
                        self.reply(c, "204 No Content", "text/plain", Data())
                    case .unavailable:
                        self.text(c, "503 Service Unavailable", "Waking the Mac's screen… This usually takes a few seconds.")
                    }
                }
            }

        case ("POST", "/input"):
            guard remoteAllowed() else { return text(c, "403 Forbidden", "Screen access is off.") }
            guard controlPermission() else {
                return text(c, "403 Forbidden", "On your Mac, allow Lid Awake in System Settings → Privacy & Security → Accessibility.")
            }
            guard let cmd = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any] else {
                return text(c, "400 Bad Request", "Bad request")
            }
            if let typed = cmd["text"] as? String, typed.count > RemoteInput.maxText {
                return text(c, "413 Payload Too Large", "That's too much text to type at once.")
            }
            FrameStream.shared.wakeNow()
            DispatchQueue.main.async { RemoteInput.perform(cmd) }
            reply(c, "204 No Content", "text/plain", Data())

        case ("POST", "/power"):
            // Lock the Mac, or turn its display off / back on, from the phone.
            guard remoteAllowed() else { return text(c, "403 Forbidden", "Screen access is off.") }
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            switch body?["action"] as? String {
            case "lock":
                guard controlPermission() else {
                    return text(c, "403 Forbidden", "On your Mac, allow Lid Awake in System Settings → Privacy & Security → Accessibility.")
                }
                // Control-⌘-Q, the macOS "Lock Screen" shortcut.
                DispatchQueue.main.async { RemoteInput.perform(["type": "key", "char": "q", "mods": ["ctrl", "cmd"]]) }
            case "displayoff":
                FrameStream.shared.displayOff()
            case "wake":
                FrameStream.shared.wakeNow()
            default:
                return text(c, "400 Bad Request", "Bad request")
            }
            reply(c, "204 No Content", "text/plain", Data())

        default:
            text(c, "404 Not Found", "Not found")
        }
    }
}
