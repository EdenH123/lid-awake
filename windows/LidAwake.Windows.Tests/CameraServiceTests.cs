using System.Buffers.Binary;
using System.Text.Json;
using LidAwake;
using Xunit;

// Task 22's live socket as the camera sees it: every message, in order, as text.
public sealed class FakeCameraSocket : ICameraSocket
{
    public readonly List<string> Sent = new();
    public readonly List<(bool Key, long Micros, byte[] Avcc)> Video = new();
    public readonly List<(int Rate, byte[] Bytes)> Sound = new();

    public FakeCameraSocket(string? token, string device = "iPhone") => (SessionToken, Device) = (token, device);

    public string? SessionToken { get; }
    public string Device { get; }
    public void SendSetup(CameraSetup s) { lock (Sent) Sent.Add($"setup {s.Codec} {s.Width}x{s.Height} {Convert.ToHexString(s.Description)}"); }
    public void SendVideo(bool keyframe, long micros, byte[] avcc)
    {
        lock (Sent) { Sent.Add($"video {(keyframe ? 3 : 4)} {micros}"); Video.Add((keyframe, micros, avcc)); }
    }
    public void SendSound(int rate, byte[] mulaw) { lock (Sent) { Sent.Add($"sound {rate} {mulaw.Length}"); Sound.Add((rate, mulaw)); } }
    public void SendCameraError(string text) { lock (Sent) Sent.Add("error " + text); }
    public void SendCameraStopped(string text) { lock (Sent) Sent.Add("stopped " + text); }
}

// The camera and microphone, driven by hand.
public sealed class FakeCameraHardware : ICameraHardware
{
    // The camera and its microphone: both stop on Dispose.
    public sealed class Capture : ICameraCapture
    {
        public bool Disposed;
        public bool MicLive = true;
        public void Dispose() { Disposed = true; MicLive = false; }
    }

    public readonly List<Capture> Opened = new();
    public ICameraSink? Sink;
    public string? Problem;  // set: Open fails with this sentence

    public ICameraCapture? Open(ICameraSink sink, out string? problem)
    {
        Sink = sink;
        problem = Problem;
        if (Problem != null) return null;
        var c = new Capture();
        Opened.Add(c);
        return c;
    }
}

// Task 22's encoder: a keyframe is SPS + PPS + IDR in Annex B, any other picture one slice.
public sealed class FakeCameraEncoders : IVideoEncoderFactory
{
    public static readonly byte[] Sps = { 0x67, 0x4D, 0x00, 0x28, 0xAA };
    public static readonly byte[] Pps = { 0x68, 0xEE, 0x3C, 0x80 };

    public bool Unavailable;
    public readonly List<(int Width, int Height, int Bitrate)> Created = new();
    public readonly List<bool> KeysAsked = new();
    public int Disposed;

    public IH264Encoder? Create(int width, int height, int bitsPerSecond)
    {
        if (Unavailable) return null;
        Created.Add((width, height, bitsPerSecond));
        return new Encoder(this);
    }

    sealed class Encoder : IH264Encoder
    {
        readonly FakeCameraEncoders owner;
        public Encoder(FakeCameraEncoders owner) => this.owner = owner;
        public IReadOnlyList<H264Unit> Encode(byte[] nv12, long micros, bool keyframe)
        {
            owner.KeysAsked.Add(keyframe);
            byte tag = nv12[0];
            byte[] annexB = keyframe
                ? new byte[] { 0, 0, 0, 1 }.Concat(Sps).Concat(new byte[] { 0, 0, 0, 1 }).Concat(Pps).Concat(new byte[] { 0, 0, 1, 0x65, tag }).ToArray()
                : new byte[] { 0, 0, 0, 1, 0x41, tag };
            return new[] { new H264Unit(annexB, keyframe, micros) };
        }
        public void Dispose() => owner.Disposed++;
    }

    // A plain Annex B → AVCC split, as Task 22's helper does it.
    public (byte[] Avcc, byte[]? Sps, byte[]? Pps) ToAvcc(byte[] annexB)
    {
        var nals = new List<byte[]>();
        int i = 0, start = -1;
        while (i < annexB.Length)
        {
            int code = i + 3 <= annexB.Length && annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 1 ? 3
                : i + 4 <= annexB.Length && annexB[i] == 0 && annexB[i + 1] == 0 && annexB[i + 2] == 0 && annexB[i + 3] == 1 ? 4 : 0;
            if (code > 0)
            {
                if (start >= 0) nals.Add(annexB[start..i]);
                i += code;
                start = i;
            }
            else i++;
        }
        if (start >= 0) nals.Add(annexB[start..]);
        byte[]? sps = null, pps = null;
        var avcc = new List<byte>();
        foreach (var nal in nals)
        {
            int type = nal[0] & 0x1F;
            if (type == 7) { sps = nal; continue; }
            if (type == 8) { pps = nal; continue; }
            var len = new byte[4];
            BinaryPrimitives.WriteInt32BigEndian(len, nal.Length);
            avcc.AddRange(len);
            avcc.AddRange(nal);
        }
        return (avcc.ToArray(), sps, pps);
    }

