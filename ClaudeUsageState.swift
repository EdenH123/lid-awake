import Foundation

// Claude plan usage (SystemInfo.swift): what the last check found, and the words for it in the
// menu bar. Foundation only, so tests/claude_usage_check.sh compiles it on its own.

// Numbers, Claude Code signed out ("claude -p /usage" then prints only a cost summary), or no
// numbers for another reason (a timeout, output that can't be read). Saved as "state" beside
// rate_limits in claude-usage.json; no "state" is ok, so the status line's files (which write only
// rate_limits and updated) and every file before 2.0.1 read as ok.
enum ClaudeUsageState: String {
    case ok, signedOut, failed

    // The file's "state" value: anything but "signedOut" or "failed" is ok.
    init(saved: Any?) {
        self = (saved as? String).flatMap(ClaudeUsageState.init(rawValue:)) ?? .ok
    }
}

enum ClaudeUsageWords {
    // Numbers saved longer ago than this say when they were saved.
    static let staleAfter: TimeInterval = 30 * 60
    static let signedOutLine = "Claude Code is signed out: in Terminal type claude, then /login"
    static let signedOutTip = "Claude Code is signed out"

    // `claude auth status` prints JSON: {"loggedIn": false, "authMethod": "none", …}. Its loggedIn
    // (a true or false, not a number), or nil when there is no such object.
    static func parseSignedIn(_ text: String) -> Bool? {
        guard let start = text.firstIndex(of: "{"), let end = text.lastIndex(of: "}"), start < end,
              let object = try? JSONSerialization.jsonObject(with: Data(text[start...end].utf8)) as? [String: Any],
              let value = object["loggedIn"] as? NSNumber, CFGetTypeID(value) == CFBooleanGetTypeID() else { return nil }
        return value.boolValue
    }

    // The saved file (its numbers as /status sends them, and its state) with what the last check
    // found at `at` (Unix seconds), unless the file's numbers were saved after it (the status line:
    // Claude Code answered, so it is signed in). With no file, only "signed out" is worth saying.
    static func merge(file: (usage: [String: Any], state: ClaudeUsageState)?,
                      last: (state: ClaudeUsageState, at: Int)?) -> (usage: [String: Any]?, state: ClaudeUsageState) {
        guard let last else { return (file?.usage, file?.state ?? .ok) }
        guard let file else { return last.state == .signedOut ? (["updated": 0], .signedOut) : (nil, last.state) }
        if (file.usage["updated"] as? Int ?? 0) > last.at { return file }
        return (file.usage, last.state)
    }

    // Numbers that may be old: the last check failed, or they were saved over 30 minutes ago (a
    // success since would have saved them again). Never when the time they were saved is unknown.
    static func isStale(updated: Int, state: ClaudeUsageState, now: Date) -> Bool {
        updated > 0 && (state == .failed || now.timeIntervalSince1970 - Double(updated) > staleAfter)
    }

    // For " · updated 3 h ago": "just now", "12 min ago" and "3 h ago" (today), "yesterday 22:52",
    // "Fri 22:52" (this past week), else the date. The time in the Mac's own format.
    static func updatedAgo(_ updated: Int, now: Date, timeZone: TimeZone = .current, locale: Locale = .current) -> String {
        let at = Date(timeIntervalSince1970: Double(updated))
        let minutes = Int((now.timeIntervalSince(at) / 60).rounded(.down))
        if minutes < 1 { return "updated just now" }
        if minutes < 60 { return "updated \(minutes) min ago" }
        var calendar = Calendar(identifier: .gregorian)
        calendar.timeZone = timeZone
        let days = calendar.dateComponents([.day], from: calendar.startOfDay(for: at), to: calendar.startOfDay(for: now)).day ?? 0
        if days <= 0 { return "updated \(minutes / 60) h ago" }
        func format(_ configure: (DateFormatter) -> Void) -> String {
            let f = DateFormatter()
            f.locale = locale
            f.timeZone = timeZone
            configure(f)
            return f.string(from: at)
        }
        let time = format { $0.dateStyle = .none; $0.timeStyle = .short }
        if days == 1 { return "updated yesterday " + time }
        if days < 7 { return "updated " + format { $0.setLocalizedDateFormatFromTemplate("EEE") } + " " + time }
        return "updated " + format { $0.setLocalizedDateFormatFromTemplate("MMMd") }
    }
}
