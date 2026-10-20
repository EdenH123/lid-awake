using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace LidAwake;

// Media Foundation, called through the COM function tables directly (no wrapper library). The
// slot numbers are the method's place in the interface, counting IUnknown's three, from
// mfobjects.h, mfidl.h and mfreadwrite.h:
//   IMFAttributes: GetUINT32 7, GetUINT64 8, GetGUID 10, GetAllocatedString 13, SetUINT32 21,
//                  SetUINT64 22, SetGUID 24 (30 methods, 3..32)
//   IMFActivate (IMFAttributes +): ActivateObject 33, ShutdownObject 34
//   IMFMediaSource (IMFMediaEventGenerator 3..6 +): Shutdown 12
//   IMFSourceReader: SetStreamSelection 4, GetNativeMediaType 5, GetCurrentMediaType 6,
//                    SetCurrentMediaType 7, ReadSample 9
//   IMFSample (IMFAttributes +): GetSampleTime 35, ConvertToContiguousBuffer 41, AddBuffer 42
//   IMFMediaBuffer: Lock 3, Unlock 4, GetCurrentLength 5, SetCurrentLength 6
internal static unsafe class Mf
{
    public const int EAccessDenied = unchecked((int)0x80070005);
    public const int ENotFound = unchecked((int)0x80070490);
    public const uint FirstVideoStream = 0xFFFFFFFC;
    public const uint AllStreams = 0xFFFFFFFE;
    public const uint ReaderError = 0x1, ReaderEndOfStream = 0x2, ReaderTypeChanged = 0x20;
    const uint Version = 0x00020070;  // MF_VERSION (MF_SDK_VERSION 2, MF_API_VERSION 0x70)

    public static readonly Guid SourceType = new("c60ac5fe-252a-478f-a0ef-bc8fa5f7cad3");       // MF_DEVSOURCE_ATTRIBUTE_SOURCE_TYPE
    public static readonly Guid SourceTypeVideo = new("8ac3587a-4ae7-42d8-99e0-0a6013eef90f");  // ..._SOURCE_TYPE_VIDCAP_GUID
    public static readonly Guid FriendlyName = new("60d0e559-52f8-4fa2-bbce-acdb34a8ec01");     // MF_DEVSOURCE_ATTRIBUTE_FRIENDLY_NAME
    public static readonly Guid SymbolicLink = new("58f0aad8-22bf-4f8a-bb3d-d2c4978c6e2f");     // ..._SOURCE_TYPE_VIDCAP_SYMBOLIC_LINK
    public static readonly Guid HardwareTransforms = new("a634a91c-822b-41b9-a494-4de4643612b0"); // MF_READWRITE_ENABLE_HARDWARE_TRANSFORMS
    public static readonly Guid AdvancedVideoProcessing = new("0f81da2c-b537-4672-a8b2-a681b17307a3"); // MF_SOURCE_READER_ENABLE_ADVANCED_VIDEO_PROCESSING
    public static readonly Guid MajorType = new("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");        // MF_MT_MAJOR_TYPE
    public static readonly Guid Subtype = new("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");          // MF_MT_SUBTYPE
    public static readonly Guid FrameSize = new("1652c33d-d6b2-4012-b834-72030849a37d");        // MF_MT_FRAME_SIZE
    public static readonly Guid FrameRate = new("c459a2e8-3d2c-4e44-b132-fee5156c7bb0");        // MF_MT_FRAME_RATE
    public static readonly Guid Video = new("73646976-0000-0010-8000-00aa00389b71");            // MFMediaType_Video
    public static readonly Guid Nv12 = new("3231564e-0000-0010-8000-00aa00389b71");             // MFVideoFormat_NV12
    public static readonly Guid Yuy2 = new("32595559-0000-0010-8000-00aa00389b71");             // MFVideoFormat_YUY2
    static readonly Guid MediaSourceIid = new("279a808d-aec7-40c8-9c6b-a6b492c78a66");          // IID_IMFMediaSource