    public byte[] AvcC(byte[] sps, byte[] pps) => new byte[] { 1, sps[1], sps[2], sps[3], 0xFF, 0xE1 }.Concat(sps).Concat(pps).ToArray();
    public string CodecString(byte[] sps) => $"avc1.{sps[1]:x2}{sps[2]:x2}{sps[3]:x2}";
}

public sealed class CameraServiceTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    DateTimeOffset now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    bool allowed = true;
    readonly CameraTickets tickets;
    readonly FakeCameraHardware hardware = new();
    readonly FakeCameraEncoders encoders = new();
    readonly AccessLog log;
    readonly TalkPlayer talk;
    int talkOutputs;
    readonly CameraService camera;

    sealed class Output : ITalkOutput { public void Dispose() { } }

    public CameraServiceTests()
    {
        tickets = new CameraTickets(() => now);
        log = new AccessLog(folder, now: () => now);
        talk = new TalkPlayer(_ => { talkOutputs++; return new Output(); }, () => now);
        camera = Make(encoders);
    }

    CameraService Make(IVideoEncoderFactory? factory) =>
        new(tickets, () => allowed, hardware, factory, talk, log, () => now, a => a());

    public void Dispose()
    {
        camera.Dispose();
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
    }

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement.Clone();

    bool Send(ICameraSocket s, string json) => camera.Receive(s, Json(json));

    FakeCameraSocket Watch(string token = "session-a", string device = "iPhone")
    {
        var s = new FakeCameraSocket(token, device);
        Assert.True(Send(s, $"{{\"camera\":true,\"ticket\":\"{tickets.Issue(token)}\"}}"));
        return s;
    }

    static CameraFrame Frame(long micros, byte tag = 0, int width = 1280, int height = 720)
    {
        var nv12 = new byte[width * height * 3 / 2];
        nv12[0] = tag;
        return new CameraFrame(nv12, width, height, micros);
    }

    List<string> LogLines() => log.Recent(50).Select(l => l[(l.IndexOf("  ", StringComparison.Ordinal) + 2)..]).Reverse().ToList();

    [Fact]
    public void SetupThenPicturesThenAKeyframeOnAskingThenStop()
    {
        Assert.False(camera.Capturing);
        var phone = Watch();
        Assert.True(camera.IsOpen(phone));
        Assert.True(camera.Capturing);
        Assert.Single(hardware.Opened);
        Assert.Empty(phone.Sent);

        var sink = hardware.Sink!;
        sink.Frame(Frame(0, tag: 1));
        sink.Frame(Frame(20_000, tag: 2));    // under 40 ms after the last: skipped
        sink.Frame(Frame(50_000, tag: 3));
        Assert.True(Send(phone, "{\"cameraKey\":1}"));
        sink.Frame(Frame(100_000, tag: 4));
        Assert.Equal(new[]
        {
            "setup avc1.4d0028 1280x720 014D0028FFE1674D0028AA68EE3C80",
            "video 3 0",
            "video 4 50000",
            "video 3 100000",
        }, phone.Sent);
        Assert.Equal(new[] { (1280, 720, 1_200_000) }, encoders.Created);
        Assert.Equal(new[] { true, false, true }, encoders.KeysAsked);
        // AVCC: the IDR slice after a 4-byte big-endian length, no SPS or PPS inside.
        Assert.Equal(new byte[] { 0, 0, 0, 2, 0x65, 1 }, phone.Video[0].Avcc);
        Assert.Equal(new byte[] { 0, 0, 0, 2, 0x41, 3 }, phone.Video[1].Avcc);

        // Sound: 100 ms at 48 kHz → one message of 1600 μ-law bytes at 16 kHz.
        sink.Sound(new float[4800], 48000);
        Assert.Equal("sound 16000 1600", phone.Sent[^1]);
        Assert.All(phone.Sound[0].Bytes, b => Assert.Equal(0xFF, b));

        Assert.True(Send(phone, "{\"camera\":false}"));
        Assert.False(camera.IsOpen(phone));
        Assert.False(camera.Capturing);
        Assert.True(hardware.Opened[0].Disposed);
        Assert.Equal(1, encoders.Disposed);
        Assert.DoesNotContain(phone.Sent, m => m.StartsWith("stopped", StringComparison.Ordinal));
        // Anything the old capture still delivers goes nowhere.
        int count = phone.Sent.Count;
        sink.Frame(Frame(500_000));
        sink.Sound(new float[4800], 48000);
        Assert.Equal(count, phone.Sent.Count);
        Assert.Equal(new[] { "Camera and microphone opened from the phone · iPhone", "Camera and microphone closed · iPhone" }, LogLines());
    }

    // The tray's camera icon: every phone with the camera open counts, until it closes it.
    [Fact]
    public void WatchersCountsThePhonesWithTheCameraOpen()
    {
        Assert.Equal(0, camera.Watchers);
        var a = Watch("session-a", "iPhone");
        Assert.Equal(1, camera.Watchers);
        var b = Watch("session-b", "Android phone");
        Assert.Equal(2, camera.Watchers);
        Assert.True(Send(a, "{\"camera\":false}"));
        Assert.Equal(1, camera.Watchers);
        camera.Closed(b);
        Assert.Equal(0, camera.Watchers);
        Watch();
        camera.StopAll();
        Assert.Equal(0, camera.Watchers);
    }

    [Fact]
    public void NoTicketNoSwitchNoCamera()
    {
        var a = new FakeCameraSocket("session-a");
        Assert.True(Send(a, "{\"camera\":true}"));
        Assert.True(Send(a, "{\"camera\":true,\"ticket\":\"made-up\"}"));
        Assert.True(Send(a, $"{{\"camera\":true,\"ticket\":\"{tickets.Issue("session-b")}\"}}"));
        Assert.Equal(Enumerable.Repeat("error " + CameraTexts.NeedsPasskey, 3), a.Sent);

        allowed = false;
        var ticket = tickets.Issue("session-a");
        var b = new FakeCameraSocket("session-a");
        Assert.True(Send(b, $"{{\"camera\":true,\"ticket\":\"{ticket}\"}}"));
        Assert.Equal(new[] { "error " + CameraTexts.Off }, b.Sent);
        // The switch is checked first, so the ticket is still good.
        allowed = true;
        Assert.True(Send(b, $"{{\"camera\":true,\"ticket\":\"{ticket}\"}}"));
        Assert.True(camera.IsOpen(b));

        Assert.Single(hardware.Opened);
        Assert.False(camera.IsOpen(a));
    }

    [Fact]
    public void ClosesByItselfAfterTenMinutes()
    {
        var phone = Watch();
        now += TimeSpan.FromMinutes(10);
        camera.Tick();
        Assert.True(camera.IsOpen(phone));
        now += TimeSpan.FromSeconds(1);
        camera.Tick();
        Assert.False(camera.IsOpen(phone));
        Assert.Equal("stopped The camera closed by itself after 10 minutes. Open it again to keep watching.", phone.Sent[^1]);
        Assert.True(hardware.Opened[0].Disposed);
    }

    [Fact]
    public void TheOwnerTurningTheSwitchOffStopsIt()
    {
        var a = Watch("session-a");
        var b = Watch("session-b", "Android phone");
        allowed = false;
        camera.Tick();
        Assert.Equal("stopped " + CameraTexts.SwitchedOff, a.Sent[^1]);
        Assert.Equal("stopped The camera was turned off on the PC.", b.Sent[^1]);
        Assert.False(camera.IsOpen(a) || camera.IsOpen(b));
        Assert.True(hardware.Opened.Single().Disposed);
        Assert.Contains("Camera and microphone closed · Android phone", LogLines());
    }

    [Fact]
    public void TheSocketClosingStopsItAndTheLastViewerTurnsTheCameraOff()
    {
        var a = Watch("session-a");
        hardware.Sink!.Frame(Frame(0));
        var b = Watch("session-b");
        // The second phone gets the setup at once, and a keyframe with the next picture.
        Assert.Equal(new[] { "setup avc1.4d0028 1280x720 014D0028FFE1674D0028AA68EE3C80" }, b.Sent);
        hardware.Sink!.Frame(Frame(100_000));
        Assert.Equal("video 3 100000", b.Sent[^1]);
        Assert.Equal("video 3 100000", a.Sent[^1]);
        Assert.Single(hardware.Opened);

        camera.Closed(a);
        Assert.False(hardware.Opened[0].Disposed);
        Assert.True(camera.Capturing);
        camera.Closed(b);
        Assert.True(hardware.Opened[0].Disposed);
        Assert.False(camera.Capturing);
        camera.Closed(b);  // twice is fine

        // A new phone opens it again, with a new setup.
        var c = Watch("session-c");
        Assert.Equal(2, hardware.Opened.Count);
        hardware.Sink!.Frame(Frame(200_000));
        Assert.StartsWith("setup", c.Sent[0]);
        Assert.Equal("video 3 200000", c.Sent[1]);
    }

    [Fact]
    public void StopAllClosesEveryCamera()
    {
        var a = Watch("session-a");
        var b = Watch("session-b");
        camera.StopAll();
        Assert.False(camera.IsOpen(a) || camera.IsOpen(b));
        Assert.False(camera.IsOpen(a));
        Assert.False(camera.IsOpen(b));
        Assert.True(hardware.Opened.Single().Disposed);
        Assert.Empty(a.Sent);
        Assert.Equal(2, LogLines().Count(l => l.StartsWith("Camera and microphone closed", StringComparison.Ordinal)));
    }

    [Fact]
    public void AProblemOpeningTheCameraIsSaid()
    {
        hardware.Problem = CameraTexts.LidClosed;
        var phone = Watch();
        Assert.Equal(new[] { "error " + CameraTexts.LidClosed }, phone.Sent);
        Assert.True(camera.IsOpen(phone));
        Assert.False(camera.Capturing);
        // The next phone tries again.
        hardware.Problem = null;
        Watch("session-b");
        Assert.True(camera.Capturing);
    }

    [Fact]
    public void WithoutAnEncoderOnlySoundGoesAndItSaysSoOnce()
    {
        camera.Dispose();
        using var noVideo = new CameraService(tickets, () => allowed, hardware, null, talk, log, () => now, a => a());
        var phone = new FakeCameraSocket("s");
        noVideo.Receive(phone, Json($"{{\"camera\":true,\"ticket\":\"{tickets.Issue("s")}\"}}"));
        hardware.Sink!.Frame(Frame(0));
        hardware.Sink!.Frame(Frame(100_000));
        hardware.Sink!.Sound(new float[1600], 16000);
        Assert.Equal(new[] { "error " + CameraTexts.NoEncoder, "sound 16000 1600" }, phone.Sent);

        encoders.Unavailable = true;
        using var noH264 = new CameraService(tickets, () => allowed, hardware, encoders, talk, log, () => now, a => a());
        var other = new FakeCameraSocket("t");
        noH264.Receive(other, Json($"{{\"camera\":true,\"ticket\":\"{tickets.Issue("t")}\"}}"));
        hardware.Sink!.Frame(Frame(0));
        hardware.Sink!.Frame(Frame(100_000));
        Assert.Equal(new[] { "error " + CameraTexts.NoEncoder }, other.Sent);
    }

    [Fact]
    public void ACameraThatStopsSendingEndsTheCaptureLikeAFailure()
    {
        var phone = Watch();
        var sink = hardware.Sink!;
        sink.Frame(Frame(0));
        var capture = hardware.Opened.Single();
        int before = phone.Sent.Count;
        now += TimeSpan.FromSeconds(4);
        camera.Tick();
        Assert.Equal(before, phone.Sent.Count);
        Assert.True(camera.Capturing);
        now += TimeSpan.FromSeconds(1);
        camera.Tick();
        camera.Tick();
        // Said once, and the whole capture ended: the camera and its microphone are off.
        Assert.Equal("error " + CameraTexts.Stalled, phone.Sent[^1]);
        Assert.Equal(before + 1, phone.Sent.Count);
        Assert.False(camera.Capturing);
        Assert.True(capture.Disposed);
        Assert.False(capture.MicLive);
        Assert.Equal(1, encoders.Disposed);
        Assert.Contains("Camera stopped: " + CameraTexts.Stalled, LogLines());
        // A late picture or sound from the stalled capture changes nothing.
        sink.Frame(Frame(100_000));
        sink.Sound(new float[1600], 16000);
        Assert.Equal(before + 1, phone.Sent.Count);
        // The phone stays on the camera view; opening it again starts a new capture.
        Assert.True(camera.IsOpen(phone));
        Assert.True(Send(phone, $"{{\"camera\":true,\"ticket\":\"{tickets.Issue("session-a")}\"}}"));
        Assert.Equal(2, hardware.Opened.Count);
        Assert.True(camera.Capturing);
    }

    [Fact]
    public void AfterAFailureThePhonesVoiceNoLongerPlays()
    {
        var phone = Watch();
        Assert.True(Send(phone, $"{{\"talk\":\"{Talk(640)}\"}}"));
        Assert.True(talk.Playing);
        hardware.Sink!.Failed(CameraTexts.NotAllowed);
        // The speakers stop with the camera, and what the phone says next isn't played.
        Assert.False(talk.Playing);
        int outputs = talkOutputs;
        Assert.True(Send(phone, $"{{\"talk\":\"{Talk(640)}\"}}"));
        Assert.False(talk.Playing);
        Assert.Equal(0, talk.SecondsAhead);
        Assert.Equal(outputs, talkOutputs);
        // Once it opens the camera again, it may talk again.
        Assert.True(Send(phone, $"{{\"camera\":true,\"ticket\":\"{tickets.Issue("session-a")}\"}}"));
        Assert.True(Send(phone, $"{{\"talk\":\"{Talk(640)}\"}}"));
        Assert.True(talk.Playing);
    }

    [Fact]
    public void AStallStopsTheVoiceToo()
    {
        var phone = Watch();
        Assert.True(Send(phone, $"{{\"talk\":\"{Talk(640)}\"}}"));
        Assert.True(talk.Playing);
        now += TimeSpan.FromSeconds(5);
        camera.Tick();
        Assert.False(talk.Playing);
        Assert.True(Send(phone, $"{{\"talk\":\"{Talk(640)}\"}}"));
        Assert.False(talk.Playing);
    }

    [Fact]
    public void ACameraThatFailsEndsTheWholeCaptureMicrophoneIncluded()
    {
        var phone = Watch();
        var sink = hardware.Sink!;
        sink.Frame(Frame(0));
        sink.Sound(new float[1600], 16000);
        var capture = hardware.Opened.Single();
        Assert.True(capture.MicLive);

        sink.Failed(CameraTexts.NotAllowed);
        Assert.Equal("error " + CameraTexts.NotAllowed, phone.Sent[^1]);
        Assert.False(camera.Capturing);
        Assert.True(capture.Disposed);
        Assert.False(capture.MicLive);
        Assert.Equal(1, encoders.Disposed);
        // The phone stays on the camera view, but nothing more comes from the failed capture.
        Assert.True(camera.IsOpen(phone));
        int count = phone.Sent.Count;
        sink.Sound(new float[1600], 16000);
        sink.Frame(Frame(100_000));
        sink.Failed(CameraTexts.Stalled);
        Assert.Equal(count, phone.Sent.Count);
        Assert.Contains("Camera stopped: " + CameraTexts.NotAllowed, LogLines());

        // The next phone opens it again, with a new setup.
        var next = Watch("session-b");
        Assert.Equal(2, hardware.Opened.Count);
        Assert.True(camera.Capturing);
        hardware.Sink!.Frame(Frame(200_000));
        Assert.StartsWith("setup", next.Sent[0]);
        Assert.Equal("video 3 200000", next.Sent[1]);
        // The 2-second check finds nothing more to say about the failed one.
        camera.Tick();
        Assert.True(camera.IsOpen(phone));
    }

    [Fact]
    public void AFailureWhileTheCameraIsStillStartingClosesItToo()
    {
        var slow = new SlowHardware();
        using var service = new CameraService(tickets, () => allowed, slow, encoders, talk, log, () => now, a => a());
        var phone = new FakeCameraSocket("s");
        service.Receive(phone, Json($"{{\"camera\":true,\"ticket\":\"{tickets.Issue("s")}\"}}"));
        Assert.Equal(new[] { "error " + CameraTexts.Stalled }, phone.Sent);
        Assert.False(service.Capturing);
        Assert.True(slow.Started!.Disposed);
        Assert.False(slow.Started!.MicLive);
    }

    // Fails from inside Open, before Open has returned the capture.
    sealed class SlowHardware : ICameraHardware
    {
        public FakeCameraHardware.Capture? Started;
        public ICameraCapture? Open(ICameraSink sink, out string? problem)
        {
            problem = null;
            Started = new FakeCameraHardware.Capture();
            sink.Failed(CameraTexts.Stalled);
            return Started;
        }
    }

    [Fact]
    public void DisposeWaitsOnlyAboutThreeSecondsForTheDevice()
    {
        var stuck = new StuckHardware();
        var service = new CameraService(tickets, () => allowed, stuck, encoders, talk, log, () => now, a => Task.Run(a));
        var phone = new FakeCameraSocket("s");
        service.Receive(phone, Json($"{{\"camera\":true,\"ticket\":\"{tickets.Issue("s")}\"}}"));
        Assert.True(stuck.Entered.Wait(TimeSpan.FromSeconds(10)));
        var watch = System.Diagnostics.Stopwatch.StartNew();
        service.Dispose();
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(2.5), TimeSpan.FromSeconds(6));
        Assert.False(service.IsOpen(phone));
        stuck.Release.Set();
        // The open that finished after everyone left is closed at once.
        Assert.True(SpinWait.SpinUntil(() => stuck.Started?.Disposed == true, TimeSpan.FromSeconds(10)));
    }

    // A camera whose open hangs until the test lets it go.
    sealed class StuckHardware : ICameraHardware
    {
        public readonly ManualResetEventSlim Entered = new();
        public readonly ManualResetEventSlim Release = new();
        public volatile FakeCameraHardware.Capture? Started;
        public ICameraCapture? Open(ICameraSink sink, out string? problem)
        {
            problem = null;
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(30));
            return Started = new FakeCameraHardware.Capture();
        }
    }

    static string Talk(int bytes) => Convert.ToBase64String(Enumerable.Repeat((byte)0x80, bytes).ToArray());

    [Fact]
    public void TalkOnlyWhileTheCameraIsOpenAndAllowed()
    {
        var stranger = new FakeCameraSocket("session-x");
        Assert.True(Send(stranger, $"{{\"talk\":\"{Talk(640)}\"}}"));
        Assert.Equal(0, talkOutputs);

        var phone = Watch();
        Assert.True(Send(phone, $"{{\"talk\":\"{Talk(640)}\"}}"));
        Assert.Equal(1, talkOutputs);
        Assert.Equal(0.04, talk.SecondsAhead, 6);
        Assert.True(Send(phone, $"{{\"talk\":\"{Talk(4000)}\"}}"));
        Assert.Equal(0.29, talk.SecondsAhead, 6);
        Assert.True(Send(phone, $"{{\"talk\":\"{Talk(4001)}\"}}"));  // too much: ignored
        Assert.True(Send(phone, "{\"talk\":\"not base64!\"}"));
        Assert.True(Send(phone, "{\"talk\":\"\"}"));
        Assert.Equal(0.29, talk.SecondsAhead, 6);
        Assert.False(Send(phone, "{\"talk\":5}"));  // not a camera message

        allowed = false;  // before the 2-second check has closed it
        Assert.True(Send(phone, $"{{\"talk\":\"{Talk(640)}\"}}"));
        Assert.Equal(0.29, talk.SecondsAhead, 6);
        Assert.Single(LogLines(), l => l == "Talked through the PC's speakers · iPhone");

        // Closing the camera stops the voice too.
        allowed = true;
        Send(phone, "{\"camera\":false}");
        Assert.False(talk.Playing);
    }

    [Fact]
    public void OtherMessagesAreLeftToTheSocket()
    {
        var phone = new FakeCameraSocket("s");
        Assert.False(Send(phone, "{\"quality\":\"fast\"}"));
        Assert.False(Send(phone, "{\"type\":\"click\"}"));
        Assert.False(Send(phone, "{\"camera\":\"yes\"}"));
        Assert.False(Send(phone, "[1,2]"));
        Assert.False(Send(phone, "\"camera\""));
        Assert.True(Send(phone, "{\"cameraKey\":null}"));
        Assert.Empty(phone.Sent);
    }

    [Fact]
    public void ANewPictureSizeMakesANewEncoderAndSetup()
    {
        var phone = Watch();
        hardware.Sink!.Frame(Frame(0));
        hardware.Sink!.Frame(Frame(100_000, width: 640, height: 480));
        Assert.Equal(new[] { (1280, 720, 1_200_000), (640, 480, 1_200_000) }, encoders.Created);
        Assert.Equal("setup avc1.4d0028 640x480 014D0028FFE1674D0028AA68EE3C80", phone.Sent[2]);
        Assert.Equal("video 3 100000", phone.Sent[3]);
    }
}

