import CryptoKit
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

// Who may use the page: this Mac's own Tailscale address, opened from a device signed in to
// the same Tailscale account as the Mac. Learned from `tailscale status` (and remembered, so a
// restart while Tailscale is starting doesn't lock the owner out).
enum Tailnet {
    private static let lock = NSLock()
    private static let defaults = UserDefaults.standard

    static var host: String? { lock.lock(); defer { lock.unlock() }; return defaults.string(forKey: "tailnetHost") }
    static var owner: String? { lock.lock(); defer { lock.unlock() }; return defaults.string(forKey: "tailnetOwner") }

    static func update(fromStatus json: [String: Any]) {
        guard let me = json["Self"] as? [String: Any], let dns = me["DNSName"] as? String, dns.hasSuffix(".ts.net."),
              let userID = (me["UserID"] as? NSNumber)?.stringValue,
              let user = (json["User"] as? [String: Any])?[userID] as? [String: Any],
              let login = user["LoginName"] as? String, !login.isEmpty else { return }
        lock.lock(); defer { lock.unlock() }
        defaults.set(String(dns.dropLast()).lowercased(), forKey: "tailnetHost")
        defaults.set(login.lowercased(), forKey: "tailnetOwner")
    }

    // A request that came through Tailscale Serve from the owner's own device, to this Mac's name.
    static func isOwner(host: String, login: String?) -> Bool {
        guard let mine = self.host, let owner = self.owner, let login else { return false }
        return host.lowercased() == mine && login.lowercased() == owner
    }

    static func isLoopback(_ host: String) -> Bool { host == "127.0.0.1" || host == "localhost" }

    // MARK: How a device reaches this Mac

    // Tailscale's view of each device (by its 100.x address): the direct address it's reached
    // at, or the relay used instead. Refreshed in the background at most every 10 seconds.
    private static var peers: [String: (direct: String, relay: String, active: Bool)] = [:]
    private static var peersAt = Date.distantPast
    private static var refreshing = false

