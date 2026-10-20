using System.Diagnostics;
using System.Runtime.InteropServices;

namespace LidAwake;

// An H.264 encoder for the live screen (VideoEncoder.swift on the Mac). Encode takes one
// picture and returns what the encoder produced for it: usually that picture, in AVCC form, with
// the decoder setup it needs. Used from one thread at a time.
public interface IVideoEncoder : IDisposable
{
    string Name { get; }
    int Width { get; }
    int Height { get; }
    int Fps { get; }
    IReadOnlyList<(VideoConfig Config, VideoChunk Chunk)> Encode(ScreenImage image, long timestamp, bool forceKey);
    void SetBitrate(int bitsPerSecond);
}

// Makes an encoder for pictures of this size, or throws with the reason there is none.
public delegate IVideoEncoder VideoEncoderFactory(int width, int height, int fps, int bitrate);

// BGRA → NV12 (the encoder's input): a full-size luma plane, then blue and red difference
// interleaved at half size, averaged over each 2×2 block. BT.709 coefficients, video range
// (16 to 235), which is what the encoder is told and what the phones assume for HD.
public static unsafe class Nv12
{
    public static int Size(int width, int height) => width * height * 3 / 2;

    public static void FromBgra(ScreenImage image, byte* destination)
    {
        int w = image.Width & ~1, h = image.Height & ~1;
        byte* src = (byte*)image.Pixels;
        byte* yPlane = destination;
        byte* uvPlane = destination + image.Width * image.Height;
        for (int y = 0; y < image.Height; y++)
        {
            byte* row = src + (long)y * image.Stride;
            byte* yRow = yPlane + (long)y * image.Width;
            for (int x = 0; x < image.Width; x++)
            {
                int b = row[x * 4], g = row[x * 4 + 1], r = row[x * 4 + 2];
                yRow[x] = (byte)(((47 * r + 157 * g + 16 * b + 128) >> 8) + 16);
            }
        }
        for (int y = 0; y < h / 2; y++)
        {
            byte* r0 = src + (long)(2 * y) * image.Stride;
            byte* r1 = r0 + image.Stride;
            byte* uvRow = uvPlane + (long)y * image.Width;
            for (int x = 0; x < w / 2; x++)
            {
                int i = x * 8;
                int b = r0[i] + r0[i + 4] + r1[i] + r1[i + 4];
                int g = r0[i + 1] + r0[i + 5] + r1[i + 1] + r1[i + 5];
                int r = r0[i + 2] + r0[i + 6] + r1[i + 2] + r1[i + 6];
                b = (b + 2) >> 2; g = (g + 2) >> 2; r = (r + 2) >> 2;
                uvRow[2 * x] = Clamp(((-26 * r - 86 * g + 112 * b + 128) >> 8) + 128);
                uvRow[2 * x + 1] = Clamp(((112 * r - 102 * g - 10 * b + 128) >> 8) + 128);
            }
        }
    }

    static byte Clamp(int v) => (byte)(v < 0 ? 0 : v > 255 ? 255 : v);
}

