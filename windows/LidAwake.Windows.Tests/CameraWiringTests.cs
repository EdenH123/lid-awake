using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using LidAwake;
using Xunit;
using Xunit.Abstractions;

// The camera joined to the rest: Task 18's passkeys (camera-purpose challenges only), the
// screen's H.264 encoder (NV12 in, Annex B out), and the live socket (camera messages first, no
// screen while it is open, closing stops it, and the session it redeems tickets for).
public class CameraPasskeyTests : IDisposable
{
    const string Host = "your-pc.your-tailnet.ts.net";
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Passkeys passkeys;
    readonly PasskeyCamera camera;
    readonly SoftwarePhone phone = new(Host);

    public CameraPasskeyTests()
    {
        passkeys = new Passkeys(new Settings(folder));
        camera = new PasskeyCamera(passkeys);
        var (cd, att) = phone.Register(passkeys.Challenge("create"));
        Assert.True(passkeys.Register(cd, att, Host));
    }

    public void Dispose()
    {
        phone.Dispose();
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    bool OpenCamera(string challenge)
    {
        var (cd, ad, sig) = phone.SignIn(challenge);
        return camera.VerifyCamera(phone.Id, cd, ad, sig, Host);
    }

    bool SignIn(string challenge)
    {
        var (cd, ad, sig) = phone.SignIn(challenge);
        return passkeys.Verify(phone.Id, cd, ad, sig, Host);
    }

    [Fact]
    public void TheCameraTakesOnlyCameraChallenges()
    {
        Assert.Equal(new[] { phone.Id }, camera.CredentialIds());
        Assert.True(OpenCamera(camera.CameraChallenge()));
        // A sign-in assertion can't be replayed to open the camera...
        var signIn = passkeys.Challenge("get");
        Assert.False(OpenCamera(signIn));
        Assert.False(SignIn(signIn), "and the challenge was used up");
        // ...and a camera assertion can't sign in.
        var forCamera = camera.CameraChallenge();
        Assert.False(SignIn(forCamera));
        Assert.False(OpenCamera(forCamera), "and the challenge was used up");
        // An adding-a-key challenge opens nothing either.
        Assert.False(OpenCamera(passkeys.Challenge("create")));
        // Each camera challenge works once.
        var once = camera.CameraChallenge();
        Assert.True(OpenCamera(once));
        Assert.False(OpenCamera(once));
    }
}

public class CameraEncoderTests
{
    readonly ITestOutputHelper output;
    public CameraEncoderTests(ITestOutputHelper output) => this.output = output;

    static readonly byte[] Sps = { 0x67, 0x4D, 0x00, 0x28, 0xAA };
    static readonly byte[] Pps = { 0x68, 0xEE, 0x3C, 0x80 };

    [Fact]
    public void AvccBackToAnnexBAndThroughTheFactory()
    {
        var avcc = Avcc.ToAvcc(new[] { new byte[] { 0x65, 1, 2, 3 }, new byte[] { 0x65, 4 } });
        var annexB = AnnexB.FromAvcc(avcc, Sps, Pps);
        Assert.Equal(new byte[] { 0, 0, 0, 1 }.Concat(Sps).Concat(new byte[] { 0, 0, 0, 1 }).Concat(Pps)
            .Concat(new byte[] { 0, 0, 0, 1, 0x65, 1, 2, 3, 0, 0, 0, 1, 0x65, 4 }), annexB);
        var factory = new MfCameraEncoders();
        var (back, sps, pps) = factory.ToAvcc(annexB);
        Assert.Equal(avcc, back);
        Assert.Equal(Sps, sps);
        Assert.Equal(Pps, pps);
        Assert.Equal(Avcc.AvcC(Sps, Pps), factory.AvcC(Sps, Pps));
        Assert.Equal("avc1.4d0028", factory.CodecString(Sps));
        // No parameter sets: only the picture; a broken length stops there.
        Assert.Equal(new byte[] { 0, 0, 0, 1, 0x41, 9 }, AnnexB.FromAvcc(new byte[] { 0, 0, 0, 2, 0x41, 9, 0, 0, 0, 50, 1 }));
    }

    [Fact]
    public void NoEncoderIsNull()
    {
        var notes = new List<string>();
        var factory = new MfCameraEncoders(make: (_, _, _, _) => throw new InvalidOperationException("No H.264 encoder on this PC."), note: notes.Add);
        Assert.Null(factory.Create(1280, 720, CameraService.Bitrate));
        Assert.Contains(notes, n => n.Contains("No H.264 encoder", StringComparison.Ordinal));
    }

