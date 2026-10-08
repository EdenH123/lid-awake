import CoreMedia
import Foundation
import VideoToolbox

// Hardware H.264 for the phone's live screen. Moving frames only carry what changed, so
// they are far smaller than full JPEGs: on a 2 Mbit/s phone link that is the difference
// between ~5 and ~25 frames a second. Tuned for delay, not file size: no B-frames, real-time
// rate control, and keyframes only when a phone asks (joining, or after a decoder error).
final class VideoEncoder {
    struct Config: Equatable {
        let codec: String        // e.g. "avc1.4d0028", for WebCodecs
        let description: Data    // avcC record (SPS/PPS) for VideoDecoder.configure
        let width: Int, height: Int
    }
    struct Chunk {
        let data: Data           // length-prefixed NAL units, as in avcC streams
        let key: Bool
        let timestamp: Int64     // microseconds
    }

    // Called on the encoder's own thread; the owner hops to its queue.
    var onOutput: ((Config, Chunk) -> Void)?

    private var session: VTCompressionSession?
    private var width = 0, height = 0
    private var forceKey = true
    private(set) var bitrate: Int

    init(bitrate: Int) { self.bitrate = bitrate }

    deinit { invalidate() }

    func requestKeyframe() { forceKey = true }

    func setBitrate(_ bps: Int) {
        bitrate = bps
        guard let session else { return }
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_AverageBitRate, value: bps as CFNumber)
        // Hard ceiling per second, so a burst (a big change on screen) can't flood the link.
        let limit = [Double(bps) * 1.5 / 8, 1.0] as CFArray
        VTSessionSetProperty(session, key: kVTCompressionPropertyKey_DataRateLimits, value: limit)
    }

    func invalidate() {
        if let session { VTCompressionSessionInvalidate(session) }
        session = nil
    }

    func encode(_ pixels: CVPixelBuffer, pts: CMTime) {
        let w = CVPixelBufferGetWidth(pixels), h = CVPixelBufferGetHeight(pixels)
        if session == nil || w != width || h != height {
            invalidate()
            guard makeSession(width: w, height: h) else { return }
        }
        guard let session else { return }
        var props: CFDictionary?
        if forceKey {
            forceKey = false
            props = [kVTEncodeFrameOptionKey_ForceKeyFrame: true] as CFDictionary
        }
        VTCompressionSessionEncodeFrame(session, imageBuffer: pixels, presentationTimeStamp: pts,
                                        duration: .invalid, frameProperties: props, infoFlagsOut: nil) {
            [weak self] status, _, sample in
            guard status == noErr, let sample, let self else { return }
            self.emit(sample)
        }
    }

    private func makeSession(width w: Int, height h: Int) -> Bool {
        let spec = [kVTVideoEncoderSpecification_EnableLowLatencyRateControl: true] as CFDictionary
        var made: VTCompressionSession?
        guard VTCompressionSessionCreate(allocator: nil, width: Int32(w), height: Int32(h),
                                         codecType: kCMVideoCodecType_H264, encoderSpecification: spec,
                                         imageBufferAttributes: nil, compressedDataAllocator: nil,
                                         outputCallback: nil, refcon: nil, compressionSessionOut: &made) == noErr,
              let made else { return false }
        let set = { (key: CFString, value: CFTypeRef) in _ = VTSessionSetProperty(made, key: key, value: value) }
        set(kVTCompressionPropertyKey_RealTime, kCFBooleanTrue)
        set(kVTCompressionPropertyKey_AllowFrameReordering, kCFBooleanFalse)
        set(kVTCompressionPropertyKey_ProfileLevel, kVTProfileLevel_H264_Main_AutoLevel)
        set(kVTCompressionPropertyKey_ExpectedFrameRate, 30 as CFNumber)
        set(kVTCompressionPropertyKey_MaxKeyFrameInterval, 100_000 as CFNumber)
        set(kVTCompressionPropertyKey_PrioritizeEncodingSpeedOverQuality, kCFBooleanTrue)
        VTCompressionSessionPrepareToEncodeFrames(made)
        session = made
        width = w; height = h
        forceKey = true
        setBitrate(bitrate)
        return true
    }

    private func emit(_ sample: CMSampleBuffer) {
        guard let format = CMSampleBufferGetFormatDescription(sample),
              let atoms = CMFormatDescriptionGetExtension(format,
                    extensionKey: kCMFormatDescriptionExtension_SampleDescriptionExtensionAtoms) as? [String: Any],
              let avcC = atoms["avcC"] as? Data, avcC.count > 4,
              let block = CMSampleBufferGetDataBuffer(sample) else { return }
        var data = Data(count: CMBlockBufferGetDataLength(block))
        let copied = data.withUnsafeMutableBytes {
            CMBlockBufferCopyDataBytes(block, atOffset: 0, dataLength: $0.count, destination: $0.baseAddress!)
        }
        guard copied == noErr else { return }
        let attachments = CMSampleBufferGetSampleAttachmentsArray(sample, createIfNecessary: false) as? [[CFString: Any]]
        let notSync = attachments?.first?[kCMSampleAttachmentKey_NotSync] as? Bool ?? false
        let pts = CMSampleBufferGetPresentationTimeStamp(sample)
        let codec = String(format: "avc1.%02x%02x%02x", avcC[1], avcC[2], avcC[3])
        let dims = CMVideoFormatDescriptionGetDimensions(format)
        onOutput?(Config(codec: codec, description: avcC, width: Int(dims.width), height: Int(dims.height)),
                  Chunk(data: data, key: !notSync, timestamp: Int64(CMTimeGetSeconds(pts) * 1_000_000)))
    }
}
