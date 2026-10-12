import CoreGraphics
import Foundation
import IOKit.pwr_mgt

// Lid closed while Lid Awake is on: keep the built-in screen from switching off, at the lowest
// brightness. With the lid shut, macOS never turns a sleeping screen back on (only opening the lid
// does), and without a screen the phone has nothing to show. Opening the lid gives the brightness
// back and the normal screen-sleep setting applies again.
enum LidScreen {
    private static var assertion: IOPMAssertionID = 0
    private static let savedKey = "brightnessBeforeLidClosed"

    // Called every couple of seconds on the main thread.
    static func update(awake: Bool) {
        let hold = awake && FrameStream.lidClosed()
        if hold {
            if assertion == 0 {
                var id: IOPMAssertionID = 0
                let made = IOPMAssertionCreateWithName(kIOPMAssertionTypePreventUserIdleDisplaySleep as CFString,
                    IOPMAssertionLevel(kIOPMAssertionLevelOn),
                    "Lid Awake keeps the screen available to the phone while the lid is closed" as CFString, &id)
                if made == kIOReturnSuccess { assertion = id }
                let screens = activeDisplays()
                StreamStats.shared.note("lid closed: keeping the screen on at lowest brightness"
                    + (screens == 0 ? " (but it was already off; it comes back when the lid opens)" : ""))
            }
            dim()  // every time: brightness can drift up again (e.g. automatic brightness)
        } else if assertion != 0 {
            IOPMAssertionRelease(assertion)
            assertion = 0
            restore()
            StreamStats.shared.note(awake ? "lid opened: brightness and screen sleep back to normal"
                                          : "Lid Awake off: brightness and screen sleep back to normal")
        } else if !FrameStream.lidClosed() {
            restore()  // e.g. the app restarted after the lid was opened
        }
    }

    // Brightness (0…1) is set through macOS's own display services.
    private typealias GetBrightness = @convention(c) (CGDirectDisplayID, UnsafeMutablePointer<Float>) -> Int32
    private typealias SetBrightness = @convention(c) (CGDirectDisplayID, Float) -> Int32
    private static let services = dlopen("/System/Library/PrivateFrameworks/DisplayServices.framework/DisplayServices", RTLD_LAZY)
    private static let getBrightness = services.flatMap { dlsym($0, "DisplayServicesGetBrightness") }
        .map { unsafeBitCast($0, to: GetBrightness.self) }
    private static let setBrightness = services.flatMap { dlsym($0, "DisplayServicesSetBrightness") }
        .map { unsafeBitCast($0, to: SetBrightness.self) }

    private static func builtIn() -> CGDirectDisplayID? {
        var ids = [CGDirectDisplayID](repeating: 0, count: 8)
        var count: UInt32 = 0
        CGGetOnlineDisplayList(8, &ids, &count)
        return ids.prefix(Int(count)).first { CGDisplayIsBuiltin($0) != 0 }
    }

    private static func activeDisplays() -> UInt32 {
        var count: UInt32 = 0
        CGGetActiveDisplayList(0, nil, &count)
        return count
    }

    private static func dim() {
        guard let display = builtIn(), let get = getBrightness, let set = setBrightness else { return }
        var now: Float = 0
        guard get(display, &now) == 0 else { return }
        // Remember the person's brightness once (not the dimmed one, e.g. after a restart).
        if UserDefaults.standard.object(forKey: savedKey) == nil { UserDefaults.standard.set(now, forKey: savedKey) }
        if now > 0.001 { _ = set(display, 0) }
    }

    private static func restore() {
        guard UserDefaults.standard.object(forKey: savedKey) != nil else { return }
        let saved = UserDefaults.standard.float(forKey: savedKey)
        UserDefaults.standard.removeObject(forKey: savedKey)
        if let display = builtIn(), let set = setBrightness { _ = set(display, saved) }
    }
}