public class CameraWireTests
{
    [Fact]
    public void VideoAndSoundBytes()
    {
        Assert.Equal(new byte[] { 3, 1, 2, 3, 4, 5, 6, 7, 8, 9, 9 }, CameraWire.Video(true, 0x0102030405060708, new byte[] { 9, 9 }));
        Assert.Equal(new byte[] { 4, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFE }, CameraWire.Video(false, -2, ReadOnlySpan<byte>.Empty));
        Assert.Equal(new byte[] { 5, 0, 0, 0x3E, 0x80, 0xFF, 0x00 }, CameraWire.Sound(16000, new byte[] { 0xFF, 0x00 }));
    }

    [Fact]
    public void TextMessages()
    {
        var setup = JsonDocument.Parse(CameraWire.Setup(new CameraSetup("avc1.4d0028", new byte[] { 1, 2, 3 }, 1280, 720))).RootElement.GetProperty("camera");
        Assert.Equal("avc1.4d0028", setup.GetProperty("codec").GetString());
        Assert.Equal("AQID", setup.GetProperty("description").GetString());
        Assert.Equal(1280, setup.GetProperty("width").GetInt32());
        Assert.Equal(720, setup.GetProperty("height").GetInt32());
        Assert.Equal(4, setup.EnumerateObject().Count());
        Assert.Equal("{\"cameraError\":\"\"}", CameraWire.Error(""));
        Assert.Equal("{\"cameraStopped\":\"The camera was turned off on the PC.\"}", CameraWire.Stopped(CameraTexts.SwitchedOff));
        // Sent as the characters themselves, like the Mac.
        Assert.Contains("“Allow camera & microphone from phone”", CameraWire.Error(CameraTexts.Off));
    }

