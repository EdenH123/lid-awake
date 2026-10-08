import CommonCrypto
import Foundation
import Security

// Password lock for the phone page. Only a salted PBKDF2 hash of the password is
// stored, and sessions are kept as SHA-256 hashes of random tokens, each with the device
// that signed in, so the owner can see and sign out every device.
enum Auth {
    static let sessionDays = 7.0
    // "__Host-": the browser only accepts it from this exact HTTPS address (not from other
    // devices' pages on the tailnet, and never over plain http).
    static let cookieName = "__Host-la_session"
    static let minPasswordLength = 10
    // Posted when sessions are revoked (new password, sign-out) so their open live sockets close.
    static let sessionsChanged = Notification.Name("LidAwakeSessionsChanged")

    private static let defaults = UserDefaults.standard
    private static let lock = NSLock()
    private static let iterations = 600_000

    enum LoginResult { case ok(token: String), wrong, locked(seconds: Int), noPassword }

    struct Session {
        let id: String        // short, public handle (start of the hash), for signing one out
        let device: String
        let created: Date
        let faceID: Bool
        let current: Bool
    }

    static var hasPassword: Bool { defaults.data(forKey: "pwHash") != nil }

    static func setPassword(_ password: String) {
        let salt = randomBytes(16)
        lock.lock(); defer { lock.unlock() }
        defaults.set(salt, forKey: "pwSalt")
        defaults.set(derive(password, salt: salt, rounds: iterations), forKey: "pwHash")
        defaults.set(iterations, forKey: "pwIterations")
        // A new password logs out every device, forgets Face ID sign-ins and the phones
        // that get alerts (they can turn alerts on again after signing in).
        defaults.removeObject(forKey: "sessions")
        Passkeys.removeAll()
        WebPush.removeAll()
        failures = 0
        lockedUntil = .distantPast
        NotificationCenter.default.post(name: sessionsChanged, object: nil)
    }

    // MARK: Wrong-password lockout (kept across restarts)

    private static var failures: Int {
        get { defaults.integer(forKey: "loginFailures") }
        set { defaults.set(newValue, forKey: "loginFailures") }
    }
    private static var lockedUntil: Date {
        get { Date(timeIntervalSince1970: defaults.double(forKey: "loginLockedUntil")) }
        set { defaults.set(newValue.timeIntervalSince1970, forKey: "loginLockedUntil") }
    }

    static var isLockedOut: Bool { lock.lock(); defer { lock.unlock() }; return lockedUntil > Date() }

    static func clearLockout() {
        lock.lock(); defer { lock.unlock() }
        failures = 0
        lockedUntil = .distantPast
    }

    // Checks the password (counting wrong tries toward the lockout), without signing in.
    private static func verify(_ password: String) -> LoginResult? {
        guard let salt = defaults.data(forKey: "pwSalt"), let hash = defaults.data(forKey: "pwHash") else { return .noPassword }
        let wait = lockedUntil.timeIntervalSinceNow
        if wait > 0 { return .locked(seconds: Int(wait.rounded(.up))) }
        let rounds = defaults.object(forKey: "pwIterations") as? Int ?? 200_000
        guard constantTimeEqual(derive(password, salt: salt, rounds: rounds), hash) else {
            failures += 1
            // After 5 wrong tries: wait 1 min, then 2, 4, … up to 1 hour.
            if failures >= 5 {
                let seconds = min(3600, 60 * pow(2, Double(min(failures - 5, 6))))
                lockedUntil = Date().addingTimeInterval(seconds)
                return .locked(seconds: Int(seconds))
            }
            return .wrong
        }
        failures = 0
        // Older passwords were hashed with fewer rounds: upgrade now that we have it.
        if rounds < iterations {
            let salt = randomBytes(16)
            defaults.set(salt, forKey: "pwSalt")
            defaults.set(derive(password, salt: salt, rounds: iterations), forKey: "pwHash")
            defaults.set(iterations, forKey: "pwIterations")
        }
        return nil
    }

    static func login(_ password: String, device: String) -> LoginResult {
        lock.lock(); defer { lock.unlock() }
        if let problem = verify(password) { return problem }
        return .ok(token: makeSession(device: device, via: "password"))
    }

    // For changes that need the password again (adding Face ID). Wrong tries count too.
    static func confirm(_ password: String) -> LoginResult {
        lock.lock(); defer { lock.unlock() }
        return verify(password) ?? .ok(token: "")
    }

    // Face ID sign-in (Passkeys verified the signature): a session like a password login.
    static func loginWithPasskey(_ credentialID: String, device: String) -> String {
        lock.lock(); defer { lock.unlock() }
        failures = 0
        return makeSession(device: device, via: "faceid:" + credentialID)
    }

    // MARK: Sessions

    private static func makeSession(device: String, via: String) -> String {
        let token = randomBytes(32).map { String(format: "%02x", $0) }.joined()
        var sessions = storedSessions()
        let now = Date().timeIntervalSince1970
        sessions[sha256(token)] = ["expires": now + sessionDays * 86400, "created": now, "device": device, "via": via]
        defaults.set(sessions, forKey: "sessions")
        return token
    }

