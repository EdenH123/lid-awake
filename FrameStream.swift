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
        // Fast is sized for slow links (mobile data at 2–3 Mbit/s): about 20 KB a frame.
        var maxWidth: Double { switch self { case .sharp: 1600; case .balanced: 1200; case .fast: 640 } }
        var quality: Double { switch self { case .sharp: 0.7; case .balanced: 0.5; case .fast: 0.35 } }
        var fps: Int32 { switch self { case .sharp: 12; case .balanced: 20; case .fast: 25 } }
        // Video (pages that can play H.264): sharper and smoother for the same bandwidth.
        // The bitrate adapts to the link below these caps.
        var videoWidth: Double { switch self { case .sharp: 1600; case .balanced: 1280; case .fast: 1024 } }
        var videoFPS: Int32 { switch self { case .sharp: 20; case .balanced: 30; case .fast: 30 } }
        var videoMaxBitrate: Int { switch self { case .sharp: 6_000_000; case .balanced: 4_000_000; case .fast: 2_500_000 } }
    }

    // A phone page that plays H.264. `send` gets the decoder setup (before the first keyframe
    // and whenever it changes) and every encoded frame, in order. `ready` says whether the
    // page has confirmed enough of what it got; the encoder skips screen frames while any
    // page is behind, so nothing queues up and no reference frame is ever dropped.
    final class VideoSink {
        enum Packet { case config(VideoEncoder.Config), chunk(VideoEncoder.Chunk) }
        let send: (Packet) -> Void
        let ready: () -> Bool
        var needsKey = true
        var config: VideoEncoder.Config?
        init(send: @escaping (Packet) -> Void, ready: @escaping () -> Bool) { self.send = send; self.ready = ready }
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
    private var wakeSuppressedSince: Date?
    private var lastInput = Date.distantPast
    private var waiters: [(after: Int, done: (Result) -> Void)] = []
    private var subscribers: [UUID: (frame: (Data) -> Void, unavailable: () -> Void)] = [:]
    private var videoSinks: [UUID: VideoSink] = [:]
    private let encoder = VideoEncoder(bitrate: 1_000_000)
    private var videoBitrate = 1_000_000
    private var lastRateChange = Date.distantPast
    private var idleTimer: DispatchSourceTimer?

    private override init() {
        super.init()
        encoder.onOutput = { [weak self] config, chunk in
            self?.queue.async { self?.distribute(config, chunk) }
        }
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
    // and keeps it on. Throttled to every 15 s. Does nothing after "Screen off" from the
    // phone, until the phone wakes it again.
    func wakeDisplay() {
        queue.async {
            guard self.wakeSuppressedSince == nil,
                  Date().timeIntervalSince(self.lastWake) > 15 else { return }
            self.lastWake = Date()
            var assertion: IOPMAssertionID = 0
            IOPMAssertionDeclareUserActivity("Lid Awake phone is viewing the screen" as CFString,
                                             kIOPMUserActiveLocal, &assertion)
        }
    }

    // "Screen off" from the phone: turn the display off and stop waking it.
    func displayOff() {
        queue.async {
            self.wakeSuppressedSince = Date()
            let p = Process()
            p.executableURL = URL(fileURLWithPath: "/usr/bin/pmset")
            p.arguments = ["displaysleepnow"]
            try? p.run()
        }
    }

    // The phone wants the screen back (Wake button or any input): wake it right away.
    func wakeNow() {
        queue.async {
            self.lastInput = Date()
            // Skip the 15 s throttle only when coming back from "Screen off".
            if self.wakeSuppressedSince != nil { self.wakeSuppressedSince = nil; self.lastWake = .distantPast }
        }
        wakeDisplay()
    }

    var isScreenOff: Bool { queue.sync { wakeSuppressedSince != nil } }

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
            self.setVideoSink(id, nil)
            self.lastRequest = Date()
        }
    }

    // A subscriber switches to video (sink) or back to JPEG frames (nil).
    func setVideo(_ id: UUID, _ sink: VideoSink?) {
        queue.async { self.setVideoSink(id, sink) }
    }

    private func setVideoSink(_ id: UUID, _ sink: VideoSink?) {
        let hadVideo = !videoSinks.isEmpty
        videoSinks[id] = sink
        if sink != nil { encoder.requestKeyframe() }
        if hadVideo != !videoSinks.isEmpty, let stream, let display {
            stream.updateConfiguration(configuration(for: display)) { _ in }
        }
        if videoSinks.isEmpty { encoder.invalidate() }
    }

    // Send → confirm time beyond the link's usual round trip, for each video frame. Rising delay
    // means frames are queuing somewhere: lower the bitrate quickly; otherwise raise it slowly.
    func videoFeedback(extraDelay: Double) {
        queue.async {
            let cap = self.preset.videoMaxBitrate
            let now = Date()
            if extraDelay > 0.12 {
                guard now.timeIntervalSince(self.lastRateChange) > 0.3 else { return }
                self.videoBitrate = max(250_000, Int(Double(self.videoBitrate) * 0.8))
            } else {
                guard now.timeIntervalSince(self.lastRateChange) > 1 else { return }
                self.videoBitrate = min(cap, Int(Double(self.videoBitrate) * 1.1))
            }
            self.lastRateChange = now
            self.encoder.setBitrate(self.videoBitrate)
        }
    }

    private func distribute(_ config: VideoEncoder.Config, _ chunk: VideoEncoder.Chunk) {
        StreamStats.shared.delivered(bytes: chunk.data.count)
        for sink in videoSinks.values {
            if sink.needsKey {
                guard chunk.key else { continue }
                sink.needsKey = false
            }
            if sink.config != config { sink.config = config; sink.send(.config(config)) }
            sink.send(.chunk(chunk))
        }
    }

    func setPreset(_ newPreset: Preset) {
        queue.async {
            guard newPreset != self.preset else { return }
            self.preset = newPreset
            self.crispShown = false
            self.videoBitrate = min(self.videoBitrate, newPreset.videoMaxBitrate)
            self.encoder.setBitrate(self.videoBitrate)
            if let stream = self.stream, let display = self.display {
                stream.updateConfiguration(self.configuration(for: display)) { _ in }
            }
        }
    }

    private func configuration(for display: SCDisplay) -> SCStreamConfiguration {
        let config = SCStreamConfiguration()
        let video = !videoSinks.isEmpty
        let scale = min(1, (video ? preset.videoWidth : preset.maxWidth) / Double(max(display.width, 1)))
        // Even sizes: the H.264 encoder needs them.
        config.width = Int(Double(display.width) * scale) / 2 * 2
        config.height = Int(Double(display.height) * scale) / 2 * 2
        config.minimumFrameInterval = CMTime(value: 1, timescale: video ? preset.videoFPS : preset.fps)
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
        encoder.invalidate()
    }

    func stream(_ stream: SCStream, didOutputSampleBuffer sampleBuffer: CMSampleBuffer, of type: SCStreamOutputType) {
        guard type == .screen,
              let attachments = CMSampleBufferGetSampleAttachmentsArray(sampleBuffer, createIfNecessary: false)
                as? [[SCStreamFrameInfo: Any]],
              let rawStatus = attachments.first?[.status] as? Int,
              SCFrameStatus(rawValue: rawStatus) == .complete,
              let pixels = sampleBuffer.imageBuffer else { return }
        let pts = CMSampleBufferGetPresentationTimeStamp(sampleBuffer)
        let lag = CMTimeGetSeconds(CMTimeSubtract(CMClockGetTime(CMClockGetHostTimeClock()), pts))
        // A frame after "Screen off" (past the last one in flight) means someone woke the
        // Mac at the keyboard, so the phone may keep it awake again.
        if let since = wakeSuppressedSince, Date().timeIntervalSince(since) > 3 { wakeSuppressedSince = nil }
        // Video pages: encode only while every page has caught up (see VideoSink).
        if !videoSinks.isEmpty, videoSinks.values.allSatisfy({ $0.ready() }) {
            encoder.encode(pixels, pts: pts)
        }
        // JPEG is only needed for pages without video and for long-polling.
        let jpegWanted = subscribers.keys.contains { videoSinks[$0] == nil } || !waiters.isEmpty
        guard jpegWanted else {
            StreamStats.shared.captured(preset: preset.rawValue + " video", lag: lag, encode: 0, diff: 0)
            return
        }
        let encodeStart = Date()
        guard let jpeg = ciContext.jpegRepresentation(
                  of: CIImage(cvPixelBuffer: pixels),
                  colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                  options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: preset.quality])
        else { return }
        let encode = Date().timeIntervalSince(encodeStart)

        let diffStart = Date()
        let changed = changedFraction(pixels)
        StreamStats.shared.captured(preset: preset.rawValue, lag: lag, encode: encode, diff: Date().timeIntervalSince(diffStart))
        handleFrame(jpeg, changed: changed)
    }

    // Share of the picture that visibly changed since the last frame, measured on a tiny
    // 96×60 thumbnail. (macOS's own "dirty rects" mark whole windows as changed when only
    // a spinner or a few letters move, so they can't tell motion from small updates.)
    private var lastThumb: [UInt8] = []
    private func changedFraction(_ pixels: CVPixelBuffer) -> Double {
        let w = 96, h = 60
        let image = CIImage(cvPixelBuffer: pixels)
        let small = image.transformed(by: CGAffineTransform(scaleX: CGFloat(w) / image.extent.width,
                                                            y: CGFloat(h) / image.extent.height))
        var thumb = [UInt8](repeating: 0, count: w * h * 4)
        ciContext.render(small, toBitmap: &thumb, rowBytes: w * 4,
                         bounds: CGRect(x: 0, y: 0, width: w, height: h), format: .RGBA8, colorSpace: nil)
        defer { lastThumb = thumb }
        guard lastThumb.count == thumb.count else { return 1 }
        var changed = 0
        for i in stride(from: 0, to: thumb.count, by: 4) {
            let d = abs(Int(thumb[i]) - Int(lastThumb[i])) + abs(Int(thumb[i + 1]) - Int(lastThumb[i + 1]))
                + abs(Int(thumb[i + 2]) - Int(lastThumb[i + 2]))
            if d > 24 { changed += 1 }
        }
        return Double(changed) / Double(w * h)
    }

    // Recent changed-area shares, reported by --selftest.
    private(set) var recentChanges: [Double] = []
    var changeReport: String { queue.sync { recentChanges.map { String(format: "%.3f", $0) }.joined(separator: " ") } }

    private func handleFrame(_ jpeg: Data, changed: Double) {
        recentChanges = Array((recentChanges + [changed]).suffix(20))
        guard preset == .fast else { return deliver(jpeg) }
        // Motion = a big part of the screen changed, or the phone is being used right now.
        let motion = changed > 0.03 || Date().timeIntervalSince(lastInput) < 1.5
        if motion { motionCount += 1 }
        if motion || !crispShown {
            crispShown = false
            deliver(jpeg)
            scheduleSharpen(restart: motion)
        } else {
            // Only a small change (cursor blink, spinner, clock) while a clear frame is shown:
            // keep it clear and refresh it soon, instead of dropping to a blurry frame.
            scheduleSharpen(restart: false)
        }
    }

    private func deliver(_ jpeg: Data) {
        StreamStats.shared.delivered(bytes: jpeg.count)
        latest = jpeg
        latestID += 1
        let ready = waiters.filter { $0.after < latestID }
        waiters.removeAll { $0.after < latestID }
        ready.forEach { $0.done(.frame(jpeg, id: latestID)) }
        for (id, subscriber) in subscribers where videoSinks[id] == nil { subscriber.frame(jpeg) }
    }

    // Fast preset: once the screen has been still for a moment, send one clearer frame
    // (about Sharp quality). While things move, frames stay small so motion stays quick.
    private var sharpenWork: DispatchWorkItem?
    private var sharpenPending = false
    private var lastSharpen = Date.distantPast
    private var crispShown = false  // the phone is showing a clear (sharpened) frame
    private var motionCount = 0     // bumps on every motion frame

    // Restart the 0.7 s "still" timer on motion; small changes only make sure one is pending.
    private func scheduleSharpen(restart: Bool) {
        guard preset == .fast else { return }
        if sharpenPending && !restart { return }
        sharpenWork?.cancel()
        sharpenPending = true
        let work = DispatchWorkItem { [weak self] in self?.sharpen() }
        sharpenWork = work
        queue.asyncAfter(deadline: .now() + 0.7, execute: work)
    }

    private func sharpen() {
        sharpenPending = false
        // At most every 1.5 s, so small ongoing changes can't flood the phone with big frames.
        guard preset == .fast, stream != nil, let display else { return }
        let wait = 1.5 - Date().timeIntervalSince(lastSharpen)
        if wait > 0 {
            sharpenPending = true
            let work = DispatchWorkItem { [weak self] in self?.sharpen() }
            sharpenWork = work
            queue.asyncAfter(deadline: .now() + wait, execute: work)
            return
        }
        lastSharpen = Date()
        let motionAtStart = motionCount
        // The clear frame must not hold up the next moving frame for long: on a slow link a big
        // one takes most of a second, and moving again would wait behind it. So pick the
        // clearest version that arrives in about 0.35 s at the link's measured speed, or none.
        let budget = LinkSpeed.bytesPerSecond.map { $0 * 0.35 } ?? .infinity
        let config = SCStreamConfiguration()
        let scale = min(1, 1280 / Double(max(display.width, 1)))
        config.width = Int(Double(display.width) * scale)
        config.height = Int(Double(display.height) * scale)
        config.showsCursor = true
        let filter = SCContentFilter(display: display, excludingWindows: [])
        Task {
            guard let image = try? await SCScreenshotManager.captureImage(contentFilter: filter, configuration: config)
            else { return }
            let full = CIImage(cgImage: image)
            var chosen: Data?
            for (width, quality) in [(1280.0, 0.6), (1024.0, 0.5), (800.0, 0.5)] {
                let s = min(1, width / full.extent.width)
                guard let jpeg = self.ciContext.jpegRepresentation(
                        of: full.transformed(by: CGAffineTransform(scaleX: s, y: s)),
                        colorSpace: CGColorSpace(name: CGColorSpace.sRGB)!,
                        options: [kCGImageDestinationLossyCompressionQuality as CIImageRepresentationOption: quality])
                else { continue }
                if Double(jpeg.count) <= budget { chosen = jpeg; break }
            }
            // Too slow for any clear frame right now: keep the moving frames (small changes still show).
            guard let jpeg = chosen else { return }
            queue.async {
                // Skip it if something moved meanwhile; the next pause sharpens again.
                guard self.motionCount == motionAtStart, self.preset == .fast, self.stream != nil else { return }
                self.deliver(jpeg)
                self.crispShown = true
            }
        }
    }

    func stream(_ stream: SCStream, didStopWithError error: Error) {
        queue.async {
            if self.stream === stream { self.stream = nil; self.display = nil }
        }
    }
}