    [Fact]
    public void TheWireSocketSendsTheseBytes()
    {
        var texts = new List<string>();
        var binaries = new List<byte[]>();
        var s = new WireCameraSocket("tok", "iPhone", texts.Add, binaries.Add);
        Assert.Equal("tok", s.SessionToken);
        Assert.Equal("iPhone", s.Device);
        s.SendSetup(new CameraSetup("avc1.640028", new byte[] { 7 }, 2, 2));
        s.SendVideo(false, 5, new byte[] { 1 });
        s.SendSound(8000, new byte[] { 2 });
        s.SendCameraError("x");
        s.SendCameraStopped("y");
        Assert.Equal(new[] { CameraWire.Setup(new CameraSetup("avc1.640028", new byte[] { 7 }, 2, 2)), "{\"cameraError\":\"x\"}", "{\"cameraStopped\":\"y\"}" }, texts);
        Assert.Equal(new[] { CameraWire.Video(false, 5, new byte[] { 1 }), CameraWire.Sound(8000, new byte[] { 2 }) }, binaries);
    }

    [Fact]
    public void SetupsCompareByContent()
    {
        var a = new CameraSetup("c", new byte[] { 1, 2 }, 1, 1);
        Assert.True(a.SameAs(new CameraSetup("c", new byte[] { 1, 2 }, 1, 1)));
        Assert.False(a.SameAs(new CameraSetup("c", new byte[] { 1, 3 }, 1, 1)));
        Assert.False(a.SameAs(new CameraSetup("c", new byte[] { 1, 2 }, 2, 1)));
        Assert.False(a.SameAs(null));
    }
}

