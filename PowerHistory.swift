import Foundation
import SQLite3

// Battery temperature and charge over the last hours, for the graphs on the phone's Status page.
// Nothing is recorded by Lid Awake: macOS already writes the battery's state about once a minute
// into its power log (the same log behind Battery in System Settings), readable by anyone.
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
        let since = Date().timeIntervalSince1970 - min(max(hours, 1), 48) * 3600
        let count = columns.split(separator: ",").count
        var points: [[Double]] = []
        var db: OpaquePointer?
        if sqlite3_open_v2("file:\(logPath)?mode=ro", &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil) == SQLITE_OK {
            sqlite3_busy_timeout(db, 500)
            var stmt: OpaquePointer?
            let sql = "SELECT timestamp, \(columns) FROM PLBatteryAgent_EventBackward_Battery WHERE timestamp > ? ORDER BY timestamp"
            if sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK {
                sqlite3_bind_double(stmt, 1, since)
                while sqlite3_step(stmt) == SQLITE_ROW {
                    if (1...count).contains(where: { sqlite3_column_type(stmt, Int32($0)) == SQLITE_NULL }) { continue }
                    let values = (1...count).map { sqlite3_column_double(stmt, Int32($0)) }
                    if let p = point(sqlite3_column_double(stmt, 0).rounded(), values) { points.append(p) }
                }
            }
            sqlite3_finalize(stmt)
        }
        sqlite3_close(db)
        let body: [String: Any] = ["points": points, "now": Date().timeIntervalSince1970.rounded()]
        return (try? JSONSerialization.data(withJSONObject: body)) ?? Data("{}".utf8)
    }
}
