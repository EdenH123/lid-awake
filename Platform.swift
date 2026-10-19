import Foundation

// Which computer this is, for the phone page: it adapts its drawing, key names and wording to
// it, and hides what this computer can't do. The Windows version answers the same fields with
// "windows" and its own list (docs/API.md).
enum Platform {
    static let os = "mac"
    static let features = ["screen", "control", "camera", "apps", "history", "memory", "push", "passkeys", "limits"]
    static var info: [String: Any] { ["os": os, "features": features] }
}
