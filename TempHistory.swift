import Foundation
import SQLite3

// Battery temperature over the last hours, for the graph on the phone's Status page.
// Nothing is recorded by Lid Awake: macOS already writes the battery's temperature about once a
// minute into its power log (the same log behind Battery in System Settings), readable by anyone.
// The format isn't documented, so if Apple changes it the graph just shows no history.
enum TempHistory {
    private static let logPath = "/private/var/db/powerlog/Library/BatteryLife/CurrentPowerlog.PLSQL"

    // [[unix seconds, °C], …] oldest first, for the last `hours` (at most 48).
    static func json(hours: Double) -> Data {
        let hours = min(max(hours, 1), 48)
        let since = Date().timeIntervalSince1970 - hours * 3600
        var points: [[Double]] = []
        var db: OpaquePointer?
        if sqlite3_open_v2("file:\(logPath)?mode=ro", &db, SQLITE_OPEN_READONLY | SQLITE_OPEN_URI, nil) == SQLITE_OK {
            sqlite3_busy_timeout(db, 500)
            var stmt: OpaquePointer?
            let sql = "SELECT timestamp, Temperature FROM PLBatteryAgent_EventBackward_Battery WHERE timestamp > ? AND Temperature > 0 ORDER BY timestamp"
            if sqlite3_prepare_v2(db, sql, -1, &stmt, nil) == SQLITE_OK {
                sqlite3_bind_double(stmt, 1, since)
                while sqlite3_step(stmt) == SQLITE_ROW {
                    let t = sqlite3_column_double(stmt, 0), c = sqlite3_column_double(stmt, 1)
                    // Sanity check: a battery reading in °C (some macOS versions stored hundredths).
                    let celsius = c > 200 ? c / 100 : c
                    if celsius > 0 && celsius < 100 { points.append([t.rounded(), (celsius * 100).rounded() / 100]) }
                }
            }
            sqlite3_finalize(stmt)
        }
        sqlite3_close(db)
        let body: [String: Any] = ["points": points, "now": Date().timeIntervalSince1970.rounded()]
        return (try? JSONSerialization.data(withJSONObject: body)) ?? Data("{}".utf8)
    }
}
