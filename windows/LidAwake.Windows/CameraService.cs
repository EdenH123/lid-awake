using System.Buffers.Binary;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace LidAwake;

// One camera picture: NV12 (Y plane, then interleaved U and V; rows exactly Width bytes), with its
// time in microseconds.
public sealed record CameraFrame(byte[] Nv12, int Width, int Height, long Micros);

// Where the camera's pictures and sound go, on the capture threads. Failed: the camera stopped
// for good (a read error, the end of the stream, unplugged, Windows' privacy switch); the whole
// capture ends, microphone included, and the phone is told why.
public interface ICameraSink
{
    void Frame(CameraFrame frame);
    void Sound(ReadOnlySpan<float> firstChannel, int rate);
    void Failed(string text);
}

// A running camera (and microphone). Disposing it stops both: the webcam light goes off.
public interface ICameraCapture : IDisposable { }

// The PC's camera and microphone (CameraHardware), or a fake in the tests.
public interface ICameraHardware
{
    // Starts the camera (and the microphone, when there is one) delivering to `sink`. null, with
    // the sentence to show the phone, when no camera could be opened.
    ICameraCapture? Open(ICameraSink sink, out string? problem);
}

// The phone's camera view (docs/API.md 7.5), as the camera parts of the Mac's LiveServer and
// CameraStream. The camera runs only while at least one phone watches through the live socket,
// with a ticket from /camera/unlock and the owner's switch on, so the webcam light is only on
// then. It stops when the last phone closes it or its socket closes, when the switch goes off,
// after 10 minutes, and when Lid Awake turns off, quits, or the session ends (StopAll).
// Several phones may watch at once: they share one capture and one encoder.
public sealed class CameraService : IDisposable
{
    public const int Minutes = 10;
    public static readonly TimeSpan Limit = TimeSpan.FromMinutes(Minutes);
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(2);
    public static readonly TimeSpan StallAfter = TimeSpan.FromSeconds(4);
    public const int Bitrate = 1_200_000;         // the Mac's camera encoder
    public const long MinFrameMicros = 40_000;    // at most ~24 frames a second, as on the Mac
    public const int MaxTalkBytes = 4000;

    readonly CameraTickets tickets;
    readonly Func<bool> allowed;
    readonly ICameraHardware hardware;
    readonly IVideoEncoderFactory? encoders;
    readonly TalkPlayer? talk;
    readonly AccessLog? log;
    readonly Func<DateTimeOffset> now;
    readonly Action<Action> run;
    readonly object gate = new();
    // Opening and closing the device, one at a time, so a new open waits for the old one to let go.
    readonly object deviceGate = new();
    readonly Dictionary<ICameraSocket, Viewer> viewers = new(ReferenceEqualityComparer.Instance);
    // viewers.Count, set under `gate` whenever it changes and read without it (Watchers): the
    // tray reads it every second on the UI thread, and `gate` is held while the encoder starts
    // and while each picture is encoded.
    volatile int watchers;
    System.Threading.Timer? timer;

    // The capture and what belongs to it. `generation` changes whenever the capture ends, so
    // pictures from an old one are ignored.
    int generation;
    bool opening;
    ICameraCapture? capture;
    IH264Encoder? encoder;
    int encodedWidth, encodedHeight;
    bool noEncoder;
    CameraSetup? setup;
    bool forceKey = true;
    long? lastEncoded;
    DateTimeOffset lastFrame;
    readonly SoundChunker sound = new();

    sealed class Viewer
    {
        public required DateTimeOffset Since;
        public bool Talked;
        // The capture this phone watched ended with a failure (or a stall): it stays on the camera
        // view with the error, but its voice is no longer played until it opens the camera again.
        public bool Failed;
    }

