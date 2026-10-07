import Cocoa
import ScreenCaptureKit

// Grabs the main display as a JPEG for the phone's Screen tab.
actor ScreenCapturer {
    static let shared = ScreenCapturer()
    private var filter: SCContentFilter?
    private var size = CGSize.zero

    func jpeg() async -> Data? {
        do {
            if filter == nil {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() })
                        ?? content.displays.first else { return nil }
                filter = SCContentFilter(display: display, excludingWindows: [])
                size = CGSize(width: display.width, height: display.height)
            }
            guard let filter else { return nil }

            // Keep frames around 1440px wide so they travel quickly over Tailscale.
            let config = SCStreamConfiguration()
            let scale = min(1, 1440 / max(size.width, 1))
            config.width = Int(size.width * scale)
            config.height = Int(size.height * scale)
            config.showsCursor = true

            let image = try await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            return NSBitmapImageRep(cgImage: image).representation(using: .jpeg, properties: [.compressionFactor: 0.6])
        } catch {
            // The display may have changed (or gone away with the lid closed); look it up again next time.
            filter = nil
            return nil
        }
    }
}

func screenPermission() -> Bool { CGPreflightScreenCaptureAccess() }
func controlPermission() -> Bool { AXIsProcessTrusted() }

func requestRemotePermissions() {
    if !CGPreflightScreenCaptureAccess() { CGRequestScreenCaptureAccess() }
    let prompt = [kAXTrustedCheckOptionPrompt.takeUnretainedValue() as String: true] as CFDictionary
    _ = AXIsProcessTrustedWithOptions(prompt)
}

// Turns taps and keys from the phone into mouse and keyboard events on the Mac.
enum RemoteInput {
    static let keyCodes: [String: CGKeyCode] = [
        "return": 36, "tab": 48, "space": 49, "delete": 51, "escape": 53,
        "left": 123, "right": 124, "down": 125, "up": 126,
    ]
    // Physical key positions (US layout) so ⌘/⌥ shortcuts work whatever input language is active.
    static let charKeyCodes: [Character: CGKeyCode] = [
        "a": 0, "s": 1, "d": 2, "f": 3, "h": 4, "g": 5, "z": 6, "x": 7, "c": 8, "v": 9, "b": 11,
        "q": 12, "w": 13, "e": 14, "r": 15, "y": 16, "t": 17, "1": 18, "2": 19, "3": 20, "4": 21,
        "6": 22, "5": 23, "=": 24, "9": 25, "7": 26, "-": 27, "8": 28, "0": 29, "]": 30, "o": 31,
        "u": 32, "[": 33, "i": 34, "p": 35, "l": 37, "j": 38, "'": 39, "k": 40, ";": 41, "\\": 42,
        ",": 43, "/": 44, "n": 45, "m": 46, ".": 47, "`": 50, " ": 49,
    ]

    // True while the phone's trackpad is holding the left button for a drag.
    private static var leftDown = false

    static func perform(_ cmd: [String: Any]) {
        let src = CGEventSource(stateID: .hidSystemState)
        let bounds = CGDisplayBounds(CGMainDisplayID())
        // A tap on the screen picture sends x/y (0…1); the trackpad sends none,
        // meaning "where the pointer is now".
        var point: CGPoint? {
            guard let x = cmd["x"] as? Double, let y = cmd["y"] as? Double else { return nil }
            return CGPoint(x: bounds.minX + min(max(x, 0), 1) * bounds.width,
                           y: bounds.minY + min(max(y, 0), 1) * bounds.height)
        }
        let current = CGEvent(source: nil)?.location ?? CGPoint(x: bounds.midX, y: bounds.midY)
        // Modifier keys held for a key press or click, e.g. ["alt"] or ["ctrl"].
        var modifierFlags: CGEventFlags = []
        for mod in cmd["mods"] as? [String] ?? [] {
            switch mod {
            case "ctrl": modifierFlags.insert(.maskControl)
            case "cmd": modifierFlags.insert(.maskCommand)
            case "shift": modifierFlags.insert(.maskShift)
            case "alt": modifierFlags.insert(.maskAlternate)
            default: break
            }
        }

        switch cmd["type"] as? String {
        case "move":
            let dx = cmd["dx"] as? Double ?? 0, dy = cmd["dy"] as? Double ?? 0
            let p = CGPoint(x: min(max(current.x + dx, bounds.minX), bounds.maxX - 1),
                            y: min(max(current.y + dy, bounds.minY), bounds.maxY - 1))
            CGEvent(mouseEventSource: src, mouseType: leftDown ? .leftMouseDragged : .mouseMoved,
                    mouseCursorPosition: p, mouseButton: .left)?.post(tap: .cghidEventTap)

        case "mousedown", "mouseup":
            let down = cmd["type"] as? String == "mousedown"
            guard down != leftDown else { return }
            leftDown = down
            CGEvent(mouseEventSource: src, mouseType: down ? .leftMouseDown : .leftMouseUp,
                    mouseCursorPosition: current, mouseButton: .left)?.post(tap: .cghidEventTap)

        case "click":
            let p = point ?? current
            let right = (cmd["button"] as? String) == "right"
            let count = Int64(cmd["count"] as? Int ?? 1)
            let button: CGMouseButton = right ? .right : .left
            CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left)?
                .post(tap: .cghidEventTap)
            for type in right ? [CGEventType.rightMouseDown, .rightMouseUp] : [.leftMouseDown, .leftMouseUp] {
                let event = CGEvent(mouseEventSource: src, mouseType: type, mouseCursorPosition: p, mouseButton: button)
                event?.setIntegerValueField(.mouseEventClickState, value: count)
                if !modifierFlags.isEmpty { event?.flags = modifierFlags }  // e.g. Option-click
                event?.post(tap: .cghidEventTap)
            }

        case "scroll":
            if let p = point {
                CGEvent(mouseEventSource: src, mouseType: .mouseMoved, mouseCursorPosition: p, mouseButton: .left)?
                    .post(tap: .cghidEventTap)
            }
            let dx = Int32(cmd["dx"] as? Double ?? 0), dy = Int32(cmd["dy"] as? Double ?? 0)
            CGEvent(scrollWheelEvent2Source: src, units: .pixel, wheelCount: 2, wheel1: dy, wheel2: dx, wheel3: 0)?
                .post(tap: .cghidEventTap)

        case "text":
            guard let text = cmd["text"] as? String else { return }
            for ch in text {
                let units = Array(String(ch).utf16)
                for down in [true, false] {
                    let event = CGEvent(keyboardEventSource: src, virtualKey: 0, keyDown: down)
                    event?.keyboardSetUnicodeString(stringLength: units.count, unicodeString: units)
                    event?.post(tap: .cghidEventTap)
                }
            }

        case "key":
            // Either a named key ("left", "return", …) or a character ("c") for shortcuts like ⌘C.
            var flags = modifierFlags
            let code: CGKeyCode
            if let name = cmd["key"] as? String, let named = keyCodes[name] {
                code = named
            } else if let char = (cmd["char"] as? String)?.first, let typed = charKeyCodes[Character(char.lowercased())] {
                code = typed
                if char.isUppercase { flags.insert(.maskShift) }
            } else {
                return
            }
            // Arrow keys carry these flags on a real keyboard; Mission Control shortcuts expect them.
            if (123...126).contains(code) { flags.formUnion([.maskSecondaryFn, .maskNumericPad]) }
            for down in [true, false] {
                let event = CGEvent(keyboardEventSource: src, virtualKey: code, keyDown: down)
                event?.flags = flags
                event?.post(tap: .cghidEventTap)
            }

        default:
            break
        }
    }
}
