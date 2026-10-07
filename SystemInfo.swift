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