    static func isValid(_ token: String?) -> Bool {
        guard let token, !token.isEmpty else { return false }
        lock.lock(); defer { lock.unlock() }
        return storedSessions()[sha256(token)] != nil
    }

    // The session from a Cookie header. If the name appears more than once (a cookie planted
    // by another page), any valid one counts.
    static func token(fromCookies header: String?) -> String? {
        let values = (header ?? "").split(separator: ";").compactMap { pair -> String? in
            let kv = pair.split(separator: "=", maxSplits: 1).map { $0.trimmingCharacters(in: .whitespaces) }
            return kv.count == 2 && kv[0] == cookieName ? kv[1] : nil
        }
        return values.first(where: { isValid($0) }) ?? values.first
    }

    static func logout(_ token: String?) {
        guard let token else { return }
        lock.lock(); defer { lock.unlock() }
        var sessions = storedSessions()
        // A made-up token signs nothing out (and mustn't disturb anyone's live screen).
        guard sessions.removeValue(forKey: sha256(token)) != nil else { return }
        defaults.set(sessions, forKey: "sessions")
        NotificationCenter.default.post(name: sessionsChanged, object: nil)
    }

    static func sessions(current token: String?) -> [Session] {
        lock.lock(); defer { lock.unlock() }
        let mine = token.map(sha256)
        return storedSessions().map { hash, s in
            Session(id: String(hash.prefix(12)), device: s["device"] as? String ?? "Unknown device",
                    created: Date(timeIntervalSince1970: s["created"] as? Double ?? 0),
                    faceID: (s["via"] as? String ?? "").hasPrefix("faceid:"), current: hash == mine)
        }.sorted { $0.created > $1.created }
    }

    // Signs out one device (by its short id), every device but `keep`, or every device.
    @discardableResult
    static func signOut(id: String? = nil, keep: String? = nil, faceID credentialID: String? = nil) -> Int {
        if let id, id.count != 12 { return 0 }
        lock.lock(); defer { lock.unlock() }
        let kept = keep.map(sha256)
        var sessions = storedSessions()
        let before = sessions.count
        sessions = sessions.filter { hash, s in
            if hash == kept { return true }
            if let id { return !hash.hasPrefix(id) }
            if let credentialID { return s["via"] as? String != "faceid:" + credentialID }
            return false
        }
        let removed = before - sessions.count
        guard removed > 0 else { return 0 }
        defaults.set(sessions, forKey: "sessions")
        NotificationCenter.default.post(name: sessionsChanged, object: nil)
        return removed
    }

    // Unexpired sessions only. (Older versions stored just the expiry time.)
    private static func storedSessions() -> [String: [String: Any]] {
        let now = Date().timeIntervalSince1970
        var out: [String: [String: Any]] = [:]
        for (hash, value) in defaults.dictionary(forKey: "sessions") ?? [:] {
            if let expiry = value as? Double {
                if expiry > now { out[hash] = ["expires": expiry, "created": expiry - sessionDays * 86400, "device": "Phone", "via": "password"] }
            } else if let s = value as? [String: Any], let expiry = s["expires"] as? Double, expiry > now {
                out[hash] = s
            }
        }
        return out
    }

    // "iPhone", "iPad", "Mac"… from the browser's description of itself.
    static func deviceName(userAgent: String?) -> String {
        let ua = userAgent ?? ""
        for (needle, name) in [("iPhone", "iPhone"), ("iPad", "iPad"), ("Android", "Android phone"), ("Macintosh", "Mac"), ("Windows", "Windows PC")]
        where ua.contains(needle) { return name }
        return "Browser"
    }

    // MARK: Helpers

    private static func derive(_ password: String, salt: Data, rounds: Int) -> Data {
        var out = Data(count: 32)
        out.withUnsafeMutableBytes { outPtr in
            salt.withUnsafeBytes { saltPtr in
                _ = CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2), password, password.utf8.count,
                    saltPtr.bindMemory(to: UInt8.self).baseAddress, salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), UInt32(rounds),
                    outPtr.bindMemory(to: UInt8.self).baseAddress, 32)
            }
        }
        return out
    }

    private static func sha256(_ text: String) -> String {
        var digest = [UInt8](repeating: 0, count: Int(CC_SHA256_DIGEST_LENGTH))
        let bytes = Array(text.utf8)
        CC_SHA256(bytes, CC_LONG(bytes.count), &digest)
        return digest.map { String(format: "%02x", $0) }.joined()
    }

    private static func randomBytes(_ count: Int) -> Data {
        var data = Data(count: count)
        _ = data.withUnsafeMutableBytes { SecRandomCopyBytes(kSecRandomDefault, count, $0.baseAddress!) }
        return data
    }

    private static func constantTimeEqual(_ a: Data, _ b: Data) -> Bool {
        guard a.count == b.count else { return false }
        return zip(a, b).reduce(0) { $0 | ($1.0 ^ $1.1) } == 0
    }
}
