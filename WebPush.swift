import CryptoKit
import Foundation

// iPhone alerts for the home-screen Lid Awake page (standard Web Push). The phone subscribes
// once; the Mac then sends short alerts through the phone's push service (Apple's, for an
// iPhone), encrypted so only that phone can read them (RFC 8291) and signed with this Mac's
// key (VAPID, RFC 8292). Alerts reach the phone even when the page is closed.
enum WebPush {
    enum Kind: String, CaseIterable { case turnedOff, lowBattery, claudeUsage, warm }

    private struct Subscription: Codable {
        let endpoint: String
        let p256dh: Data   // the phone's public key (65 bytes)
        let auth: Data     // shared secret (16 bytes)
        var off: [String]  // alert kinds this phone turned off
    }

    private static let defaults = UserDefaults.standard
    private static let lock = NSLock()
    // Only real push services: the Mac never posts to an address a page made up.
    private static let allowedHosts = ["web.push.apple.com", "fcm.googleapis.com", "updates.push.services.mozilla.com"]

    // This Mac's VAPID key, made once.
    private static var signingKey: P256.Signing.PrivateKey {
        if let raw = defaults.data(forKey: "pushKey"), let key = try? P256.Signing.PrivateKey(rawRepresentation: raw) { return key }
        let key = P256.Signing.PrivateKey()
        defaults.set(key.rawRepresentation, forKey: "pushKey")
        return key
    }

    static var publicKey: String { Passkeys.base64url(signingKey.publicKey.x963Representation) }

    static var count: Int { lock.lock(); defer { lock.unlock() }; return stored().count }

    static func subscribe(endpoint: String, p256dh: String, auth: String, off: [String], pageHost: String) -> Bool {
        guard let url = URL(string: endpoint), url.scheme == "https", let host = url.host,
              allowedHosts.contains(where: { host == $0 || host.hasSuffix("." + $0) }),
              let key = Passkeys.fromBase64url(p256dh), key.count == 65, key.first == 4,
              let secret = Passkeys.fromBase64url(auth), secret.count == 16 else { return false }
        lock.lock(); defer { lock.unlock() }
        defaults.set("https://\(pageHost)", forKey: "pushContact")
        var all = stored().filter { $0.endpoint != endpoint }
        all.append(Subscription(endpoint: endpoint, p256dh: key, auth: secret, off: off.filter { Kind(rawValue: $0) != nil }))
        save(all)
        return true
    }

    static func setOff(endpoint: String, off: [String]) {
        lock.lock(); defer { lock.unlock() }
        var all = stored()
        guard let i = all.firstIndex(where: { $0.endpoint == endpoint }) else { return }
        all[i].off = off.filter { Kind(rawValue: $0) != nil }
        save(all)
    }

    static func unsubscribe(endpoint: String) {
        lock.lock(); defer { lock.unlock() }
        save(stored().filter { $0.endpoint != endpoint })
    }

    static func removeAll() {
        lock.lock(); defer { lock.unlock() }
        defaults.removeObject(forKey: "pushSubscriptions")
    }

    // Sends to every phone that wants this kind. `done` runs when all have answered (or 8 s pass).
    static func send(_ kind: Kind?, title: String, body: String, done: (() -> Void)? = nil) {
        lock.lock()
        let targets = stored().filter { kind == nil || !$0.off.contains(kind!.rawValue) }
        lock.unlock()
        guard !targets.isEmpty else { done?(); return }
        let payload = (try? JSONSerialization.data(withJSONObject: ["title": title, "body": body, "tag": kind?.rawValue ?? "test"])) ?? Data()
        let group = DispatchGroup()
        for target in targets {
            guard let request = request(for: target, payload: payload) else { continue }
            group.enter()
            URLSession.shared.dataTask(with: request) { _, response, _ in
                let status = (response as? HTTPURLResponse)?.statusCode ?? 0
                // Gone or unknown: the phone unsubscribed or removed the app.
                if status == 404 || status == 410 { unsubscribe(endpoint: target.endpoint) }
                StreamStats.shared.note("alert \"\(title)\" → \(URL(string: target.endpoint)?.host ?? "?") \(status)")
                group.leave()
            }.resume()
        }
        if let done {
            DispatchQueue.global().async {
                _ = group.wait(timeout: .now() + 8)
                DispatchQueue.main.async(execute: done)
            }
        }
    }