    [DllImport("ole32.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int CoInitializeEx(IntPtr reserved, uint coInit);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int MFStartup(uint version, uint flags);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int MFCreateAttributes(out IntPtr attributes, uint initialSize);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int MFCreateMediaType(out IntPtr mediaType);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int MFCreateSample(out IntPtr sample);

    [DllImport("mfplat.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    public static extern int MFCreateMemoryBuffer(uint maxLength, out IntPtr buffer);

    [DllImport("mf.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int MFEnumDeviceSources(IntPtr attributes, out IntPtr activates, out uint count);

    [DllImport("mfreadwrite.dll", ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern int MFCreateSourceReaderFromMediaSource(IntPtr source, IntPtr attributes, out IntPtr reader);

    static readonly object startGate = new();
    static int started;  // 0 not tried, 1 running, -1 not available

    // COM on this thread (multithreaded; an STA thread keeps its own) and MFStartup, once for the
    // app. False when this Windows has no Media Foundation (an N edition without its media pack).
    public static bool Start()
    {
        ComOnThisThread();
        lock (startGate)
        {
            if (started == 0)
            {
                try { started = MFStartup(Version, 0) >= 0 ? 1 : -1; }
                catch (Exception) { started = -1; }  // DllNotFoundException, EntryPointNotFoundException
            }
            return started == 1;
        }
    }

    public static void ComOnThisThread()
    {
        try { CoInitializeEx(IntPtr.Zero, 0); }
        catch (Exception) { }
    }

    static void* Slot(IntPtr o, int i) => (*(void***)o)[i];

    public static void Release(IntPtr p)
    {
        if (p != IntPtr.Zero) Marshal.Release(p);
    }

    // IMFAttributes
    public static int SetUInt32(IntPtr a, Guid key, uint v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint, int>)Slot(a, 21))(a, &key, v);
    public static int SetUInt64(IntPtr a, Guid key, ulong v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong, int>)Slot(a, 22))(a, &key, v);
    public static int SetGuid(IntPtr a, Guid key, Guid v) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(a, 24))(a, &key, &v);

    public static uint? GetUInt32(IntPtr a, Guid key)
    {
        uint v;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, uint*, int>)Slot(a, 7))(a, &key, &v) >= 0 ? v : null;
    }

    public static ulong? GetUInt64(IntPtr a, Guid key)
    {
        ulong v;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, ulong*, int>)Slot(a, 8))(a, &key, &v) >= 0 ? v : null;
    }

    public static Guid? GetGuid(IntPtr a, Guid key)
    {
        Guid v;
        return ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, Guid*, int>)Slot(a, 10))(a, &key, &v) >= 0 ? v : null;
    }

    public static string? GetString(IntPtr a, Guid key)
    {
        IntPtr text;
        uint length;
        if (((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, uint*, int>)Slot(a, 13))(a, &key, &text, &length) < 0) return null;
        try { return Marshal.PtrToStringUni(text, (int)length); }
        finally { Marshal.FreeCoTaskMem(text); }
    }

    // IMFActivate
    static int ActivateObject(IntPtr act, Guid iid, IntPtr* obj) => ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, int>)Slot(act, 33))(act, &iid, obj);
    public static int ShutdownObject(IntPtr act) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(act, 34))(act);

    // IMFMediaSource
    public static int Shutdown(IntPtr source) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(source, 12))(source);

    // IMFSourceReader
    public static int SetStreamSelection(IntPtr r, uint stream, bool on) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int, int>)Slot(r, 4))(r, stream, on ? 1 : 0);
    public static int GetNativeMediaType(IntPtr r, uint stream, uint index, IntPtr* type) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, IntPtr*, int>)Slot(r, 5))(r, stream, index, type);
    public static int GetCurrentMediaType(IntPtr r, uint stream, IntPtr* type) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr*, int>)Slot(r, 6))(r, stream, type);
    public static int SetCurrentMediaType(IntPtr r, uint stream, IntPtr type) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint*, IntPtr, int>)Slot(r, 7))(r, stream, null, type);
    public static int ReadSample(IntPtr r, uint stream, uint* actual, uint* flags, long* time, IntPtr* sample) =>
        ((delegate* unmanaged[Stdcall]<IntPtr, uint, uint, uint*, uint*, long*, IntPtr*, int>)Slot(r, 9))(r, stream, 0, actual, flags, time, sample);

    // IMFSample
    public static int GetSampleTime(IntPtr s, long* time) => ((delegate* unmanaged[Stdcall]<IntPtr, long*, int>)Slot(s, 35))(s, time);
    public static int ConvertToContiguousBuffer(IntPtr s, IntPtr* buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr*, int>)Slot(s, 41))(s, buffer);
    public static int AddBuffer(IntPtr s, IntPtr buffer) => ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)Slot(s, 42))(s, buffer);

    // IMFMediaBuffer
    public static int Lock(IntPtr b, byte** data, uint* max, uint* current) => ((delegate* unmanaged[Stdcall]<IntPtr, byte**, uint*, uint*, int>)Slot(b, 3))(b, data, max, current);
    public static int Unlock(IntPtr b) => ((delegate* unmanaged[Stdcall]<IntPtr, int>)Slot(b, 4))(b);
    public static int SetCurrentLength(IntPtr b, uint length) => ((delegate* unmanaged[Stdcall]<IntPtr, uint, int>)Slot(b, 6))(b, length);

    // A sample's bytes, as one piece.
    public static byte[]? SampleBytes(IntPtr sample)
    {
        IntPtr buffer;
        if (ConvertToContiguousBuffer(sample, &buffer) < 0 || buffer == IntPtr.Zero) return null;
        try
        {
            byte* data;
            uint max, length;
            if (Lock(buffer, &data, &max, &length) < 0) return null;
            try { return new ReadOnlySpan<byte>(data, (int)length).ToArray(); }
            finally { Unlock(buffer); }
        }
        finally { Release(buffer); }
    }

    // A sample holding these bytes in one memory buffer (for the self-check in the tests).
    public static IntPtr MakeSample(byte[] bytes)
    {
        if (MFCreateSample(out var sample) < 0) return IntPtr.Zero;
        if (MFCreateMemoryBuffer((uint)bytes.Length, out var buffer) < 0) { Release(sample); return IntPtr.Zero; }
        try
        {
            byte* data;
            uint max, length;
            if (Lock(buffer, &data, &max, &length) < 0) { Release(sample); return IntPtr.Zero; }
            try { bytes.CopyTo(new Span<byte>(data, (int)max)); }
            finally { Unlock(buffer); }
            SetCurrentLength(buffer, (uint)bytes.Length);
            if (AddBuffer(sample, buffer) < 0) { Release(sample); return IntPtr.Zero; }
            return sample;
        }
        finally { Release(buffer); }
    }

    // The video capture devices (cameras), with each one's IMFActivate: the caller releases them.
    public static List<(string Name, string Link, IntPtr Activate)> VideoDevices()
    {
        var found = new List<(string Name, string Link, IntPtr Activate)>();
        IntPtr attributes = IntPtr.Zero, array = IntPtr.Zero;
        try
        {
            if (MFCreateAttributes(out attributes, 1) < 0) return found;
            if (SetGuid(attributes, SourceType, SourceTypeVideo) < 0) return found;
            if (MFEnumDeviceSources(attributes, out array, out var count) < 0) return found;
            for (int i = 0; i < count; i++)
            {
                var act = Marshal.ReadIntPtr(array, i * IntPtr.Size);
                if (act == IntPtr.Zero) continue;
                found.Add((GetString(act, FriendlyName) ?? "Camera", GetString(act, SymbolicLink) ?? "", act));
            }
        }
        finally
        {
            if (array != IntPtr.Zero) Marshal.FreeCoTaskMem(array);
            Release(attributes);
        }
        return found;
    }

    public static int Activate(IntPtr act, out IntPtr source)
    {
        IntPtr s;
        int hr = ActivateObject(act, MediaSourceIid, &s);
        source = hr >= 0 ? s : IntPtr.Zero;
        return hr;
    }

    // A source reader that may decode (MJPG) and convert (to NV12) for us, in hardware when it can.
    public static int CreateReader(IntPtr source, out IntPtr reader)
    {
        reader = IntPtr.Zero;
        int hr = MFCreateAttributes(out var attributes, 2);
        if (hr < 0) return hr;
        try
        {
            SetUInt32(attributes, HardwareTransforms, 1);
            SetUInt32(attributes, AdvancedVideoProcessing, 1);
            return MFCreateSourceReaderFromMediaSource(source, attributes, out reader);
        }
        finally { Release(attributes); }
    }

    public static VideoFormat Describe(IntPtr type)
    {
        var size = GetUInt64(type, FrameSize) ?? 0;
        var rate = GetUInt64(type, FrameRate) ?? 0;
        uint num = (uint)(rate >> 32), den = (uint)rate;
        return new VideoFormat(GetGuid(type, Subtype) ?? Guid.Empty, (int)(size >> 32), (int)(uint)size, den == 0 ? 0 : num / (double)den);
    }
}

