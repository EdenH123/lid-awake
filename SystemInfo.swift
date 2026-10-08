import CoreLocation
import CoreWLAN
import Darwin
import Foundation
import SystemConfiguration

// Mac health and network numbers for the Status page. Rates (processor load, network
// speed) are measured between two calls, so each status request reports the time since
// the previous one.
final class SystemInfo: NSObject, CLLocationManagerDelegate {
    static let shared = SystemInfo()

    private var lastCPU: (busy: UInt64, total: UInt64)?
    private var lastNet: (rx: UInt64, tx: UInt64, at: Date)?
    private let location = CLLocationManager()

    // macOS only reveals the Wi-Fi name to apps with Location permission. Ask once.
    func requestWiFiNameAccess() {
        location.delegate = self
        if location.authorizationStatus == .notDetermined { location.requestWhenInUseAuthorization() }
    }

    func snapshot() -> [String: Any] {
        var mac: [String: Any] = [
            "memTotal": ProcessInfo.processInfo.physicalMemory,
            "uptime": uptimeSeconds(),
        ]
        if let cpu = cpuPercent() { mac["cpu"] = cpu }
        if let used = memoryUsed() { mac["memUsed"] = used }
        if let disk = diskSpace() { mac["diskFree"] = disk.free; mac["diskTotal"] = disk.total }

        var net: [String: Any] = [:]
        if let wifi = CWWiFiClient.shared().interface(), wifi.powerOn() {
            if let name = wifi.ssid() { net["wifi"] = name }
            let rssi = wifi.rssiValue()
            if rssi != 0 { net["rssi"] = rssi }
        }
        if let iface = primaryInterface() {
            net["iface"] = iface
            net["wired"] = !(iface == CWWiFiClient.shared().interface()?.interfaceName)
            if let rate = throughput(iface) { net["down"] = rate.down; net["up"] = rate.up }
        }
        return ["mac": mac, "net": net]
    }

    // MARK: Claude plan usage

    // Saved by the Claude Code status line script (~/.claude/statusline-lidawake.py) after each
    // terminal reply, and by refreshClaudeUsage() below: the 5-hour and weekly limits.
    private static let usageFile = FileManager.default.homeDirectoryForCurrentUser
        .appendingPathComponent("Library/Application Support/LidAwake/claude-usage.json")
    private var usageCheckedAt = Date.distantPast
    private var usageChecking = false

    // Asks Claude Code for the numbers ("/usage" sends no message and uses none of the limit).
    // Called on each status request; runs at most every 3 minutes, in the background, so the
    // numbers stay fresh while the phone is looking even when no terminal session is running.
    func refreshClaudeUsage() {
        guard !usageChecking, Date().timeIntervalSince(usageCheckedAt) > 180,
              let claude = ["/opt/homebrew/bin/claude", "/usr/local/bin/claude",
                            NSHomeDirectory() + "/.local/bin/claude", NSHomeDirectory() + "/.claude/local/claude"]
                .first(where: { FileManager.default.isExecutableFile(atPath: $0) }) else { return }
        usageChecking = true
        usageCheckedAt = Date()
        DispatchQueue.global(qos: .utility).async {
            let p = Process()
            p.executableURL = URL(fileURLWithPath: claude)
            // Skip the user's hooks and plugins, and don't save a session for this check.
            p.arguments = ["-p", "/usage", "--setting-sources", "local", "--no-session-persistence"]
            p.currentDirectoryURL = FileManager.default.homeDirectoryForCurrentUser
            let out = Pipe()
            p.standardOutput = out
            p.standardError = FileHandle.nullDevice
            var text = ""
            if (try? p.run()) != nil {
                let killer = DispatchWorkItem { if p.isRunning { p.terminate() } }
                DispatchQueue.global().asyncAfter(deadline: .now() + 45, execute: killer)
                text = String(data: out.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
                p.waitUntilExit()
                killer.cancel()
            }
            if let limits = Self.parseUsage(text),
               let data = try? JSONSerialization.data(withJSONObject: ["rate_limits": limits, "updated": Int(Date().timeIntervalSince1970)]) {
                try? FileManager.default.createDirectory(at: Self.usageFile.deletingLastPathComponent(), withIntermediateDirectories: true)
                try? data.write(to: Self.usageFile, options: .atomic)
            }
            DispatchQueue.main.async { self.usageChecking = false }
        }
    }

    // "Current session: 74% used · resets Oct 8 at 3:09pm (Asia/Jerusalem)" and
    // "Current week (all models): 84% used · resets Oct 10 at 10:59pm (…)" → status-line format.
    static func parseUsage(_ text: String) -> [String: Any]? {
        var limits: [String: Any] = [:]
        for (label, key) in [("Current session", "five_hour"), ("Current week (all models)", "seven_day")] {
            guard let line = text.components(separatedBy: "\n").first(where: { $0.hasPrefix(label + ":") }),
                  let pctRange = line.range(of: #"(\d+(\.\d+)?)% used"#, options: .regularExpression),
                  let pct = Double(line[pctRange].dropLast(6)) else { continue }
            var window: [String: Any] = ["used_percentage": pct]
            if let r = line.range(of: #"resets (.+?) \("#, options: .regularExpression),
               let date = resetDate(String(line[r].dropFirst(7).dropLast(2))) {
                window["resets_at"] = Int(date.timeIntervalSince1970)
            }
            limits[key] = window
        }
        return limits.isEmpty ? nil : limits
    }

    // "Oct 8 at 3:09pm" or "Oct 10 at 11pm" (this year, local time).
    private static func resetDate(_ s: String) -> Date? {
        let f = DateFormatter()
        f.locale = Locale(identifier: "en_US_POSIX")
        let year = Calendar.current.component(.year, from: Date())
        for format in ["MMM d 'at' h:mma yyyy", "MMM d 'at' ha yyyy"] {
            f.dateFormat = format
            if var d = f.date(from: s.uppercased().replacingOccurrences(of: " AT ", with: " at ") + " \(year)") {
                if d < Date().addingTimeInterval(-86400 * 30) { d = Calendar.current.date(byAdding: .year, value: 1, to: d) ?? d }
                return d
            }
        }
        return nil
    }

    func claudeUsage() -> [String: Any]? {
        let url = Self.usageFile
        guard let data = try? Data(contentsOf: url),
              let saved = try? JSONSerialization.jsonObject(with: data) as? [String: Any],
              let limits = saved["rate_limits"] as? [String: Any] else { return nil }
        var out: [String: Any] = ["updated": saved["updated"] ?? 0]
        for (key, name) in [("five_hour", "fiveHour"), ("seven_day", "sevenDay")] {
            if let w = limits[key] as? [String: Any], let pct = w["used_percentage"] as? Double {
                out[name] = ["pct": pct, "resetsAt": w["resets_at"] ?? 0]
            }
        }
        return out
    }

    // MARK: Processor

    private func cpuPercent() -> Double? {
        var info = host_cpu_load_info()
        var count = mach_msg_type_number_t(MemoryLayout<host_cpu_load_info>.size / MemoryLayout<integer_t>.size)
        let ok = withUnsafeMutablePointer(to: &info) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics(mach_host_self(), HOST_CPU_LOAD_INFO, $0, &count)
            }
        }
        guard ok == KERN_SUCCESS else { return nil }
        let t = info.cpu_ticks
        let user = UInt64(t.0), system = UInt64(t.1), idle = UInt64(t.2), nice = UInt64(t.3)
        let busy = user + system + nice, total = busy + idle
        defer { lastCPU = (busy, total) }
        guard let last = lastCPU, total > last.total else { return nil }
        return Double(busy - last.busy) / Double(total - last.total) * 100
    }

    // MARK: Memory (like Activity Monitor's "Memory Used": app + wired + compressed)

    private func memoryUsed() -> UInt64? {
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.size / MemoryLayout<integer_t>.size)
        let ok = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        guard ok == KERN_SUCCESS else { return nil }
        let page = UInt64(vm_kernel_page_size)
        let app = UInt64(stats.internal_page_count) - UInt64(stats.purgeable_count)
        return (app + UInt64(stats.wire_count) + UInt64(stats.compressor_page_count)) * page
    }