    // The real Media Foundation encoder with camera-style NV12 pictures, on the runner: a keyframe
    // that carries SPS and PPS, which the camera's setup is made from. Skipped without an encoder.
    [Fact]
    public void TheRealEncoderTakesNv12()
    {
        var factory = new MfCameraEncoders(allowHardware: false);
        using var encoder = factory.Create(320, 240, CameraService.Bitrate);
        if (encoder == null) { output.WriteLine("RUNNER: no H.264 encoder for the camera"); return; }
        var units = new List<H264Unit>();
        for (int i = 0; i < 6; i++)
        {
            var nv12 = new byte[320 * 240 * 3 / 2];
            for (int p = 0; p < 320 * 240; p++) nv12[p] = (byte)((p + i * 7) & 0xFF);
            Array.Fill(nv12, (byte)128, 320 * 240, 320 * 240 / 2);
            units.AddRange(encoder.Encode(nv12, i * 40_000L, keyframe: i == 0));
        }
        Assert.NotEmpty(units);
        var first = units[0];
        Assert.True(first.Key);
        var (avcc, sps, pps) = factory.ToAvcc(first.AnnexB);
        Assert.NotNull(sps);
        Assert.NotNull(pps);
        Assert.NotEmpty(avcc);
        Assert.StartsWith("avc1.", factory.CodecString(sps!));
        output.WriteLine($"RUNNER: camera encoder {factory.CodecString(sps!)}, {units.Count} pictures, sizes {string.Join(",", units.Select(u => u.AnnexB.Length))}");
    }
}

// The camera's microphone never comes on after the camera failed (CameraHardware.Running).
public class CameraRunningTests
{
    sealed class Recorder : ICameraSink
    {
        public readonly List<string> Failures = new();
        public int Sounds;
        public void Frame(CameraFrame frame) { }
        public void Sound(ReadOnlySpan<float> firstChannel, int rate) => Sounds++;
        public void Failed(string text) => Failures.Add(text);
    }

    sealed class Thing : IDisposable
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    [Fact]
    public void AFailureBeforeStartMeansNoMicrophone()
    {
        var sink = new Recorder();
        int opens = 0;
        var running = new CameraHardware.Running(sink, _ => { opens++; return new Thing(); });
        running.Failed(CameraTexts.Busy);  // the camera failed while it was still opening
        var video = new Thing();
        running.Start(video);
        Assert.Equal(0, opens);
        Assert.Equal(new[] { CameraTexts.Busy }, sink.Failures);
        running.Dispose();
        Assert.True(video.Disposed);
    }

    [Fact]
    public void AFailureWhileTheMicrophoneStartsStopsIt()
    {
        var sink = new Recorder();
        var mic = new Thing();
        CameraHardware.Running? running = null;
        running = new CameraHardware.Running(sink, sound =>
        {
            running!.Failed(CameraTexts.Stalled);
            return mic;
        });
        running.Start(new Thing());
        Assert.True(mic.Disposed);
    }

    [Fact]
    public void TheMicrophoneStopsWithTheCamera()
    {
        var sink = new Recorder();
        var mic = new Thing();
        Action<float[], int>? deliver = null;
        var running = new CameraHardware.Running(sink, sound => { deliver = sound; return mic; });
        running.Start(new Thing());
        deliver!(new float[160], 16000);
        Assert.Equal(1, sink.Sounds);
        running.Failed(CameraTexts.NotAllowed);
        Assert.True(mic.Disposed);
        deliver(new float[160], 16000);
        Assert.Equal(1, sink.Sounds);
    }
}

// The camera on the live socket, end to end, with a fake camera and encoder.
[Collection("WebServer")]
public class CameraSocketTests
{
    static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
    readonly CameraTickets tickets = new();
    readonly FakeCameraHardware hardware = new();
    readonly FakeCameraEncoders encoders = new();
    bool allowed = true;

    CameraService NewCamera() => new(tickets, () => allowed, hardware, encoders, null, null, null, a => a());

    static string Body((bool Text, byte[] Data) m) => Encoding.UTF8.GetString(m.Data);
    static bool IsCameraSetup((bool Text, byte[] Data) m) => m.Text && Body(m).StartsWith("{\"camera\":{", StringComparison.Ordinal);
    static bool IsCameraVideo((bool Text, byte[] Data) m) => !m.Text && m.Data.Length > 9 && m.Data[0] is 3 or 4;

    static CameraFrame Frame(long micros) => new(new byte[1280 * 720 * 3 / 2], 1280, 720, micros);

    static async Task<bool> Until(Func<bool> condition)
    {
        var end = DateTime.UtcNow + Soon;
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            await Task.Delay(25);
        }
        return condition();
    }

    async Task<LiveClient> OpenCamera(LiveHarness h, string? cookie = null, string? token = null)
    {
        var c = await h.Connect(cookie: cookie ?? "");
        Assert.Equal(101, c.Status);
        await c.Send($"{{\"camera\":true,\"ticket\":\"{tickets.Issue(token ?? h.Token)}\"}}");
        return c;
    }