// One camera through a Media Foundation source reader, read on its own thread. The device runs
// (and its light is on) from the first read until Dispose.
internal sealed unsafe class MfCapture : ICameraCapture
{
    readonly ICameraSink sink;
    readonly object sourceGate = new();
    IntPtr activate, source, reader;
    Guid subtype;
    int width, height;
    volatile bool stopping;
    Thread? thread;

    MfCapture(ICameraSink sink) => this.sink = sink;

    // Opens the camera with this symbolic link. The HRESULT of what failed (0 when it opened).
    public static int TryOpen(string link, ICameraSink sink, out MfCapture? capture)
    {
        capture = null;
        var devices = Mf.VideoDevices();
        IntPtr act = IntPtr.Zero;
        foreach (var d in devices)
        {
            if (act == IntPtr.Zero && string.Equals(d.Link, link, StringComparison.OrdinalIgnoreCase)) act = d.Activate;
            else Mf.Release(d.Activate);
        }
        if (act == IntPtr.Zero) return Mf.ENotFound;
        var c = new MfCapture(sink) { activate = act };
        int hr = c.Setup();
        if (hr < 0) { c.Close(); return hr; }
        c.thread = new Thread(c.Loop) { IsBackground = true, Name = "Lid Awake camera" };
        c.thread.SetApartmentState(ApartmentState.MTA);
        c.thread.Start();
        capture = c;
        return 0;
    }

