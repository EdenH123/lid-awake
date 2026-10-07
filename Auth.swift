import CommonCrypto
import Foundation
import Security

// Password lock for the phone page. Only a salted PBKDF2 hash of the password is
// stored, and sessions are kept as SHA-256 hashes of random tokens.
enum Auth {
    static let sessionDays = 7.0
    static let cookieName = "la_session"
    // Posted when sessions are revoked (new password, logout) so open live sockets close.
    static let sessionsChanged = Notification.Name("LidAwakeSessionsChanged")

    private static let defaults = UserDefaults.standard
    private static let lock = NSLock()
    private static var failures = 0
    private static var lockedUntil = Date.distantPast

    enum LoginResult { case ok(token: String), wrong, locked(seconds: Int), noPassword }

    static var hasPassword: Bool { defaults.data(forKey: "pwHash") != nil }

    static func setPassword(_ password: String) {
        let salt = randomBytes(16)
        lock.lock(); defer { lock.unlock() }
        defaults.set(salt, forKey: "pwSalt")
        defaults.set(derive(password, salt: salt), forKey: "pwHash")
        // A new password logs out every device.
        defaults.removeObject(forKey: "sessions")
        failures = 0
        lockedUntil = .distantPast
        NotificationCenter.default.post(name: sessionsChanged, object: nil)
    }

    static func login(_ password: String) -> LoginResult {
        lock.lock(); defer { lock.unlock() }
        guard let salt = defaults.data(forKey: "pwSalt"), let hash = defaults.data(forKey: "pwHash") else { return .noPassword }
        let wait = lockedUntil.timeIntervalSinceNow
        if wait > 0 { return .locked(seconds: Int(wait.rounded(.up))) }

        guard constantTimeEqual(derive(password, salt: salt), hash) else {
            failures += 1
            // After 5 wrong tries: wait 1 min, then 2, 4, … up to 1 hour.
            if failures >= 5 {
                let seconds = min(3600, 60 * pow(2, Double(failures - 5)))
                lockedUntil = Date().addingTimeInterval(seconds)
                return .locked(seconds: Int(seconds))
            }
            return .wrong
        }
        failures = 0
        let token = randomBytes(32).map { String(format: "%02x", $0) }.joined()
        var sessions = storedSessions()
        sessions[sha256(token)] = Date().addingTimeInterval(sessionDays * 86400).timeIntervalSince1970
        defaults.set(sessions, forKey: "sessions")
        return .ok(token: token)
    }

    static func isValid(_ token: String?) -> Bool {
        guard let token, !token.isEmpty else { return false }
        lock.lock(); defer { lock.unlock() }
        guard let expiry = storedSessions()[sha256(token)] else { return false }
        return expiry > Date().timeIntervalSince1970
    }

    static func logout(_ token: String?) {
        guard let token else { return }
        lock.lock(); defer { lock.unlock() }
        var sessions = storedSessions()
        sessions[sha256(token)] = nil
        defaults.set(sessions, forKey: "sessions")
        NotificationCenter.default.post(name: sessionsChanged, object: nil)
    }

    // Unexpired sessions only.
    private static func storedSessions() -> [String: Double] {
        let now = Date().timeIntervalSince1970
        let all = defaults.dictionary(forKey: "sessions") as? [String: Double] ?? [:]
        return all.filter { $0.value > now }
    }

    private static func derive(_ password: String, salt: Data) -> Data {
        var out = Data(count: 32)
        out.withUnsafeMutableBytes { outPtr in
            salt.withUnsafeBytes { saltPtr in
                _ = CCKeyDerivationPBKDF(
                    CCPBKDFAlgorithm(kCCPBKDF2), password, password.utf8.count,
                    saltPtr.bindMemory(to: UInt8.self).baseAddress, salt.count,
                    CCPseudoRandomAlgorithm(kCCPRFHmacAlgSHA256), 200_000,
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
