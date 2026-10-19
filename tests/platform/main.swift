import Foundation

// Platform.swift (compiled together by platform_check.sh).
var failed = false
func check(_ name: String, _ ok: Bool) {
    print((ok ? "PASS " : "FAIL ") + name)
    if !ok { failed = true }
}

let info = Platform.info
check("os is mac", info["os"] as? String == "mac")
let features = info["features"] as? [String] ?? []
for f in ["screen", "control", "camera", "apps", "history", "memory", "push", "passkeys", "limits"] {
    check("feature \(f)", features.contains(f))
}
check("info is valid JSON", JSONSerialization.isValidJSONObject(info))
exit(failed ? 1 : 0)