public class CameraChoiceTests
{
    const string Usb = @"\\?\usb#vid_046d&pid_085b&mi_00#7&1e1d#0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\global";
    const string UsbInternal = @"\\?\USB#VID_0BDA&PID_58FD&MI_00#6&2d4#0000#{e5323777-f976-4f5b-9b55-b94699c46e44}\GLOBAL";
    const string Virtual = @"\\?\SWD#VCAMDEVAPI#{e5323777-f976-4f5b-9b55-b94699c46e44}";

    const string Dshow = @"\\?\ROOT#IMAGE#0000#{e5323777-f976-4f5b-9b55-b94699c46e44}";

    [Fact]
    public void WhereACameraIs()
    {
        Assert.Equal(CameraPlace.External, CameraChoice.PlaceOf("Logitech C920", Usb, false));
        Assert.Equal(CameraPlace.External, CameraChoice.PlaceOf("Logitech C920", Usb, null));
        Assert.Equal(CameraPlace.BuiltIn, CameraChoice.PlaceOf("Integrated Camera", UsbInternal, true));
        Assert.Equal(CameraPlace.Other, CameraChoice.PlaceOf("Some camera", Dshow, false));
        Assert.Equal(CameraPlace.Other, CameraChoice.PlaceOf("", "", null));
    }

