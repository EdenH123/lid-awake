import AppKit
import SQLite3

// Battery temperature, charge and processor use over the last hours, for the graphs on the phone's
// Status page. Nothing is recorded by Lid Awake: macOS already writes the battery's state about once
// a minute, and each app's processor use, into its power log (the same log behind Battery in System
// Settings), readable by anyone.
// The format isn't documented, so if Apple changes it the graphs just show no history.
enum PowerHistory {
    private static let logPath = "/private/var/db/powerlog/Library/BatteryLife/CurrentPowerlog.PLSQL"

    // Temperature: [[unix seconds, °C], …] oldest first, for the last `hours` (at most 48).
    static func temperature(hours: Double) -> Data {
        rows("Temperature", hours: hours) { t, c in
            // Sanity check: a battery reading in °C (some macOS versions stored hundredths).
            let celsius = c[0] > 200 ? c[0] / 100 : c[0]
            return celsius > 0 && celsius < 100 ? [t, (celsius * 100).rounded() / 100] : nil
        }
    }

    // Charge: [[unix seconds, percent, plugged in 0/1, charging 0/1], …] oldest first.
    static func battery(hours: Double) -> Data {
        rows("Level, ExternalConnected, IsCharging", hours: hours) { t, c in
            c[0] >= 0 && c[0] <= 100 ? [t, c[0].rounded(), c[1] != 0 ? 1 : 0, c[2] != 0 ? 1 : 0] : nil
        }
    }

    private static func rows(_ columns: String, hours: Double, _ point: (Double, [Double]) -> [Double]?) -> Data {
        let count = columns.split(separator: ",").count
        var points: [[Double]] = []
        query("SELECT timestamp, \(columns) FROM PLBatteryAgent_EventBackward_Battery WHERE timestamp > ? ORDER BY timestamp", hours: hours) { stmt in
            if (1...count).contains(where: { sqlite3_column_type(stmt, Int32($0)) == SQLITE_NULL }) { return }
            let values = (1...count).map { sqlite3_column_double(stmt, Int32($0)) }
            if let p = point(sqlite3_column_double(stmt, 0).rounded(), values) { points.append(p) }
        }
        let body: [String: Any] = ["points": points, "now": Date().timeIntervalSince1970.rounded()]
        return (try? JSONSerialization.data(withJSONObject: body)) ?? Data("{}".utf8)
    }

    // One read-only query on the power log, for rows newer than `hours` ago (1…48); `?` = that time.
    private static func query(_ sql: String, hours: Double, _ row: (OpaquePointer) -> Void) {
        let since = Date().timeIntervalSince1970 - min(max(hours, 1), 48) * 3600
        var db: OpaquePointer?
        if sqlite3_open_v2("file:\(logPath)?mode=ro", &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil) == SQLITE_OK {
            sqlite3_busy_timeout(db, 500)
            var stmt: OpaquePointer?
            if sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK, let stmt {
                sqlite3_bind_double(stmt, 1, since)
                while sqlite3_step(stmt) == SQLITE_ROW { row(stmt) }
            }
            sqlite3_finalize(stmt)
        }
        sqlite3_close(db)
    }

    // MARK: Processor

    // Processor use per app. Every 10 s to a few minutes macOS logs how many processor-seconds each
    // app or background job used. Answer: {now, cores, apps: [{key, name, kind: app|macos|other, color?}],
    // intervals: [[start, end, {"app index": seconds}, other seconds]]} oldest first. Per stretch, apps
    // under 0.5% of its total and under 0.2 s are summed into "other" to keep the answer small.
    static func cpu(hours: Double) -> Data {
        var apps: [[String: Any]] = [], index: [String: Int] = [:], seen: Set<String> = []
        var intervals: [Any] = []
        var stretch: (start: Double, end: Double, secs: [Int: Double])?
        func close() {
            guard let s = stretch, s.end > s.start else { return }
            let total = s.secs.values.reduce(0, +)
            var listed: [String: Double] = [:], other = total
            for (i, secs) in s.secs where secs >= 0.2 || secs >= total * 0.005 {
                listed[String(i)] = round2(secs)
                other -= secs
            }
            intervals.append([s.start, s.end, listed, round2(max(0, other))])
        }
        query("SELECT timestamp, timestampEnd, BundleId, LaunchdName, cpu_time FROM PLCoalitionAgent_EventInterval_CoalitionInterval WHERE timestamp > ? ORDER BY timestamp", hours: hours) { stmt in
            let start = sqlite3_column_double(stmt, 0).rounded(), end = sqlite3_column_double(stmt, 1).rounded()
            if stretch?.start != start { close(); stretch = (start, end, [:]) }
            if let longest = stretch?.end, end > longest { stretch?.end = end }
            let secs = sqlite3_column_double(stmt, 4)
            guard secs > 0 else { return }
            let job = who(bundle: text(stmt, 2), launchd: text(stmt, 3))
            let i: Int
            if let known = index[job.key] { i = known } else {
                i = apps.count
                index[job.key] = i
                var app: [String: Any] = ["key": job.key, "name": job.name, "kind": job.kind]
                if let color = job.color { app["color"] = color }
                apps.append(app)
                if job.kind == "app" { seen.insert(job.key) }
            }
            stretch?.secs[i, default: 0] += secs
        }
        close()
        lock.lock(); iconKeys.formUnion(seen); lock.unlock()
        let body: [String: Any] = ["now": Date().timeIntervalSince1970.rounded(), "cores": ProcessInfo.processInfo.activeProcessorCount,
                                   "apps": apps, "intervals": intervals]
        return (try? JSONSerialization.data(withJSONObject: body)) ?? Data("{}".utf8)
    }