    private static func refreshPeers() {
        lock.lock()
        guard !refreshing, Date().timeIntervalSince(peersAt) > 10 else { lock.unlock(); return }
        refreshing = true
        lock.unlock()
        DispatchQueue.global(qos: .utility).async {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/Applications/Tailscale.app/Contents/MacOS/Tailscale")
            p.arguments = ["status", "--json"]
            var env = ProcessInfo.processInfo.environment
            env["TAILSCALE_BE_CLI"] = "1"
            p.environment = env
            let out = Pipe()
            p.standardOutput = out
            p.standardError = FileHandle.nullDevice
            var found: [String: (direct: String, relay: String, active: Bool)] = [:]
            if (try? p.run()) != nil {
                let data = out.fileHandleForReading.readDataToEndOfFile()
                p.waitUntilExit()
                let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any]
                for case let peer as [String: Any] in (json?["Peer"] as? [String: Any] ?? [:]).values {
                    for ip in peer["TailscaleIPs"] as? [String] ?? [] {
                        found[ip] = (peer["CurAddr"] as? String ?? "", peer["Relay"] as? String ?? "", peer["Active"] as? Bool ?? false)
                    }
                }
            }
            lock.lock()
            if !found.isEmpty { peers = found }
            peersAt = Date()
            refreshing = false
            lock.unlock()
        }
    }

    // "Direct · same Wi-Fi", "Direct · over the internet" or "Through a relay in Paris", for the
    // device at this Tailscale address (Tailscale Serve reports it; it can't be faked).
    static func path(to ip: String?) -> String? {
        refreshPeers()
        guard let ip else { return nil }
        lock.lock(); let peer = peers[ip]; lock.unlock()
        // An idle device has no live path yet (the page is about to make one).
        guard let peer, peer.active || !peer.direct.isEmpty else { return nil }
        guard !peer.direct.isEmpty else {
            let cities = ["par": "Paris", "fra": "Frankfurt", "ams": "Amsterdam", "lhr": "London", "mad": "Madrid",
                          "waw": "Warsaw", "tlv": "Tel Aviv", "nyc": "New York", "sfo": "San Francisco", "sin": "Singapore"]
            return cities[peer.relay].map { "Through a relay in \($0)" } ?? "Through a relay server"
        }
        // The address part of "1.2.3.4:41641" or "[fe80::1]:41641".
        var address = peer.direct
        if let colon = address.lastIndex(of: ":") { address = String(address[..<colon]) }
        address = address.trimmingCharacters(in: CharacterSet(charactersIn: "[]"))
        if address.hasPrefix("172.20.10.") { return "Direct · iPhone hotspot" }
        let octets = address.split(separator: ".").compactMap { Int($0) }
        let privateV4 = octets.count == 4 && (octets[0] == 10 || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 172 && (16...31).contains(octets[1])) || (octets[0] == 169 && octets[1] == 254))
        let privateV6 = address.lowercased().hasPrefix("fe80") || address.lowercased().hasPrefix("fd")
        return privateV4 || privateV6 ? "Direct · same Wi-Fi" : "Direct · over the internet"
    }
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

    if let me = (try? JSONSerialization.jsonObject(with: Data(run(["status", "--json"]).utf8))) as? [String: Any] {
        Tailnet.update(fromStatus: me)
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
    private static var lastWake: [String: String] = [:]  // per device; only touched on the server queue
    private let listener: NWListener
    private let queue = DispatchQueue(label: "lidawake.status-server")
    private let status: () -> Data
    private let remoteAllowed: () -> Bool
    private let controlAllowed: () -> Bool
    private let cameraAllowed: () -> Bool
    private let awake: () -> Bool
    private let turnOff: () -> Void
    private let setTimer: (Int) -> Bool
    private let setLimit: (String, Bool) -> Bool

    init?(status: @escaping () -> Data, remoteAllowed: @escaping () -> Bool, controlAllowed: @escaping () -> Bool,
          cameraAllowed: @escaping () -> Bool, awake: @escaping () -> Bool,
          turnOff: @escaping () -> Void, setLimit: @escaping (String, Bool) -> Bool,
          setTimer: @escaping (Int) -> Bool) {
        let params = NWParameters.tcp
        params.allowLocalEndpointReuse = true
        params.requiredLocalEndpoint = .hostPort(host: "127.0.0.1", port: NWEndpoint.Port(rawValue: statusPort)!)
        guard let listener = try? NWListener(using: params) else { return nil }
        self.listener = listener
        self.status = status
        self.remoteAllowed = remoteAllowed
        self.controlAllowed = controlAllowed
        self.cameraAllowed = cameraAllowed
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
            + "Cross-Origin-Resource-Policy: same-origin\r\nReferrer-Policy: no-referrer\r\n"
            + extra + "Connection: close\r\n\r\n"
        connection.send(content: Data(head.utf8) + body, completion: .contentProcessed { _ in connection.cancel() })
    }

    private func text(_ connection: NWConnection, _ code: String, _ message: String) {
        reply(connection, code, "text/plain; charset=utf-8", Data(message.utf8))
    }

    // The page may only run its own two scripts and talk to this Mac (defense in depth: the page
    // never puts Mac data into HTML, but if it ever did, injected script still couldn't run).
    private static let scriptHashes: String = {
        var hashes: [String] = []
        var rest = dashboardHTML[...]
        while let open = rest.range(of: "<script>"), let close = rest.range(of: "</script>", range: open.upperBound..<rest.endIndex) {
            let digest = SHA256.hash(data: Data(rest[open.upperBound..<close.lowerBound].utf8))
            hashes.append("'sha256-\(Data(digest).base64EncodedString())'")
            rest = rest[close.upperBound...]
        }
        return hashes.joined(separator: " ")
    }()

    // The page, with the launch-screen links for every iPhone size filled in.
    private static let pageHTML = Data(dashboardHTML.replacingOccurrences(of: "<!--SPLASH-->", with: Splash.linkTags).utf8)

    private func pageHeaders(_ host: String) -> [String] {
        ["Content-Security-Policy: default-src 'none'; script-src \(Self.scriptHashes); style-src 'unsafe-inline'; "
            + "img-src 'self' blob:; connect-src 'self' wss://\(host); worker-src 'self'; manifest-src 'self'; "
            + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'"]
    }

    private func page(_ c: NWConnection, _ host: String) {
        reply(c, "200 OK", "text/html; charset=utf-8", Self.pageHTML, extraHeaders: pageHeaders(host))
    }

    private func sessionCookie(_ token: String) -> [String] {
        ["Set-Cookie: \(Auth.cookieName)=\(token); Path=/; Max-Age=\(Int(Auth.sessionDays * 86400)); HttpOnly; SameSite=Strict; Secure",
         "Set-Cookie: la_session=; Path=/; Max-Age=0"]  // the old name, from before
    }

    // A new password sign-in or Face ID key: tell the owner on the Mac and on their phones,
    // so a sign-in they didn't make doesn't go unnoticed.
    private func announce(_ title: String, _ body: String) {
        notify("\(title). \(body)")
        WebPush.send(.security, title: title, body: body)
        AccessLog.write(title + " · " + body)
    }

    private func route(_ req: HTTPRequest, _ c: NWConnection) {
        let host = ((req.headers["host"] ?? "").split(separator: ":").first.map(String.init) ?? "").lowercased()
        // Tailscale marks requests that came from the public internet (Funnel): never answer those.
        guard req.headers["tailscale-funnel-request"] == nil else {
            return text(c, "403 Forbidden", "Forbidden")
        }
        let isStatic = ["/apple-touch-icon.png", "/apple-touch-icon-precomposed.png", "/icon-512.png", "/sw.js",
                        "/manifest.webmanifest"].contains(req.path) || req.path.hasPrefix("/splash/") || req.path.hasPrefix("/sky/")

        if Tailnet.isLoopback(host) {
            // On this Mac's own 127.0.0.1 address the page only points to the Tailscale address:
            // signing in here would hand the session to every other local web server.
            if req.path == "/" { return page(c, host) }
            if req.path == "/auth" {
                let info: [String: Any] = ["loopback": true, "url": Tailnet.host.map { "https://\($0)/" } ?? ""]
                return reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: info)) ?? Data())
            }
            if !isStatic { return text(c, "403 Forbidden", "Open Lid Awake at its Tailscale address.") }
        } else if !Tailnet.isOwner(host: host, login: req.headers["tailscale-user-login"]) {
            // Only this Mac's own Tailscale name, from a device signed in to the Mac's Tailscale
            // account (Tailscale adds who it is; it can't be faked from another device). Also
            // stops DNS rebinding, since no other name is answered.
            return text(c, "403 Forbidden", "This device isn't signed in to the Tailscale account this Mac uses.")
        }

        // Requests started by other websites (or other devices' pages on the tailnet) are refused,
        // except opening the page itself.
        if !isStatic && req.path != "/", let site = req.headers["sec-fetch-site"], site != "same-origin" && site != "none" {
            return text(c, "403 Forbidden", "Forbidden")
        }

        // Home-screen icon and app manifest: static, needed by iOS before any login.
        switch req.path {
        case "/apple-touch-icon.png", "/apple-touch-icon-precomposed.png":
            return reply(c, "200 OK", "image/png", appIcon180)
        case "/icon-512.png":
            return reply(c, "200 OK", "image/png", appIcon512)
        case let path where path.hasPrefix("/sky/"):
            // Photos for the opening animation (sunrise, day, sunset, night), bundled with the app.
            let name = String(path.dropFirst("/sky/".count))
            guard ["sunrise.jpg", "day.jpg", "sunset.jpg", "night.jpg"].contains(name),
                  let url = Bundle.main.url(forResource: "sky-" + name.dropLast(4), withExtension: "jpg"),
                  let jpeg = try? Data(contentsOf: url) else { return text(c, "404 Not Found", "Not found") }
            return reply(c, "200 OK", "image/jpeg", jpeg)
        case let path where path.hasPrefix("/splash/"):
            // Launch screens (see Splash): needed by iOS before any sign-in.
            guard let png = Splash.png(forPath: path) else { return text(c, "404 Not Found", "Not found") }
            return reply(c, "200 OK", "image/png", png)
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
            case "/": return page(c, host)
            case "/auth": return reply(c, "200 OK", "application/json", Data(#"{"off":true}"#.utf8))
            default: return text(c, "423 Locked", "Lid Awake is off on your Mac.")
            }
        }

        let token = Auth.token(fromCookies: req.headers["cookie"])
        let device = Auth.deviceName(userAgent: req.headers["user-agent"])
        // The page and its login calls are open; everything else needs a valid session.
        let isPublic = req.path == "/" || req.path == "/auth" || req.path == "/login" || req.path == "/logout"
            || req.path == "/passkey/login-options" || req.path == "/passkey/login"
        if !isPublic && !Auth.isValid(token) {
            return text(c, "401 Unauthorized", "Locked")
        }
        // Every change is a POST with a custom header, which other websites can't send.
        if req.method == "POST" && req.headers["x-lidawake"] != "1" {
            return text(c, "403 Forbidden", "Forbidden")
        }

        switch (req.method, req.path) {
        case ("GET", "/"):
            page(c, host)

        case ("GET", "/auth"):
            let info: [String: Any] = ["hasPassword": Auth.hasPassword, "loggedIn": Auth.isValid(token),
                                       "faceID": Passkeys.count > 0]
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: info)) ?? Data())

        case ("POST", "/login"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            switch Auth.login(body?["password"] as? String ?? "", device: device) {
            case .ok(let newToken):
                reply(c, "204 No Content", "text/plain", Data(), extraHeaders: sessionCookie(newToken))
                announce("New sign-in to Lid Awake", "With the password, on \(device == "Browser" ? "a browser" : "an \(device)").")
            case .wrong:
                text(c, "401 Unauthorized", "Wrong password.")
            case .locked(let seconds):
                text(c, "429 Too Many Requests", "Too many wrong tries. Try again in \(seconds) seconds.")
            case .noPassword:
                text(c, "409 Conflict", "No password is set yet.")
            }

        // Face ID (passkeys). Signing in: a one-time challenge, then the phone's signature.
        case ("GET", "/passkey/login-options"):
            // No list of keys: the phone offers its own (and strangers learn nothing).
            let options: [String: Any] = ["challenge": Passkeys.challenge("get"), "rpId": host]
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
            reply(c, "204 No Content", "text/plain", Data(), extraHeaders: sessionCookie(Auth.loginWithPasskey(id, device: device)))

        // Adding Face ID: signed in, and the password typed again before the phone is even asked
        // (the one-time challenge is only handed out after the password checks out).
        case ("POST", "/passkey/register-options"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            switch Auth.confirm(body?["password"] as? String ?? "") {
            case .ok: break
            case .locked(let seconds): return text(c, "429 Too Many Requests", "Too many wrong tries. Try again in \(seconds) seconds.")
            default: return text(c, "401 Unauthorized", "Wrong password.")
            }
            let options: [String: Any] = ["challenge": Passkeys.challenge("create"), "rpId": host, "ids": Passkeys.credentialIDs]
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: options)) ?? Data())

        case ("POST", "/passkey/register"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            guard let clientData = (body?["clientData"] as? String).flatMap(Passkeys.fromBase64url),
                  let attestation = (body?["attestation"] as? String).flatMap(Passkeys.fromBase64url),
                  Passkeys.register(clientData: clientData, attestation: attestation, host: host, device: device)
            else { return text(c, "400 Bad Request", "Couldn't turn on Face ID. Try again.") }
            reply(c, "204 No Content", "text/plain", Data())
            announce("Face ID added to Lid Awake", "On \(device == "Browser" ? "a browser" : "an \(device)"). If this wasn't you, remove it from the Lid Awake menu on your Mac.")

        // Camera: Face ID every time. A one-time challenge (only while the Mac menu allows the
        // camera), then the phone's signature buys a one-minute ticket that the live socket redeems.
        case ("POST", "/camera/options"):
            guard remoteAllowed() && cameraAllowed() else {
                return text(c, "403 Forbidden", "The camera is off. On your Mac, tick “Allow camera & microphone from phone” in the Lid Awake menu.")
            }
            guard Passkeys.count > 0, host.hasSuffix(".ts.net") else {
                return text(c, "409 Conflict", "The camera needs Face ID. Turn it on first in Settings → Sign-in.")
            }
            let options: [String: Any] = ["challenge": Passkeys.challenge("camera"), "rpId": host, "ids": Passkeys.credentialIDs]
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: options)) ?? Data())

        case ("POST", "/camera/unlock"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            guard remoteAllowed() && cameraAllowed(), let token,
                  let id = body?["id"] as? String,
                  let clientData = (body?["clientData"] as? String).flatMap(Passkeys.fromBase64url),
                  let authData = (body?["authenticatorData"] as? String).flatMap(Passkeys.fromBase64url),
                  let signature = (body?["signature"] as? String).flatMap(Passkeys.fromBase64url),
                  Passkeys.verify(credentialID: id, clientData: clientData, authenticatorData: authData,
                                  signature: signature, host: host, purpose: "camera")
            else { return text(c, "401 Unauthorized", "Face ID didn't work, so the camera stays closed.") }
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: ["ticket": CameraTickets.issue(for: token)])) ?? Data())

        // The newest lines of the activity log, for Settings.
        case ("GET", "/activity"):
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: ["lines": AccessLog.recent(8)])) ?? Data())

        // Signed-in devices: see them, sign one out, or all but this one.
        case ("GET", "/sessions"):
            let list = Auth.sessions(current: token).map { s -> [String: Any] in
                ["id": s.id, "device": s.device, "created": Int(s.created.timeIntervalSince1970), "faceID": s.faceID, "current": s.current]
            }
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: ["sessions": list])) ?? Data())

        case ("POST", "/sessions/signout"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            if body?["others"] as? Bool == true {
                Auth.signOut(keep: token)
            } else if let id = body?["id"] as? String {
                Auth.signOut(id: id, keep: token)
            } else {
                return text(c, "400 Bad Request", "Which device?")
            }
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
                "Set-Cookie: \(Auth.cookieName)=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict; Secure",
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
            // Icons only for apps on the owner's list, or open right now (the dock).
            let png: Data? = DispatchQueue.main.sync {
                let id = req.query["id"] ?? ""
                return (PhoneApps.path(for: id) ?? PhoneApps.runningPath(for: id))
                    .flatMap { PhoneApps.iconPNG($0, size: Int(req.query["size"] ?? "") ?? 256) }
            }
            guard let png else { return text(c, "404 Not Found", "No such app.") }
            reply(c, "200 OK", "image/png", png)

        case ("POST", "/open-app"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            let opened: Bool = DispatchQueue.main.sync {
                guard let path = PhoneApps.path(for: body?["id"] as? String ?? "") else { return false }
                PhoneApps.open(path)
                AccessLog.write("Opened \(PhoneApps.name(path)) from the phone · \(device)")
                return true
            }
            guard opened else { return text(c, "404 Not Found", "That app isn't on your list any more.") }
            FrameStream.shared.wakeNow()
            reply(c, "204 No Content", "text/plain", Data())

        // The dock: bring an app that's already open to the front, hide it, or quit it (as ⌘Q;
        // it can still ask to save). Only apps already open; it can't start new ones.
        case ("POST", "/focus-app"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            let action = body?["action"] as? String ?? "focus"
            guard ["focus", "hide", "quit"].contains(action) else { return text(c, "400 Bad Request", "Unknown action.") }
            let name: String? = DispatchQueue.main.sync {
                guard let path = PhoneApps.runningPath(for: body?["id"] as? String ?? "") else { return nil }
                let done = action == "hide" ? PhoneApps.hide(path) : action == "quit" ? PhoneApps.quit(path) : PhoneApps.activate(path)
                return done ? PhoneApps.name(path) : nil
            }
            guard let name else { return text(c, "404 Not Found", "That app isn't open any more.") }
            AccessLog.write("\(["focus": "Brought", "hide": "Hid", "quit": "Quit"][action]!) \(name)\(action == "focus" ? " to the front" : "") from the phone · \(device)")
            FrameStream.shared.wakeNow()
            reply(c, "204 No Content", "text/plain", Data())

        case ("POST", "/quit-app"):
            let body = (try? JSONSerialization.jsonObject(with: req.body)) as? [String: Any]
            let result: Bool? = DispatchQueue.main.sync {
                guard let path = PhoneApps.path(for: body?["id"] as? String ?? "") else { return nil }
                AccessLog.write("Quit \(PhoneApps.name(path)) from the phone · \(device)")
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
            // The phone's "stay awake" state, logged when it changes (to see why a phone went to sleep).
            if let raw = req.query["wl"]?.replacingOccurrences(of: "+", with: " ").removingPercentEncoding {
                let state = String(raw.filter { $0.isLetter || $0.isNumber || " :()-".contains($0) }.prefix(60))
                let who = req.headers["x-forwarded-for"] ?? "local"
                if Self.lastWake[who] != state {
                    Self.lastWake[who] = state
                    StreamStats.shared.note("phone stay-awake: \(state)")
                }
            }
            // Plus how this very device reaches the Mac (Serve says which device asked).
            var data = status()
            if let link = Tailnet.path(to: req.headers["x-forwarded-for"]),
               var obj = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] {
                obj["link"] = link
                data = (try? JSONSerialization.data(withJSONObject: obj)) ?? data
            }
            reply(c, "200 OK", "application/json", data)

        // Battery temperature and charge over the last hours (from macOS's own power log) for the graphs.
        case ("GET", "/temp-history"):
            reply(c, "200 OK", "application/json", PowerHistory.temperature(hours: Double(req.query["hours"] ?? "") ?? 24))
        case ("GET", "/battery-history"):
            reply(c, "200 OK", "application/json", PowerHistory.battery(hours: Double(req.query["hours"] ?? "") ?? 24))
        case ("GET", "/cpu-history"):
            reply(c, "200 OK", "application/json", PowerHistory.cpu(hours: Double(req.query["hours"] ?? "") ?? 24))
        case ("GET", "/cpu-icon"):
            // Only apps seen in the processor history, so the page can't fetch just any app's icon.
            let png: Data? = DispatchQueue.main.sync { PowerHistory.iconPNG(key: req.query["key"] ?? "") }
            guard let png else { return text(c, "404 Not Found", "No such app.") }
            reply(c, "200 OK", "image/png", png)

        // The Memory window: read fresh each time (the phone asks every 2 s while it's open).
        case ("GET", "/memory"):
            let snap = MemoryInfo.snapshot { PhoneApps.id($0) }
            reply(c, "200 OK", "application/json", (try? JSONSerialization.data(withJSONObject: snap)) ?? Data("{}".utf8))

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
            guard controlAllowed() else { return text(c, "403 Forbidden", "View only: control from the phone is off in the Lid Awake menu on your Mac.") }
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
                guard controlAllowed() else { return text(c, "403 Forbidden", "View only: control from the phone is off in the Lid Awake menu on your Mac.") }
                AccessLog.write("Mac locked from the phone · \(device)")
                guard controlPermission() else {
                    return text(c, "403 Forbidden", "On your Mac, allow Lid Awake in System Settings → Privacy & Security → Accessibility.")
                }
                // Control-⌘-Q, the macOS "Lock Screen" shortcut.
                DispatchQueue.main.async { RemoteInput.perform(["type": "key", "char": "q", "mods": ["ctrl", "cmd"]]) }
            case "displayoff":
                AccessLog.write("Display turned off from the phone · \(device)")
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