    [Fact]
    public void APhoneLentAsACameraIsNeverUsed()
    {
        // Phone Link's connected camera: Windows' virtual-camera API, named after the phone.
        Assert.Equal(CameraPlace.Phone, CameraChoice.PlaceOf("Galaxy S24", Virtual, null));
        Assert.Equal(CameraPlace.Phone, CameraChoice.PlaceOf("Galaxy S24", Virtual.ToLowerInvariant(), false));
        Assert.Equal(CameraPlace.Phone, CameraChoice.PlaceOf("Eden's iPhone Camera", Usb, false));
        Assert.Equal(CameraPlace.Phone, CameraChoice.PlaceOf("iPad camera", Usb, true));
        Assert.Equal(CameraPlace.Phone, CameraChoice.PlaceOf("Phone Link camera", Dshow, null));
        var list = new[]
        {
            new CameraDevice("Galaxy S24", Virtual, CameraPlace.Phone),
            new CameraDevice("Integrated Camera", UsbInternal, CameraPlace.BuiltIn),
        };
        Assert.Equal(new[] { "Integrated Camera" }, CameraChoice.Order(list).Select(c => c.Name));
        Assert.Empty(CameraChoice.Order(list.Take(1)));
    }

    [Fact]
    public void APluggedInCameraComesFirst()
    {
        var list = new[]
        {
            new CameraDevice("Integrated Camera", UsbInternal, CameraPlace.BuiltIn),
            new CameraDevice("Some other camera", Dshow, CameraPlace.Other),
            new CameraDevice("Logitech C920", Usb, CameraPlace.External),
            new CameraDevice("Second USB", Usb + "2", CameraPlace.External),
        };
        Assert.Equal(new[] { "Logitech C920", "Second USB", "Integrated Camera", "Some other camera" },
            CameraChoice.Order(list).Select(c => c.Name));
        Assert.Equal(new[] { "Integrated Camera" }, CameraChoice.Order(list.Take(1)).Select(c => c.Name));
        Assert.Empty(CameraChoice.Order(Array.Empty<CameraDevice>()));
    }

