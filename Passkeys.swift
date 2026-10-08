import CryptoKit
import Foundation

// Face ID / Touch ID sign-in for the phone page (WebAuthn passkeys). The phone keeps a private
// key that only unlocks with the owner's face; the Mac stores just the public key and checks
// the signature on each sign-in. Adding one needs the password again, a new password removes
// them all, and they only work on the Tailscale HTTPS address (passkeys need HTTPS).
enum Passkeys {
    private struct Stored: Codable {
        let id: String          // credential id, base64url
        let publicKey: Data     // P-256, x9.63 (04 || x || y)
        var signCount: UInt32
        var added: Date?        // (not kept by older versions)
        var device: String?
    }

    struct Info { let id: String; let added: Date?; let device: String }

    private static let defaults = UserDefaults.standard
    private static let key = "passkeys"
    private static let lock = NSLock()
    private static var challenges: [String: (purpose: String, expires: Date)] = [:]

    static var count: Int { lock.lock(); defer { lock.unlock() }; return stored().count }

    static func removeAll() {
        lock.lock(); defer { lock.unlock() }
        defaults.removeObject(forKey: key)
    }

    // A one-time challenge for "create" (adding Face ID) or "get" (signing in), valid 2 minutes.
    static func challenge(_ purpose: String) -> String {
        lock.lock(); defer { lock.unlock() }
        let now = Date()
        challenges = challenges.filter { $0.value.expires > now }
        // Only a handful are ever needed; drop the oldest so requests can't pile them up.
        while challenges.count >= 32, let oldest = challenges.min(by: { $0.value.expires < $1.value.expires }) {
            challenges[oldest.key] = nil
        }
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        let value = base64url(Data(bytes))
        challenges[value] = (purpose, now.addingTimeInterval(120))
        return value
    }

    static var credentialIDs: [String] { lock.lock(); defer { lock.unlock() }; return stored().map(\.id) }

    static var list: [Info] {
        lock.lock(); defer { lock.unlock() }
        return stored().map { Info(id: $0.id, added: $0.added, device: $0.device ?? "Phone") }
    }

    static func remove(id: String) {
        lock.lock(); defer { lock.unlock() }
        save(stored().filter { $0.id != id })
    }

    // Adding Face ID: checks what the phone signed up with and keeps its public key.
    static func register(clientData: Data, attestation: Data, host: String, device: String = "Phone") -> Bool {
        guard host.hasSuffix(".ts.net"),
              checkClientData(clientData, type: "webauthn.create", purpose: "create", host: host),
              case .map(let att)? = try? CBOR.decode(attestation),
              case .bytes(let authData)? = att[.text("authData")],
              let parsed = AuthData(authData),
              parsed.rpIDHash == Data(SHA256.hash(data: Data(host.utf8))),
              parsed.userVerified,
              let credentialID = parsed.credentialID,
              let publicKey = parsed.publicKey else { return false }
        lock.lock(); defer { lock.unlock() }
        var all = stored()
        // A key with the same id is never replaced (that would let someone swap in their own).
        guard !all.contains(where: { $0.id == base64url(credentialID) }), all.count < 20 else { return false }
        all.append(Stored(id: base64url(credentialID), publicKey: publicKey, signCount: parsed.signCount,
                          added: Date(), device: device))
        save(all)
        return true
    }

    // Signing in: the signature must come from a stored key, over this challenge, for this address.
    static func verify(credentialID: String, clientData: Data, authenticatorData: Data, signature: Data, host: String) -> Bool {
        guard host.hasSuffix(".ts.net"),
              checkClientData(clientData, type: "webauthn.get", purpose: "get", host: host),
              let parsed = AuthData(authenticatorData),
              parsed.rpIDHash == Data(SHA256.hash(data: Data(host.utf8))),
              parsed.userVerified else { return false }
        lock.lock(); defer { lock.unlock() }
        var all = stored()
        guard let index = all.firstIndex(where: { $0.id == credentialID }),
              let key = try? P256.Signing.PublicKey(x963Representation: all[index].publicKey),
              let ecdsa = try? P256.Signing.ECDSASignature(derRepresentation: signature) else { return false }
        let signed = authenticatorData + Data(SHA256.hash(data: clientData))
        guard key.isValidSignature(ecdsa, for: signed) else { return false }
        // Counters only ever go up (synced passkeys always report 0, which is fine).
        let old = all[index].signCount
        if old != 0 || parsed.signCount != 0 {
            guard parsed.signCount > old else { return false }
            all[index].signCount = parsed.signCount
            save(all)
        }
        return true
    }