    int Setup()
    {
        int hr = Mf.Activate(activate, out source);
        if (hr < 0) return hr;
        hr = Mf.CreateReader(source, out reader);
        if (hr < 0) return hr;
        Mf.SetStreamSelection(reader, Mf.AllStreams, false);
        Mf.SetStreamSelection(reader, Mf.FirstVideoStream, true);
        // The camera's own format: 1280×720 when it offers it.
        var formats = new List<VideoFormat>();
        for (uint i = 0; i < 1000; i++)
        {
            IntPtr type;
            if (Mf.GetNativeMediaType(reader, Mf.FirstVideoStream, i, &type) < 0) break;
            try { formats.Add(Mf.Describe(type)); }
            finally { Mf.Release(type); }
        }
        int pick = VideoFormats.Choose(formats);
        if (pick >= 0)
        {
            IntPtr native;
            if (Mf.GetNativeMediaType(reader, Mf.FirstVideoStream, (uint)pick, &native) >= 0)
            {
                Mf.SetCurrentMediaType(reader, Mf.FirstVideoStream, native);
                Mf.Release(native);
            }
        }
        var size = pick >= 0 ? formats[pick] : new VideoFormat(Guid.Empty, VideoFormats.Width, VideoFormats.Height, 30);
        // What the reader gives us: NV12 (converted for us), or YUY2 converted here.
        hr = -1;
        foreach (var wanted in new[] { Mf.Nv12, Mf.Yuy2 })
        {
            if (Mf.MFCreateMediaType(out var output) < 0) continue;
            try
            {
                Mf.SetGuid(output, Mf.MajorType, Mf.Video);
                Mf.SetGuid(output, Mf.Subtype, wanted);
                Mf.SetUInt64(output, Mf.FrameSize, ((ulong)(uint)size.Width << 32) | (uint)size.Height);
                hr = Mf.SetCurrentMediaType(reader, Mf.FirstVideoStream, output);
            }
            finally { Mf.Release(output); }
            if (hr >= 0) break;
        }
        if (hr < 0) return hr;
        ReadCurrentType();
        return width > 0 && height > 0 ? 0 : -1;
    }

    void ReadCurrentType()
    {
        IntPtr type;
        if (Mf.GetCurrentMediaType(reader, Mf.FirstVideoStream, &type) < 0) return;
        try
        {
            var f = Mf.Describe(type);
            (subtype, width, height) = (f.Subtype, f.Width, f.Height);
        }
        finally { Mf.Release(type); }
    }

    void Loop()
    {
        Mf.ComOnThisThread();
        try
        {
            while (!stopping)
            {
                uint actual, flags;
                long time;
                IntPtr sample = IntPtr.Zero;
                int hr = Mf.ReadSample(reader, Mf.FirstVideoStream, &actual, &flags, &time, &sample);
                try
                {
                    if (stopping) break;
                    if (hr < 0 || (flags & (Mf.ReaderError | Mf.ReaderEndOfStream)) != 0)
                    {
                        // Unplugged, or Windows took the camera away (privacy switch, lid).
                        sink.Problem(hr == Mf.EAccessDenied ? CameraTexts.NotAllowed : CameraTexts.Stalled);
                        break;
                    }
                    if ((flags & Mf.ReaderTypeChanged) != 0) ReadCurrentType();
                    if (sample == IntPtr.Zero) continue;
                    var raw = Mf.SampleBytes(sample);
                    if (raw == null) continue;
                    var nv12 = subtype == Mf.Yuy2 ? CameraImage.Yuy2ToNv12(raw, width, height) : CameraImage.Nv12(raw, width, height);
                    // 100-nanosecond units → microseconds.
                    if (nv12 != null) sink.Frame(new CameraFrame(nv12, width, height, time / 10));
                }
                finally { Mf.Release(sample); }
            }
        }
        catch (Exception) { }
        finally { Close(); }
    }

