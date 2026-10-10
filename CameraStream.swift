import AVFoundation
import CoreMedia
import Foundation

// The Mac's camera and microphone for the phone's Screen page, only while someone is watching:
// the camera (and its green light) starts with the first viewer and stops with the last, so
// nothing is captured or sent otherwise. Video is H.264 like the screen; sound is 16 kHz mono
// μ-law (one byte a sample, ~128 kbit/s), which any phone can play without a decoder.
final class CameraStream: NSObject, AVCaptureVideoDataOutputSampleBufferDelegate,
                          AVCaptureAudioDataOutputSampleBufferDelegate, @unchecked Sendable {
    static let shared = CameraStream()

    enum Packet {
        case config(VideoEncoder.Config)
        case video(VideoEncoder.Chunk)
        case audio(rate: Int, samples: Data)
        case problem(String)
    }

    private let queue = DispatchQueue(label: "local.lidawake.camera")
    private var viewers: [UUID: (Packet) -> Void] = [:]
    private var session: AVCaptureSession?
    private let encoder = VideoEncoder(bitrate: 1_200_000)
    private var config: VideoEncoder.Config?
    private var lastFrame = Date()
    private var lastEncoded = CMTime.invalid
    private var stalled = false
    private var watchdog: DispatchSourceTimer?
    private var sound = Data(), soundRate = 16000

    override init() {
        super.init()
        encoder.onOutput = { [weak self] config, chunk in
            self?.queue.async { self?.emit(config, chunk) }
        }
    }

    // Asked once from the Mac menu, so the macOS prompts appear while someone is at the Mac.
    static func requestPermissions() {
        AVCaptureDevice.requestAccess(for: .video) { _ in AVCaptureDevice.requestAccess(for: .audio) { _ in } }
    }

    func start(_ id: UUID, send: @escaping (Packet) -> Void) {
        queue.async {
            self.viewers[id] = send
            if self.session == nil { return self.begin() }
            if let config = self.config { send(.config(config)) }
            self.encoder.requestKeyframe()
        }
    }

    func stop(_ id: UUID) {
        queue.async {
            self.viewers[id] = nil
            if self.viewers.isEmpty { self.end() }
        }
    }

    // A phone's decoder (re)started: it needs a keyframe to show anything.
    func keyframe() { queue.async { self.encoder.requestKeyframe() } }

    private func broadcast(_ packet: Packet) { viewers.values.forEach { $0(packet) } }

    private func begin() {
        switch AVCaptureDevice.authorizationStatus(for: .video) {
        case .authorized:
            break
        case .notDetermined:
            broadcast(.problem("Your Mac is asking whether Lid Awake may use the camera. Tap the notch to go back to the screen, click Allow on the Mac, then tap the notch again."))
            return CameraStream.requestPermissions()
        default:
            return broadcast(.problem("Lid Awake isn't allowed to use the camera. On your Mac: System Settings → Privacy & Security → Camera → turn on Lid Awake."))
        }
        let cameras = AVCaptureDevice.DiscoverySession(deviceTypes: [.external, .builtInWideAngleCamera],
                                                       mediaType: .video, position: .unspecified).devices
        // A plugged-in camera first: unlike the built-in one, it keeps working with the lid closed.
        guard let camera = cameras.first(where: { $0.deviceType == .external && !$0.isSuspended })
                ?? cameras.first(where: { !$0.isSuspended }),
              let input = try? AVCaptureDeviceInput(device: camera) else {
            return broadcast(.problem(cameras.isEmpty ? "This Mac has no camera."
                : "The Mac's camera is off while the lid is closed. Open the lid, or plug in a USB camera."))
        }
        let s = AVCaptureSession()
        s.beginConfiguration()
        if s.canSetSessionPreset(.hd1280x720) { s.sessionPreset = .hd1280x720 }
        guard s.canAddInput(input) else { return broadcast(.problem("The Mac's camera is busy. Try again.")) }
        s.addInput(input)
        let video = AVCaptureVideoDataOutput()
        video.videoSettings = [kCVPixelBufferPixelFormatTypeKey as String: kCVPixelFormatType_420YpCbCr8BiPlanarVideoRange]
        video.alwaysDiscardsLateVideoFrames = true
        video.setSampleBufferDelegate(self, queue: queue)
        if s.canAddOutput(video) { s.addOutput(video) }
        // Sound only if allowed; the picture works without it. (With the lid closed the built-in
        // microphone is cut off by the hardware, so a closed Mac is silent unless one is plugged in.)
        if AVCaptureDevice.authorizationStatus(for: .audio) == .authorized,
           let mic = AVCaptureDevice.default(for: .audio),
           let micInput = try? AVCaptureDeviceInput(device: mic), s.canAddInput(micInput) {
            s.addInput(micInput)
            let audio = AVCaptureAudioDataOutput()
            audio.audioSettings = [AVFormatIDKey: kAudioFormatLinearPCM, AVSampleRateKey: 16000, AVNumberOfChannelsKey: 1,
                                   AVLinearPCMBitDepthKey: 16, AVLinearPCMIsFloatKey: false,
                                   AVLinearPCMIsBigEndianKey: false, AVLinearPCMIsNonInterleaved: false]
            audio.setSampleBufferDelegate(self, queue: queue)
            if s.canAddOutput(audio) { s.addOutput(audio) }
        }
        s.commitConfiguration()
        session = s
        lastFrame = Date()
        stalled = false
        s.startRunning()
        // The camera stops delivering when the lid closes (or it's unplugged): say so.
        let timer = DispatchSource.makeTimerSource(queue: queue)
        timer.schedule(deadline: .now() + 4, repeating: 2)
        timer.setEventHandler { [weak self] in
            guard let self, self.session != nil else { return }
            if !self.stalled && Date().timeIntervalSince(self.lastFrame) > 4 {
                self.stalled = true
                self.broadcast(.problem("The camera stopped sending. If the Mac's lid is closed, open it or plug in a USB camera."))
            }
        }
        timer.resume()
        watchdog = timer
    }

    private func end() {
        watchdog?.cancel()
        watchdog = nil
        session?.stopRunning()
        session = nil
        encoder.invalidate()
        config = nil
        lastEncoded = .invalid
        sound.removeAll()
    }

    private func emit(_ config: VideoEncoder.Config, _ chunk: VideoEncoder.Chunk) {
        guard session != nil else { return }
        if config != self.config {
            self.config = config
            broadcast(.config(config))
        }
        broadcast(.video(chunk))
    }

    func captureOutput(_ output: AVCaptureOutput, didOutput sample: CMSampleBuffer, from connection: AVCaptureConnection) {
        guard session != nil else { return }
        if output is AVCaptureVideoDataOutput {
            guard let pixels = CMSampleBufferGetImageBuffer(sample) else { return }
            let pts = CMSampleBufferGetPresentationTimeStamp(sample)
            lastFrame = Date()
            if stalled { stalled = false; broadcast(.problem("")) }
            // At most ~24 frames a second: plenty for a camera, and lighter on the link.
            if lastEncoded.isValid && CMTimeGetSeconds(CMTimeSubtract(pts, lastEncoded)) < 0.04 { return }
            lastEncoded = pts
            encoder.encode(pixels, pts: pts)
        } else {
            addSound(sample)
        }
    }

    // Collects ~100 ms of sound per message (fewer, bigger messages than the capture's ~20 ms).
    private func addSound(_ sample: CMSampleBuffer) {
        guard let format = CMSampleBufferGetFormatDescription(sample),
              let asbd = CMAudioFormatDescriptionGetStreamBasicDescription(format)?.pointee,
              let block = CMSampleBufferGetDataBuffer(sample) else { return }
        let length = CMBlockBufferGetDataLength(block)
        var raw = Data(count: length)
        guard raw.withUnsafeMutableBytes({ CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: length, destination: $0.baseAddress!) }) == noErr else { return }
        let channels = max(1, Int(asbd.mChannelsPerFrame))
        let isFloat = asbd.mFormatFlags & kAudioFormatFlagIsFloat != 0
        let rate = Int(asbd.mSampleRate)
        if rate != soundRate { sound.removeAll(); soundRate = rate }
        // First channel only, as μ-law bytes (whatever format the microphone actually delivered).
        raw.withUnsafeBytes { bytes in
            if isFloat && asbd.mBitsPerChannel == 32 {
                let floats = bytes.bindMemory(to: Float32.self)
                stride(from: 0, to: floats.count, by: channels).forEach {
                    sound.append(Self.mulaw(Int16(max(-1, min(1, floats[$0])) * 32767)))
                }
            } else if asbd.mBitsPerChannel == 16 {
                let ints = bytes.bindMemory(to: Int16.self)
                stride(from: 0, to: ints.count, by: channels).forEach { sound.append(Self.mulaw(ints[$0])) }
            }
        }
        if sound.count >= soundRate / 10 {
            broadcast(.audio(rate: soundRate, samples: sound))
            sound.removeAll(keepingCapacity: true)
        }
    }

    // G.711 μ-law: 16-bit sample → 1 byte.
    static func mulaw(_ value: Int16) -> UInt8 {
        var sample = Int(value)
        let sign = sample < 0 ? 0x80 : 0
        if sign != 0 { sample = -sample }
        sample = min(sample, 32635) + 0x84
        var exponent = 7, mask = 0x4000
        while exponent > 0 && sample & mask == 0 { exponent -= 1; mask >>= 1 }
        let mantissa = (sample >> (exponent + 3)) & 0x0F
        return UInt8(~(sign | (exponent << 4) | mantissa) & 0xFF)
    }
}

// Face ID every time the camera opens: a good signature buys a one-time ticket, valid one
// minute and only for the signed-in session that asked, which the live socket then redeems.
enum CameraTickets {
    private static let lock = NSLock()
    private static var tickets: [String: (token: String, expires: Date)] = [:]

    static func issue(for token: String) -> String {
        lock.lock(); defer { lock.unlock() }
        let now = Date()
        tickets = tickets.filter { $0.value.expires > now }
        var bytes = [UInt8](repeating: 0, count: 32)
        _ = SecRandomCopyBytes(kSecRandomDefault, bytes.count, &bytes)
        let ticket = Passkeys.base64url(Data(bytes))
        if tickets.count < 16 { tickets[ticket] = (token, now.addingTimeInterval(60)) }
        return ticket
    }

    static func redeem(_ ticket: String, token: String?) -> Bool {
        lock.lock(); defer { lock.unlock() }
        guard let token, let issued = tickets.removeValue(forKey: ticket) else { return false }
        return issued.token == token && issued.expires > Date()
    }
}
