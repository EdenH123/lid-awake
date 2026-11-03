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
    // What the last check found, and when (Unix seconds); also in the file, when there is one.
    // Set on a background queue, read from the menu and the web server: under the lock.
    private let usageStateLock = NSLock()
    private var usageState: (state: ClaudeUsageState, at: Int)?

    // Asks Claude Code for the numbers ("/usage" sends no message and uses none of the limit).
    // Called on each status request; runs at most every 3 minutes, in the background, so the
    // numbers stay fresh while the phone is looking even when no terminal session is running.
    // No numbers: "claude auth status" says whether Claude Code is signed out (asked only then, so
    // never more often than "/usage"; signed out, the checks go on, so it recovers by itself).
    func refreshClaudeUsage(every interval: TimeInterval = 180) {
        guard !usageChecking, Date().timeIntervalSince(usageCheckedAt) > interval,
              let claude = ["/opt/homebrew/bin/claude", "/usr/local/bin/claude",
                            NSHomeDirectory() + "/.local/bin/claude", NSHomeDirectory() + "/.claude/local/claude"]
                .first(where: { FileManager.default.isExecutableFile(atPath: $0) && ownedSafely($0) }) else { return }
        usageChecking = true
        usageCheckedAt = Date()
        DispatchQueue.global(qos: .utility).async {
            // Skip the user's hooks and plugins (an empty folder has no project settings), and
            // don't save a session for this check.
            var template = Array((NSTemporaryDirectory() + "lidawake-usage.XXXXXX").utf8CString)
            let found = template.withUnsafeMutableBufferPointer { buffer -> (limits: [String: Any]?, signedIn: Bool?) in
                guard let dir = mkdtemp(buffer.baseAddress).map({ String(cString: $0) }) else { return (nil, nil) }
                defer { try? FileManager.default.removeItem(atPath: dir) }
                let text = runOnItsOwn(claude, ["-p", "/usage", "--setting-sources", "local", "--no-session-persistence"],
                                       in: dir, timeout: 45) ?? ""
                if let limits = Self.parseUsage(text) { return (limits, nil) }
                return (nil, ClaudeUsageWords.parseSignedIn(runOnItsOwn(claude, ["auth", "status"], in: dir, timeout: 15) ?? ""))
            }
            let now = Int(Date().timeIntervalSince1970)
            let state: ClaudeUsageState
            if let limits = found.limits {
                state = .ok
                if let data = try? JSONSerialization.data(withJSONObject: ["rate_limits": limits, "updated": now]) {
                    try? FileManager.default.createDirectory(at: Self.usageFile.deletingLastPathComponent(), withIntermediateDirectories: true)
                    try? data.write(to: Self.usageFile, options: .atomic)
                }
            } else {
                state = found.signedIn == false ? .signedOut : .failed
                Self.saveUsageState(state)
            }
            self.usageStateLock.lock()
            self.usageState = (state, now)
            self.usageStateLock.unlock()
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

    // A check that found no numbers: its state goes beside the numbers already saved, which stay
    // as they were (with their "updated"). Only into a file that is there, and only when it changes.
    private static func saveUsageState(_ state: ClaudeUsageState) {
        guard var saved = savedUsageFile(), saved["rate_limits"] is [String: Any],
              ClaudeUsageState(saved: saved["state"]) != state else { return }
        saved["state"] = state == .ok ? nil : state.rawValue
        if let data = try? JSONSerialization.data(withJSONObject: saved) { try? data.write(to: usageFile, options: .atomic) }
    }

    // A small, ordinary file only; the numbers are checked by the reader so odd values can't crash anything.
    private static func savedUsageFile() -> [String: Any]? {
        guard let size = try? usageFile.resourceValues(forKeys: [.fileSizeKey, .isRegularFileKey]),
              size.isRegularFile == true, (size.fileSize ?? 0) < 64 * 1024,
              let data = try? Data(contentsOf: usageFile) else { return nil }
        return try? JSONSerialization.jsonObject(with: data) as? [String: Any]
    }

    // For /status (docs/API.md 6.3): the numbers, and "signedOut": true while Claude Code is signed out.
    func claudeUsage() -> [String: Any]? {
        let (usage, state) = claudeUsageAndState()
        guard var out = usage else { return nil }
        if state == .signedOut { out["signedOut"] = true }
        return out
    }

    // The saved numbers with what the last check found (ClaudeUsageWords.merge).
    func claudeUsageAndState() -> (usage: [String: Any]?, state: ClaudeUsageState) {
        usageStateLock.lock()
        let last = usageState
        usageStateLock.unlock()
        return ClaudeUsageWords.merge(file: savedClaudeUsage(), last: last)
    }

    private func savedClaudeUsage() -> (usage: [String: Any], state: ClaudeUsageState)? {
        guard let saved = Self.savedUsageFile(), let limits = saved["rate_limits"] as? [String: Any] else { return nil }
        let updated = (saved["updated"] as? Double).flatMap { $0.isFinite && $0 > 0 && $0 < 1e11 ? Int($0) : nil } ?? 0
        var out: [String: Any] = ["updated": updated]
        for (key, name) in [("five_hour", "fiveHour"), ("seven_day", "sevenDay")] {
            if let w = limits[key] as? [String: Any], let pct = w["used_percentage"] as? Double,
               pct.isFinite, (0...1000).contains(pct) {
                let resets = (w["resets_at"] as? Double).flatMap { $0.isFinite && $0 > 0 && $0 < 1e11 ? Int($0) : nil } ?? 0
                out[name] = ["pct": pct, "resetsAt": resets]
            }
        }
        return (out, ClaudeUsageState(saved: saved["state"]))
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
        MemoryInfo.counts().map(MemoryInfo.used)
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

// A program file (after links) that belongs to this user or the system and that no other
// account can change.
func ownedSafely(_ path: String) -> Bool {
    guard let real = realpath(path, nil) else { return false }
    defer { free(real) }
    var info = stat()
    guard stat(real, &info) == 0 else { return false }
    return (info.st_uid == getuid() || info.st_uid == 0) && info.st_mode & (S_IWGRP | S_IWOTH) == 0
}

// Runs a helper program as its own program in macOS's eyes, not as part of Lid Awake, so it
// gets none of Lid Awake's Screen Recording or Accessibility permission (the same thing Terminal
// does for the commands it runs). It inherits no open files either. Returns its output, or nil
// if it couldn't be started that way.
func runOnItsOwn(_ path: String, _ args: [String], in dir: String, timeout: TimeInterval) -> String? {
    typealias Disclaim = @convention(c) (UnsafeMutablePointer<posix_spawnattr_t?>, Int32) -> Int32
    guard let symbol = dlsym(UnsafeMutableRawPointer(bitPattern: -2), "responsibility_spawnattrs_setdisclaim") else { return nil }
    var attr: posix_spawnattr_t?
    posix_spawnattr_init(&attr)
    defer { posix_spawnattr_destroy(&attr) }
    guard unsafeBitCast(symbol, to: Disclaim.self)(&attr, 1) == 0,
          posix_spawnattr_setflags(&attr, Int16(POSIX_SPAWN_CLOEXEC_DEFAULT)) == 0 else { return nil }

    var fds: [Int32] = [0, 0]
    guard pipe(&fds) == 0 else { return nil }
    var actions: posix_spawn_file_actions_t?
    posix_spawn_file_actions_init(&actions)
    defer { posix_spawn_file_actions_destroy(&actions) }
    posix_spawn_file_actions_addopen(&actions, 0, "/dev/null", O_RDONLY, 0)
    posix_spawn_file_actions_adddup2(&actions, fds[1], 1)
    posix_spawn_file_actions_addopen(&actions, 2, "/dev/null", O_WRONLY, 0)
    posix_spawn_file_actions_addchdir_np(&actions, dir)

    let argv = ([path] + args).map { strdup($0) } + [nil]
    defer { argv.forEach { free($0) } }
    var pid: pid_t = 0
    let started = posix_spawn(&pid, path, &actions, &attr, argv, environ)
    close(fds[1])
    guard started == 0 else { close(fds[0]); return nil }

    let killer = DispatchWorkItem { kill(pid, SIGTERM) }
    DispatchQueue.global().asyncAfter(deadline: .now() + timeout, execute: killer)
    let output = FileHandle(fileDescriptor: fds[0], closeOnDealloc: true).readDataToEndOfFile()
    var status: Int32 = 0
    waitpid(pid, &status, 0)
    killer.cancel()
    return String(data: output, encoding: .utf8)
}
