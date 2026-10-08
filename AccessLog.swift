import Foundation

// Who did what from the phone, for the owner to look back on: sign-ins, Face ID changes,
// screen viewing and control, apps opened or quit. One line each in
// ~/Library/Logs/LidAwake-access.log (the previous 1 MB is kept as .old).
enum AccessLog {
    private static let lock = NSLock()
    static let url = URL(fileURLWithPath: NSHomeDirectory() + "/Library/Logs/LidAwake-access.log")

    static func write(_ event: String) {
        let clean = String(event.unicodeScalars.filter { !CharacterSet.controlCharacters.contains($0) }.prefix(300))
        let time = ISO8601DateFormatter.string(from: Date(), timeZone: .current, formatOptions: [.withInternetDateTime])
        let line = Data("\(time)  \(clean)\n".utf8)
        lock.lock(); defer { lock.unlock() }
        if let size = try? url.resourceValues(forKeys: [.fileSizeKey]).fileSize, size > 1_000_000 {
            let old = url.appendingPathExtension("old")
            try? FileManager.default.removeItem(at: old)
            try? FileManager.default.moveItem(at: url, to: old)
        }
        if let handle = try? FileHandle(forWritingTo: url) {
            handle.seekToEndOfFile(); handle.write(line); try? handle.close()
        } else {
            try? line.write(to: url)
        }
    }

    // The newest lines first, for the phone's Settings page.
    static func recent(_ count: Int) -> [String] {
        lock.lock(); defer { lock.unlock() }
        guard let text = try? String(contentsOf: url, encoding: .utf8) else { return [] }
        return Array(text.split(separator: "\n").suffix(count).reversed().map(String.init))
    }
}