    // Releasing the reader shuts the source down; the source and its activation object are shut
    // down here as well, so the camera (and its light) is off whatever happened.
    void Close()
    {
        lock (sourceGate)
        {
            Mf.Release(reader);
            reader = IntPtr.Zero;
            if (source != IntPtr.Zero) { Mf.Shutdown(source); Mf.Release(source); source = IntPtr.Zero; }
            if (activate != IntPtr.Zero) { Mf.ShutdownObject(activate); Mf.Release(activate); activate = IntPtr.Zero; }
        }
    }

    public void Dispose()
    {
        stopping = true;
        var t = thread;
        if (t == null) { Close(); return; }
        if (t.Join(1500)) return;
        // A camera that stopped delivering keeps ReadSample waiting: shutting the source down ends it.
        lock (sourceGate)
        {
            if (source != IntPtr.Zero) Mf.Shutdown(source);
        }
        t.Join(1500);
    }
}

// The default microphone, through WASAPI (shared mode): the first channel, at its own rate.
internal sealed class WasapiMicrophone : IDisposable
{
    readonly WasapiCapture capture;

    WasapiMicrophone(WasapiCapture capture) => this.capture = capture;

    static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    // null when there is no microphone, or Windows doesn't allow it: then there's only a picture.
    public static WasapiMicrophone? Open(Action<float[], int> sound)
    {
        WasapiCapture? capture = null;
        try
        {
            var devices = new MMDeviceEnumerator();
            var mic = devices.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Console);
            capture = new WasapiCapture(mic);
            var format = capture.WaveFormat;
            bool isFloat = format.Encoding == WaveFormatEncoding.IeeeFloat
                || (format is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
            if (SoundChunker.KindOf(isFloat, format.BitsPerSample) is not { } kind) { capture.Dispose(); return null; }
            int channels = format.Channels, rate = format.SampleRate;
            capture.DataAvailable += (_, e) =>
            {
                try { sound(SoundChunker.FirstChannel(e.Buffer.AsSpan(0, e.BytesRecorded), kind, channels), rate); }
                catch (Exception) { }
            };
            capture.StartRecording();
            return new WasapiMicrophone(capture);
        }
        catch (Exception)
        {
            try { capture?.Dispose(); }
            catch (Exception) { }
            return null;
        }
    }

    public void Dispose()
    {
        try { capture.StopRecording(); }
        catch (Exception) { }
        try { capture.Dispose(); }
        catch (Exception) { }
    }
}

// Talk back on the default speakers through WASAPI (shared mode), in the speakers' own format.
internal sealed class WasapiTalkOutput : ITalkOutput, IWaveProvider
{
    static readonly Guid FloatSubFormat = new("00000003-0000-0010-8000-00aa00389b71");

    readonly WasapiOut player;
    readonly TalkPlayer source;
    readonly TalkResampler resampler;
    readonly bool isFloat;
    float[] mono = Array.Empty<float>();

    public WaveFormat WaveFormat { get; }

    WasapiTalkOutput(MMDevice speakers, WaveFormat mix, bool isFloat, TalkPlayer source)
    {
        WaveFormat = mix;
        this.isFloat = isFloat;
        this.source = source;
        resampler = new TalkResampler(mix.SampleRate);
        player = new WasapiOut(speakers, AudioClientShareMode.Shared, true, 100);
    }

    // Starts playing what `source` has; null when there are no speakers.
    public static ITalkOutput? Open(TalkPlayer source)
    {
        WasapiTalkOutput? output = null;
        try
        {
            var devices = new MMDeviceEnumerator();
            var speakers = devices.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            var mix = speakers.AudioClient.MixFormat;
            bool isFloat = mix.Encoding == WaveFormatEncoding.IeeeFloat || (mix is WaveFormatExtensible ext && ext.SubFormat == FloatSubFormat);
            if (!(isFloat && mix.BitsPerSample == 32) && !(!isFloat && mix.BitsPerSample == 16)) return null;
            output = new WasapiTalkOutput(speakers, mix, isFloat, source);
            output.player.Init(output);
            output.player.Play();
            return output;
        }
        catch (Exception)
        {
            output?.Dispose();
            return null;
        }
    }