    // `allowed`: Lid Awake on and the owner's switches (screen and camera). `encoders`: Task 22's
    // H.264 encoder (none: no picture, only sound and a cameraError). `talk`: the speakers (none:
    // talk is ignored). `run` does slow work (opening and closing the device) off the caller's
    // thread; the tests pass one that runs it at once.
    public CameraService(CameraTickets tickets, Func<bool> allowed, ICameraHardware hardware, IVideoEncoderFactory? encoders,
                         TalkPlayer? talk = null, AccessLog? log = null, Func<DateTimeOffset>? now = null, Action<Action>? run = null)
    {
        this.tickets = tickets;
        this.allowed = allowed;
        this.hardware = hardware;
        this.encoders = encoders;
        this.talk = talk;
        this.log = log;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.run = run ?? (a => Task.Run(a));
    }

    // The diagnostics log (DiagnosticsLog): one line per capture when it ends (StreamStats), and
    // each encoder fallback. Set before Start.
    public Action<string>? Diag { get; set; }

    // The capture's numbers (under `gate`), from the device opening until EndCapture.
    StreamStats? session;

    // Lines made under `gate` wait here, and are written by FlushDiag once the gate is let go, so
    // the file is never written while a phone's frames wait for the lock.
    readonly List<string> diagLines = new();

    // Under `gate`.
    void DiagNote(string text) => diagLines.Add(text);

    // Outside `gate`: writes what DiagNote kept, in order.
    void FlushDiag()
    {
        List<string> lines;
        lock (gate)
        {
            if (diagLines.Count == 0) return;
            lines = diagLines.ToList();
            diagLines.Clear();
        }
        foreach (var line in lines)
        {
            try { Diag?.Invoke(line); }
            catch (Exception) { }
        }
    }

    // The check every 2 seconds (switch off, 10 minutes, a camera that stopped sending).
    public void Start() => timer ??= new System.Threading.Timer(_ => Tick(), null, CheckEvery, CheckEvery);

    public bool IsOpen(ICameraSocket socket) { lock (gate) return viewers.ContainsKey(socket); }

    // Running now (the webcam light is on).
    public bool Capturing { get { lock (gate) return capture != null; } }

    // Phones that have the camera open right now (the tray icon shows a video camera meanwhile, as
    // the Mac's menu bar does). One whose capture failed still counts until it closes the camera.
    public int Watchers => watchers;

    // A message from the phone on its live socket (7.3). True when it was a camera message
    // (`camera`, `cameraKey`, `talk` as a string), which the socket then handles no further.
    public bool Receive(ICameraSocket socket, JsonElement message)
    {
        if (message.ValueKind != JsonValueKind.Object) return false;
        if (message.TryGetProperty("camera", out var wants) && wants.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            if (wants.ValueKind == JsonValueKind.True)
            {
                var ticket = message.TryGetProperty("ticket", out var t) && t.ValueKind == JsonValueKind.String ? t.GetString() : null;
                Open(socket, ticket);
            }
            else Stop(socket, null);
            return true;
        }
        if (message.TryGetProperty("cameraKey", out _)) { RequestKeyframe(); return true; }
        if (message.TryGetProperty("talk", out var voice) && voice.ValueKind == JsonValueKind.String)
        {
            Talk(socket, voice.GetString() ?? "");
            return true;
        }
        return false;
    }

    // The socket closed.
    public void Closed(ICameraSocket socket) => Stop(socket, null);

    // A phone's decoder (re)started, or its socket fell too far behind: the next picture is a keyframe.
    public void RequestKeyframe() { lock (gate) forceKey = true; }

