import AppKit
import CoreImage.CIFilterBuiltins
import SwiftUI

// "Set up Lid Awake": everything a new Mac still needs, each with its fix, a box for the phone
// password and a QR code that opens the phone page on the iPhone. Opens by itself until a
// phone password is set; later from the menu (Set up iPhone…).
final class SetupModel: ObservableObject {
    @Published var tailscaleInstalled = false
    @Published var signedIn = false
    @Published var httpsOn = false
    @Published var host: String?
    @Published var sleepRule = false
    @Published var hasPassword = Auth.hasPassword
    @Published var awake = false
    @Published var screen = screenPermission()
    @Published var control = controlPermission()
    @Published var remoteScreen = false

    let isAwake: () -> Bool
    let turnOn: () -> Void
    let allowScreen: () -> Void
    private var checking = false

    init(isAwake: @escaping () -> Bool, turnOn: @escaping () -> Void, allowScreen: @escaping () -> Void) {
        self.isAwake = isAwake
        self.turnOn = turnOn
        self.allowScreen = allowScreen
        refresh()
    }

    // The slow checks (Tailscale, the keep-awake rule) run in the background.
    func refresh() {
        hasPassword = Auth.hasPassword
        awake = isAwake()
        screen = screenPermission()
        control = controlPermission()
        remoteScreen = UserDefaults.standard.bool(forKey: "remoteScreen")
        guard !checking else { return }
        checking = true
        DispatchQueue.global(qos: .userInitiated).async {
            let installed = FileManager.default.isExecutableFile(atPath: tailscalePath)
            let status = installed ? Self.run(tailscalePath, ["status", "--json"]) : nil
            let json = status.flatMap { try? JSONSerialization.jsonObject(with: $0) as? [String: Any] }
            if let json { Tailnet.update(fromStatus: json) }
            let running = json?["BackendState"] as? String == "Running"
            let https = !((json?["CertDomains"] as? [Any]) ?? []).isEmpty
            let rule = Self.run("/usr/bin/sudo", ["-n", "-l", "/usr/bin/pmset", "-a", "disablesleep", "1"]) != nil
            DispatchQueue.main.async {
                self.tailscaleInstalled = installed
                self.signedIn = running
                self.httpsOn = https
                self.host = running ? Tailnet.host : nil
                self.sleepRule = rule
                self.checking = false
            }
        }
    }

    // Output of a command that succeeded, or nil.
    private static func run(_ path: String, _ args: [String]) -> Data? {
        let p = Process()
        p.executableURL = URL(fileURLWithPath: path)
        p.arguments = args
        var env = ProcessInfo.processInfo.environment
        env["TAILSCALE_BE_CLI"] = "1"
        p.environment = env
        let out = Pipe()
        p.standardOutput = out
        p.standardError = FileHandle.nullDevice
        p.standardInput = FileHandle.nullDevice
        guard (try? p.run()) != nil else { return nil }
        let data = out.fileHandleForReading.readDataToEndOfFile()
        p.waitUntilExit()
        return p.terminationStatus == 0 ? data : nil
    }

    var pageURL: String? { host.map { "https://\($0)/" } }
    var ready: Bool { signedIn && httpsOn && sleepRule && hasPassword }
}

let tailscalePath = "/Applications/Tailscale.app/Contents/MacOS/Tailscale"

struct SetupView: View {
    @ObservedObject var model: SetupModel
    @State private var password = ""
    @State private var repeated = ""
    @State private var problem = ""
    private let tick = Timer.publish(every: 2, on: .main, in: .common).autoconnect()

