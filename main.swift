import Cocoa
import IOKit.ps

// A switch drawn by hand so it stays green when on, even though a menu bar
// app is never the frontmost app (NSSwitch greys itself out in that case).
final class GreenSwitch: NSView {
    var isOn = false { didSet { needsDisplay = true; setAccessibilityValue(isOn) } }
    var isEnabled = true { didSet { needsDisplay = true } }
    var onToggle: (() -> Void)?

    override init(frame: NSRect) {
        super.init(frame: frame)
        setAccessibilityElement(true)
        setAccessibilityRole(.checkBox)
        setAccessibilityLabel("Stay awake with lid closed")
    }
    required init?(coder: NSCoder) { fatalError() }

    override func draw(_ dirtyRect: NSRect) {
        let track = bounds.insetBy(dx: 1, dy: 1)
        let radius = track.height / 2
        let trackColor = isOn ? NSColor.systemGreen : NSColor.systemGray.withAlphaComponent(0.35)
        trackColor.withAlphaComponent(isEnabled ? trackColor.alphaComponent : trackColor.alphaComponent * 0.5).setFill()
        NSBezierPath(roundedRect: track, xRadius: radius, yRadius: radius).fill()

        let d = track.height - 4
        let x = isOn ? track.maxX - d - 2 : track.minX + 2
        let knob = NSRect(x: x, y: track.minY + 2, width: d, height: d)
        NSGraphicsContext.saveGraphicsState()
        let shadow = NSShadow()
        shadow.shadowColor = NSColor.black.withAlphaComponent(0.25)
        shadow.shadowBlurRadius = 2
        shadow.shadowOffset = NSSize(width: 0, height: -1)
        shadow.set()
        NSColor.white.setFill()
        NSBezierPath(ovalIn: knob).fill()
        NSGraphicsContext.restoreGraphicsState()
    }

    private func flip() {
        guard isEnabled else { return }
        isOn.toggle()
        onToggle?()
    }

    override func mouseUp(with event: NSEvent) { flip() }

    override func accessibilityPerformPress() -> Bool {
        flip()
        return true
    }
}

// MARK: - System helpers

let batteryLimit = 10
let timerChoices: [(title: String, minutes: Int)] = [
    ("Never", 0), ("15 minutes", 15), ("30 minutes", 30), ("1 hour", 60), ("2 hours", 120), ("4 hours", 240), ("8 hours", 480),
]

func batteryStatus() -> (percent: Int, onBattery: Bool)? {
    guard let info = IOPSCopyPowerSourcesInfo()?.takeRetainedValue(),
          let list = IOPSCopyPowerSourcesList(info)?.takeRetainedValue() as? [CFTypeRef] else { return nil }
    for source in list {
        guard let desc = IOPSGetPowerSourceDescription(info, source)?.takeUnretainedValue() as? [String: Any],
              let current = desc[kIOPSCurrentCapacityKey] as? Int,
              let max = desc[kIOPSMaxCapacityKey] as? Int, max > 0 else { continue }
        let state = desc[kIOPSPowerSourceStateKey] as? String
        return (current * 100 / max, state == kIOPSBatteryPowerValue)
    }
    return nil
}

let maxBatteryTemp = 40.0

// Battery temperature in °C (the battery reports hundredths of a degree).
func batteryTemperature() -> Double? {
    let service = IOServiceGetMatchingService(kIOMainPortDefault, IOServiceMatching("AppleSmartBattery"))
    guard service != 0 else { return nil }
    defer { IOObjectRelease(service) }
    let value = IORegistryEntryCreateCFProperty(service, "Temperature" as CFString, kCFAllocatorDefault, 0)?
        .takeRetainedValue() as? Int
    return value.map { Double($0) / 100 }
}

// Why lid-awake should not be on right now, or nil if it is fine.
func safetyStop(batteryGuard: Bool, heatGuard: Bool) -> String? {
    if batteryGuard, let b = batteryStatus(), b.onBattery, b.percent < batteryLimit {
        return "Battery is at \(b.percent)%."
    }
    if heatGuard {
        if let t = batteryTemperature(), t >= maxBatteryTemp {
            return String(format: "Battery is too hot (%.1f°C).", t)
        }
        let thermal = ProcessInfo.processInfo.thermalState
        if thermal == .serious || thermal == .critical {
            return "Your Mac is overheating."
        }
    }
    return nil
}

