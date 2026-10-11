import CryptoKit
import Foundation
// Plays a phone's part with a software key: register, sign in, and the attacks that must fail.
UserDefaults.standard.removeObject(forKey: "passkeys")
let host = "your-mac.your-tailnet.ts.net"
let key = P256.Signing.PrivateKey()
let credID = Data((0..<16).map { _ in UInt8.random(in: 0...255) })
func clientData(_ type: String, _ challenge: String, origin: String = "https://\(host)") -> Data {
    try! JSONSerialization.data(withJSONObject: ["type": type, "challenge": challenge, "origin": origin])
}
func cborBytes(_ d: Data) -> Data { d.count < 24 ? Data([0x40 | UInt8(d.count)]) + d : Data([0x58, UInt8(d.count)]) + d }
func cborText(_ t: String) -> Data { Data([0x60 | UInt8(t.utf8.count)]) + Data(t.utf8) }
let raw = key.publicKey.rawRepresentation  // x || y
let cose = Data([0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21]) + cborBytes(raw.prefix(32)) + Data([0x22]) + cborBytes(raw.suffix(32))
func authData(flags: UInt8, count: UInt32, attested: Bool, rp: String = host) -> Data {
    var d = Data(SHA256.hash(data: Data(rp.utf8))) + Data([flags]) + withUnsafeBytes(of: count.bigEndian) { Data($0) }
    if attested { d += Data(repeating: 0, count: 16) + Data([0, UInt8(credID.count)]) + credID + cose }
    return d
}
func attestation(_ ad: Data) -> Data { Data([0xA3]) + cborText("fmt") + cborText("none") + cborText("attStmt") + Data([0xA0]) + cborText("authData") + cborBytes(ad) }
func check(_ name: String, _ ok: Bool) { print((ok ? "PASS " : "FAIL ") + name) }

let c1 = Passkeys.challenge("create")
check("register", Passkeys.register(clientData: clientData("webauthn.create", c1), attestation: attestation(authData(flags: 0x45, count: 0, attested: true)), host: host))
check("register reuse of challenge refused", !Passkeys.register(clientData: clientData("webauthn.create", c1), attestation: attestation(authData(flags: 0x45, count: 0, attested: true)), host: host))
let c1b = Passkeys.challenge("create")
check("register without Face ID (no UV) refused", !Passkeys.register(clientData: clientData("webauthn.create", c1b), attestation: attestation(authData(flags: 0x41, count: 0, attested: true)), host: host))
check("one passkey stored", Passkeys.count == 1)
let c1c = Passkeys.challenge("create")
check("same key id can't be registered again (no swapping keys)", !Passkeys.register(clientData: clientData("webauthn.create", c1c), attestation: attestation(authData(flags: 0x45, count: 0, attested: true)), host: host))
check("still one passkey", Passkeys.count == 1)

func signIn(challenge: String, origin: String = "https://\(host)", flags: UInt8 = 0x05, count: UInt32 = 0, signer: P256.Signing.PrivateKey = key, rp: String = host, type: String = "webauthn.get") -> Bool {
    let cd = clientData(type, challenge, origin: origin)
    let ad = authData(flags: flags, count: count, attested: false, rp: rp)
    let sig = try! signer.signature(for: ad + Data(SHA256.hash(data: cd))).derRepresentation
    return Passkeys.verify(credentialID: Passkeys.base64url(credID), clientData: cd, authenticatorData: ad, signature: sig, host: host)
}
let g1 = Passkeys.challenge("get")
check("sign in", signIn(challenge: g1))
check("same challenge twice refused", !signIn(challenge: g1))
check("made-up challenge refused", !signIn(challenge: "abc"))
check("other website origin refused", !signIn(challenge: Passkeys.challenge("get"), origin: "https://evil.example"))
check("other rpId refused", !signIn(challenge: Passkeys.challenge("get"), rp: "evil.example"))
check("wrong key refused", !signIn(challenge: Passkeys.challenge("get"), signer: P256.Signing.PrivateKey()))
check("no Face ID (UV) refused", !signIn(challenge: Passkeys.challenge("get"), flags: 0x01))
check("create-challenge used to sign in refused", !signIn(challenge: Passkeys.challenge("create")))
check("http host refused", !Passkeys.verify(credentialID: Passkeys.base64url(credID), clientData: Data(), authenticatorData: Data(), signature: Data(), host: "127.0.0.1"))
check("counter going up ok", signIn(challenge: Passkeys.challenge("get"), count: 5))
check("counter going back refused", !signIn(challenge: Passkeys.challenge("get"), count: 3))
Passkeys.removeAll()
check("removed", Passkeys.count == 0)
UserDefaults.standard.removeObject(forKey: "passkeys")