    // MARK: Encryption (RFC 8291, aes128gcm) and VAPID (RFC 8292)

    private static func request(for target: Subscription, payload: Data) -> URLRequest? {
        guard let url = URL(string: target.endpoint), let host = url.host,
              let body = try? encrypt(payload, to: target), let jwt = try? vapidToken(audience: "https://\(host)")
        else { return nil }
        var request = URLRequest(url: url, timeoutInterval: 8)
        request.httpMethod = "POST"
        request.httpBody = body
        request.setValue("vapid t=\(jwt), k=\(publicKey)", forHTTPHeaderField: "Authorization")
        request.setValue("aes128gcm", forHTTPHeaderField: "Content-Encoding")
        request.setValue("application/octet-stream", forHTTPHeaderField: "Content-Type")
        request.setValue("86400", forHTTPHeaderField: "TTL")
        request.setValue("high", forHTTPHeaderField: "Urgency")
        return request
    }

    static func encrypt(_ plaintext: Data, phoneKey: Data, authSecret: Data,
                        serverKey: P256.KeyAgreement.PrivateKey = .init(), salt: Data? = nil) throws -> Data {
        let salt = salt ?? Data((0..<16).map { _ in UInt8.random(in: 0...255) })
        let phone = try P256.KeyAgreement.PublicKey(x963Representation: phoneKey)
        let serverPublic = serverKey.publicKey.x963Representation
        let shared = try serverKey.sharedSecretFromKeyAgreement(with: phone)
        let ecdh = shared.withUnsafeBytes { Data($0) }
        // IKM = HKDF(auth, ecdh, "WebPush: info" 0 phoneKey serverKey)
        let ikm = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ecdh), salt: authSecret,
                                         info: Data("WebPush: info".utf8) + [0] + phoneKey + serverPublic, outputByteCount: 32)
        let ikmData = ikm.withUnsafeBytes { Data($0) }
        let cek = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ikmData), salt: salt,
                                         info: Data("Content-Encoding: aes128gcm".utf8) + [0], outputByteCount: 16)
        let nonce = HKDF<SHA256>.deriveKey(inputKeyMaterial: SymmetricKey(data: ikmData), salt: salt,
                                           info: Data("Content-Encoding: nonce".utf8) + [0], outputByteCount: 12)
        let sealed = try AES.GCM.seal(plaintext + [2], using: cek,  // 0x02: last (only) record
                                      nonce: AES.GCM.Nonce(data: nonce.withUnsafeBytes { Data($0) }))
        // Header: salt | record size (4096) | key length | server public key, then the record.
        return salt + Data([0, 0, 0x10, 0]) + [UInt8(serverPublic.count)] + serverPublic + sealed.ciphertext + sealed.tag
    }

    private static func encrypt(_ plaintext: Data, to target: Subscription) throws -> Data {
        try encrypt(plaintext, phoneKey: target.p256dh, authSecret: target.auth)
    }

    static func vapidToken(audience: String) throws -> String {
        let header = Passkeys.base64url(Data(#"{"typ":"JWT","alg":"ES256"}"#.utf8))
        let claims: [String: Any] = ["aud": audience, "exp": Int(Date().timeIntervalSince1970) + 12 * 3600,
                                     "sub": defaults.string(forKey: "pushContact") ?? "https://lid-awake.example"]
        let body = Passkeys.base64url(try JSONSerialization.data(withJSONObject: claims))
        let signature = try signingKey.signature(for: Data("\(header).\(body)".utf8))
        return "\(header).\(body).\(Passkeys.base64url(signature.rawRepresentation))"
    }

    private static func stored() -> [Subscription] {
        guard let data = defaults.data(forKey: "pushSubscriptions") else { return [] }
        return (try? JSONDecoder().decode([Subscription].self, from: data)) ?? []
    }

    private static func save(_ all: [Subscription]) {
        defaults.set(try? JSONEncoder().encode(all), forKey: "pushSubscriptions")
    }
}
