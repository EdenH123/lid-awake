import CoreImage
import Foundation
import IOKit.pwr_mgt
@preconcurrency import ScreenCaptureKit

// Live screen frames for the phone. While someone is watching, a ScreenCaptureKit
// stream runs and delivers a frame only when the screen changes; each phone request
// waits ("long-polls") for a frame newer than the one it already has, so updates
// arrive as soon as they happen. The stream stops when nobody has asked for a while.
final class FrameStream: NSObject, SCStreamOutput, SCStreamDelegate, @unchecked Sendable {
    static let shared = FrameStream()

    enum Result { case frame(Data, id: Int), unchanged, unavailable }

    private let queue = DispatchQueue(label: "lidawake.frames")
    private let ciContext = CIContext()
    private var stream: SCStream?
    private var starting = false
    private var latest: Data?
    private var latestID = 0
    private var lastRequest = Date.distantPast
    private var waiters: [(after: Int, done: (Result) -> Void)] = []

    private override init() {
        super.init()
        // Stop capturing when the phone stops asking (tab closed, phone locked).
        let idle = DispatchSource.makeTimerSource(queue: queue)
        idle.schedule(deadline: .now() + 5, repeating: 5)
        idle.setEventHandler { [weak self] in
            guard let self, self.stream != nil, Date().timeIntervalSince(self.lastRequest) > 10 else { return }
            self.stop()
        }
        idle.resume()
        idleTimer = idle
    }
    private var idleTimer: DispatchSourceTimer?

    private var lastWake = Date.distantPast

    // Calls back with a frame newer than `after`, waiting up to 2 seconds for one.
    func frame(after: Int, done: @escaping (Result) -> Void) {
        queue.async {
            self.lastRequest = Date()
            // Count the phone watching as user activity: wakes the display if it went to
            // sleep and keeps it on while someone is looking (like nudging the mouse).
            if Date().timeIntervalSince(self.lastWake) > 15 {
                self.lastWake = Date()
                var assertion: IOPMAssertionID = 0
                IOPMAssertionDeclareUserActivity("Lid Awake phone is viewing the screen" as CFString,
                                                 kIOPMUserActiveLocal, &assertion)
            }
            self.startIfNeeded()
            if let latest = self.latest, self.latestID > after {
                return done(.frame(latest, id: self.latestID))
            }
            self.waiters.append((after, done))
            self.queue.asyncAfter(deadline: .now() + 2) { self.expireWaiters() }
        }
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

                // About 1200px wide is plenty for a phone and keeps frames small.
                let config = SCStreamConfiguration()
                let scale = min(1, 1200 / Double(max(display.width, 1)))
                config.width = Int(Double(display.width) * scale)
                config.height = Int(Double(display.height) * scale)
                config.minimumFrameInterval = CMTime(value: 1, timescale: 12)
                config.pixelFormat = kCVPixelFormatType_32BGRA
                config.showsCursor = true
                config.queueDepth = 3

                let filter = SCContentFilter(display: display, excludingWindows: [])
                let stream = SCStream(filter: filter, configuration: config, delegate: self)
                try stream.addStreamOutput(self, type: .screen, sampleHandlerQueue: queue)
                try await stream.startCapture()
                queue.async {
                    self.stream = stream
                    self.starting = false
                }
            } catch {
                // No display (e.g. lid closed with no monitor) or no permission.
                queue.async {
                    self.starting = false
                    let now = self.waiters
                    self.waiters = []
                    now.forEach { $0.done(.unavailable) }
                }
            }
        }
    }

    private func stop() {
        stream?.stopCapture { _ in }
        stream = nil
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
                  options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: 0.5])
        else { return }

        latest = jpeg
        latestID += 1
        let ready = waiters.filter { $0.after < latestID }
        waiters.removeAll { $0.after < latestID }
        ready.forEach { $0.done(.frame(jpeg, id: latestID)) }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async {
            if self.stream === stream { self.stream = nil }
        }
    }
}
