using System.Net;
using LidAwake;
using Xunit;

// The diagnostics log: the file, its cap, the lines it gets, and that none of them carries a
// secret or an address.
public class DiagnosticsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "lidawake-diag-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static async Task<bool> Eventually(Func<bool> condition)
    {
        var end = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            await Task.Delay(20);
        }
        return condition();
    }

    // ---- The file ----

    [Fact]
    public void TheLogIsNamedLikeTheMacsAndWritesTimeTwoSpacesText()
    {
        var at = new DateTimeOffset(2026, 10, 20, 15, 4, 5, TimeSpan.FromHours(2));
        var log = new DiagnosticsLog(folder, now: () => at);
        Assert.Equal(Path.Combine(folder, "LidAwake-diagnostics.log"), log.FilePath);
        log.Write("phone stay-awake: held on screen");
        Assert.Equal("2026-10-20T15:04:05+02:00  phone stay-awake: held on screen\n", File.ReadAllText(log.FilePath));
    }

    [Fact]
    public void OverTheCapTheFileIsKeptAsOldAndANewOneStarts()
    {
        // About 48 bytes a line: the file passes 300 bytes once, after the 7th line.
        var log = new DiagnosticsLog(folder, maxBytes: 300);
        for (int i = 0; i < 10; i++) log.Write($"screen session: line {i}");
        var old = log.FilePath + ".old";
        Assert.True(File.Exists(old));
        Assert.True(new FileInfo(log.FilePath).Length <= 300 + 100);
        Assert.True(new FileInfo(old).Length > 300);
        // The newest line is in the current file, and nothing was lost between the two.
        Assert.Contains("line 9", File.ReadAllText(log.FilePath));
        var all = File.ReadAllText(old) + File.ReadAllText(log.FilePath);
        Assert.Contains("line 0", all);
        Assert.Equal(10, all.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        // Only these two files: the activity log's code was used, under the other name.
        Assert.Equal(new[] { "LidAwake-diagnostics.log", "LidAwake-diagnostics.log.old" },
            Directory.GetFiles(folder).Select(Path.GetFileName).OrderBy(n => n, StringComparer.Ordinal));
    }

    [Fact]
    public void AFolderThatCantBeWrittenNeverThrows()
    {
        var file = Path.Combine(folder, "a-file");
        Directory.CreateDirectory(folder);
        File.WriteAllText(file, "x");
        new DiagnosticsLog(Path.Combine(file, "below")).Write("anything");
    }

    // ---- Nothing secret ----

    [Theory]
    [InlineData("see https://login.tailscale.com/f/serve?node=nAbCdEf123 to allow", "see login.tailscale.com/… to allow")]
    [InlineData("posted to https://web.push.apple.com/QGd3k-very/long?x=1", "posted to web.push.apple.com/…")]
    [InlineData("from 100.101.102.103 and 192.168.1.20", "from (address) and (address)")]
    [InlineData("from fd7a:115c:a1e0::1234 and fe80::1%12", "from (address) and (address)")]
    [InlineData("the PC itself 127.0.0.1:8787 and ::1", "the PC itself 127.0.0.1:8787 and ::1")]
    [InlineData("at 12:30:45 the screen: ok", "at 12:30:45 the screen: ok")]
    [InlineData("token eyJhbGciOiJFUzI1NiJ9eyJhdWQiOiJodHRwczovL2ZjbS5nb29nbGVhcGlzLmNvbSJ9 end", "token (hidden) end")]
    public void ScrubKeepsHostsAndHidesAddressesAndKeys(string text, string expected)
    {
        Assert.Equal(expected, DiagnosticsLog.Scrub(text));
    }

    [Fact]
    public void EveryWrittenLineIsScrubbed()
    {
        var log = new DiagnosticsLog(folder);
        log.Write("Tailscale Serve: the phone page not published: open https://login.tailscale.com/a/1f2e3d4c5b from 100.64.0.7");
        var line = Assert.Single(log.Recent(5));
        Assert.EndsWith("  Tailscale Serve: the phone page not published: open login.tailscale.com/… from (address)", line);
    }

    // ---- Phone alerts ----

    [Fact]
    public void APushLineHasTheKindTheHostAndTheStatusOnly()
    {
        Assert.Equal("alert lowBattery → web.push.apple.com 201",
            DiagnosticsLog.PushLine(AlertKind.LowBattery, "https://web.push.apple.com/QGd3k_secret-phone-id", 201));
        Assert.Equal("alert warm → fcm.googleapis.com not delivered (no answer in 8 s)",
            DiagnosticsLog.PushLine(AlertKind.Warm, "https://fcm.googleapis.com/fcm/send/abc", 0, DiagnosticsLog.Failure(new TaskCanceledException())));
        // A kind that isn't one is not written as it came.
        Assert.Equal("alert ? → ? 410", DiagnosticsLog.PushLine("Battery at 5% <b>", "not a url", 410));
    }

    [Fact]
    public async Task EachSentAlertWritesItsAnswerWithoutTheEndpointKeysOrToken()
    {
        var machine = Settings.ForMachine(folder);
        var service = new FakePushService { Answer = uri => uri.EndsWith("gone") ? HttpStatusCode.Gone : HttpStatusCode.Created };
        var lines = new List<string>();
        using var push = new WebPush(machine, service) { Diag = line => { lock (lines) lines.Add(line); } };
        using var phone = new FakePhone();
        Assert.True(push.Subscribe("https://web.push.apple.com/QGd3kPhoneSecretPath", phone.P256dh, phone.AuthText, null, "pc.example.ts.net"));
        Assert.True(push.Subscribe("https://fcm.googleapis.com/fcm/send/gone", phone.P256dh, phone.AuthText, null, "pc.example.ts.net"));
        await push.Send(AlertKind.ClaudeSession, "Claude finished", "The secret message body");

        List<string> written;
        lock (lines) written = lines.OrderBy(l => l, StringComparer.Ordinal).ToList();
        Assert.Equal(new[] { "alert claudeSession → fcm.googleapis.com 410", "alert claudeSession → web.push.apple.com 201" }, written);
        var seen = service.Seen;
        foreach (var line in written)
        {
            Assert.DoesNotContain("QGd3k", line);
            Assert.DoesNotContain("send/gone", line);
            Assert.DoesNotContain(phone.P256dh, line);
            Assert.DoesNotContain(phone.AuthText, line);
            Assert.DoesNotContain("secret", line, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Claude finished", line);
            foreach (var s in seen) Assert.DoesNotContain(s.Authorization!.Split(' ', ',')[1], line);
        }
    }

    [Fact]
    public async Task APushServiceThatDoesntAnswerIsWrittenAsNotDelivered()
    {
        var machine = Settings.ForMachine(folder);
        var service = new FakePushService { Throw = new HttpRequestException("https://web.push.apple.com/QGd3kPhoneSecretPath failed") };
        var lines = new List<string>();
        using var push = new WebPush(machine, service) { Diag = line => { lock (lines) lines.Add(line); } };
        using var phone = new FakePhone();
        Assert.True(push.Subscribe("https://web.push.apple.com/QGd3kPhoneSecretPath", phone.P256dh, phone.AuthText, null, "pc.example.ts.net"));
        await push.Send(AlertKind.TurnedOff, "Lid Awake turned off", "Timer finished.");
        lock (lines) Assert.Equal(new[] { "alert turnedOff → web.push.apple.com not delivered (network error)" }, lines);
    }

    // ---- The phone's stay-awake state ----

    [Fact]
    public void TheStayAwakeStateIsWrittenWhenItChangesForThatCaller()
    {
        var log = new StayAwakeLog();
        Assert.Equal("phone stay-awake: held on screen", log.Seen("100.1.2.3", "held on screen"));
        Assert.Null(log.Seen("100.1.2.3", "held on screen"));
        // Another phone has its own.
        Assert.Equal("phone stay-awake: held on screen", log.Seen("100.9.9.9", "held on screen"));
        Assert.Equal("phone stay-awake: released by phone on status", log.Seen("100.1.2.3", "released by phone on status"));
        // Nothing to say: nothing written.
        Assert.Null(log.Seen("100.1.2.3", null));
        Assert.Null(log.Seen("100.1.2.3", "<>!"));
    }

    [Fact]
    public void TheStayAwakeStateKeepsOnlyTheMacsCharactersAndSixtyOfThem()
    {
        Assert.Equal("refused: NotAllowedError on screen sideways", StayAwakeLog.Clean("refused: NotAllowedError on screen sideways"));
        Assert.Equal("held on scriptalert1script", StayAwakeLog.Clean("held on <script>alert(1)</script>".Replace("(1)", "1")));
        Assert.Equal("ab(c)-d: e", StayAwakeLog.Clean("a\nb\t(c)-d: e%"));
        Assert.Equal(60, StayAwakeLog.Clean(new string('x', 100))!.Length);
        var line = new StayAwakeLog().Seen("", "http://evil.example/ 100.1.2.3");
        Assert.Equal("phone stay-awake: http:evilexample 100123", line);
    }

    // ---- Stream sessions ----

    [Fact]
    public void AStreamLineSaysHowLongHowMuchAndWithWhat()
    {
        Assert.Equal("screen session: 2m05s, 3612 frames (28.9 fps), 41.2 MB, 12 keyframes, capture Desktop Duplication, encoder H264 Encoder MFT, 0 fallbacks",
            StreamStats.Format("screen", 125_000, 3612, 0, 41_234_567, 12, "Desktop Duplication", new[] { "H264 Encoder MFT" }, 0));
        Assert.Equal("screen session: 45s, 90 frames (2.0 fps, 90 still), 812.3 KB, 0 keyframes, capture GDI, encoder none, 1 fallback",
            StreamStats.Format("screen", 45_000, 0, 90, 812_300, 0, "GDI", Array.Empty<string>(), 1));
        Assert.Equal("camera session: 1h02m05s, 1 frame (0.0 fps), 812 B, 1 keyframe, encoder NVIDIA H.264 Encoder MFT then H264 Encoder MFT, 1 fallback",
            StreamStats.Format("camera", 3_725_000, 1, 0, 812, 1, null, new[] { "NVIDIA H.264 Encoder MFT", "H264 Encoder MFT" }, 1));
        Assert.Equal("camera session: 0s, 0 frames (0.0 fps), 0 B, 0 keyframes, encoder none, 0 fallbacks",
            StreamStats.Format("camera", 0, 0, 0, 0, 0, null, Array.Empty<string>(), 0));
    }

    [Fact]
    public void StreamStatsCountsFramesStillsAndEncoderFallbacks()
    {
        long now = 1_000;
        var stats = new StreamStats("screen", () => now);
        stats.Capture("GDI");
        Assert.Null(stats.Encoder("hardware"));
        Assert.Null(stats.Encoder("hardware"));
        stats.Frame(1000, key: true);
        stats.Frame(500, key: false);
        Assert.Equal("hardware", stats.Encoder("software"));
        stats.Frame(1500, key: true);
        stats.Picture(2000);
        now += 2_000;
        Assert.Equal("screen session: 2s, 4 frames (2.0 fps, 1 still), 5.0 KB, 2 keyframes, capture GDI, encoder hardware then software, 1 fallback",
            stats.Line());
    }

    [Fact]
    public async Task AScreenSessionWritesItsLineWhenItEnds()
    {
        var screens = new FakeScreens();
        var lines = new List<string>();
        using var feed = new ScreenFeed(screens.Open, idleStop: TimeSpan.FromMilliseconds(200)) { Diag = l => { lock (lines) lines.Add(l); } };
        int frames = 0;
        var id = Guid.NewGuid();
        feed.Subscribe(id, _ => Interlocked.Increment(ref frames), () => { });
        Assert.True(await Eventually(() => Volatile.Read(ref frames) >= 3));
        lock (lines) Assert.Empty(lines);
        feed.Unsubscribe(id);
        Assert.True(await Eventually(() => { lock (lines) return lines.Count == 1; }));
        lock (lines)
        {
            Assert.StartsWith("screen session: ", lines[0]);
            Assert.Contains(" still), ", lines[0]);
            Assert.Contains(", capture fake screen, encoder none, 0 fallbacks", lines[0]);
        }
    }

    [Fact]
    public async Task AnEncoderThatFailsAndIsReplacedIsWrittenAsAFallback()
    {
        var screens = new FakeScreens();
        int made = 0;
        IVideoEncoder Make(int w, int h, int fps, int bitrate) =>
            Interlocked.Increment(ref made) == 1 ? new BrokenEncoder(w, h, fps) : new FakeEncoder(w, h, fps);
        var lines = new List<string>();
        using var feed = new ScreenFeed(screens.Open, Make, TimeSpan.FromMilliseconds(200)) { Diag = l => { lock (lines) lines.Add(l); } };
        int pictures = 0;
        var id = Guid.NewGuid();
        feed.Subscribe(id, _ => { }, () => { });
        feed.SetVideo(id, new VideoSink(p => { if (p is VideoPacket.Picture) Interlocked.Increment(ref pictures); }, () => true));
        Assert.True(await Eventually(() => Volatile.Read(ref pictures) >= 3));
        feed.Unsubscribe(id);
        Assert.True(await Eventually(() => { lock (lines) return lines.Any(l => l.StartsWith("screen session: ")); }));
        lock (lines)
        {
            Assert.Contains("screen encoder fallback: broken hardware → fake H.264", lines);
            var session = lines.Single(l => l.StartsWith("screen session: "));
            Assert.Contains("encoder broken hardware then fake H.264, 1 fallback", session);
            Assert.Contains(" keyframe", session);
        }
    }

    sealed class BrokenEncoder : IVideoEncoder
    {
        public BrokenEncoder(int w, int h, int f) => (Width, Height, Fps) = (w, h, f);
        public string Name => "broken hardware";
        public int Width { get; }
        public int Height { get; }
        public int Fps { get; }
        public IReadOnlyList<(VideoConfig Config, VideoChunk Chunk)> Encode(ScreenImage image, long timestamp, bool forceKey) =>
            throw new InvalidOperationException("The graphics card's encoder broke.");
        public void SetBitrate(int bitsPerSecond) { }
        public void Dispose() { }
    }
}