    void Open(ICameraSocket socket, string? ticket)
    {
        int startGeneration = -1;
        lock (gate)
        {
            // Open already (a failed one may open again).
            if (viewers.TryGetValue(socket, out var already) && !already.Failed) return;
            if (!SafeAllowed()) { socket.SendCameraError(CameraTexts.Off); return; }
            if (!tickets.Redeem(ticket, socket.SessionToken)) { socket.SendCameraError(CameraTexts.NeedsPasskey); return; }
            viewers[socket] = new Viewer { Since = now() };
            watchers = viewers.Count;
            Log($"Camera and microphone opened from the phone · {socket.Device}");
            if (capture == null && !opening)
            {
                opening = true;
                startGeneration = generation;
            }
            else if (setup != null) socket.SendSetup(setup);
            forceKey = true;
        }
        if (startGeneration >= 0) run(() => StartCapture(startGeneration));
    }

    void StartCapture(int startGeneration)
    {
        lock (deviceGate)
        {
            lock (gate)
            {
                // Everyone left before it started.
                if (startGeneration != generation || viewers.Count == 0) return;
            }
            ICameraCapture? started = null;
            string? problem = null;
            try { started = hardware.Open(new Sink(this, startGeneration), out problem); }
            catch (Exception) { problem = CameraTexts.Busy; }
            bool keep;
            lock (gate)
            {
                keep = startGeneration == generation && viewers.Count > 0;
                if (keep)
                {
                    opening = false;
                    capture = started;
                    if (started != null) session = new StreamStats("camera");
                    lastFrame = now();
                    forceKey = true;
                    if (started == null) Broadcast(v => v.SendCameraError(problem ?? CameraTexts.Busy));
                }
            }
            if (!keep) Close(started);
        }
    }

    void Stop(ICameraSocket socket, string? reason)
    {
        ICameraCapture? ended = null;
        bool last;
        lock (gate)
        {
            if (!viewers.Remove(socket)) return;
            watchers = viewers.Count;
            Log($"Camera and microphone closed · {socket.Device}");
            if (reason != null) socket.SendCameraStopped(reason);
            last = viewers.Count == 0;
            if (last) ended = EndCapture();
        }
        FlushDiag();
        if (last) talk?.Stop();
        if (ended != null) run(() => { lock (deviceGate) Close(ended); });
    }

    // Lid Awake turned off, quit, or Windows is ending the session: every phone's camera closes now.
    public void StopAll() => StopAll(wait: false);

    void StopAll(bool wait)
    {
        ICameraCapture? ended;
        lock (gate)
        {
            foreach (var socket in viewers.Keys) Log($"Camera and microphone closed · {socket.Device}");
            viewers.Clear();
            watchers = 0;
            ended = EndCapture();
        }
        FlushDiag();
        talk?.Stop();
        if (!wait)
        {
            if (ended != null) run(() => { lock (deviceGate) Close(ended); });
            return;
        }
        // Quit: wait (at most about 3 seconds) for a device close or open still under way, then
        // close this one. Past that, the app ends anyway and Windows lets go of the camera.
        if (!Monitor.TryEnter(deviceGate, DisposeWait)) return;
        try { Close(ended); }
        finally { Monitor.Exit(deviceGate); }
    }

    public static readonly TimeSpan DisposeWait = TimeSpan.FromSeconds(3);

    // Under `gate`: the capture stops being used. Closing the device is the caller's, outside the lock.
    ICameraCapture? EndCapture()
    {
        generation++;
        opening = false;
        var ended = capture;
        capture = null;
        if (session != null) DiagNote(session.Line());
        session = null;
        try { encoder?.Dispose(); }
        catch (Exception) { }
        encoder = null;
        noEncoder = false;
        setup = null;
        lastEncoded = null;
        forceKey = true;
        sound.Reset();
        return ended;
    }

    static void Close(ICameraCapture? c)
    {
        try { c?.Dispose(); }
        catch (Exception) { }
    }

