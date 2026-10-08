import CryptoKit
import Foundation
// Phone side keys, then the Mac encrypts an alert for that phone.
let phone = P256.KeyAgreement.PrivateKey()
let auth = Data((0..<16).map { _ in UInt8.random(in: 0...255) })
let msg = Data(#"{"title":"Lid Awake","body":"Battery is at 9%. Turned off so your Mac can sleep."}"#.utf8)
let body = try! WebPush.encrypt(msg, phoneKey: phone.publicKey.x963Representation, authSecret: auth)
let out: [String: String] = ["phonePrivate": phone.rawRepresentation.base64EncodedString(),
                             "phonePublic": phone.publicKey.x963Representation.base64EncodedString(),
                             "auth": auth.base64EncodedString(), "body": body.base64EncodedString(),
                             "expected": String(data: msg, encoding: .utf8)!]
print(String(data: try! JSONSerialization.data(withJSONObject: out), encoding: .utf8)!)