func sleepDisabled() -> Bool {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/pmset")
    p.arguments = ["-g"]
    let pipe = Pipe()
    p.standardOutput = pipe
    try? p.run()
    p.waitUntilExit()
    let out = String(data: pipe.fileHandleForReading.readDataToEndOfFile(), encoding: .utf8) ?? ""
    let line = out.split(separator: "\n").first { $0.contains("SleepDisabled") }
    return line?.split(whereSeparator: { $0 == " " || $0 == "\t" }).last == "1"
}

func notify(_ message: String) {
    let p = Process()
    p.executableURL = URL(fileURLWithPath: "/usr/bin/osascript")
    p.arguments = ["-e", "display notification \"\(message)\" with title \"Lid Awake\""]
    try? p.run()
}

// Countdown text: "1:23:45" or "23:45".
func formatRemaining(_ seconds: TimeInterval) -> String {
    let total = max(0, Int(seconds.rounded(.up)))
    let h = total / 3600, m = (total % 3600) / 60, s = total % 60
    return h > 0 ? String(format: "%d:%02d:%02d", h, m, s) : String(format: "%d:%02d", m, s)
}

// MARK: - App

final class AppDelegate: NSObject, NSApplicationDelegate, NSMenuDelegate {
    var statusItem: NSStatusItem!
    let toggle = GreenSwitch(frame: NSRect(x: 226, y: 8, width: 42, height: 24))
    let statusLine = NSMenuItem(title: "", action: nil, keyEquivalent: "")
    let timerMenu = NSMenu()
    let batteryItem = NSMenuItem(title: "Turn off below \(batteryLimit)% battery", action: nil, keyEquivalent: "")
    let heatItem = NSMenuItem(title: "Turn off if battery is hotter than \(Int(maxBatteryTemp))°C", action: nil, keyEquivalent: "")
    let infoLine = NSMenuItem(title: "", action: nil, keyEquivalent: "")
    let remoteItem = NSMenuItem(title: "Allow screen & control from phone", action: nil, keyEquivalent: "")
    let passwordItem = NSMenuItem(title: "Set phone password…", action: nil, keyEquivalent: "")
    let defaults = UserDefaults.standard
    var busy = false
    var isOn = false
    var servedState: Bool?  // last lid-awake state the Tailscale link was synced to
    var server: StatusServer?
    var liveServer: LiveServer?
    let computerName = Host.current().localizedName ?? "Mac"

