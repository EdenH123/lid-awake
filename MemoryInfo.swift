import Darwin
import Foundation

// The phone's Memory window: where the Mac's memory goes right now, how hard macOS is working
// to find room (pressure), and the apps using the most. Read fresh on every call; nothing is kept.
enum MemoryInfo {
    struct Counts { var app, wired, compressed, cached, free: UInt64 }

    // Bytes per kind, from the kernel's page counts (the same ones Activity Monitor uses).
    static func counts() -> Counts? {
        var stats = vm_statistics64()
        var count = mach_msg_type_number_t(MemoryLayout<vm_statistics64>.size / MemoryLayout<integer_t>.size)
        let ok = withUnsafeMutablePointer(to: &stats) {
            $0.withMemoryRebound(to: integer_t.self, capacity: Int(count)) {
                host_statistics64(mach_host_self(), HOST_VM_INFO64, $0, &count)
            }
        }
        guard ok == KERN_SUCCESS else { return nil }
        let page = UInt64(vm_kernel_page_size)
        let anon = UInt64(stats.internal_page_count)
        return Counts(app: (anon - min(UInt64(stats.purgeable_count), anon)) * page,
                      wired: UInt64(stats.wire_count) * page,
                      compressed: UInt64(stats.compressor_page_count) * page,
                      cached: UInt64(stats.external_page_count) * page,
                      free: UInt64(stats.free_count) * page)
    }

    // Activity Monitor's "Memory Used": app + wired + compressed.
    static func used(_ c: Counts) -> UInt64 { c.app + c.wired + c.compressed }

    // Memory moved out to the disk because RAM ran short.
    static func swapUsed() -> UInt64 {
        var usage = xsw_usage(), size = MemoryLayout<xsw_usage>.size
        return sysctlbyname("vm.swapusage", &usage, &size, nil, 0) == 0 ? usage.xsu_used : 0
    }

    private static func sysctlInt(_ name: String) -> Int32? {
        var value: Int32 = 0, size = MemoryLayout<Int32>.size
        return sysctlbyname(name, &value, &size, nil, 0) == 0 ? value : nil
    }

    // The outermost .app a program lives in ("/Applications/Google Chrome.app" for every Chrome
    // helper), or nil for programs that aren't part of an app.
    static func appBundle(of path: String) -> String? {
        guard let r = path.range(of: ".app/") else { return nil }
        return String(path[..<r.lowerBound]) + ".app"
    }

    // The app's own name ("Claude Code" rather than its file name "claude").
    static func appName(_ path: String) -> String {
        let info = Bundle(path: path)?.infoDictionary
        let name = info?["CFBundleDisplayName"] as? String ?? info?["CFBundleName"] as? String
        return name.flatMap { $0.isEmpty ? nil : $0 } ?? URL(fileURLWithPath: path).deletingPathExtension().lastPathComponent
    }

    // The apps using the most memory, helpers included, by Activity Monitor's "Memory" figure.
    // Programs of other users (and the system's own) can't be read and are skipped.
    static func topApps(limit: Int = 5, id: (String) -> String?) -> [[String: Any]] {
        let n = proc_listallpids(nil, 0)
        guard n > 0 else { return [] }
        var pids = [pid_t](repeating: 0, count: Int(n) + 64)
        let got = pids.withUnsafeMutableBufferPointer {
            proc_listallpids($0.baseAddress, Int32($0.count * MemoryLayout<pid_t>.size))
        }
        var sizes: [String: UInt64] = [:]
        var buf = [CChar](repeating: 0, count: 4 * Int(MAXPATHLEN))
        for pid in pids.prefix(Int(max(got, 0))) where pid > 0 {
            guard proc_pidpath(pid, &buf, UInt32(buf.count)) > 0,
                  let app = appBundle(of: String(cString: buf)) else { continue }
            var info = rusage_info_v4()
            let ok = withUnsafeMutablePointer(to: &info) {
                $0.withMemoryRebound(to: rusage_info_t?.self, capacity: 1) { proc_pid_rusage(pid, RUSAGE_INFO_V4, $0) }
            }
            if ok == 0 { sizes[app, default: 0] += info.ri_phys_footprint }
        }
        return sizes.sorted { $0.value > $1.value }.prefix(limit).map { path, bytes in
            var item: [String: Any] = ["name": appName(path), "bytes": bytes]
            if let i = id(path) { item["id"] = i }
            return item
        }
    }

    // Everything the Memory window shows. `id` names an app for the phone's icon route.
    static func snapshot(id: (String) -> String? = { _ in nil }) -> [String: Any] {
        var out: [String: Any] = ["total": ProcessInfo.processInfo.physicalMemory, "swapUsed": swapUsed(),
                                  "top": topApps(id: id)]
        if let c = counts() {
            out["used"] = used(c); out["app"] = c.app; out["wired"] = c.wired
            out["compressed"] = c.compressed; out["cached"] = c.cached; out["free"] = c.free
        }
        if let freePercent = sysctlInt("kern.memorystatus_level") { out["pressure"] = max(0, min(100, 100 - Int(freePercent))) }
        let level = sysctlInt("kern.memorystatus_vm_pressure_level") ?? 1   // 1 normal, 2 warn, 4 critical
        out["level"] = level >= 4 ? "critical" : level >= 2 ? "warn" : "normal"
        return out
    }
}