    var body: some View {
        VStack(alignment: .leading, spacing: 16) {
            VStack(alignment: .leading, spacing: 4) {
                Text("Set up Lid Awake").font(.title2.bold())
                Text("A few things this Mac needs, then your iPhone.").foregroundStyle(.secondary)
            }

            Row(done: model.tailscaleInstalled && model.signedIn, title: "Tailscale",
                detail: !model.tailscaleInstalled ? "Install Tailscale and sign in. It's the private link between your iPhone and this Mac."
                    : model.signedIn ? "Signed in." : "Open Tailscale in the menu bar and sign in.") {
                if !model.tailscaleInstalled { Button("Download Tailscale") { open("https://tailscale.com/download/mac") } }
                else if !model.signedIn { Button("Open Tailscale") { NSWorkspace.shared.open(URL(fileURLWithPath: "/Applications/Tailscale.app")) } }
            }
            Row(done: model.httpsOn, title: "Private web address",
                detail: model.httpsOn ? (model.pageURL ?? "Ready.")
                    : "In Tailscale's settings, turn on MagicDNS and HTTPS Certificates (once for your account).") {
                if model.signedIn && !model.httpsOn { Button("Open Tailscale settings") { open("https://login.tailscale.com/admin/dns") } }
            }
            Row(done: model.sleepRule, title: "Permission to keep the Mac awake",
                detail: model.sleepRule ? "Allowed." : "Open “Install Lid Awake” in the Lid Awake folder; it asks for your Mac password once.") { EmptyView() }
            Row(done: model.hasPassword, title: "Phone password",
                detail: model.hasPassword ? "Set. Change it from the Lid Awake menu."
                    : "Your iPhone asks for it before showing anything. At least \(Auth.minPasswordLength) characters.") {
                if !model.hasPassword {
                    VStack(alignment: .leading, spacing: 6) {
                        SecureField("New password", text: $password).frame(width: 240)
                        SecureField("Repeat password", text: $repeated).frame(width: 240).onSubmit(save)
                        if !problem.isEmpty { Text(problem).font(.callout).foregroundStyle(.red) }
                        Button("Save password", action: save).keyboardShortcut(.defaultAction)
                    }
                }
            }

            Divider()

            HStack(alignment: .top, spacing: 16) {
                QRCode(text: model.pageURL ?? "")
                    .frame(width: 150, height: 150)
                    .opacity(model.ready ? 1 : 0.25)
                VStack(alignment: .leading, spacing: 8) {
                    Text("On your iPhone").font(.headline)
                    Step(n: 1, text: "Install Tailscale and sign in with the same account.")
                    Step(n: 2, text: model.awake ? "Lid Awake is on, so the page is open." : "Turn Lid Awake on: the page only opens while it's on.")
                    if !model.awake && model.ready { Button("Turn Lid Awake on", action: model.turnOn) }
                    Step(n: 3, text: "Point the Camera at this code and open the link in Safari.")
                    Step(n: 4, text: "Share → Add to Home Screen, then sign in with your password.")
                }
            }

            Divider()

            Row(done: model.remoteScreen && model.screen && model.control, title: "See and control the screen (optional)",
                detail: !model.remoteScreen ? "Lets your iPhone show this Mac's screen and use it as a trackpad."
                    : !model.screen ? "Allow Lid Awake in System Settings → Privacy & Security → Screen Recording."
                    : !model.control ? "Allow Lid Awake in System Settings → Privacy & Security → Accessibility."
                    : "Allowed. Turn it off any time in the Lid Awake menu.") {
                if !model.remoteScreen { Button("Allow screen from phone", action: model.allowScreen) }
                else if !model.screen { Button("Open Screen Recording settings") { open("x-apple.systempreferences:com.apple.preference.security?Privacy_ScreenCapture") } }
                else if !model.control { Button("Open Accessibility settings") { open("x-apple.systempreferences:com.apple.preference.security?Privacy_Accessibility") } }
            }
        }
        .padding(24)
        .frame(width: 500)
        .onReceive(tick) { _ in model.refresh() }
    }

    private func save() {
        if password.count < Auth.minPasswordLength { problem = "Use at least \(Auth.minPasswordLength) characters."; return }
        if password != repeated { problem = "The two passwords don't match."; return }
        Auth.setPassword(password)
        AccessLog.write("Phone password set on the Mac (setup)")
        password = ""; repeated = ""; problem = ""
        model.refresh()
    }

    private func open(_ link: String) { if let url = URL(string: link) { NSWorkspace.shared.open(url) } }
}

private struct Row<Fix: View>: View {
    let done: Bool, title: String, detail: String
    @ViewBuilder let fix: () -> Fix
    var body: some View {
        HStack(alignment: .top, spacing: 10) {
            Image(systemName: done ? "checkmark.circle.fill" : "circle")
                .font(.title3).foregroundStyle(done ? Color.green : Color.secondary)
            VStack(alignment: .leading, spacing: 4) {
                Text(title).font(.headline)
                Text(detail).font(.callout).foregroundStyle(.secondary).textSelection(.enabled)
                    .fixedSize(horizontal: false, vertical: true)
                if !done { fix() }
            }
        }
    }
}

private struct Step: View {
    let n: Int, text: String
    var body: some View {
        HStack(alignment: .top, spacing: 8) {
            Text("\(n)").font(.caption.bold()).frame(width: 18, height: 18)
                .background(Circle().fill(Color.secondary.opacity(0.2)))
            Text(text).font(.callout).fixedSize(horizontal: false, vertical: true)
        }
    }
}

// The phone page's address as a QR code, drawn with sharp square dots.
private struct QRCode: View {
    let text: String
    var body: some View {
        if let image = Self.make(text) {
            Image(nsImage: image).interpolation(.none).resizable()
                .padding(8).background(RoundedRectangle(cornerRadius: 10).fill(Color.white))
        } else {
            RoundedRectangle(cornerRadius: 10).fill(Color.secondary.opacity(0.15))
                .overlay(Text("The code appears when Tailscale is ready").font(.caption).multilineTextAlignment(.center).padding())
        }
    }
    static func make(_ text: String) -> NSImage? {
        guard !text.isEmpty else { return nil }
        let filter = CIFilter.qrCodeGenerator()
        filter.message = Data(text.utf8)
        filter.correctionLevel = "M"
        // Enlarged in whole steps before it becomes a picture, so every dot stays a crisp square.
        guard let output = filter.outputImage?.transformed(by: CGAffineTransform(scaleX: 12, y: 12)),
              let picture = CIContext().createCGImage(output, from: output.extent) else { return nil }
        return NSImage(cgImage: picture, size: output.extent.size)
    }
}

final class SetupWindow {
    private var window: NSWindow?

    func show(model: SetupModel) {
        model.refresh()
        if window == nil {
            let w = NSWindow(contentRect: NSRect(x: 0, y: 0, width: 500, height: 700),
                             styleMask: [.titled, .closable], backing: .buffered, defer: false)
            w.title = "Set up Lid Awake"
            w.isReleasedWhenClosed = false
            w.contentView = NSHostingView(rootView: SetupView(model: model))
            w.center()
            window = w
        }
        NSApp.activate()
        window?.makeKeyAndOrderFront(nil)
    }
}
