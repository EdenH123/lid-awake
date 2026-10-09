import AppKit
import CryptoKit

// Apps the owner picked on the Mac ("Apps on phone" menu) to open with one tap from the
// phone. The phone only ever sends an id from this list, never a path.
enum PhoneApps {
    private static let key = "phoneApps"
    private static var iconCache: [String: Data] = [:]  // by path and size

    // Saved app paths, in the order they were added. Main thread only.
    static var paths: [String] {
        get { UserDefaults.standard.stringArray(forKey: key) ?? [] }
        set { UserDefaults.standard.set(newValue, forKey: key) }
    }

    static func add(_ urls: [URL]) {
        var list = paths
        for url in urls where url.pathExtension == "app" && !list.contains(url.path) { list.append(url.path) }
        paths = list
    }

    static func remove(_ path: String) {
        paths.removeAll { $0 == path }
        iconCache = iconCache.filter { !$0.key.hasPrefix(path + "#") }
    }

    static func name(_ path: String) -> String {
        let shown = FileManager.default.displayName(atPath: path)
        return shown.hasSuffix(".app") ? String(shown.dropLast(4)) : shown
    }

    // A short id that stays the same for the same app, so a tap can't open the wrong one.
    static func id(_ path: String) -> String {
        SHA256.hash(data: Data(path.utf8)).prefix(6).map { String(format: "%02x", $0) }.joined()
    }

    // For the status JSON. Apps that were deleted since being added are left out.
    static func list() -> [[String: Any]] {
        let running = Set(NSWorkspace.shared.runningApplications.compactMap { $0.bundleURL?.path })
        return paths.filter { FileManager.default.fileExists(atPath: $0) }.map {
            ["id": id($0), "name": name($0), "running": running.contains($0)]
        }
    }

    static func path(for id: String) -> String? { paths.first { self.id($0) == id } }

    // The phone's dock: apps open on the Mac that aren't on the owner's list (ordinary windowed
    // apps only, not Lid Awake itself), so they can be brought to the front with one tap.
    static func others() -> [[String: Any]] {
        let mine = Set(paths)
        var seen = Set<String>()
        return NSWorkspace.shared.runningApplications.compactMap { app in
            guard app.activationPolicy == .regular, let url = app.bundleURL, url.pathExtension == "app",
                  app.processIdentifier != getpid(), !mine.contains(url.path), seen.insert(url.path).inserted
            else { return nil }
            return ["id": id(url.path), "name": app.localizedName ?? name(url.path)]
        }
    }

    // A running app by its id (for the dock: only apps that are already open).
    static func runningPath(for id: String) -> String? {
        NSWorkspace.shared.runningApplications.lazy.compactMap { $0.bundleURL?.path }.first { self.id($0) == id }
    }

    // Hides an open app (⌘H). False if it isn't open.
    static func hide(_ path: String) -> Bool {
        let apps = NSWorkspace.shared.runningApplications.filter { $0.bundleURL?.path == path }
        apps.forEach { $0.hide() }
        return !apps.isEmpty
    }

    // Brings an open app to the front, as clicking it in the Dock does. False if it isn't open.
    static func activate(_ path: String) -> Bool {
        let apps = NSWorkspace.shared.runningApplications.filter { $0.bundleURL?.path == path }
        apps.forEach { $0.unhide(); $0.activate(options: [.activateAllWindows]) }
        return !apps.isEmpty
    }

    // New order from the phone. Unknown ids are ignored; apps not mentioned keep their place at the end.
    static func reorder(_ ids: [String]) {
        var rest = paths
        var ordered: [String] = []
        for id in ids {
            if let i = rest.firstIndex(where: { self.id($0) == id }) { ordered.append(rest.remove(at: i)) }
        }
        paths = ordered + rest
    }

    // The app's icon drawn at `size` px (the processor graph also reads its colours).
    static func iconBitmap(_ path: String, size: Int) -> NSBitmapImageRep? {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: size, pixelsHigh: size, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSWorkspace.shared.icon(forFile: path).draw(in: NSRect(x: 0, y: 0, width: size, height: size))
        NSGraphicsContext.restoreGraphicsState()
        return rep
    }

    // The app's icon as a PNG: 256 px for the grid, 512 px for the big zoom when opening.
    static func iconPNG(_ path: String, size: Int = 256) -> Data? {
        let size = size >= 512 ? 512 : 256
        if let cached = iconCache[path + "#\(size)"] { return cached }
        let png = iconBitmap(path, size: size)?.representation(using: .png, properties: [:])
        iconCache[path + "#\(size)"] = png
        return png
    }

    // Asks the app to quit, the same as ⌘Q: it can still ask to save unsaved work.
    // Returns false if it wasn't running.
    static func quit(_ path: String) -> Bool {
        let running = NSWorkspace.shared.runningApplications.filter { $0.bundleURL?.path == path }
        running.forEach { $0.terminate() }
        return !running.isEmpty
    }

    static func open(_ path: String) {
        let config = NSWorkspace.OpenConfiguration()
        config.activates = true
        NSWorkspace.shared.openApplication(at: URL(fileURLWithPath: path), configuration: config)
    }
}