    [Fact]
    public async Task TheCameraReplacesTheScreenAndClosingTheSocketStopsIt()
    {
        using var camera = NewCamera();
        await using var h = new LiveHarness(camera: camera);
        var c = await h.Connect();
        // A screen phone first (it asks for video).
        await c.Send("{\"quality\":\"balanced\",\"acks\":true,\"video\":true,\"why\":\"\"}");
        c.AutoAck = true;
        Assert.NotNull(await c.WaitFor(LiveClient.IsVideo, Soon));
        await c.Send($"{{\"camera\":true,\"ticket\":\"{tickets.Issue(h.Token)}\"}}");
        Assert.True(await Until(() => camera.Capturing));
        // Whatever of the screen was already on its way arrives first.
        await c.Collect(TimeSpan.FromSeconds(0.3));
        hardware.Sink!.Frame(Frame(0));
        Assert.NotNull(await c.WaitFor(IsCameraSetup, Soon));
        Assert.NotNull(await c.WaitFor(IsCameraVideo, Soon));
        // Video for the screen can't be turned back on while the camera is open.
        await c.Send("{\"video\":true,\"why\":\"test\"}");
        await c.Send("{\"videoRestart\":true,\"why\":\"test\"}");
        hardware.Sink!.Frame(Frame(50_000));
        var after = await c.Collect(TimeSpan.FromSeconds(1));
        Assert.DoesNotContain(after, m => LiveClient.IsJpeg(m) || LiveClient.IsVideo(m) || LiveClient.IsSetup(m));
        Assert.Contains(after, IsCameraVideo);

        // The socket closes: the camera stops, and its light goes off.
        await c.DisposeAsync();
        Assert.True(await Until(() => !camera.Capturing));
        Assert.True(hardware.Opened.Single().Disposed);
        Assert.False(hardware.Opened.Single().MicLive);
    }

    [Fact]
    public async Task ASignedOutSessionStopsTheCameraAtTheTwoSecondCheck()
    {
        using var camera = NewCamera();
        await using var h = new LiveHarness(video: false, camera: camera);
        var c = await OpenCamera(h);
        Assert.True(await Until(() => camera.Capturing));
        h.Auth.Logout(h.Token);
        Assert.True(await c.WaitClosed(TimeSpan.FromSeconds(5)));
        Assert.True(await Until(() => !camera.Capturing));
        Assert.True(hardware.Opened.Single().Disposed);
    }

    [Fact]
    public async Task TheSocketRedeemsTicketsForTheSessionUnlockChose()
    {
        using var camera = NewCamera();
        await using var h = new LiveHarness(video: false, camera: camera);
        var second = h.Auth.Login("correct horse battery", "iPad").Token!;
        // A stale cookie first, then two valid ones: both pick the first valid one (`second`).
        var tokens = new[] { new string('a', 64), second, h.Token };
        var cookie = string.Join("; ", tokens.Select(t => $"{Auth.CookieName}={t}"));
        Assert.Equal(second, WebServer.SessionOf(h.Auth, tokens));

        // POST /camera/unlock's own path: the ticket is bound to that session.
        var access = new CameraAccess(new FakeCameraPasskeys(), tickets, () => true);
        var body = JsonDocument.Parse("{\"id\":\"cred-1\",\"clientData\":\"AA\",\"authenticatorData\":\"AA\",\"signature\":\"AA\"}")
            .RootElement.EnumerateObject().ToDictionary(p => p.Name, p => p.Value.Clone());
        var answer = access.Unlock(LiveClient.TailHost, WebServer.SessionOf(h.Auth, tokens), body);
        Assert.Equal(200, answer.Code);
        var ticket = JsonDocument.Parse(answer.Json!).RootElement.GetProperty("ticket").GetString();

        var c = await h.Connect(cookie: cookie);
        Assert.Equal(101, c.Status);
        await c.Send($"{{\"camera\":true,\"ticket\":\"{ticket}\"}}");
        Assert.True(await Until(() => camera.Capturing));

        // A ticket for the other valid session in the same cookies is not this socket's.
        var d = await h.Connect(cookie: cookie);
        await c.DisposeAsync();
        Assert.True(await Until(() => !camera.Capturing));
        await d.Send($"{{\"camera\":true,\"ticket\":\"{tickets.Issue(h.Token)}\"}}");
        var error = await d.WaitFor(m => m.Text && Body(m).Contains("cameraError", StringComparison.Ordinal), Soon);
        Assert.NotNull(error);
        Assert.Contains("Face ID is needed", Body(error!.Value));
        Assert.False(camera.Capturing);
    }

