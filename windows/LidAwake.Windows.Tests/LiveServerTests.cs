using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using LidAwake;
using Xunit;
using Xunit.Abstractions;

// The live socket end to end (docs/API.md 7), with a phone's socket written by hand and a
// stand-in screen and encoder: the handshake rules, the setup and frame layouts, the acks and
// flow-control limits, ping, the 8-socket cap, and closing when access goes away.
[Collection("WebServer")]
public class LiveServerTests
{
    static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);
    readonly ITestOutputHelper output;

    public LiveServerTests(ITestOutputHelper output) => this.output = output;

    const string Hello = "{\"quality\":\"balanced\",\"acks\":true,\"video\":false,\"why\":\"\"}";
    const string HelloVideo = "{\"quality\":\"balanced\",\"acks\":true,\"video\":true,\"why\":\"\"}";

    [Fact]
    public async Task TheHandshakeNeedsEveryRule()
    {
        await using var h = new LiveHarness();
        // Everything right: accepted.
        var ok = await h.Connect();
        Assert.Equal(101, ok.Status);

        var refused = new (string Why, Func<Task<LiveClient>> Open)[]
        {
            ("no session cookie", () => h.Connect(cookie: null)),
            ("a session that doesn't exist", () => h.Connect(cookie: Auth.CookieName + "=" + new string('a', 64))),
            ("another host", () => h.Connect(host: "other.example.ts.net", origin: "https://other.example.ts.net")),
            ("the PC's own address", () => h.Connect(host: "127.0.0.1", origin: "http://127.0.0.1:8787")),
            ("no Tailscale login", () => h.Connect(owner: null)),
            ("someone else's Tailscale login", () => h.Connect(owner: "mallory@example.com")),
            ("no Origin", () => h.Connect(origin: null)),
            ("another Origin", () => h.Connect(origin: "https://evil.example.com")),
            ("a plain-http Origin", () => h.Connect(origin: "http://pc.example.ts.net")),
            ("a cross-site request", () => h.Connect(fetchSite: "cross-site")),
            ("through Funnel", () => h.Connect(funnel: true)),
            ("not a WebSocket upgrade", () => h.Connect(upgrade: false)),
        };
        foreach (var (why, open) in refused)
        {
            var c = await open();
            Assert.True(c.Status == 400, $"{why}: answered {c.Status}, expected 400");
        }
        // Sec-Fetch-Site same-origin is fine.
        Assert.Equal(101, (await h.Connect(fetchSite: "same-origin")).Status);

        h.On = false;
        Assert.Equal(400, (await h.Connect()).Status);
        h.On = true;
        h.Allowed = false;
        Assert.Equal(400, (await h.Connect()).Status);
        h.Allowed = true;
        Assert.Equal(101, (await h.Connect()).Status);
    }

    [Fact]
    public async Task AJpegPhoneGetsPicturesAndAtMostTwoUnconfirmed()
    {
        await using var h = new LiveHarness(video: false);
        var c = await h.Connect();
        Assert.Equal(101, c.Status);
        await c.Send(Hello);
        // The screen changes all the time, but without confirmations only two pictures come.
        var first = await c.Collect(TimeSpan.FromSeconds(1.5));
        var pictures = first.Where(m => !m.Text).ToList();
        Assert.InRange(pictures.Count, 1, 2);
        Assert.All(pictures, m => Assert.True(LiveClient.IsJpeg(m)));
        // Confirming one lets exactly one more through.
        await c.Send("{\"ack\":1}");
        var next = await c.WaitFor(m => !m.Text, Soon);
        Assert.NotNull(next);
        Assert.True(LiveClient.IsJpeg(next!.Value));
        var after = await c.Collect(TimeSpan.FromSeconds(0.8));
        Assert.DoesNotContain(after, m => !m.Text);
        // A phone that stops confirming for 3 seconds starts afresh.
        var resumed = await c.WaitFor(m => !m.Text, TimeSpan.FromSeconds(5));
        Assert.NotNull(resumed);
    }

    [Fact]
    public async Task AJpegPhoneThatConfirmsKeepsGettingPictures()
    {
        await using var h = new LiveHarness(video: false);
        var c = await h.Connect();
        c.AutoAck = true;
        await c.Send(Hello);
        var got = await c.Collect(TimeSpan.FromSeconds(2));
        // The balanced preset is 20 pictures a second; a confirming phone gets many of them.
        Assert.True(got.Count(m => LiveClient.IsJpeg(m)) >= 8, $"only {got.Count(m => !m.Text)} pictures");
        Assert.Equal("balanced", h.Feed.PresetName);
    }

    [Fact]
    public async Task QualityMessagesChangeThePreset()
    {
        await using var h = new LiveHarness(video: false);
        var c = await h.Connect();
        await c.Send("{\"quality\":\"fast\"}");
        Assert.True(await Until(() => h.Feed.PresetName == "fast"));
        await c.Send("{\"quality\":\"ultra\"}");
        await c.Send("{\"quality\":\"max\"}");
        Assert.True(await Until(() => h.Feed.PresetName == "max"));
    }

    [Fact]
    public async Task AVideoPhoneGetsTheSetupThenAKeyframeAndAtMostSixUnconfirmed()
    {
        await using var h = new LiveHarness();
        var c = await h.Connect();
        await c.Send(HelloVideo);
        var setup = await c.WaitFor(LiveClient.IsSetup, Soon);
        Assert.NotNull(setup);
        using (var doc = JsonDocument.Parse(setup!.Value.Data))
        {
            var v = doc.RootElement.GetProperty("video");
            Assert.Equal("avc1.4d0028", v.GetProperty("codec").GetString());
            Assert.Equal(Avcc.AvcC(FakeEncoder.Sps, FakeEncoder.Pps), Convert.FromBase64String(v.GetProperty("description").GetString()!));
            Assert.Equal(FakeGrabber.W, v.GetProperty("width").GetInt32());
            Assert.Equal(FakeGrabber.H, v.GetProperty("height").GetInt32());
        }
        // The first video message after it is the keyframe: [1][timestamp, 8 bytes big-endian][AVCC].
        var key = await c.WaitFor(LiveClient.IsVideo, Soon);
        Assert.NotNull(key);
        Assert.True(LiveClient.IsKeyframe(key!.Value));
        var data = key.Value.Data;
        Assert.True(BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(1, 8)) >= 0);
        Assert.Equal(4, BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(9, 4)));
        Assert.Equal(13 + 4, data.Length);
        Assert.Equal(0x65, data[13]);  // an IDR slice

        // No confirmations: the encoder stops while 6 are unconfirmed (counting what came before).
        var more = await c.Collect(TimeSpan.FromSeconds(1.5));
        int video = more.Count(LiveClient.IsVideo);
        Assert.InRange(video, 0, 5);
        Assert.All(more.Where(LiveClient.IsVideo), m => Assert.Equal(2, m.Data[0]));

        // Confirming everything lets video flow again.
        c.AutoAck = true;
        for (int i = 0; i < 8; i++) await c.Send("{\"ack\":1}");
        var flowing = await c.Collect(TimeSpan.FromSeconds(1.5));
        Assert.True(flowing.Count(LiveClient.IsVideo) >= 5, $"only {flowing.Count(LiveClient.IsVideo)} video frames");

        // The decoder failed: the setup and a keyframe again, in that order.
        await c.Send("{\"videoRestart\":true,\"why\":\"test\"}");
        var again = await c.WaitFor(LiveClient.IsSetup, Soon);
        Assert.NotNull(again);
        var key2 = await c.WaitFor(LiveClient.IsVideo, Soon);
        Assert.NotNull(key2);
        Assert.True(LiveClient.IsKeyframe(key2!.Value));

        // Back to JPEG.
        await c.Send("{\"video\":false,\"why\":\"test\"}");
        Assert.NotNull(await c.WaitFor(LiveClient.IsJpeg, Soon));
    }

    [Fact]
    public async Task WithoutAnEncoderAVideoPhoneGetsJpeg()
    {
        await using var h = new LiveHarness(encoders: (_, _, _, _) => throw new InvalidOperationException("No H.264 encoder on this PC."));
        var c = await h.Connect();
        c.AutoAck = true;
        await c.Send(HelloVideo);
        var got = await c.Collect(TimeSpan.FromSeconds(2));
        Assert.DoesNotContain(got, LiveClient.IsSetup);
        Assert.DoesNotContain(got, LiveClient.IsVideo);
        Assert.True(got.Count(LiveClient.IsJpeg) >= 3);
        Assert.Contains("No H.264 encoder", h.Feed.VideoProblem);
    }

    // With the real Media Foundation encoder (and the stand-in screen): video when the runner has
    // an H.264 encoder, JPEG otherwise. Either way the phone gets pictures.
    [Fact]
    public async Task TheRealEncoderOverTheSocketOrJpeg()
    {
        await using var h = new LiveHarness(encoders: MfH264Encoder.Factory());
        var c = await h.Connect();
        c.AutoAck = true;
        await c.Send(HelloVideo);
        var first = await c.WaitFor(m => LiveClient.IsSetup(m) || (LiveClient.IsJpeg(m) && h.Feed.VideoProblem != null), TimeSpan.FromSeconds(10));
        Assert.NotNull(first);
        if (LiveClient.IsSetup(first!.Value))
        {
            var key = await c.WaitFor(LiveClient.IsVideo, Soon);
            Assert.NotNull(key);
            Assert.True(LiveClient.IsKeyframe(key!.Value));
            output.WriteLine($"RUNNER: video over the socket with {h.Feed.EncoderName}: {Encoding.UTF8.GetString(first.Value.Data)}");
        }
        else output.WriteLine($"RUNNER: no H.264 encoder, JPEG over the socket instead ({h.Feed.VideoProblem})");
    }

    [Fact]
    public async Task PingIsAnsweredAndInputIsIgnoredForNow()
    {
        await using var h = new LiveHarness(video: false);
        var c = await h.Connect();
        await c.Send("{\"type\":\"click\",\"x\":0.5,\"y\":0.5}");
        await c.Send("{\"camera\":true,\"ticket\":\"x\"}");
        await c.Send("not json");
        await c.Send("[1,2]");
        await c.Send("{\"type\":\"ping\"}");
        var pong = await c.WaitFor(m => m.Text, Soon);
        Assert.NotNull(pong);
        Assert.Equal("{\"pong\":true}", Encoding.UTF8.GetString(pong!.Value.Data));
    }

    [Fact]
    public async Task AFloodOfPingsGetsAtMost240AnswersASecond()
    {
        await using var h = new LiveHarness(video: false);
        var c = await h.Connect();
        var started = DateTime.UtcNow;
        for (int i = 0; i < 1000; i++) await c.Send("{\"type\":\"ping\"}");
        double seconds = (DateTime.UtcNow - started).TotalSeconds;
        var got = await c.Collect(TimeSpan.FromSeconds(1.5));
        int pongs = got.Count(m => m.Text && Encoding.UTF8.GetString(m.Data) == "{\"pong\":true}");
        output.WriteLine($"1000 pings in {seconds:0.00} s: {pongs} answers");
        Assert.InRange(pongs, 1, LiveServer.InputRate.PerSecond * ((int)Math.Ceiling(seconds) + 1));
        Assert.True(pongs < 1000);
        Assert.False(c.Closed);
    }

    [Fact]
    public async Task AtMostEightSockets()
    {
        await using var h = new LiveHarness(video: false);
        var open = new List<LiveClient>();
        for (int i = 0; i < LiveServer.MaxSockets; i++)
        {
            var c = await h.Connect();
            Assert.Equal(101, c.Status);
            open.Add(c);
        }
        Assert.True(await Until(() => h.Live.Viewers == LiveServer.MaxSockets));
        var ninth = await h.Connect();
        Assert.NotEqual(101, ninth.Status);

        // One leaves: there is room again.
        await open[0].DisposeAsync();
        Assert.True(await Until(() => h.Live.Viewers == LiveServer.MaxSockets - 1));
        int status = 0;
        for (int tries = 0; tries < 20 && status != 101; tries++)
        {
            status = (await h.Connect()).Status;
            if (status != 101) await Task.Delay(250);
        }
        Assert.Equal(101, status);
    }

    [Fact]
    public async Task SocketsCloseWhenTheScreenSwitchOrLidAwakeGoesOff()
    {
        await using var h = new LiveHarness(video: false);
        var counts = new List<int>();
        h.Live.ViewersChanged += n => { lock (counts) counts.Add(n); };

        var a = await h.Connect();
        Assert.True(await Until(() => h.Live.Viewers == 1));
        h.Allowed = false;
        // The 2-second check closes it.
        Assert.True(await a.WaitClosed(TimeSpan.FromSeconds(5)));
        h.Allowed = true;

        var b = await h.Connect();
        Assert.Equal(101, b.Status);
        h.On = false;
        h.Live.CloseAll();  // what the tray does at once
        Assert.True(await b.WaitClosed(TimeSpan.FromSeconds(2)));
        Assert.True(await Until(() => h.Live.Viewers == 0));
        lock (counts) Assert.Equal(new[] { 1, 0, 1, 0 }, counts);
    }

    [Fact]
    public async Task ASignedOutSessionsSocketCloses()
    {
        await using var h = new LiveHarness(video: false);
        var c = await h.Connect();
        Assert.Equal(101, c.Status);
        h.Auth.Logout(h.Token);
        Assert.True(await c.WaitClosed(TimeSpan.FromSeconds(5)));
        Assert.Equal(400, (await h.Connect()).Status);
    }

    [Fact]
    public async Task CaptureRunsOnlyWhileAPhoneWatches()
    {
        await using var h = new LiveHarness(video: false, idleStop: TimeSpan.FromMilliseconds(300));
        Assert.False(h.Feed.IsCapturing);
        var c = await h.Connect();
        await c.Send(Hello);
        Assert.True(await Until(() => h.Feed.IsCapturing));
        await c.DisposeAsync();
        Assert.True(await Until(() => !h.Feed.IsCapturing));
        Assert.All(h.Screens.Opened, g => Assert.True(g.Disposed));
    }

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
}