    // Every 2 seconds, and at once when the owner changes the switch: the switch, the 10 minutes,
    // and a camera that stopped sending (lid closed, unplugged). A silent camera ends the capture
    // like a failure does (microphone included): the phones get the stall sentence.
    public void Tick()
    {
        var stops = new List<(ICameraSocket, string)>();
        ICameraCapture? stalled = null;
        bool stall = false;
        lock (gate)
        {
            if (viewers.Count > 0)
            {
                bool ok = SafeAllowed();
                var t = now();
                foreach (var (socket, viewer) in viewers)
                {
                    if (!ok) stops.Add((socket, CameraTexts.SwitchedOff));
                    else if (t - viewer.Since > Limit) stops.Add((socket, CameraTexts.TenMinutes));
                }
                if (capture != null && t - lastFrame > StallAfter)
                {
                    stall = true;
                    stalled = FailUnderGate(CameraTexts.Stalled);
                }
            }
        }
        FlushDiag();
        if (stall) AfterFailure(CameraTexts.Stalled, stalled);
        foreach (var (socket, why) in stops) Stop(socket, why);
        talk?.Idle();
    }

    bool SafeAllowed()
    {
        try { return allowed(); }
        catch (Exception) { return false; }
    }

    void Talk(ICameraSocket socket, string base64)
    {
        // Base64 of 4000 bytes is 5336 characters: anything much longer is too much.
        if (base64.Length > 5400) return;
        Viewer? viewer;
        lock (gate) viewers.TryGetValue(socket, out viewer);
        // Only while this phone's camera is open and working (a failed one isn't, 7.5).
        if (viewer == null || viewer.Failed || !SafeAllowed()) return;
        var bytes = new byte[MaxTalkBytes];
        if (!Convert.TryFromBase64String(base64, bytes, out int n) || n == 0) return;
        lock (gate)
        {
            if (!viewers.TryGetValue(socket, out var current) || current != viewer || viewer.Failed) return;
            if (!viewer.Talked) { viewer.Talked = true; Log($"Talked through the PC's speakers · {socket.Device}"); }
        }
        if (talk == null) return;
        talk.Play(bytes.AsSpan(0, n));
        // The last phone may have closed the camera (or it failed) while this was starting the
        // speakers: then they are let go again (TalkPlayer also drops an open that finishes after
        // its Stop).
        bool nobody;
        lock (gate) nobody = !viewers.Values.Any(v => !v.Failed);
        if (nobody) talk.Stop();
    }

    void Log(string text)
    {
        try { log?.Write(text); }
        catch (Exception) { }
    }

    // Under `gate`.
    void Broadcast(Action<ICameraSocket> send)
    {
        foreach (var socket in viewers.Keys)
        {
            try { send(socket); }
            catch (Exception) { }
        }
    }

    void OnFrame(int fromGeneration, CameraFrame frame)
    {
        try { OnFrameUnderGate(fromGeneration, frame); }
        finally { FlushDiag(); }
    }

    void OnFrameUnderGate(int fromGeneration, CameraFrame frame)
    {
        lock (gate)
        {
            if (fromGeneration != generation || viewers.Count == 0) return;
            lastFrame = now();
            // At most ~24 frames a second. A clock that went back starts over.
            if (lastEncoded is { } last && frame.Micros >= last && frame.Micros - last < MinFrameMicros) return;
            lastEncoded = frame.Micros;
            if (encoders == null || noEncoder)
            {
                if (!noEncoder) NoEncoder();
                return;
            }
            if (encoder == null || frame.Width != encodedWidth || frame.Height != encodedHeight)
            {
                try { encoder?.Dispose(); }
                catch (Exception) { }
                encoder = null;
                try { encoder = encoders.Create(frame.Width, frame.Height, Bitrate); }
                catch (Exception) { }
                if (encoder == null) { NoEncoder(); return; }
                if (session?.Encoder(SafeName(encoder)) is { } before) DiagNote($"camera encoder fallback: {before} → {SafeName(encoder)}");
                (encodedWidth, encodedHeight) = (frame.Width, frame.Height);
                forceKey = true;
            }
            bool key = forceKey;
            forceKey = false;
            IReadOnlyList<H264Unit> units;
            try { units = encoder.Encode(frame.Nv12, frame.Micros, key); }
            catch (Exception)
            {
                // Start that encoder over with the next picture.
                try { encoder.Dispose(); }
                catch (Exception) { }
                encoder = null;
                return;
            }
            foreach (var unit in units) Emit(unit);
        }
    }