// The Media Foundation H.264 encoder (an MFT), called through its COM interfaces. A hardware
// encoder (the graphics card's, found with MFTEnumEx and MFT_ENUM_FLAG_HARDWARE) is tried first;
// those are asynchronous: they say by event when they want a picture and when one is ready. If it
// can't be set up, or fails while encoding, Microsoft's synchronous software encoder is used from
// then on. Tuned like the Mac: low latency, constant bit rate, no B-frames, keyframes only when a
// phone needs one (forced with CODECAPI_AVEncVideoForceKeyFrame).
public sealed unsafe class MfH264Encoder : IVideoEncoder
{
    // mfapi.h, mftransform.h, codecapi.h, mferror.h
    static readonly Guid MFT_CATEGORY_VIDEO_ENCODER = new("f79eac7d-e545-4387-bdee-d647d7bde42a");
    static readonly Guid MFMediaType_Video = new("73646976-0000-0010-8000-00aa00389b71");
    static readonly Guid MFVideoFormat_H264 = new("34363248-0000-0010-8000-00aa00389b71");
    static readonly Guid MFVideoFormat_NV12 = new("3231564e-0000-0010-8000-00aa00389b71");
    static readonly Guid MF_MT_MAJOR_TYPE = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
    static readonly Guid MF_MT_SUBTYPE = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
    static readonly Guid MF_MT_FRAME_SIZE = new("1652c33d-d6b2-4012-b834-72030849a37d");
    static readonly Guid MF_MT_FRAME_RATE = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");
    static readonly Guid MF_MT_AVG_BITRATE = new("20332624-fb0d-4d9e-bd0d-cbf6786c102e");
    static readonly Guid MF_MT_INTERLACE_MODE = new("e2724bb8-e676-4806-b4b2-a8d6efb44ccd");
    static readonly Guid MF_MT_MPEG2_PROFILE = new("ad76a80b-2d5c-4e0b-b375-64e520137036");
    static readonly Guid MF_MT_PIXEL_ASPECT_RATIO = new("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6");
    static readonly Guid MF_MT_YUV_MATRIX = new("3e23d450-2c75-4d25-a00e-b91670d12327");
    static readonly Guid MF_MT_VIDEO_NOMINAL_RANGE = new("c21b8ee5-b956-4071-8daf-325edf5cab11");
    static readonly Guid MF_MT_MPEG_SEQUENCE_HEADER = new("3c036de7-3ad0-4c9e-9216-ee6d6ac21cb3");
    static readonly Guid MF_TRANSFORM_ASYNC = new("f81a699a-649a-497d-8c73-29f8fed6ad7a");
    static readonly Guid MF_TRANSFORM_ASYNC_UNLOCK = new("e5666d6b-3422-4eb6-a421-da7db1f8e207");
    static readonly Guid MFT_FRIENDLY_NAME_Attribute = new("314ffbae-5b41-4c95-9c19-4e7d586face3");
    static readonly Guid IID_IMFTransform = new("bf94c121-5b05-4e6f-8000-ba598961414d");
    static readonly Guid IID_IMFMediaEventGenerator = new("2cd0bd52-bcd5-4b89-b62c-eadc0c031e7d");
    static readonly Guid IID_ICodecAPI = new("901db4c7-31ce-41a2-85dc-8fa0bf41b8da");
    static readonly Guid CODECAPI_AVLowLatencyMode = new("9c27891a-ed7a-40e1-88e8-b22727a024ee");
    static readonly Guid CODECAPI_AVEncCommonRateControlMode = new("1c0608e9-370c-4710-8a58-cb6181c42423");
    static readonly Guid CODECAPI_AVEncCommonMeanBitRate = new("f7222374-2144-4815-b550-a37f8e12ee52");
    static readonly Guid CODECAPI_AVEncMPVDefaultBPictureCount = new("8d390aac-dc5c-4200-b57f-814d04babab2");
    static readonly Guid CODECAPI_AVEncVideoForceKeyFrame = new("398c1b98-8353-475a-9ef2-8f265d260345");
    static readonly Guid CODECAPI_AVEncMPVGOPSize = new("95f31b26-95a4-41aa-9303-246a7fc6eef1");
    static readonly Guid CODECAPI_AVEncVideoMaxKeyframeDistance = new("2987123a-ba93-4704-b489-ec1e5f25292c");

    const uint MF_VERSION = 0x00020070;
    const uint MFT_ENUM_FLAG_SYNCMFT = 0x1, MFT_ENUM_FLAG_HARDWARE = 0x4, MFT_ENUM_FLAG_LOCALMFT = 0x10, MFT_ENUM_FLAG_SORTANDFILTER = 0x40;
    const int MFT_MESSAGE_NOTIFY_BEGIN_STREAMING = 0x10000000, MFT_MESSAGE_NOTIFY_END_STREAMING = 0x10000001,
              MFT_MESSAGE_NOTIFY_START_OF_STREAM = 0x10000003;
    const uint MFT_OUTPUT_STREAM_PROVIDES_SAMPLES = 0x100, MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES = 0x200;
    const int MF_E_TRANSFORM_NEED_MORE_INPUT = unchecked((int)0xC00D6D72);
    const int MF_E_TRANSFORM_STREAM_CHANGE = unchecked((int)0xC00D6D61);
    const int MF_E_NOTACCEPTING = unchecked((int)0xC00D36B5);
    const int MF_E_NO_EVENTS_AVAILABLE = unchecked((int)0xC00D3E80);
    const uint METransformNeedInput = 601, METransformHaveOutput = 602, MEError = 1;
    const ushort VT_BOOL = 11, VT_UI4 = 19;
    const uint eAVEncH264VProfile_Main = 77;
    const uint MFVideoInterlace_Progressive = 2, MFVideoTransferMatrix_BT709 = 1, MFNominalRange_16_235 = 2;