    static readonly Guid Mjpg = new("47504a4d-0000-0010-8000-00aa00389b71");
    static readonly Guid Yuy2 = new("32595559-0000-0010-8000-00aa00389b71");

    [Fact]
    public void SevenTwentyPWhenOffered()
    {
        var formats = new[]
        {
            new VideoFormat(Yuy2, 640, 480, 30),
            new VideoFormat(Yuy2, 1280, 720, 10),
            new VideoFormat(Mjpg, 1920, 1080, 30),
            new VideoFormat(Mjpg, 1280, 720, 30),
            new VideoFormat(Mjpg, 1280, 720, 60),
        };
        Assert.Equal(3, VideoFormats.Choose(formats));  // 60 counts as 30: the first at 30 wins
        Assert.Equal(1, VideoFormats.Choose(formats.Take(3).ToList()));
        Assert.Equal(0, VideoFormats.Choose(new[] { formats[0], formats[2] }));  // the biggest that fits
        Assert.Equal(0, VideoFormats.Choose(new[] { new VideoFormat(Mjpg, 1920, 1080, 30), new VideoFormat(Mjpg, 3840, 2160, 30) }));
        Assert.Equal(-1, VideoFormats.Choose(Array.Empty<VideoFormat>()));
        Assert.Equal(-1, VideoFormats.Choose(new[] { new VideoFormat(Mjpg, 0, 0, 30) }));
    }