    // Under `gate`: no picture from now on, only sound, until the capture starts again.
    void NoEncoder()
    {
        noEncoder = true;
        session?.Fallback();
        DiagNote("camera encoder fallback: no H.264 encoder works, sound only");
        Broadcast(v => v.SendCameraError(CameraTexts.NoEncoder));
    }

    static string SafeName(IH264Encoder encoder)
    {
        try { return encoder.Name; }
        catch (Exception) { return "H.264 encoder"; }
    }

    // Under `gate`: setup first whenever it changes, then the picture (7.2).
    void Emit(H264Unit unit)
    {
        if (encoders == null) return;
        byte[] avcc;
        try
        {
            var (data, sps, pps) = encoders.ToAvcc(unit.AnnexB);
            avcc = data;
            if (sps != null && pps != null)
            {
                var next = new CameraSetup(encoders.CodecString(sps), encoders.AvcC(sps, pps), encodedWidth, encodedHeight);
                if (!next.SameAs(setup))
                {
                    setup = next;
                    Broadcast(v => v.SendSetup(next));
                }
            }
        }
        catch (Exception) { return; }
        // Nothing can be decoded before a setup: wait for a keyframe that carries SPS and PPS.
        if (setup == null) { forceKey = true; return; }
        if (avcc.Length == 0) return;
        session?.Frame(avcc.Length, unit.Key);
        Broadcast(v => v.SendVideo(unit.Key, unit.Micros, avcc));
    }

    void OnSound(int fromGeneration, ReadOnlySpan<float> mono, int rate)
    {
        lock (gate)
        {
            if (fromGeneration != generation || viewers.Count == 0) return;
            foreach (var (r, bytes) in sound.Add(mono, rate)) Broadcast(v => v.SendSound(r, bytes));
        }
    }

    // The camera stopped for good: the phones are told, and the whole capture (microphone
    // included) is closed. The phones stay on the camera view, and the next phone that opens the
    // camera starts a new capture.
    void OnFailed(int fromGeneration, string text)
    {
        ICameraCapture? ended;
        lock (gate)
        {
            if (fromGeneration != generation) return;
            ended = FailUnderGate(text);
        }
        FlushDiag();
        AfterFailure(text, ended);
    }

    // Under `gate`: the phones are told, their voices no longer play, and the capture ends.
    ICameraCapture? FailUnderGate(string text)
    {
        // A phone whose camera failed earlier already has its error.
        foreach (var (socket, viewer) in viewers)
        {
            if (viewer.Failed) continue;
            viewer.Failed = true;
            try { socket.SendCameraError(text); }
            catch (Exception) { }
        }
        return EndCapture();
    }

    // Outside `gate`: the speakers stop, and the device closes off the capture's own thread
    // (closing it waits for that thread to end).
    void AfterFailure(string text, ICameraCapture? ended)
    {
        talk?.Stop();
        Log("Camera stopped: " + text);
        if (ended != null) run(() => { lock (deviceGate) Close(ended); });
    }

    // The capture's view of this service, tied to the capture it was given to.
    sealed class Sink : ICameraSink
    {
        readonly CameraService owner;
        readonly int fromGeneration;
        public Sink(CameraService owner, int fromGeneration) => (this.owner, this.fromGeneration) = (owner, fromGeneration);
        public void Frame(CameraFrame frame) => owner.OnFrame(fromGeneration, frame);
        public void Sound(ReadOnlySpan<float> firstChannel, int rate) => owner.OnSound(fromGeneration, firstChannel, rate);
        public void Failed(string text) => owner.OnFailed(fromGeneration, text);
    }

