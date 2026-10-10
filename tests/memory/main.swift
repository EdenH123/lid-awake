import Foundation

// MemoryInfo.swift against this Mac's real numbers (compiled together by memory_check.sh).
var failed = false
func check(_ name: String, _ ok: Bool, _ detail: String = "") {
    print((ok ? "PASS " : "FAIL ") + name + (detail.isEmpty ? "" : " (\(detail))"))
    if !ok { failed = true }
}

check("helper counts toward its app", MemoryInfo.appBundle(of: "/Applications/Google Chrome.app/Contents/Frameworks/G.framework/Helpers/Google Chrome Helper (Renderer).app/Contents/MacOS/Google Chrome Helper (Renderer)") == "/Applications/Google Chrome.app")
check("plain program is not an app", MemoryInfo.appBundle(of: "/usr/libexec/trustd") == nil)
check("folder named .apps is not an app", MemoryInfo.appBundle(of: "/Users/a/my.apps/tool") == nil)

let s = MemoryInfo.snapshot { "id:" + $0 }
var mem: UInt64 = 0, size = MemoryLayout<UInt64>.size
sysctlbyname("hw.memsize", &mem, &size, nil, 0)
let total = s["total"] as? UInt64 ?? 0, used = s["used"] as? UInt64 ?? 0
check("total is hw.memsize", total == mem, "\(total)")
check("used is app + wired + compressed", used > 0 && used == (s["app"] as? UInt64 ?? 0) + (s["wired"] as? UInt64 ?? 0) + (s["compressed"] as? UInt64 ?? 0))
check("used ≤ total", used <= total)
check("free + files ≤ total", (s["free"] as? UInt64 ?? 0) + (s["cached"] as? UInt64 ?? 0) <= total)
let pressure = s["pressure"] as? Int ?? -1
check("pressure 0–100", (0...100).contains(pressure), "\(pressure)")
check("level is a known word", ["normal", "warn", "critical"].contains(s["level"] as? String ?? ""))
let top = s["top"] as? [[String: Any]] ?? []
let bytes = top.compactMap { $0["bytes"] as? UInt64 }
check("top apps 1–5", (1...5).contains(top.count), "\(top.map { $0["name"] as? String ?? "?" })")
check("top apps largest first", bytes == bytes.sorted(by: >) && bytes.allSatisfy { $0 > 0 })
check("top apps have names", top.allSatisfy { !($0["name"] as? String ?? "").isEmpty })
// Apps get an id for their icon; plain programs (no .app) don't, so the icon route never sees them.
check("only apps have ids", top.allSatisfy { ($0["id"] as? String).map { $0.hasPrefix("id:/") && $0.hasSuffix(".app") } ?? true })
exit(failed ? 1 : 0)