    [StructLayout(LayoutKind.Sequential)]
    struct RegisterTypeInfo { public Guid Major, Sub; }

    [StructLayout(LayoutKind.Sequential)]
    struct OutputStreamInfo { public uint Flags, Size, Alignment; }

    [StructLayout(LayoutKind.Sequential)]
    struct OutputDataBuffer
    {
        public uint StreamId;
        public IntPtr Sample;
        public uint Status;
        public IntPtr Events;
    }

    // A VARIANT (24 bytes on 64-bit Windows), only as VT_UI4 or VT_BOOL.
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    struct Variant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public uint UInt;
        [FieldOffset(8)] public short Bool;
    }

    [DllImport("mfplat.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int MFStartup(uint version, uint flags);
    [DllImport("mfplat.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int MFTEnumEx(Guid category, uint flags, RegisterTypeInfo* input, RegisterTypeInfo* output, IntPtr* activates, uint* count);
    [DllImport("mfplat.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int MFCreateMediaType(IntPtr* type);
    [DllImport("mfplat.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int MFCreateSample(IntPtr* sample);
    [DllImport("mfplat.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int MFCreateMemoryBuffer(uint maxLength, IntPtr* buffer);
    [DllImport("ole32.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    // Started once for the whole app; the reason when Media Foundation isn't there.
    static readonly object startGate = new();
    static bool started;
    static string? startProblem;
    // A hardware encoder failed: the software one is used from then on.
    static volatile bool hardwareFailed;

    readonly bool hardware;
    readonly bool isAsync;
    IntPtr activate, transform, codecApi, events;
    readonly bool providesSamples;
    readonly uint outputSize;
    int needInput;            // asynchronous: pictures the encoder has asked for
    byte[]? sps, pps;
    VideoConfig? config;
    readonly long frameDuration;

    public string Name { get; }
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public bool Hardware => hardware;
    public bool Asynchronous => isAsync;

    // Whether a hardware encoder has failed in this run (then only software is tried).
    public static bool HardwareFailed => hardwareFailed;

    // A factory for ScreenFeed. `allowHardware` false: only the software encoder.
    public static VideoEncoderFactory Factory(bool allowHardware = true) =>
        (width, height, fps, bitrate) => Create(width, height, fps, bitrate, allowHardware);

    // The first encoder that can be set up: hardware first (unless one already failed), then
    // software. Throws with the reasons when none can.
    public static MfH264Encoder Create(int width, int height, int fps, int bitrate, bool allowHardware = true)
    {
        Start();
        var problems = new List<string>();
        if (allowHardware && !hardwareFailed)
        {
            if (TryEach(MFT_ENUM_FLAG_HARDWARE | MFT_ENUM_FLAG_SORTANDFILTER, true, width, height, fps, bitrate, problems) is { } hw) return hw;
        }
        if (TryEach(MFT_ENUM_FLAG_SYNCMFT | MFT_ENUM_FLAG_LOCALMFT | MFT_ENUM_FLAG_SORTANDFILTER, false, width, height, fps, bitrate, problems) is { } sw) return sw;
        throw new InvalidOperationException(problems.Count == 0 ? "No H.264 encoder on this PC." : string.Join("; ", problems));
    }

    static void Start()
    {
        lock (startGate)
        {
            if (started) return;
            if (startProblem != null) throw new InvalidOperationException(startProblem);
            try
            {
                Com.Check(MFStartup(MF_VERSION, 0), "MFStartup");
                started = true;
            }
            catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException or COMException)
            {
                startProblem = "Media Foundation isn't available: " + e.Message;
                throw new InvalidOperationException(startProblem);
            }
        }
    }

    static MfH264Encoder? TryEach(uint flags, bool hardware, int width, int height, int fps, int bitrate, List<string> problems)
    {
        // The thread must have COM; a thread that has it already keeps it as it is.
        CoInitializeEx(IntPtr.Zero, 0);  // COINIT_MULTITHREADED
        var input = new RegisterTypeInfo { Major = MFMediaType_Video, Sub = MFVideoFormat_NV12 };
        var output = new RegisterTypeInfo { Major = MFMediaType_Video, Sub = MFVideoFormat_H264 };
        IntPtr list;
        uint count;
        int hr = MFTEnumEx(MFT_CATEGORY_VIDEO_ENCODER, flags, &input, &output, &list, &count);
        if (hr < 0) { problems.Add($"MFTEnumEx failed (0x{hr:X8})"); return null; }
        if (count == 0 || list == IntPtr.Zero)
        {
            if (list != IntPtr.Zero) Marshal.FreeCoTaskMem(list);
            problems.Add(hardware ? "no hardware H.264 encoder" : "no software H.264 encoder");
            return null;
        }
        var activates = (IntPtr*)list;
        MfH264Encoder? chosen = null;
        try
        {
            for (int i = 0; i < count; i++)
            {
                if (chosen != null) break;
                var name = FriendlyName(activates[i]) ?? (hardware ? "hardware H.264 encoder" : "H.264 encoder");
                try
                {
                    chosen = new MfH264Encoder(activates[i], name, hardware, width, height, fps, bitrate);
                    activates[i] = IntPtr.Zero;  // the encoder keeps it
                }
                catch (Exception e) { problems.Add($"{name}: {e.Message}"); }
            }
        }
        finally
        {
            for (int i = 0; i < count; i++) Com.Release(ref activates[i]);
            Marshal.FreeCoTaskMem(list);
        }
        return chosen;
    }

    MfH264Encoder(IntPtr activate, string name, bool hardware, int width, int height, int fps, int bitrate)
    {
        Name = name;
        this.hardware = hardware;
        Width = width;
        Height = height;
        Fps = Math.Max(1, fps);
        frameDuration = 10_000_000L / Fps;
        try
        {
            // IMFActivate::ActivateObject
            var iid = IID_IMFTransform;
            IntPtr t;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Com.Slot(activate, 33))(activate, &iid, &t), "ActivateObject");
            transform = t;

            // An asynchronous (hardware) encoder must be unlocked before use, and talks by events.
            IntPtr attributes;
            // IMFTransform::GetAttributes
            if (((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Slot(transform, 8))(transform, &attributes) >= 0 && attributes != IntPtr.Zero)
            {
                try
                {
                    isAsync = GetUInt32(attributes, MF_TRANSFORM_ASYNC) == 1;
                    if (isAsync) SetUInt32(attributes, MF_TRANSFORM_ASYNC_UNLOCK, 1);
                }
                finally { Com.Release(ref attributes); }
            }
            if (isAsync)
            {
                var eiid = IID_IMFMediaEventGenerator;
                IntPtr e;
                Com.Check(Com.QueryInterface(transform, &eiid, &e), "IMFMediaEventGenerator");
                events = e;
            }
            var ciid = IID_ICodecAPI;
            IntPtr api;
            if (Com.QueryInterface(transform, &ciid, &api) >= 0) codecApi = api;

            Tune(bitrate);
            SetOutputType(bitrate);
            SetInputType();
            Tune(bitrate);  // some encoders only take these once the types are set

            OutputStreamInfo info;
            // IMFTransform::GetOutputStreamInfo
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, OutputStreamInfo*, int>)Com.Slot(transform, 7))(transform, 0, &info), "GetOutputStreamInfo");
            providesSamples = (info.Flags & (MFT_OUTPUT_STREAM_PROVIDES_SAMPLES | MFT_OUTPUT_STREAM_CAN_PROVIDE_SAMPLES)) != 0;
            outputSize = Math.Max(info.Size, (uint)(width * height * 2));

            Message(MFT_MESSAGE_NOTIFY_BEGIN_STREAMING);
            Message(MFT_MESSAGE_NOTIFY_START_OF_STREAM);
        }
        catch
        {
            // The caller still holds (and releases) the activation object; let go of the encoder.
            bool activated = transform != IntPtr.Zero;
            Dispose();
            if (activated) Shutdown(activate);
            throw;
        }
        this.activate = activate;  // kept, and shut down by Dispose
    }

    // Low latency, constant bit rate, no B-frames. An encoder that doesn't know a setting keeps
    // its default.
    void Tune(int bitrate)
    {
        SetCodecValue(CODECAPI_AVLowLatencyMode, new Variant { Type = VT_BOOL, Bool = -1 });
        SetCodecValue(CODECAPI_AVEncCommonRateControlMode, new Variant { Type = VT_UI4, UInt = 0 });  // CBR
        SetCodecValue(CODECAPI_AVEncCommonMeanBitRate, new Variant { Type = VT_UI4, UInt = (uint)bitrate });
        SetCodecValue(CODECAPI_AVEncMPVDefaultBPictureCount, new Variant { Type = VT_UI4, UInt = 0 });
        // Keyframes only on request, like the Mac's MaxKeyFrameInterval 100 000: a very long GOP
        // (0 means "the encoder's default" on some MFTs, so an explicit number), the largest one
        // the encoder takes. The SDK has no IDR-interval setting; the keyframe distance is the
        // closest, set the same way where it exists.
        foreach (var gop in LongGops)
            if (SetCodecValue(CODECAPI_AVEncMPVGOPSize, new Variant { Type = VT_UI4, UInt = gop })) break;
        foreach (var gop in LongGops)
            if (SetCodecValue(CODECAPI_AVEncVideoMaxKeyframeDistance, new Variant { Type = VT_UI4, UInt = gop })) break;
    }

    static readonly uint[] LongGops = { 1_000_000, 100_000, 65_535, 30_000 };

    // Output before input, as Media Foundation's encoders want it.
    void SetOutputType(int bitrate)
    {
        var type = NewType(MFVideoFormat_H264);
        try
        {
            SetUInt32(type, MF_MT_AVG_BITRATE, (uint)bitrate);
            SetUInt32(type, MF_MT_MPEG2_PROFILE, eAVEncH264VProfile_Main);
            // IMFTransform::SetOutputType
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Slot(transform, 16))(transform, 0, type, 0), "SetOutputType");
        }
        finally { Com.Release(ref type); }
    }

    void SetInputType()
    {
        // With the colour space first; an encoder that refuses it gets the plain type.
        foreach (var withColour in new[] { true, false })
        {
            var type = NewType(MFVideoFormat_NV12);
            try
            {
                if (withColour)
                {
                    SetUInt32(type, MF_MT_YUV_MATRIX, MFVideoTransferMatrix_BT709);
                    SetUInt32(type, MF_MT_VIDEO_NOMINAL_RANGE, MFNominalRange_16_235);
                }
                // IMFTransform::SetInputType
                int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Slot(transform, 15))(transform, 0, type, 0);
                if (hr >= 0) return;
                if (!withColour) Com.Check(hr, "SetInputType");
            }
            finally { Com.Release(ref type); }
        }
    }

    IntPtr NewType(Guid subtype)
    {
        IntPtr type;
        Com.Check(MFCreateMediaType(&type), "MFCreateMediaType");
        try
        {
            SetGuid(type, MF_MT_MAJOR_TYPE, MFMediaType_Video);
            SetGuid(type, MF_MT_SUBTYPE, subtype);
            SetUInt64(type, MF_MT_FRAME_SIZE, ((ulong)(uint)Width << 32) | (uint)Height);
            SetUInt64(type, MF_MT_FRAME_RATE, ((ulong)(uint)Fps << 32) | 1);
            SetUInt64(type, MF_MT_PIXEL_ASPECT_RATIO, (1UL << 32) | 1);
            SetUInt32(type, MF_MT_INTERLACE_MODE, MFVideoInterlace_Progressive);
            return type;
        }
        catch
        {
            Com.Release(ref type);
            throw;
        }
    }

    public void SetBitrate(int bitsPerSecond) =>
        SetCodecValue(CODECAPI_AVEncCommonMeanBitRate, new Variant { Type = VT_UI4, UInt = (uint)Math.Max(1, bitsPerSecond) });

    public IReadOnlyList<(VideoConfig Config, VideoChunk Chunk)> Encode(ScreenImage image, long timestamp, bool forceKey)
    {
        if (image.Width != Width || image.Height != Height) throw new ArgumentException("The picture's size changed.", nameof(image));
        var produced = new List<(VideoConfig, VideoChunk)>();
        try
        {
            if (forceKey) SetCodecValue(CODECAPI_AVEncVideoForceKeyFrame, new Variant { Type = VT_UI4, UInt = 1 });
            var sample = MakeSample(image, timestamp);
            try
            {
                if (isAsync) FeedAsync(sample, produced);
                else FeedSync(sample, produced);
            }
            finally { Com.Release(ref sample); }
        }
        catch (Exception) when (hardware)
        {
            hardwareFailed = true;
            throw;
        }
        return produced;
    }

    void FeedSync(IntPtr sample, List<(VideoConfig, VideoChunk)> produced)
    {
        int hr = ProcessInput(sample);
        if (hr == MF_E_NOTACCEPTING)
        {
            DrainSync(produced);
            hr = ProcessInput(sample);
        }
        Com.Check(hr, "ProcessInput");
        DrainSync(produced);
    }

    void DrainSync(List<(VideoConfig, VideoChunk)> produced)
    {
        for (int guard = 0; guard < 64; guard++)
            if (ProcessOneOutput(produced) == Output.NeedsInput) return;
    }

    enum Output { NeedsInput, Taken, FormatChanged }

    // Asynchronous: the picture goes in once the encoder asks for one (METransformNeedInput);
    // whatever is ready meanwhile is taken out (METransformHaveOutput). Then a short wait for this
    // picture's output, so the phone isn't a frame behind.
    void FeedAsync(IntPtr sample, List<(VideoConfig, VideoChunk)> produced)
    {
        var clock = Stopwatch.StartNew();
        bool fed = false;
        long deadline = 1000;
        int before = produced.Count;
        var spin = new SpinWait();
        while (true)
        {
            while (NextEvent() is { } type)
            {
                if (type == METransformNeedInput) needInput++;
                else if (type == METransformHaveOutput)
                {
                    // After a format change the same output is still waiting: take it now.
                    for (int tries = 0; tries < 4 && ProcessOneOutput(produced) == Output.FormatChanged; tries++) { }
                }
            }
            if (!fed && needInput > 0)
            {
                Com.Check(ProcessInput(sample), "ProcessInput");
                needInput--;
                fed = true;
                deadline = clock.ElapsedMilliseconds + 60;
            }
            if (fed && produced.Count > before) return;
            if (clock.ElapsedMilliseconds > deadline)
            {
                if (!fed) throw new TimeoutException("The hardware encoder didn't ask for a picture.");
                return;  // its output comes with a later picture
            }
            spin.SpinOnce();
        }
    }

    // The next event of an asynchronous encoder, without waiting; null when there is none.
    uint? NextEvent()
    {
        IntPtr e;
        // IMFMediaEventGenerator::GetEvent (MF_EVENT_FLAG_NO_WAIT)
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(events, 3))(events, 1, &e);
        if (hr == MF_E_NO_EVENTS_AVAILABLE) return null;
        Com.Check(hr, "GetEvent");
        try
        {
            uint type;
            int status;
            // IMFMediaEvent::GetType and GetStatus
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint*, int>)Com.Slot(e, 33))(e, &type), "GetType");
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int*, int>)Com.Slot(e, 35))(e, &status), "GetStatus");
            if (type == MEError || status < 0) throw new COMException($"The hardware encoder reported an error (0x{status:X8}).", status);
            return type;
        }
        finally { Com.Release(ref e); }
    }

    int ProcessInput(IntPtr sample) =>
        // IMFTransform::ProcessInput
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Slot(transform, 24))(transform, 0, sample, 0);

    // One ProcessOutput: a picture taken (or nothing to take yet), or the output format changed
    // (the output is still there for the next call).
    Output ProcessOneOutput(List<(VideoConfig, VideoChunk)> produced)
    {
        IntPtr ours = IntPtr.Zero;
        if (!providesSamples) ours = MakeOutputSample();
        var buffer = new OutputDataBuffer { StreamId = 0, Sample = ours };
        uint status;
        // IMFTransform::ProcessOutput
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, OutputDataBuffer*, uint*, int>)Com.Slot(transform, 25))(transform, 0, 1, &buffer, &status);
        if (buffer.Events != IntPtr.Zero) Com.Release(ref buffer.Events);
        var got = buffer.Sample;
        try
        {
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT) return Output.NeedsInput;
            if (hr == MF_E_TRANSFORM_STREAM_CHANGE)
            {
                // The encoder settled its output format (with SPS and PPS): take its own type.
                ResetOutputType();
                return Output.FormatChanged;
            }
            Com.Check(hr, "ProcessOutput");
            if (got != IntPtr.Zero) Read(got, produced);
            return Output.Taken;
        }
        finally
        {
            if (got != ours) Com.Release(ref got);
            Com.Release(ref ours);
        }
    }

    void ResetOutputType()
    {
        IntPtr type;
        // IMFTransform::GetOutputAvailableType, then SetOutputType
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Com.Slot(transform, 14))(transform, 0, 0, &type), "GetOutputAvailableType");
        try { Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, uint, int>)Com.Slot(transform, 16))(transform, 0, type, 0), "SetOutputType"); }
        finally { Com.Release(ref type); }
    }

    // One encoded picture: Annex B → AVCC, with the decoder setup from its SPS and PPS (or, for an
    // encoder that keeps them out of the stream, from its output type's sequence header).
    void Read(IntPtr sample, List<(VideoConfig, VideoChunk)> produced)
    {
        IntPtr buffer;
        // IMFSample::ConvertToContiguousBuffer
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Com.Slot(sample, 41))(sample, &buffer), "ConvertToContiguousBuffer");
        byte[] data;
        try
        {
            byte* bytes;
            uint max, length;
            // IMFMediaBuffer::Lock and Unlock
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Com.Slot(buffer, 3))(buffer, &bytes, &max, &length), "Lock");
            try { data = new ReadOnlySpan<byte>(bytes, (int)length).ToArray(); }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(buffer, 4))(buffer); }
        }
        finally { Com.Release(ref buffer); }
        long time;
        // IMFSample::GetSampleTime (100-nanosecond units)
        if (((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Com.Slot(sample, 35))(sample, &time) < 0) time = 0;

        var unit = Avcc.FromAnnexB(data);
        if (unit.Sps != null) sps = unit.Sps;
        if (unit.Pps != null) pps = unit.Pps;
        if (sps == null || pps == null) FromSequenceHeader();
        if (sps == null || pps == null || unit.Data.Length == 0) return;  // nothing the phone could decode
        var now = VideoConfig.From(sps, pps, Width, Height);
        if (config == null || !config.Equals(now)) config = now;
        produced.Add((config, new VideoChunk(unit.Data, unit.Key, time / 10)));
    }

    void FromSequenceHeader()
    {
        IntPtr type;
        // IMFTransform::GetOutputCurrentType
        if (((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Com.Slot(transform, 18))(transform, 0, &type) < 0) return;
        try
        {
            if (GetBlob(type, MF_MT_MPEG_SEQUENCE_HEADER) is not { } header) return;
            foreach (var nal in Avcc.SplitAnnexB(header))
            {
                if (Avcc.NalType(nal) == Avcc.Sps) sps = nal;
                else if (Avcc.NalType(nal) == Avcc.Pps) pps = nal;
            }
        }
        finally { Com.Release(ref type); }
    }

    IntPtr MakeSample(ScreenImage image, long timestamp)
    {
        uint size = (uint)Nv12.Size(Width, Height);
        IntPtr buffer, sample = IntPtr.Zero;
        Com.Check(MFCreateMemoryBuffer(size, &buffer), "MFCreateMemoryBuffer");
        try
        {
            byte* bytes;
            uint max, length;
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Com.Slot(buffer, 3))(buffer, &bytes, &max, &length), "Lock");
            try { Nv12.FromBgra(image, bytes); }
            finally { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(buffer, 4))(buffer); }
            // IMFMediaBuffer::SetCurrentLength
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Com.Slot(buffer, 6))(buffer, size), "SetCurrentLength");
            Com.Check(MFCreateSample(&sample), "MFCreateSample");
            // IMFSample::AddBuffer, SetSampleTime, SetSampleDuration
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Com.Slot(sample, 42))(sample, buffer), "AddBuffer");
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Com.Slot(sample, 36))(sample, timestamp * 10), "SetSampleTime");
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, long, int>)Com.Slot(sample, 38))(sample, frameDuration), "SetSampleDuration");
            return sample;
        }
        catch
        {
            Com.Release(ref sample);
            throw;
        }
        finally { Com.Release(ref buffer); }
    }

    IntPtr MakeOutputSample()
    {
        IntPtr buffer, sample = IntPtr.Zero;
        Com.Check(MFCreateMemoryBuffer(outputSize, &buffer), "MFCreateMemoryBuffer");
        try
        {
            Com.Check(MFCreateSample(&sample), "MFCreateSample");
            Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Com.Slot(sample, 42))(sample, buffer), "AddBuffer");
            return sample;
        }
        catch
        {
            Com.Release(ref sample);
            throw;
        }
        finally { Com.Release(ref buffer); }
    }

    void Message(int message) =>
        // IMFTransform::ProcessMessage
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, int, nuint, int>)Com.Slot(transform, 23))(transform, message, 0), "ProcessMessage");

    // True when the encoder took it.
    bool SetCodecValue(Guid key, Variant value)
    {
        if (codecApi == IntPtr.Zero) return false;
        // ICodecAPI::SetValue; a setting the encoder doesn't support is left out.
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Variant*, int>)Com.Slot(codecApi, 9))(codecApi, &key, &value) >= 0;
    }

    // IMFAttributes (slots 3 to 32), shared by media types, samples, events and activation objects.
    static uint GetUInt32(IntPtr attributes, Guid key)
    {
        uint value;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Com.Slot(attributes, 7))(attributes, &key, &value) >= 0 ? value : 0;
    }

    static void SetUInt32(IntPtr attributes, Guid key, uint value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Com.Slot(attributes, 21))(attributes, &key, value), "SetUINT32");

    static void SetUInt64(IntPtr attributes, Guid key, ulong value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Com.Slot(attributes, 22))(attributes, &key, value), "SetUINT64");

    static void SetGuid(IntPtr attributes, Guid key, Guid value) =>
        Com.Check(((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Com.Slot(attributes, 24))(attributes, &key, &value), "SetGUID");

    static byte[]? GetBlob(IntPtr attributes, Guid key)
    {
        uint size;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Com.Slot(attributes, 14))(attributes, &key, &size) < 0 || size == 0) return null;
        var blob = new byte[size];
        fixed (byte* p = blob)
        {
            uint got;
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, byte*, uint, uint*, int>)Com.Slot(attributes, 15))(attributes, &key, p, size, &got) < 0) return null;
        }
        return blob;
    }

    static string? FriendlyName(IntPtr attributes)
    {
        try
        {
            var key = MFT_FRIENDLY_NAME_Attribute;
            char* text;
            uint length;
            // IMFAttributes::GetAllocatedString
            if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, char**, uint*, int>)Com.Slot(attributes, 13))(attributes, &key, &text, &length) < 0) return null;
            try { return new string(text, 0, (int)length); }
            finally { Marshal.FreeCoTaskMem((IntPtr)text); }
        }
        catch (Exception) { return null; }
    }

    public void Dispose()
    {
        try { if (transform != IntPtr.Zero) Message(MFT_MESSAGE_NOTIFY_END_STREAMING); }
        catch (Exception) { }
        Com.Release(ref events);
        Com.Release(ref codecApi);
        Com.Release(ref transform);
        if (activate != IntPtr.Zero)
        {
            Shutdown(activate);
            Com.Release(ref activate);
        }
    }

    // IMFActivate::ShutdownObject (a hardware encoder holds the graphics card until then).
    static void Shutdown(IntPtr activation)
    {
        try { ((delegate* unmanaged[Stdcall]<IntPtr, int>)Com.Slot(activation, 34))(activation); }
        catch (Exception) { }
    }
}