    // Quit: the camera stops before the app ends.
    public void Dispose()
    {
        try { timer?.Dispose(); }
        catch (Exception) { }
        timer = null;
        StopAll(wait: true);
        talk?.Dispose();
    }
}

// The camera's messages on the live socket, byte for byte (docs/API.md 7.2).
public static class CameraWire
{
    static readonly JsonSerializerOptions Json = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static string Setup(CameraSetup s) => JsonSerializer.Serialize(new
    {
        camera = new { codec = s.Codec, description = Convert.ToBase64String(s.Description), width = s.Width, height = s.Height },
    }, Json);

    public static string Error(string text) => JsonSerializer.Serialize(new { cameraError = text }, Json);
    public static string Stopped(string text) => JsonSerializer.Serialize(new { cameraStopped = text }, Json);

    // [3 keyframe | 4 other][Int64 µs, big-endian][AVCC].
    public static byte[] Video(bool keyframe, long micros, ReadOnlySpan<byte> avcc)
    {
        var b = new byte[9 + avcc.Length];
        b[0] = (byte)(keyframe ? 3 : 4);
        BinaryPrimitives.WriteInt64BigEndian(b.AsSpan(1, 8), micros);
        avcc.CopyTo(b.AsSpan(9));
        return b;
    }

    // [5][UInt32 sample rate, big-endian][μ-law bytes].
    public static byte[] Sound(int rate, ReadOnlySpan<byte> mulaw)
    {
        var b = new byte[5 + mulaw.Length];
        b[0] = 5;
        BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(1, 4), (uint)rate);
        mulaw.CopyTo(b.AsSpan(5));
        return b;
    }
}

// An ICameraSocket over a live socket's two ways of sending (text and binary): LiveServer makes
// one per phone.
public sealed class WireCameraSocket : ICameraSocket
{
    readonly Action<string> sendText;
    readonly Action<byte[]> sendBinary;

    public WireCameraSocket(string? sessionToken, string device, Action<string> sendText, Action<byte[]> sendBinary)
    {
        SessionToken = sessionToken;
        Device = device;
        this.sendText = sendText;
        this.sendBinary = sendBinary;
    }

    public string? SessionToken { get; }
    public string Device { get; }
    public void SendSetup(CameraSetup setup) => sendText(CameraWire.Setup(setup));
    public void SendVideo(bool keyframe, long micros, byte[] avcc) => sendBinary(CameraWire.Video(keyframe, micros, avcc));
    public void SendSound(int rate, byte[] mulaw) => sendBinary(CameraWire.Sound(rate, mulaw));
    public void SendCameraError(string text) => sendText(CameraWire.Error(text));
    public void SendCameraStopped(string text) => sendText(CameraWire.Stopped(text));
}

// Where a camera is. Windows says a camera is part of the PC when it has an enclosure location
// (front or back panel, or in the lid: DeviceInformation.EnclosureLocation); a USB camera without
// one is plugged in. Anything else comes last. Phone: a phone lent to Windows as a camera, never
// used (as on the Mac, the phone would only film itself).
public enum CameraPlace { External, BuiltIn, Other, Phone }

public sealed record CameraDevice(string Name, string Link, CameraPlace Place);

public static class CameraChoice
{
    // `inEnclosure`: Windows put the camera in the PC's enclosure; null when it couldn't say.
    public static CameraPlace PlaceOf(string name, string link, bool? inEnclosure)
    {
        if (IsPhone(name, link)) return CameraPlace.Phone;
        if (inEnclosure == true) return CameraPlace.BuiltIn;
        return link.StartsWith(@"\\?\usb#", StringComparison.OrdinalIgnoreCase) ? CameraPlace.External : CameraPlace.Other;
    }