    [Fact]
    public void Nv12IsPackedToItsWidth()
    {
        var exact = Enumerable.Range(0, 4 * 2 * 3 / 2).Select(i => (byte)i).ToArray();
        Assert.Same(exact, CameraImage.Nv12(exact, 4, 2));
        // Rows of 6 bytes for a 4-pixel picture: 3 rows (2 of Y, 1 of UV).
        var padded = new byte[] { 1, 2, 3, 4, 0, 0, 5, 6, 7, 8, 0, 0, 9, 10, 11, 12, 0, 0 };
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12 }, CameraImage.Nv12(padded, 4, 2));
        Assert.Null(CameraImage.Nv12(new byte[5], 4, 2));
        Assert.Null(CameraImage.Nv12(new byte[12], 3, 2));
    }

    [Fact]
    public void Yuy2BecomesNv12()
    {
        // 4×2: row 0 Y 10 11 12 13, U 100 / V 200 then U 101 / V 201; row 1 Y 20..23.
        var yuy2 = new byte[] { 10, 100, 11, 200, 12, 101, 13, 201, 20, 50, 21, 60, 22, 51, 23, 61 };
        Assert.Equal(new byte[] { 10, 11, 12, 13, 20, 21, 22, 23, 100, 200, 101, 201 }, CameraImage.Yuy2ToNv12(yuy2, 4, 2));
        Assert.Null(CameraImage.Yuy2ToNv12(new byte[4], 4, 2));
    }
}

public class CameraFeatureTests
{
    [Fact]
    public void OnlyWithAllThreeSeams()
    {
        var passkeys = new FakeCameraPasskeys();
        var encoders = new FakeCameraEncoders();
        Assert.True(CameraFeature.Ready(passkeys, encoders, true));
        Assert.False(CameraFeature.Ready(null, encoders, true));
        Assert.False(CameraFeature.Ready(passkeys, null, true));
        Assert.False(CameraFeature.Ready(passkeys, encoders, false));
    }

    static JsonElement Run(StatusInfo info) =>
        JsonDocument.Parse(JsonSerializer.Serialize(info.Build(true))).RootElement.Clone();

    [Fact]
    public void StatusSaysCameraAllowedAndOffersCameraOnlyWhenWired()
    {
        var info = new StatusInfo(new FakeReadings());
        var s = Run(info);
        Assert.False(s.GetProperty("cameraAllowed").GetBoolean());
        Assert.DoesNotContain("camera", s.GetProperty("features").EnumerateArray().Select(f => f.GetString()));

        info.CameraAllowed = () => true;
        Assert.True(Run(info).GetProperty("cameraAllowed").GetBoolean());
        Assert.DoesNotContain("camera", Run(info).GetProperty("features").EnumerateArray().Select(f => f.GetString()));

        info.CameraFeature = true;
        Assert.Equal(new[] { "limits", "history", "passkeys", "push", "camera" }, Run(info).GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray());
        // The shared lists are untouched.
        Assert.Equal(new[] { "limits", "history", "passkeys", "push" }, StatusInfo.Features);
        Assert.Equal(new[] { "limits", "history", "heat", "passkeys", "push" }, StatusInfo.FeaturesWithHeat);
    }
}
