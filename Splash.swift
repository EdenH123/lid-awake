import AppKit

// Launch screens for the home-screen app: iPhones show one while the page loads, but only if it
// matches the phone's exact pixel size, so each supported size is drawn here (a closed MacBook on
// black, where the page's opening animation begins) and kept after the first request.
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

    // "/splash/1206x2622.png" → the PNG, for listed sizes only, spelled exactly as the link tags
    // spell them ("/splash/01206x2622.png" is not drawn), so the cache holds one picture per size.
    static func png(forPath path: String) -> Data? {
        guard let size = sizes.first(where: { "/splash/\($0.w * $0.scale)x\($0.h * $0.scale).png" == path }) else { return nil }
        lock.lock(); defer { lock.unlock() }
        if let cached = cache[path] { return cached }
        let png = draw(width: size.w * size.scale, height: size.h * size.scale, scale: size.scale)
        cache[path] = png
        return png
    }

    // A closed MacBook on black, exactly where the page's opening animation starts (see #intro in
    // Dashboard.swift: the Mac is min(58% of the width, 300 pt) wide, centred), so the lid opens
    // seamlessly once the page has loaded.
    private static func draw(width: Int, height: Int, scale: Int) -> Data? {
        guard let rep = NSBitmapImageRep(bitmapDataPlanes: nil, pixelsWide: width, pixelsHigh: height, bitsPerSample: 8,
                                         samplesPerPixel: 4, hasAlpha: true, isPlanar: false,
                                         colorSpaceName: .deviceRGB, bytesPerRow: 0, bitsPerPixel: 0) else { return nil }
        NSGraphicsContext.saveGraphicsState()
        NSGraphicsContext.current = NSGraphicsContext(bitmapImageRep: rep)
        NSColor.black.setFill()
        NSRect(x: 0, y: 0, width: width, height: height).fill()
        let w = CGFloat(width), h = CGFloat(height)
        let macW = min(w * 0.58, 300 * CGFloat(scale)), macH = macW * 170 / 220
        let bottom = h / 2 - macH / 2                       // AppKit counts from the bottom
        let baseH = macH * 0.07
        let base = NSRect(x: (w - macW) / 2, y: bottom, width: macW, height: baseH)
        let shape = NSBezierPath(roundedRect: base, xRadius: baseH * 0.6, yRadius: baseH * 0.6)
        NSGradient(starting: NSColor(white: 0.82, alpha: 1), ending: NSColor(white: 0.56, alpha: 1))?.draw(in: shape, angle: -90)
        // The closed lid: a thin dark edge resting on the base.
        NSColor(white: 0.23, alpha: 1).setFill()
        NSBezierPath(roundedRect: NSRect(x: base.minX + macW * 0.045, y: base.maxY, width: macW * 0.91, height: max(2, baseH * 0.25)),
                     xRadius: 2, yRadius: 2).fill()
        NSGraphicsContext.restoreGraphicsState()
        return rep.representation(using: .png, properties: [:])
    }
}
