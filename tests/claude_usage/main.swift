import Foundation

// ClaudeUsageState.swift (compiled together by claude_usage_check.sh).
var failed = false
func check(_ name: String, _ ok: Bool) {
    print((ok ? "PASS " : "FAIL ") + name)
    if !ok { failed = true }
}

// ---- claude auth status ----

// What a signed-out Claude Code prints (its exit code is 1; the output is what counts).
let signedOut = """
{
  "loggedIn": false,
  "authMethod": "none",
  "apiProvider": "firstParty",
  "analyticsDisabled": false
}
"""
check("auth status signed out", ClaudeUsageWords.parseSignedIn(signedOut) == false)
check("auth status signed in", ClaudeUsageWords.parseSignedIn(#"{"loggedIn": true, "authMethod": "claude.ai"}"#) == true)
check("auth status after a warning line", ClaudeUsageWords.parseSignedIn("warning: x\n{\"loggedIn\":false}\n") == false)
for (name, text) in [("empty", ""), ("plain text", "Not logged in"), ("a string", #"{"loggedIn": "no"}"#),
                     ("a number", #"{"loggedIn": 0}"#), ("missing", #"{"authMethod": "none"}"#),
                     ("cut short", #"{"loggedIn": fals"#), ("braces backwards", "} {")] {
    check("auth status \(name) is unknown", ClaudeUsageWords.parseSignedIn(text) == nil)
}

// ---- The saved state ----

check("no state is ok (old files, the status line's)", ClaudeUsageState(saved: nil) == .ok)
check("signedOut", ClaudeUsageState(saved: "signedOut") == .signedOut)
check("failed", ClaudeUsageState(saved: "failed") == .failed)
check("anything else is ok", ClaudeUsageState(saved: "later") == .ok && ClaudeUsageState(saved: 3) == .ok)

let file: (usage: [String: Any], state: ClaudeUsageState) = (["updated": 1000, "fiveHour": ["pct": 21.0, "resetsAt": 0]], .ok)
let noCheck = ClaudeUsageWords.merge(file: file, last: nil)
check("no check yet: the file", noCheck.state == .ok && noCheck.usage?["updated"] as? Int == 1000)
let out = ClaudeUsageWords.merge(file: file, last: (.signedOut, 2000))
check("signed out at the last check, numbers kept", out.state == .signedOut && out.usage?["fiveHour"] != nil)
check("numbers saved after the check: the file's state", ClaudeUsageWords.merge(file: file, last: (.signedOut, 999)).state == .ok)
let nothing = ClaudeUsageWords.merge(file: nil, last: (.signedOut, 2000))
check("signed out with no file is still said", nothing.state == .signedOut && nothing.usage?["updated"] as? Int == 0)
check("failed with no file is nothing", ClaudeUsageWords.merge(file: nil, last: (.failed, 2000)).usage == nil)
check("ok after signed out", ClaudeUsageWords.merge(file: (["updated": 3000], .signedOut), last: (.ok, 3000)).state == .ok)

// ---- Old numbers ----

// Tuesday 20 October 2026, 12:00 UTC.
let noon = Date(timeIntervalSince1970: 1_792_497_600)
let utc = TimeZone(identifier: "UTC")!
let gb = Locale(identifier: "en_GB")
func ago(_ seconds: TimeInterval) -> Int { Int(noon.timeIntervalSince1970 - seconds) }
func words(_ updated: Int) -> String { ClaudeUsageWords.updatedAgo(updated, now: noon, timeZone: utc, locale: gb) }
check("just now", words(ago(20)) == "updated just now")
check("a clock that moved", words(ago(-300)) == "updated just now")
check("12 min ago", words(ago(12 * 60)) == "updated 12 min ago")
check("3 h ago", words(ago(200 * 60)) == "updated 3 h ago")
check("yesterday 22:52 (\(words(ago(13 * 3600 + 8 * 60))))", words(ago(13 * 3600 + 8 * 60)) == "updated yesterday 22:52")
check("Fri 22:52 (\(words(ago(3 * 86400 + 13 * 3600 + 8 * 60))))", words(ago(3 * 86400 + 13 * 3600 + 8 * 60)) == "updated Fri 22:52")
check("a date (\(words(ago(6 * 86400 + 13 * 3600 + 8 * 60))))", words(ago(6 * 86400 + 13 * 3600 + 8 * 60)) == "updated 13 Oct")

check("30 minutes is current", !ClaudeUsageWords.isStale(updated: ago(30 * 60), state: .ok, now: noon))
check("31 minutes is old", ClaudeUsageWords.isStale(updated: ago(31 * 60), state: .ok, now: noon))
check("failed is old", ClaudeUsageWords.isStale(updated: ago(60), state: .failed, now: noon))
check("an unknown time is never old", !ClaudeUsageWords.isStale(updated: 0, state: .failed, now: noon))
check("the menu line", ClaudeUsageWords.signedOutLine == "Claude Code is signed out: in Terminal type claude, then /login")
exit(failed ? 1 : 0)