    // The icon of an app seen in the processor history, and only those. Call on the main thread.
    static func iconPNG(key: String) -> Data? {
        lock.lock(); let allowed = iconKeys.contains(key); lock.unlock()
        guard allowed, let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: key) else { return nil }
        return PhoneApps.iconPNG(url.path)
    }

    // Who a log row belongs to: an app (found by its bundle id), one of macOS's own jobs, or another helper.
    private struct Job { let key: String, name: String, kind: String, color: String? }
    private static let lock = NSLock()
    private static var jobs: [String: Job] = [:]      // by log id; under `lock`
    private static var iconKeys: Set<String> = []     // apps /cpu-icon may show; under `lock`

    private static func who(bundle: String, launchd: String) -> Job {
        let id = bundle.isEmpty ? launchd : bundle
        lock.lock(); let cached = jobs[id]; lock.unlock()
        if let cached { return cached }
        let job: Job = onMain {
            switch id {
            case "com.apple.WindowServer": return Job(key: "macos.screen", name: "Screen & windows", kind: "macos", color: nil)
            case "com.apple.kernel_task": return Job(key: "macos.core", name: "macOS core", kind: "macos", color: nil)
            default:
                // The app itself, or the app a helper belongs to (local.lidawake.selftest → Lid Awake).
                // Apple's own background agents live in .app bundles too; only real apps count as apps.
                var parts = id.split(separator: ".")
                while parts.count >= 2 {
                    let bundleID = parts.joined(separator: ".")
                    if let url = NSWorkspace.shared.urlForApplication(withBundleIdentifier: bundleID),
                       !bundleID.hasPrefix("com.apple.") || bundleID == "com.apple.finder"
                        || ["/Applications/", "/System/Applications/"].contains(where: url.path.hasPrefix) {
                        var name = FileManager.default.displayName(atPath: url.path)
                        if name.hasSuffix(".app") { name.removeLast(4) }
                        return Job(key: bundleID, name: name, kind: "app", color: iconColor(url.path))
                    }
                    parts.removeLast()
                }
                if id.hasPrefix("com.apple.") { return Job(key: "macos.other", name: "macOS (other)", kind: "macos", color: nil) }
                let short = id.split(separator: ".").last.map(String.init) ?? id
                return short.contains("$(") ? Job(key: "other.unnamed", name: "Unnamed helper", kind: "other", color: nil)
                                            : Job(key: "other." + short, name: short, kind: "other", color: nil)
            }
        }
        lock.lock(); jobs[id] = job; lock.unlock()
        return job
    }

    // The colour that stands out in an app's icon: its most common bright hue, averaged.
    // Grey or black-and-white icons give nil (the page then uses grey).
    private static func iconColor(_ path: String) -> String? {
        guard let rep = PhoneApps.iconBitmap(path, size: 32) else { return nil }
        var buckets = [(w: Double, r: Double, g: Double, b: Double)](repeating: (0, 0, 0, 0), count: 12)
        for x in 0..<32 { for y in 0..<32 {
            guard let c = rep.colorAt(x: x, y: y)?.usingColorSpace(.deviceRGB), c.alphaComponent > 0.8,
                  c.saturationComponent > 0.35, c.brightnessComponent > 0.3 else { continue }
            let i = min(11, Int(c.hueComponent * 12)), w = Double(c.saturationComponent)
            buckets[i].w += w
            buckets[i].r += Double(c.redComponent) * w
            buckets[i].g += Double(c.greenComponent) * w
            buckets[i].b += Double(c.blueComponent) * w
        }}
        guard let best = buckets.max(by: { $0.w < $1.w }), best.w > 8 else { return nil }
        return String(format: "#%02x%02x%02x", Int(best.r / best.w * 255), Int(best.g / best.w * 255), Int(best.b / best.w * 255))
    }

    private static func onMain<T>(_ work: () -> T) -> T { Thread.isMainThread ? work() : DispatchQueue.main.sync(execute: work) }
    private static func text(_ stmt: OpaquePointer, _ column: Int32) -> String {
        sqlite3_column_text(stmt, column).map { String(cString: $0) } ?? ""
    }
    private static func round2(_ v: Double) -> Double { (v * 100).rounded() / 100 }
}