    // A phone lent to Windows as a camera. Phone Link's "connected camera" (Windows 11) is made
    // through Windows' virtual-camera API, whose cameras have a device path under
    // SWD\VCAMDEVAPI; that path is what is checked, since the camera is named after the phone.
    // A name with iPhone, iPad or Phone Link in it counts too (the Mac's own rule, by name).
    public static bool IsPhone(string name, string link) =>
        link.Contains("#VCAMDEVAPI#", StringComparison.OrdinalIgnoreCase)
        || new[] { "iPhone", "iPad", "Phone Link" }.Any(w => name.Contains(w, StringComparison.OrdinalIgnoreCase));

    // The order to try them in: a plugged-in camera first (unlike the built-in one, it keeps
    // working with the lid closed, as on the Mac), then the built-in one, then the rest; in
    // Windows' order within each. A phone is left out.
    public static List<CameraDevice> Order(IEnumerable<CameraDevice> cameras) =>
        cameras.Where(c => c.Place != CameraPlace.Phone)
            .Select((c, i) => (c, i)).OrderBy(x => (int)x.c.Place).ThenBy(x => x.i).Select(x => x.c).ToList();
}

// One format a camera offers.
public sealed record VideoFormat(Guid Subtype, int Width, int Height, double Fps);

public static class VideoFormats
{
    public const int Width = 1280, Height = 720;

    // Which of the camera's own formats to use: 1280×720 when offered (the fastest, up to 30 a
    // second), else the biggest that fits in 1280×720, else the smallest. -1 when there are none.
    public static int Choose(IReadOnlyList<VideoFormat> formats)
    {
        int best = -1;
        (int, long, double) bestScore = default;
        for (int i = 0; i < formats.Count; i++)
        {
            var f = formats[i];
            if (f.Width <= 0 || f.Height <= 0) continue;
            bool exact = f.Width == Width && f.Height == Height;
            bool fits = f.Width <= Width && f.Height <= Height;
            long area = (long)f.Width * f.Height;
            var score = (exact ? 2 : fits ? 1 : 0, fits ? area : -area, Math.Min(f.Fps, 30));
            if (best < 0 || score.CompareTo(bestScore) > 0) { best = i; bestScore = score; }
        }
        return best;
    }
}

// The camera's pictures as NV12, whatever the reader gave.
public static class CameraImage
{
    // NV12 with rows exactly `width` bytes. A buffer with longer rows is packed; null when it
    // doesn't fit the size.
    public static byte[]? Nv12(byte[] raw, int width, int height)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0) return null;
        int size = width * height * 3 / 2;
        if (raw.Length == size) return raw;
        if (raw.Length < size) return null;
        int pitch = (int)(raw.Length * 2L / (3L * height));
        if (pitch < width || (long)pitch * height * 3 / 2 > raw.Length) return null;
        var packed = new byte[size];
        for (int row = 0; row < height * 3 / 2; row++)
            Buffer.BlockCopy(raw, row * pitch, packed, row * width, width);
        return packed;
    }

    // YUY2 (Y0 U Y1 V for every two pixels) → NV12, chroma from every other row.
    public static byte[]? Yuy2ToNv12(byte[] raw, int width, int height)
    {
        if (width <= 0 || height <= 0 || width % 2 != 0 || height % 2 != 0) return null;
        int pitch = raw.Length / height;
        if (pitch < width * 2) return null;
        var nv12 = new byte[width * height * 3 / 2];
        int uv = width * height;
        for (int y = 0; y < height; y++)
        {
            int src = y * pitch;
            int dst = y * width;
            for (int x = 0; x < width; x += 2)
            {
                nv12[dst + x] = raw[src + x * 2];
                nv12[dst + x + 1] = raw[src + x * 2 + 2];
                if ((y & 1) == 0)
                {
                    int c = uv + (y / 2) * width + x;
                    nv12[c] = raw[src + x * 2 + 1];
                    nv12[c + 1] = raw[src + x * 2 + 3];
                }
            }
        }
        return nv12;
    }
}