    // MARK: Disk and uptime

    private func diskSpace() -> (free: Int64, total: Int64)? {
        let values = try? URL(fileURLWithPath: "/").resourceValues(
            forKeys: [.volumeTotalCapacityKey, .volumeAvailableCapacityForImportantUsageKey])
        guard let total = values?.volumeTotalCapacity, let free = values?.volumeAvailableCapacityForImportantUsage
        else { return nil }
        return (free, Int64(total))
    }

    private func uptimeSeconds() -> Int {
        var boot = timeval()
        var size = MemoryLayout<timeval>.size
        guard sysctlbyname("kern.boottime", &boot, &size, nil, 0) == 0 else {
            return Int(ProcessInfo.processInfo.systemUptime)
        }
        return Int(Date().timeIntervalSince1970) - boot.tv_sec
    }

    // MARK: Network

    // The interface the Mac uses for the internet right now (e.g. en0).
    private func primaryInterface() -> String? {
        guard let store = SCDynamicStoreCreate(nil, "LidAwake" as CFString, nil, nil),
              let global = SCDynamicStoreCopyValue(store, "State:/Network/Global/IPv4" as CFString) as? [String: Any]
        else { return nil }
        return global["PrimaryInterface"] as? String
    }

    // Bytes per second received / sent on `iface` since the previous call.
    private func throughput(_ iface: String) -> (down: Double, up: Double)? {
        var rx: UInt64 = 0, tx: UInt64 = 0
        var addrs: UnsafeMutablePointer<ifaddrs>?
        guard getifaddrs(&addrs) == 0, let first = addrs else { return nil }
        defer { freeifaddrs(addrs) }
        var cursor: UnsafeMutablePointer<ifaddrs>? = first
        while let ifa = cursor {
            if String(cString: ifa.pointee.ifa_name) == iface,
               ifa.pointee.ifa_addr?.pointee.sa_family == UInt8(AF_LINK),
               let data = ifa.pointee.ifa_data?.assumingMemoryBound(to: if_data.self) {
                rx = UInt64(data.pointee.ifi_ibytes)
                tx = UInt64(data.pointee.ifi_obytes)
            }
            cursor = ifa.pointee.ifa_next
        }
        let now = Date()
        defer { lastNet = (rx, tx, now) }
        guard let last = lastNet else { return nil }
        let seconds = now.timeIntervalSince(last.at)
        // The counters are 32-bit and wrap around; skip a sample when that happens.
        guard seconds > 0.2, rx >= last.rx, tx >= last.tx else { return nil }
        return (Double(rx - last.rx) / seconds, Double(tx - last.tx) / seconds)
    }
}