    // The browser's own summary: right kind of request, our one-time challenge, our address.
    private static func checkClientData(_ data: Data, type: String, purpose: String, host: String) -> Bool {
        guard let json = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any],
              json["type"] as? String == type,
              json["origin"] as? String == "https://\(host)",
              let challenge = json["challenge"] as? String else { return false }
        lock.lock(); defer { lock.unlock() }
        guard let issued = challenges.removeValue(forKey: challenge),
              issued.purpose == purpose, issued.expires > Date() else { return false }
        return true
    }

    private static func stored() -> [Stored] {
        guard let data = defaults.data(forKey: key) else { return [] }
        return (try? JSONDecoder().decode([Stored].self, from: data)) ?? []
    }

    private static func save(_ all: [Stored]) {
        defaults.set(try? JSONEncoder().encode(all), forKey: key)
    }

    static func base64url(_ data: Data) -> String {
        data.base64EncodedString().replacingOccurrences(of: "+", with: "-")
            .replacingOccurrences(of: "/", with: "_").replacingOccurrences(of: "=", with: "")
    }

    static func fromBase64url(_ text: String) -> Data? {
        var s = text.replacingOccurrences(of: "-", with: "+").replacingOccurrences(of: "_", with: "/")
        while s.count % 4 != 0 { s += "=" }
        return Data(base64Encoded: s)
    }
}

// WebAuthn authenticator data: rpIdHash(32) flags(1) signCount(4) [aaguid(16) idLen(2) id key(CBOR)].
private struct AuthData {
    let rpIDHash: Data
    let userVerified: Bool
    let signCount: UInt32
    var credentialID: Data?
    var publicKey: Data?

    init?(_ data: Data) {
        let d = [UInt8](data)
        guard d.count >= 37 else { return nil }
        rpIDHash = Data(d[0..<32])
        let flags = d[32]
        // User present (0x01) and verified with Face ID / passcode (0x04).
        userVerified = flags & 0x01 != 0 && flags & 0x04 != 0
        signCount = d[33..<37].reduce(0) { $0 << 8 | UInt32($1) }
        guard flags & 0x40 != 0 else { return }  // no attested credential (sign-in)
        guard d.count >= 55 else { return nil }
        let idLength = Int(d[53]) << 8 | Int(d[54])
        guard d.count >= 55 + idLength else { return nil }
        credentialID = Data(d[55..<(55 + idLength)])
        // COSE key: {1: 2 (EC2), 3: -7 (ES256), -1: 1 (P-256), -2: x, -3: y}
        guard case .map(let cose)? = try? CBOR.decode(Data(d[(55 + idLength)...]), allowTrailing: true),
              cose[.int(1)] == .int(2), cose[.int(3)] == .int(-7), cose[.int(-1)] == .int(1),
              case .bytes(let x)? = cose[.int(-2)], case .bytes(let y)? = cose[.int(-3)],
              x.count == 32, y.count == 32 else { return nil }
        publicKey = Data([4]) + x + y
    }
}

// Just enough CBOR (RFC 8949) to read WebAuthn attestation objects and COSE keys.
enum CBOR: Hashable {
    case int(Int), bytes(Data), text(String), array([CBOR]), map([CBOR: CBOR]), simple(UInt8)

    struct Malformed: Error {}

    static func decode(_ data: Data, allowTrailing: Bool = false) throws -> CBOR {
        var reader = Reader(bytes: [UInt8](data))
        let value = try reader.item(depth: 0)
        if !allowTrailing && reader.offset != reader.bytes.count { throw Malformed() }
        return value
    }

    private struct Reader {
        let bytes: [UInt8]
        var offset = 0

        mutating func byte() throws -> UInt8 {
            guard offset < bytes.count else { throw Malformed() }
            defer { offset += 1 }
            return bytes[offset]
        }

        mutating func length(_ info: UInt8) throws -> UInt64 {
            switch info {
            case 0..<24: return UInt64(info)
            case 24: return UInt64(try byte())
            case 25, 26, 27:
                var value: UInt64 = 0
                for _ in 0..<(1 << (info - 24)) { value = value << 8 | UInt64(try byte()) }
                return value
            default: throw Malformed()
            }
        }

        mutating func take(_ count: UInt64) throws -> [UInt8] {
            guard count <= UInt64(bytes.count - offset) else { throw Malformed() }
            defer { offset += Int(count) }
            return Array(bytes[offset..<(offset + Int(count))])
        }

        mutating func item(depth: Int) throws -> CBOR {
            guard depth < 16 else { throw Malformed() }
            let first = try byte()
            let major = first >> 5, info = first & 0x1f
            switch major {
            case 0:
                let n = try length(info); guard n <= UInt64(Int.max) else { throw Malformed() }
                return .int(Int(n))
            case 1:
                let n = try length(info); guard n < UInt64(Int.max) else { throw Malformed() }
                return .int(-1 - Int(n))
            case 2: return .bytes(Data(try take(try length(info))))
            case 3:
                guard let s = String(bytes: try take(try length(info)), encoding: .utf8) else { throw Malformed() }
                return .text(s)
            case 4:
                let n = try length(info); guard n < 1024 else { throw Malformed() }
                return .array(try (0..<n).map { _ in try item(depth: depth + 1) })
            case 5:
                let n = try length(info); guard n < 1024 else { throw Malformed() }
                var map: [CBOR: CBOR] = [:]
                for _ in 0..<n { let k = try item(depth: depth + 1); map[k] = try item(depth: depth + 1) }
                return .map(map)
            case 7:
                guard info < 24 else { throw Malformed() }  // true/false/null/undefined only
                return .simple(info)
            default: throw Malformed()
            }
        }
    }
}