    // Chosen timer length in minutes (0 = never) and when the current run ends.
    var timerMinutes: Int {
        get { defaults.integer(forKey: "timerMinutes") }
        set { defaults.set(newValue, forKey: "timerMinutes") }
    }
    var endDate: Date? {
        get {
            let t = defaults.double(forKey: "endDate")
            return t > 0 ? Date(timeIntervalSince1970: t) : nil
        }
        set { defaults.set(newValue?.timeIntervalSince1970 ?? 0, forKey: "endDate") }
    }
    var batteryGuard: Bool {
        get { defaults.object(forKey: "batteryGuard") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "batteryGuard") }
    }
    // Off by default: the phone can only see and control the screen once this is ticked.
    var remoteScreen: Bool {
        get { defaults.bool(forKey: "remoteScreen") }
        set { defaults.set(newValue, forKey: "remoteScreen") }
    }
    var heatGuard: Bool {
        get { defaults.object(forKey: "heatGuard") as? Bool ?? true }
        set { defaults.set(newValue, forKey: "heatGuard") }
    }

    func applicationDidFinishLaunching(_ notification: Notification) {
        statusItem = NSStatusBar.system.statusItem(withLength: NSStatusItem.variableLength)
        // Remember where the user ⌘-drags the icon, so restarts and updates keep that spot
        // instead of putting it back on the far left (where the notch can hide it).
        statusItem.autosaveName = "LidAwake"
        statusItem.behavior = []
        statusItem.isVisible = true
        statusItem.button?.imagePosition = .imageLeft
        statusItem.button?.font = NSFont.monospacedDigitSystemFont(ofSize: NSFont.systemFontSize, weight: .regular)

        let row = NSView(frame: NSRect(x: 0, y: 0, width: 284, height: 40))
        let title = NSTextField(labelWithString: "Stay awake with lid closed")
        title.frame = NSRect(x: 14, y: 11, width: 205, height: 18)
        toggle.onToggle = { [weak self] in self?.switched() }
        row.addSubview(title)
        row.addSubview(toggle)
        let switchItem = NSMenuItem()
        switchItem.view = row

        for (i, choice) in timerChoices.enumerated() {
            let item = NSMenuItem(title: choice.title, action: #selector(pickTimer(_:)), keyEquivalent: "")
            item.target = self
            item.tag = i
            timerMenu.addItem(item)
        }
        let timerItem = NSMenuItem(title: "Turn off after", action: nil, keyEquivalent: "")
        timerItem.submenu = timerMenu

        batteryItem.target = self
        batteryItem.action = #selector(toggleBatteryGuard)
        heatItem.target = self
        heatItem.action = #selector(toggleHeatGuard)
        statusLine.isEnabled = false
        infoLine.isEnabled = false

        let menu = NSMenu()
        menu.delegate = self
        menu.addItem(switchItem)
        menu.addItem(statusLine)
        menu.addItem(.separator())
        menu.addItem(timerItem)
        menu.addItem(batteryItem)
        menu.addItem(heatItem)
        menu.addItem(.separator())
        menu.addItem(infoLine)
        let pageItem = NSMenuItem(title: "Open live page", action: #selector(openLivePage), keyEquivalent: "")
        pageItem.target = self
        menu.addItem(pageItem)
        remoteItem.target = self
        remoteItem.action = #selector(toggleRemote)
        menu.addItem(remoteItem)
        passwordItem.target = self
        passwordItem.action = #selector(setPhonePassword)
        menu.addItem(passwordItem)
        statusItem.menu = menu

        // Serve the live page; the status is built on the main thread from the
        // same state the menu shows.
        server = StatusServer(
            status: { DispatchQueue.main.sync { self.statusJSON() } },
            remoteAllowed: { UserDefaults.standard.bool(forKey: "remoteScreen") },
            awake: { DispatchQueue.main.sync { self.isOn } },
            turnOff: { [weak self] in
                guard let self, self.isOn, !self.busy else { return }
                self.setAwake(false)
                notify("Turned off from your phone. Your Mac will sleep normally again.")
            }
        )
        liveServer = LiveServer(checks: .init(
            sessionValid: { Auth.isValid($0) },
            awake: { DispatchQueue.main.sync { self.isOn } },
            remoteAllowed: { UserDefaults.standard.bool(forKey: "remoteScreen") },
            screenPermission: { screenPermission() },
            controlPermission: { controlPermission() }))

        // Re-check the Tailscale link every minute (refresh() also syncs it whenever
        // lid-awake turns on or off).
        let serveCheck = Timer(timeInterval: 60, repeats: true) { [weak self] _ in
            guard let self else { return }
            let on = self.isOn
            DispatchQueue.global(qos: .utility).async { ensureTailscaleServe(enabled: on) }
        }
        RunLoop.main.add(serveCheck, forMode: .common)

        // Check the timer and battery every 15 seconds, and update the countdown
        // every second. Common run loop mode keeps both running while the menu is open.
        let check = Timer(timeInterval: 15, repeats: true) { [weak self] _ in self?.tick() }
        let countdown = Timer(timeInterval: 1, repeats: true) { [weak self] _ in self?.updateCountdown() }
        RunLoop.main.add(check, forMode: .common)
        RunLoop.main.add(countdown, forMode: .common)
        tick()
    }

    func updateCountdown() {
        guard isOn, let end = endDate else { return }
        let r = end.timeIntervalSinceNow
        if r <= 0 {
            tick()
            return
        }
        statusItem.button?.title = " " + formatRemaining(r)
        statusLine.title = "Turns off in \(formatRemaining(r))"
    }

    func menuWillOpen(_ menu: NSMenu) { refresh() }

    func tick() {
        refresh()
        guard isOn, !busy else { return }
        if let end = endDate, Date() >= end {
            setAwake(false)
            notify("Timer finished. Your Mac will sleep normally again.")
        } else if let reason = safetyStop(batteryGuard: batteryGuard, heatGuard: heatGuard) {
            setAwake(false)
            notify("\(reason) Turned off so your Mac can sleep.")
        }
    }

    func refresh() {
        let wasOn = servedState
        isOn = sleepDisabled()
        if !isOn { endDate = nil }
        // The phone link exists only while lid-awake is on.
        if wasOn != isOn {
            servedState = isOn
            let on = isOn
            DispatchQueue.global(qos: .utility).async { ensureTailscaleServe(enabled: on) }
        }
        toggle.isOn = isOn

        let image = NSImage(systemSymbolName: isOn ? "cup.and.saucer.fill" : "moon.zzz",
                            accessibilityDescription: isOn ? "Lid Awake on" : "Lid Awake off")
        image?.isTemplate = true
        statusItem.button?.image = image

        let remaining = endDate.map { $0.timeIntervalSinceNow }
        if isOn, let r = remaining, r > 0 {
            statusItem.button?.title = " " + formatRemaining(r)
            statusLine.title = "Turns off in \(formatRemaining(r))"
        } else {
            statusItem.button?.title = ""
            statusLine.title = isOn ? "On, no timer" : "Off"
        }
        statusItem.button?.toolTip = isOn ? "Lid Awake: ON" : "Lid Awake: OFF"

        for item in timerMenu.items {
            item.state = timerChoices[item.tag].minutes == timerMinutes ? .on : .off
        }
        batteryItem.state = batteryGuard ? .on : .off
        heatItem.state = heatGuard ? .on : .off
        remoteItem.state = remoteScreen ? .on : .off
        passwordItem.title = Auth.hasPassword ? "Change phone password…" : "Set phone password…"

        var info: [String] = []
        if let b = batteryStatus() {
            let power = powerInfo()
            let toFull = power["minutesToFull"] as? Int ?? -1
            let charging = power["charging"] as? Bool ?? false
            var line = "Battery \(b.percent)%"
            if charging && toFull > 0 { line += " · full in \(toFull / 60)h \(toFull % 60)m" }
            else if charging { line += " · charging" }
            else if !b.onBattery { line += " (plugged in)" }
            info.append(line)
        }
        if let t = batteryTemperature() { info.append(String(format: "%.1f°C", t)) }
        infoLine.title = info.isEmpty ? "No battery info" : info.joined(separator: " · ")
    }

    func switched() {
        statusItem.menu?.cancelTracking()
        if toggle.isOn {
            if let reason = safetyStop(batteryGuard: batteryGuard, heatGuard: heatGuard) {
                toggle.isOn = false
                notify("\(reason) Can't keep your Mac awake right now.")
                return
            }
            endDate = timerMinutes > 0 ? Date().addingTimeInterval(Double(timerMinutes) * 60) : nil
            setAwake(true)
        } else {
            setAwake(false)
        }
    }

    @objc func pickTimer(_ sender: NSMenuItem) {
        timerMinutes = timerChoices[sender.tag].minutes
        // Changing the timer while on restarts the countdown from now.
        if isOn {
            endDate = timerMinutes > 0 ? Date().addingTimeInterval(Double(timerMinutes) * 60) : nil
        }
        refresh()
    }

    @objc func toggleBatteryGuard() {
        batteryGuard.toggle()
        tick()
    }

    @objc func toggleHeatGuard() {
        heatGuard.toggle()
        tick()
    }

    @objc func setPhonePassword() {
        NSApp.activate()
        let alert = NSAlert()
        alert.messageText = Auth.hasPassword ? "Change phone password" : "Set phone password"
        alert.informativeText = "Your phone will ask for this password before showing anything. "
            + "Use at least 6 characters. Saving a new password logs out every device."
        let password = NSSecureTextField(frame: NSRect(x: 0, y: 32, width: 260, height: 24))
        password.placeholderString = "New password"
        let confirm = NSSecureTextField(frame: NSRect(x: 0, y: 0, width: 260, height: 24))
        confirm.placeholderString = "Repeat password"
        let box = NSView(frame: NSRect(x: 0, y: 0, width: 260, height: 56))
        box.addSubview(password)
        box.addSubview(confirm)
        alert.accessoryView = box
        alert.addButton(withTitle: "Save")
        alert.addButton(withTitle: "Cancel")
        alert.window.initialFirstResponder = password
        guard alert.runModal() == .alertFirstButtonReturn else { return }

        var problem: String?
        if password.stringValue.count < 6 { problem = "The password needs at least 6 characters." }
        else if password.stringValue != confirm.stringValue { problem = "The two passwords don't match." }
        if let problem {
            let error = NSAlert()
            error.messageText = problem
            error.runModal()
            return setPhonePassword()
        }
        Auth.setPassword(password.stringValue)
        notify("Phone password saved. Your phone will ask for it next time.")
        refresh()
    }

    @objc func toggleRemote() {
        remoteScreen.toggle()
        if remoteScreen { requestRemotePermissions() }
        refresh()
    }

    @objc func openLivePage() {
        NSWorkspace.shared.open(URL(string: "http://127.0.0.1:\(statusPort)/")!)
    }

    func statusJSON() -> Data {
        let thermalNames = ["nominal", "fair", "serious", "critical"]
        var s: [String: Any] = [
            "host": computerName,
            "lidAwake": isOn,
            "timerMinutes": timerMinutes,
            "batteryGuard": batteryGuard,
            "heatGuard": heatGuard,
            "batteryLimit": batteryLimit,
            "maxBatteryTemp": Int(maxBatteryTemp),
            "thermalState": thermalNames[min(ProcessInfo.processInfo.thermalState.rawValue, 3)],
            "remoteScreen": remoteScreen,
            "screenPermission": screenPermission(),
            "controlPermission": controlPermission(),
            "screenWidth": CGDisplayBounds(CGMainDisplayID()).width,
            "screenOff": FrameStream.shared.isScreenOff,
        ]
        if isOn, let end = endDate, end > Date() { s["timerRemaining"] = end.timeIntervalSinceNow }
        if let t = batteryTemperature() { s["batteryTemp"] = (t * 10).rounded() / 10 }
        s.merge(powerInfo()) { $1 }
        return (try? JSONSerialization.data(withJSONObject: s)) ?? Data("{}".utf8)
    }

    func setAwake(_ on: Bool) {
        busy = true
        toggle.isEnabled = false
        if !on { endDate = nil }

        // Try passwordless sudo first (works once /etc/sudoers.d/lidawake allows
        // this exact pmset command); otherwise fall back to the password prompt.
        // osascript runs as a separate process so this app's main thread never
        // blocks and the dialog can receive keyboard input.
        let pmset = "/usr/bin/pmset -a disablesleep \(on ? 1 : 0)"
        let p = Process()
        p.executableURL = URL(fileURLWithPath: "/bin/sh")
        p.arguments = ["-c", "/usr/bin/sudo -n \(pmset) 2>/dev/null || /usr/bin/osascript -e 'do shell script \"\(pmset)\" with administrator privileges'"]
        p.terminationHandler = { _ in
            DispatchQueue.main.async {
                self.busy = false
                self.toggle.isEnabled = true
                self.refresh()
            }
        }
        do {
            try p.run()
        } catch {
            busy = false
            toggle.isEnabled = true
            refresh()
        }
    }
}

if CommandLine.arguments.contains("--battery") {
    if let b = batteryStatus() {
        print("battery \(b.percent)% onBattery=\(b.onBattery)")
    } else {
        print("no battery found")
    }
    print("temperature \(batteryTemperature().map { String(format: "%.1f°C", $0) } ?? "unknown")")
    print("thermalState \(ProcessInfo.processInfo.thermalState.rawValue)")
    print("safetyStop \(safetyStop(batteryGuard: true, heatGuard: true) ?? "none")")
    exit(0)
}

// Self-check, run under launchd so macOS judges permissions for the app itself:
// reports both permissions and tries to capture one frame (nothing is clicked).
if CommandLine.arguments.contains("--selftest") {
    let how = ProcessInfo.processInfo.environment["XPC_SERVICE_NAME"] ?? "none"
    var lines = ["\(Date()) launched-by=\(how) pid=\(getpid())",
                 "screenPermission \(screenPermission())",
                 "controlPermission \(controlPermission())"]
    let done = DispatchSemaphore(value: 0)
    Task {
        let jpeg = await ScreenCapturer.shared.jpeg()
        lines.append(jpeg.map { "frame \($0.count) bytes" } ?? "frame none")
        // Check the live stream used by the phone: first frame, then how a
        // "wait for a newer frame" request behaves.
        let start = Date()
        FrameStream.shared.frame(after: 0) { first in
            let ms = Int(Date().timeIntervalSince(start) * 1000)
            guard case .frame(let jpeg, let id) = first else {
                lines.append("stream first frame: none after \(ms) ms")
                done.signal()
                return
            }
            lines.append("stream first frame: \(jpeg.count) bytes, id \(id), \(ms) ms")
            let waitStart = Date()
            FrameStream.shared.frame(after: id) { next in
                let waited = Int(Date().timeIntervalSince(waitStart) * 1000)
                switch next {
                case .frame(let j, let nid): lines.append("next frame: \(j.count) bytes, id \(nid), after \(waited) ms")
                case .unchanged: lines.append("next: unchanged after \(waited) ms (screen didn't change)")
                case .unavailable: lines.append("next: unavailable after \(waited) ms")
                }
                // Live socket: a private LiveServer on port 18788 that accepts a test token,
                // so no real login is created. For each preset, reports the frames received
                // in 3 s and the size of the last one (Sharp should be largest, Fast smallest).
                let test = LiveServer(port: 18788, checks: .init(
                    sessionValid: { $0 == "selftest" }, awake: { true }, remoteAllowed: { true },
                    screenPermission: { true }, controlPermission: { true }))
                Task {
                    for preset in ["sharp", "balanced", "fast"] {
                        var request = URLRequest(url: URL(string: "ws://127.0.0.1:18788/live")!)
                        request.setValue("http://127.0.0.1:\(statusPort)", forHTTPHeaderField: "Origin")
                        request.setValue("\(Auth.cookieName)=selftest", forHTTPHeaderField: "Cookie")
                        let socket = URLSession.shared.webSocketTask(with: request)
                        socket.resume()
                        try? await socket.send(.string(#"{"quality":"\#(preset)"}"#))
                        var frames = 0, firstBytes = 0, lastBytes = 0
                        let end = Date().addingTimeInterval(3)
                        while Date() < end {
                            let message = try? await withThrowingTaskGroup(of: URLSessionWebSocketTask.Message?.self) { group in
                                group.addTask { try await socket.receive() }
                                group.addTask { try await Task.sleep(nanoseconds: 500_000_000); return nil }
                                let first = try await group.next() ?? nil
                                group.cancelAll()
                                return first
                            }
                            if case .data(let d)? = message { frames += 1; if firstBytes == 0 { firstBytes = d.count }; lastBytes = d.count }
                        }
                        lines.append("live \(preset): \(frames) frames, first \(firstBytes) bytes, last \(lastBytes) bytes")
                        if preset == "fast" {
                            try? await socket.send(.string(#"{"type":"ping"}"#))
                            var echoed = false
                            for _ in 0..<40 {
                                if case .string(let s)? = try? await socket.receive(), s.contains("pong") { echoed = true; break }
                            }
                            lines.append("live input echo: \(echoed ? "ok" : "missing")")
                            lines.append("live fast changed areas: \(FrameStream.shared.changeReport)")
                        }
                        socket.cancel(with: .normalClosure, reason: nil)
                        try? await Task.sleep(nanoseconds: 300_000_000)
                    }
                    _ = test
                    done.signal()
                }
            }
        }
    }
    done.wait()
    let text = lines.joined(separator: "\n") + "\n\n"
    print(text, terminator: "")
    // Also append to a log file, since an app opened from Finder has no visible output.
    let log = URL(fileURLWithPath: NSHomeDirectory() + "/Library/Logs/LidAwake-selftest.log")
    if let handle = try? FileHandle(forWritingTo: log) {
        handle.seekToEndOfFile()
        handle.write(Data(text.utf8))
        try? handle.close()
    } else {
        try? text.write(to: log, atomically: true, encoding: .utf8)
    }
    exit(0)
}

// Run exactly one copy: the one launchd keeps alive. A copy opened another way
// (double-click, or macOS's "Quit & Reopen" after granting a permission) steps
// aside, and launchd's copy replaces any stray one.
let launchedByLaunchd = ProcessInfo.processInfo.environment["XPC_SERVICE_NAME"] == "local.lidawake"
let agentInstalled = FileManager.default.fileExists(atPath: NSHomeDirectory() + "/Library/LaunchAgents/local.lidawake.plist")
if agentInstalled && !launchedByLaunchd { exit(0) }
for other in NSRunningApplication.runningApplications(withBundleIdentifier: "local.lidawake")
where other.processIdentifier != getpid() {
    other.forceTerminate()
}

let app = NSApplication.shared
let delegate = AppDelegate()
app.delegate = delegate
app.setActivationPolicy(.accessory)
app.run()