    // A phone watching the camera isn't watching the screen: it doesn't hold the display (7.7).
    [Fact]
    public async Task APhoneOnItsCameraIsNoScreenViewer()
    {
        using var camera = NewCamera();
        await using var h = new LiveHarness(video: false, camera: camera);
        var told = new List<int>();
        h.Live.ViewersChanged += n => { lock (told) told.Add(n); };
        var c = await OpenCamera(h);
        Assert.True(await Until(() => camera.Capturing));
        Assert.True(await Until(() => h.Live.Viewers == 0));
        Assert.True(await Until(() => { lock (told) return told.SequenceEqual(new[] { 1, 0 }); }));
        // A screen phone counts.
        var screen = await h.Connect();
        Assert.True(await Until(() => h.Live.Viewers == 1));
        // The camera closes by itself: back to the screen, and counted again.
        allowed = false;
        camera.Tick();
        h.Live.Housekeeping();
        Assert.True(await Until(() => h.Live.Viewers == 2));
        Assert.True(await Until(() => { lock (told) return told[^1] == 2; }));
        Assert.False(c.Closed);
        Assert.False(screen.Closed);
    }

    [Fact]
    public async Task WhenTheCameraClosesByItselfTheScreenComesBack()
    {
        using var camera = NewCamera();
        await using var h = new LiveHarness(video: false, camera: camera);
        var c = await OpenCamera(h);
        Assert.True(await Until(() => camera.Capturing));
        await c.Collect(TimeSpan.FromSeconds(0.5));
        // The owner turns the camera switch off: the camera's own check stops it.
        allowed = false;
        camera.Tick();
        Assert.NotNull(await c.WaitFor(m => m.Text && Body(m).Contains("cameraStopped", StringComparison.Ordinal), Soon));
        // The socket's 2-second check hands the screen back.
        h.Live.Housekeeping();
        c.AutoAck = true;
        Assert.NotNull(await c.WaitFor(LiveClient.IsJpeg, Soon));
    }
}

// The camera's messages in one phone's queue: no confirmations, their own cap.
public class CameraQueueTests
{
    static LiveServer.Peer NewPeer() =>
        new(WebSocket.CreateFromStream(Stream.Null, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero), "token", "iPhone");

    static byte[] Camera(byte type) => new byte[] { type, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 2, 0x65, 1 };

    [Fact]
    public void CameraPicturesAreNeverWaitingForAnAck()
    {
        var peer = NewPeer();
        peer.FlowControl = true;
        for (int i = 0; i < 10; i++)
        {
            Assert.Equal(LiveServer.Peer.Queued.Ok, peer.Enqueue(Camera((byte)(i % 3 + 3)), text: false, video: false, camera: true));
            Assert.NotNull(peer.Next());
            peer.Sent();
        }
        Assert.Equal(0, peer.Unconfirmed);
        // A screen picture still counts.
        peer.SetPending(new byte[] { 0xFF, 0xD8, 1 });
        Assert.NotNull(peer.Next());
        peer.Sent();
        Assert.Equal(1, peer.Unconfirmed);
    }

    [Fact]
    public void MoreThanNinetyCameraMessagesAreDroppedForAKeyframe()
    {
        var peer = NewPeer();
        peer.Enqueue(Encoding.UTF8.GetBytes("{\"camera\":{}}"), text: true, video: false);
        for (int i = 0; i < LiveServer.Peer.MaxCameraQueued; i++)
            Assert.Equal(LiveServer.Peer.Queued.Ok, peer.Enqueue(Camera(4), text: false, video: false, camera: true));
        Assert.Equal(LiveServer.Peer.Queued.CameraDropped, peer.Enqueue(Camera(4), text: false, video: false, camera: true));
        Assert.Equal(0, peer.CameraWaiting());
        Assert.Equal(new[] { true }, peer.Waiting());
        // More than 60 is fine for the camera (the screen's video limit isn't its own).
        for (int i = 0; i < 70; i++) peer.Enqueue(Camera(4), text: false, video: false, camera: true);
        Assert.Equal(70, peer.CameraWaiting());
        // Going back to the screen keeps the camera's; the camera closing drops them.
        peer.ClearOutbox();
        Assert.Equal(70, peer.CameraWaiting());
        peer.DropCamera();
        Assert.Equal(0, peer.CameraWaiting());
    }

    [Fact]
    public void TheWholeQueueIsStillCapped()
    {
        var peer = NewPeer();
        var big = new byte[3 * 1024 * 1024];
        Assert.Equal(LiveServer.Peer.Queued.Ok, peer.Enqueue(big, text: false, video: false, camera: true));
        Assert.Equal(LiveServer.Peer.Queued.Ok, peer.Enqueue(big, text: false, video: false, camera: true));
        Assert.Equal(LiveServer.Peer.Queued.Overflow, peer.Enqueue(big, text: false, video: false, camera: true));
    }
}
