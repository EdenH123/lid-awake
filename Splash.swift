import AppKit

// Launch screens for the home-screen app: iPhones show one while the page loads, but only if it
// matches the phone's exact pixel size, so each supported size is drawn here (the app icon and
// "Lid Awake" on black, the manifest's background) and kept after the first request.
enum Splash {
    // iPhone screens in points and their scale (portrait), newest first.
    static let sizes: [(w: Int, h: Int, scale: Int)] = [
        (440, 956, 3), (402, 874, 3), (420, 912, 3), (430, 932, 3), (393, 852, 3), (428, 926, 3),
        (390, 844, 3), (414, 896, 3), (375, 812, 3), (414, 896, 2), (375, 667, 2),
    ]

    // <link> tags for the page head, one per size.
    static let linkTags: String = sizes.map { s in
        "<link rel=\"apple-touch-startup-image\" media=\"(device-width: \(s.w)px) and (device-height: \(s.h)px) and "
            + "(-webkit-device-pixel-ratio: \(s.scale)) and (orientation: portrait)\" href=\"/splash/\(s.w * s.scale)x\(s.h * s.scale).png\">"
    }.joined(separator: "\n")

    private static var cache: [String: Data] = [:]
    private static let lock = NSLock()

    // "/splash/1206x2622.png" → the PNG, for listed sizes only.
    static func png(forPath path: String) -> Data? {
        let name = path.dropFirst("/splash/".count).replacingOccurrences(of: ".png", with: "")
        let parts = name.split(separator: "x").compactMap { Int($0) }
        guard parts.count == 2, sizes.contains(where: { $0.w * $0.scale == parts[0] && $0.h * $0.scale == parts[1] }) else { return nil }
        lock.lock(); defer { lock.unlock() }
        if let cached = cache[name] { return cached }
        let png = draw(width: parts[0], height: parts[1])
        cache[name] = png
        return png
    }

    private static func draw(width: Int, height: Int) -> Data? {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0),
              let icon = NSImage(data: appIcon512) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor.black.setFill()
        NSRect(x: 0, y: 0, width: width, height: height).fill()
        // The icon a little above the middle, about a quarter of the width; the name below it.
        let side = CGFloat(width) * 0.27
        let iconRect = NSRect(x: (CGFloat(width) - side) / 2, y: CGFloat(height) * 0.5, width: side, height: side)
        // Rounded like a home-screen icon (the icon file itself is square; iOS rounds it).
        NSGraphicsContext.saveGraphicsState()
        NSBezierPath(roundedRect: iconRect, xRadius: side * 0.2237, yRadius: side * 0.2237).addClip()
        icon.draw(in: iconRect)
        NSGraphicsContext.restoreGraphicsState()
        let font = NSFont.systemFont(ofSize: CGFloat(width) * 0.05, weight: .semibold)
        let text = NSAttributedString(string: "Lid Awake", attributes: [.font: font, .foregroundColor: NSColor.white])
        let size = text.size()
        text.draw(at: NSPoint(x: (CGFloat(width) - size.width) / 2, y: iconRect.minY - size.height - CGFloat(width) * 0.04))
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }
}
