import CoreGraphics
import Foundation

// iPhone alerts for Claude Code sessions on this Mac: "Claude finished" when a session is done
// replying, and "Claude needs you" when it asks for permission or waits for an answer.
//
// Claude Code runs a tiny hook (claude-event.sh, installed in ~/.claude/hooks) that drops each
// event as a file into ~/Library/Application Support/LidAwake/claude-events; this picks them up,
// so nothing new listens on the network. Alerts only go out while the owner is away from the Mac
// (no keyboard or mouse for 2 minutes, or the lid closed), at most one every 20 s per session.
enum ClaudeAlerts {
    static let folder = URL(fileURLWithPath: NSHomeDirectory() + "/Library/Application Support/LidAwake/claude-events")
    private static let projects = NSHomeDirectory() + "/.claude/projects/"
    private static var lastAlert: [String: Date] = [:]     // per session
    private static var lastFinished: [String: Date] = [:]  // per session, for "waiting" after "finished"
    private static var timer: DispatchSourceTimer?
    private static let queue = DispatchQueue(label: "lidawake.claude-alerts")

    static func start() {
        try? FileManager.default.createDirectory(at: folder, withIntermediateDirectories: true,
                                                 attributes: [.posixPermissions: 0o700])
        let t = DispatchSource.makeTimerSource(queue: queue)
        t.schedule(deadline: .now() + 2, repeating: 2)
        t.setEventHandler { scan() }
        t.resume()
        timer = t
    }

    private static func scan() {
        let files = (try? FileManager.default.contentsOfDirectory(at: folder, includingPropertiesForKeys: [.fileSizeKey, .contentModificationDateKey]))?
            .filter { $0.pathExtension == "json" }.sorted { $0.lastPathComponent < $1.lastPathComponent } ?? []
        for file in files.prefix(50) {
            defer { try? FileManager.default.removeItem(at: file) }
            guard let info = try? file.resourceValues(forKeys: [.fileSizeKey, .contentModificationDateKey]),
                  (info.fileSize ?? 0) < 64 * 1024,
                  Date().timeIntervalSince(info.contentModificationDate ?? .distantPast) < 300,  // stale: skip
                  let data = try? Data(contentsOf: file),
                  let event = (try? JSONSerialization.jsonObject(with: data)) as? [String: Any] else { continue }
            handle(event)
        }
    }

    private static func handle(_ event: [String: Any]) {
        let session = (event["session_id"] as? String).map { String($0.prefix(64)) } ?? "?"
        let project = (event["cwd"] as? String).map { URL(fileURLWithPath: $0).lastPathComponent } ?? "Claude"
        let now = Date()
        let title: String, body: String
        switch event["hook_event_name"] as? String {
        case "Stop":
            guard event["stop_hook_active"] as? Bool != true else { return note(event, "skipped: hook re-run") }
            lastFinished[session] = now
            title = "Claude finished"
            body = lastReply(event["transcript_path"] as? String).map { "\(project): \($0)" } ?? "\(project) is done."
        case "Notification":
            let message = clean(event["message"] as? String ?? "", limit: 140)
            let type = event["notification_type"] as? String ?? ""
            let waiting = type == "idle_prompt" || message.lowercased().contains("waiting for your input")
            if waiting {
                // Already told them it finished; the 60-second "still waiting" nudge adds nothing.
                if let done = lastFinished[session], now.timeIntervalSince(done) < 600 { return note(event, "skipped: already said finished") }
                title = "Claude is waiting for you"
            } else if type == "permission_prompt" || message.lowercased().contains("permission") {
                title = "Claude needs your OK"
            } else if type.isEmpty || type == "elicitation_dialog" {
                title = "Claude needs you"
            } else {
                return note(event, "skipped: not a call for you")  // e.g. "signed in"
            }
            body = message.isEmpty ? project : "\(project): \(message)"
        default:
            return note(event, "skipped: other event")
        }
        guard ownerAway() else { return note(event, "skipped: you were at the Mac") }
        if let last = lastAlert[session], now.timeIntervalSince(last) < 20 { return note(event, "skipped: too soon after the last one") }
        lastAlert[session] = now
        note(event, "sending \"\(title)\" to \(WebPush.count) phone(s)")
        WebPush.send(.claudeSession, title: title, body: body)
    }

    // One line per event in the stream log (no message text), to see why an alert did or didn't go.
    private static func note(_ event: [String: Any], _ what: String) {
        let kind = [event["hook_event_name"] as? String, event["notification_type"] as? String].compactMap { $0 }.joined(separator: "/")
        let idle = Int(CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: CGEventType(rawValue: ~0)!))
        StreamStats.shared.note("claude \(kind) → \(what) (idle \(idle) s, lid \(FrameStream.lidClosed() ? "closed" : "open"))")
    }

    // Away: no keyboard, mouse or trackpad on this Mac for 2 minutes, or the lid is closed.
    private static func ownerAway() -> Bool {
        let anyInput = CGEventType(rawValue: ~0)!
        let idle = CGEventSource.secondsSinceLastEventType(.combinedSessionState, eventType: anyInput)
        return idle > 120 || FrameStream.lidClosed()
    }

    // The start of Claude's last reply, from the session's own transcript (only files under
    // ~/.claude/projects, and only the end of the file).
    private static func lastReply(_ path: String?) -> String? {
        guard let path, let real = realpath(path, nil) else { return nil }
        let resolved = String(cString: real)
        free(real)
        guard resolved.hasPrefix(projects), resolved.hasSuffix(".jsonl"),
              let handle = FileHandle(forReadingAtPath: resolved) else { return nil }
        defer { try? handle.close() }
        let size = (try? handle.seekToEnd()) ?? 0
        try? handle.seek(toOffset: size > 262_144 ? size - 262_144 : 0)
        guard let data = try? handle.readToEnd(), let text = String(data: data, encoding: .utf8) else { return nil }
        for line in text.split(separator: "\n").reversed() {
            guard let entry = (try? JSONSerialization.jsonObject(with: Data(line.utf8))) as? [String: Any],
                  entry["type"] as? String == "assistant",
                  let content = (entry["message"] as? [String: Any])?["content"] as? [[String: Any]] else { continue }
            let said = content.compactMap { $0["type"] as? String == "text" ? $0["text"] as? String : nil }.joined(separator: " ")
            let short = clean(said.replacingOccurrences(of: "**", with: "").replacingOccurrences(of: "`", with: ""), limit: 110)
            if !short.isEmpty { return short }
        }
        return nil
    }

    // One plain line, cut at a word near `limit`.
    private static func clean(_ text: String, limit: Int) -> String {
        let flat = text.components(separatedBy: .whitespacesAndNewlines).filter { !$0.isEmpty }.joined(separator: " ")
        guard flat.count > limit else { return flat }
        let cut = flat.prefix(limit)
        return (cut.lastIndex(of: " ").map { String(cut[..<$0]) } ?? String(cut)) + "…"
    }
}
