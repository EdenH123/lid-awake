import CoreImage
import Foundation
import IOKit.pwr_mgt
@preconcurrency import ScreenCaptureKit

// Live screen frames for the phone. While someone is watching, a ScreenCaptureKit
// stream runs and delivers a frame only when the screen changes. Frames reach the
// phone two ways: pushed to WebSocket subscribers (LiveServer), or handed to
// long-poll requests (/screen.jpg) that wait for a frame newer than their last.
// The stream stops when nobody has watched for a while.
final class FrameStream: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    static let shared = FrameStream()

    enum Result { case frame(Data, id: Int), unchanged, unavailable }

    enum Preset: String {
        case sharp, balanced, fast
        var maxWidth: Double { switch self { case .sharp: 1600; case .balanced: 1200; case .fast: 800 } }
        var quality: Double { switch self { case .sharp: 0.7; case .balanced: 0.5; case .fast: 0.4 } }
        var fps: Int32 { switch self { case .sharp: 12; case .balanced: 20; case .fast: 25 } }
    }

    private let queue = DispatchQueue(label: "lidawake.frames")
    private let ciContext = CIContext()
    private var stream: SCStream?
    private var display: SCDisplay?
    private var starting = false
    private var preset = Preset.balanced
    private var latest: Data?
    private var latestID = 0
    private var lastRequest = Date.distantPast
    private var lastWake = Date.distantPast
    private var waiters: [(after: Int, done: (Result) -> Void)] = []
    private var subscribers: [UUID: (frame: (Data) -> Void, unavailable: () -> Void)] = [:]
    private var idleTimer: DispatchSourceTimer?

    private override init() {
        super.init()
        let idle = DispatchSource.makeTimerSource(queue: queue)
        idle.schedule(deadline: .now() + 5, repeating: 5)
        idle.setEventHandler { [weak self] in self?.housekeeping() }
        idle.resume()
        idleTimer = idle
    }

    // Every 5 s: keep the display awake and the stream running while someone watches;
    // stop capturing once nobody has for 10 s.
    private func housekeeping() {
        if !subscribers.isEmpty {
            wakeDisplay()
            startIfNeeded()
        } else if stream != nil, Date().timeIntervalSince(lastRequest) > 10 {
            stop()
        }
    }

    // Counts as user activity (like nudging the mouse): wakes the display if it slept
    // and keeps it on. Throttled to every 15 s.
    func wakeDisplay() {
        queue.async {
            guard Date().timeIntervalSince(self.lastWake) > 15 else { return }
            self.lastWake = Date()
            var assertion: IOPMAssertionID = 0
            IOPMAssertionDeclareUserActivity("Lid Awake phone is viewing the screen" as CFString,
                                             kIOPMUserActiveLocal, &assertion)
        }
    }

    // Long-poll: calls back with a frame newer than `after`, waiting up to 2 seconds.
    func frame(after: Int, done: @escaping (Result) -> Void) {
        wakeDisplay()
        queue.async {
            self.lastRequest = Date()
            self.startIfNeeded()
            if let latest = self.latest, self.latestID > after {
                return done(.frame(latest, id: self.latestID))
            }
            self.waiters.append((after, done))
            self.queue.asyncAfter(deadline: .now() + 2) { self.expireWaiters() }
        }
    }

    // Push: `frame` is called with every new frame (and once right away with the latest).
    func subscribe(_ id: UUID, frame: @escaping (Data) -> Void, unavailable: @escaping () -> Void) {
        wakeDisplay()
        queue.async {
            self.subscribers[id] = (frame, unavailable)
            self.lastRequest = Date()
            self.startIfNeeded()
            if let latest = self.latest { frame(latest) }
        }
    }

    func unsubscribe(_ id: UUID) {
        queue.async {
            self.subscribers[id] = nil
            self.lastRequest = Date()
        }
    }

    func setPreset(_ newPreset: Preset) {
        queue.async {
            guard newPreset != self.preset else { return }
            self.preset = newPreset
            if let stream = self.stream, let display = self.display {
                stream.updateConfiguration(self.configuration(for: display)) { _ in }
            }
        }
    }

    private func configuration(for display: SCDisplay) -> SCStreamConfiguration {
        let config = SCStreamConfiguration()
        let scale = min(1, preset.maxWidth / Double(max(display.width, 1)))
        config.width = Int(Double(display.width) * scale)
        config.height = Int(Double(display.height) * scale)
        config.minimumFrameInterval = CMTime(value: 1, timescale: preset.fps)
        config.pixelFormat = kCVPixelFormatType_32BGRA
        config.showsCursor = true
        config.queueDepth = 3
        return config
    }

    private func expireWaiters() {
        let now = waiters
        waiters = []
        for waiter in now {
            if stream == nil && !starting { waiter.done(.unavailable) } else { waiter.done(.unchanged) }
        }
    }

    private func startIfNeeded() {
        guard stream == nil, !starting else { return }
        starting = true
        Task {
            do {
                let content = try await SCShareableContent.excludingDesktopWindows(false, onScreenWindowsOnly: true)
                guard let display = content.displays.first(where: { $0.displayID == CGMainDisplayID() })
                        ?? content.displays.first else { throw CocoaError(.featureUnsupported) }
                let config = queue.sync { configuration(for: display) }
                let filter = SCContentFilter(display: display, excludingWindows: [])
                let stream = SCStream(filter: filter, configuration: config, delegate: self)
                try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
                try await stream.startCapture()
                queue.async {
                    self.stream = stream
                    self.display = display
                    self.starting = false
                }
            } catch {
                // No display (e.g. lid closed with no monitor, display asleep) or no permission.
                queue.async {
                    self.starting = false
                    let now = self.waiters
                    self.waiters = []
                    now.forEach { $0.done(.unavailable) }
                    self.subscribers.values.forEach { $0.unavailable() }
                }
            }
        }
    }

    private func stop() {
        stream?.stopCapture { _ in }
        stream = nil
        display = nil
        latest = nil
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete,
              let pixels = sampleBuffer.imageBuffer,
              let jpeg = ciContext.jpegRepresentation(
                  of: CIImage(cvPixelBuffer: pixels),
                  colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                  options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: preset.quality])
        else { return }

        latest = jpeg
        latestID += 1
        let ready = waiters.filter { $0.after < latestID }
        waiters.removeAll { $0.after < latestID }
        ready.forEach { $0.done(.frame(jpeg, id: latestID)) }
        subscribers.values.forEach { $0.frame(jpeg) }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async {
            if self.stream === stream { self.stream = nil; self.display = nil }
        }
    }
}
