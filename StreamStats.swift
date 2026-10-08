import Foundation

// Diagnostics for the live picture: where the time goes between the Mac's screen and the
// phone. While frames flow, one summary line is appended every 2 s to
// ~/Library/Logs/LidAwake-stream.log (kept under 1 MB).
final class StreamStats {
    static let shared = StreamStats()

    private let lock = NSLock()
    private var preset = ""
    private var captured = 0, lagMs = 0.0, encodeMs = 0.0, diffMs = 0.0
    private var delivered = 0, deliveredBytes = 0, maxBytes = 0
    private var sent = 0, sentBytes = 0, acks = 0, ackMs = 0.0, ackMaxMs = 0.0, held = 0
    private var polls = 0, pollBytes = 0
    private var timer: DispatchSourceTimer?
    private let log = URL(fileURLWithPath: NSHomeDirectory() + "/Library/Logs/LidAwake-stream.log")

    private init() {
        let t = DispatchSource.makeTimerSource(queue: .global(qos: .utility))
        t.schedule(deadline: .now() + 2, repeating: 2)
        t.setEventHandler { [weak self] in self?.flush() }
        t.resume()
        timer = t
    }

    // A frame from the screen: how old it already was, and how long compressing took.
    func captured(preset: String, lag: Double, encode: Double, diff: Double) {
        lock.lock(); defer { lock.unlock() }
        self.preset = preset
        captured += 1; lagMs += lag * 1000; encodeMs += encode * 1000; diffMs += diff * 1000
    }
    func delivered(bytes: Int) {
        lock.lock(); defer { lock.unlock() }
        delivered += 1; deliveredBytes += bytes; maxBytes = max(maxBytes, bytes)
    }
    // Live socket: a frame handed to the network, and the phone confirming one (send → ack).
    func sent(bytes: Int) { lock.lock(); sent += 1; sentBytes += bytes; lock.unlock() }
    func acked(after seconds: Double) {
        lock.lock(); defer { lock.unlock() }
        acks += 1; ackMs += seconds * 1000; ackMaxMs = max(ackMaxMs, seconds * 1000)
    }
    // A newer frame was ready but two were still unconfirmed, so it waited.
    func heldBack() { lock.lock(); held += 1; lock.unlock() }
    // Fallback (no live socket): one long-poll answer.
    func polled(bytes: Int) { lock.lock(); polls += 1; pollBytes += bytes; lock.unlock() }

    // A one-off event (e.g. why a phone can't play video), written right away.
    func note(_ text: String) {
        let time = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
        write("\(time) \(text)\n")
    }

    private func flush() {
        lock.lock()
        guard captured + sent + polls > 0 else { lock.unlock(); return }
        func avg(_ total: Double, _ n: Int) -> Int { n > 0 ? Int(total / Double(n)) : 0 }
        let time = DateFormatter.localizedString(from: Date(), dateStyle: .none, timeStyle: .medium)
        let line = "\(time) \(preset): captured \(captured) (\(captured / 2)/s, already \(avg(lagMs, captured)) ms old, "
            + "encode \(avg(encodeMs, captured)) ms, diff \(avg(diffMs, captured)) ms) · "
            + "delivered \(delivered) avg \(delivered > 0 ? deliveredBytes / delivered / 1000 : 0) KB max \(maxBytes / 1000) KB · "
            + "socket sent \(sent) (\(sentBytes / 2000) KB/s) acks \(acks) avg \(avg(ackMs, acks)) ms max \(Int(ackMaxMs)) ms held \(held) · "
            + "polls \(polls) (\(pollBytes / 2000) KB/s)\n"
        captured = 0; lagMs = 0; encodeMs = 0; diffMs = 0
        delivered = 0; deliveredBytes = 0; maxBytes = 0
        sent = 0; sentBytes = 0; acks = 0; ackMs = 0; ackMaxMs = 0; held = 0
        polls = 0; pollBytes = 0
        lock.unlock()

        write(line)
    }

    private let fileLock = NSLock()
    private func write(_ line: String) {
        fileLock.lock(); defer { fileLock.unlock() }
        if let size = try? log.resourceValues(forKeys: [.fileSizeKey]).fileSize, size > 1_000_000 {
            try? FileManager.default.removeItem(at: log)
        }
        if let handle = try? FileHandle(forWritingTo: log) {
            handle.seekToEndOfFile(); handle.write(Data(line.utf8)); try? handle.close()
        } else {
            try? Data(line.utf8).write(to: log)
        }
    }
}