    // WASAPI's thread asks for the next bytes.
    public int Read(byte[] buffer, int offset, int count)
    {
        int channels = Math.Max(1, WaveFormat.Channels);
        int frames = count / WaveFormat.BlockAlign;
        if (mono.Length < frames) mono = new float[frames];
        var samples = mono.AsSpan(0, frames);
        resampler.Fill(samples, b => source.Read(b));
        var span = buffer.AsSpan(offset, frames * WaveFormat.BlockAlign);
        int at = 0;
        for (int i = 0; i < frames; i++)
        {
            for (int ch = 0; ch < channels; ch++)
            {
                if (isFloat) { BitConverter.TryWriteBytes(span.Slice(at, 4), samples[i]); at += 4; }
                else { BitConverter.TryWriteBytes(span.Slice(at, 2), Mulaw.ToInt16(samples[i])); at += 2; }
            }
        }
        return frames * WaveFormat.BlockAlign;
    }

    public void Dispose()
    {
        try { player.Stop(); }
        catch (Exception) { }
        try { player.Dispose(); }
        catch (Exception) { }
    }
}

// The PC's camera and microphone. The camera is chosen as on the Mac: a plugged-in USB camera
// first, then the built-in one, then any other (CameraChoice); if one fails to open, the next is
// tried. The microphone is Windows' default one; with none (or not allowed) there's only a picture.
public sealed class CameraHardware : ICameraHardware
{
    readonly Func<bool> lidClosed;

    // `lidClosed`: whether the lid is closed now (Task 24's lid state; none: never).
    public CameraHardware(Func<bool>? lidClosed = null) => this.lidClosed = lidClosed ?? (() => false);

    // The cameras Windows has, with where each one is. Empty when Media Foundation is missing.
    public static List<CameraDevice> Cameras()
    {
        if (!Mf.Start()) return new();
        var builtIn = BuiltInByLink();
        var cameras = new List<CameraDevice>();
        foreach (var (name, link, act) in Mf.VideoDevices())
        {
            Mf.Release(act);
            cameras.Add(new CameraDevice(name, link, CameraChoice.PlaceOf(link, builtIn.TryGetValue(link, out var b) ? b : null)));
        }
        return cameras;
    }

    // Windows' enclosure location for each camera (by its device interface path, which is the
    // Media Foundation symbolic link): true when it is part of the PC. Empty when Windows can't say.
    static Dictionary<string, bool> BuiltInByLink()
    {
        var map = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        try
        {
            var find = global::Windows.Devices.Enumeration.DeviceInformation
                .FindAllAsync(global::Windows.Devices.Enumeration.DeviceClass.VideoCapture).AsTask();
            if (!find.Wait(TimeSpan.FromSeconds(3))) return map;
            foreach (var d in find.Result)
            {
                var e = d.EnclosureLocation;
                map[d.Id] = e != null && (e.InLid || e.Panel != global::Windows.Devices.Enumeration.Panel.Unknown);
            }
        }
        catch (Exception) { }
        return map;
    }

    public ICameraCapture? Open(ICameraSink sink, out string? problem)
    {
        problem = null;
        if (!Mf.Start()) { problem = CameraTexts.NoMediaFoundation; return null; }
        var cameras = CameraChoice.Order(Cameras());
        int firstError = 0;
        foreach (var camera in cameras)
        {
            int hr = MfCapture.TryOpen(camera.Link, sink, out var video);
            if (hr >= 0 && video != null)
                return new Running(video, WasapiMicrophone.Open((mono, rate) => sink.Sound(mono, rate)));
            if (firstError == 0 || hr == Mf.EAccessDenied) firstError = hr;
        }
        problem = CameraTexts.OpenFailed(cameras.Count > 0, SafeLid(), firstError);
        return null;
    }

    bool SafeLid()
    {
        try { return lidClosed(); }
        catch (Exception) { return false; }
    }

    sealed class Running : ICameraCapture
    {
        readonly MfCapture video;
        readonly WasapiMicrophone? mic;
        public Running(MfCapture video, WasapiMicrophone? mic) => (this.video, this.mic) = (video, mic);

        public void Dispose()
        {
            mic?.Dispose();
            video.Dispose();
        }
    }
}
