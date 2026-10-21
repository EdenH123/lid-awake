using LidAwake;
using Xunit;

// The screen feed with a stand-in screen and encoder: capture only while watched, the long poll,
// the presets, video setup before the first keyframe, and the falls back to JPEG.
public class ScreenFeedTests
{
    static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    // Background work that should happen at once, with time enough for a busy runner.
    static Task<bool> Eventually(Func<bool> condition) => TestThreads.Until(condition);

    [Fact]
    public async Task CaptureStartsWithAWatcherAndStopsAfterTheLastOneLeaves()
    {
        var screens = new FakeScreens();
        using var feed = new ScreenFeed(screens.Open, idleStop: TimeSpan.FromMilliseconds(300));
        var watching = new List<bool>();
        feed.WatchingChanged += on => { lock (watching) watching.Add(on); };
        Assert.False(feed.IsCapturing);
        Assert.Empty(screens.Opened);

        int frames = 0;
        var id = Guid.NewGuid();
        feed.Subscribe(id, _ => Interlocked.Increment(ref frames), () => { });
        Assert.True(feed.IsCapturing);
        Assert.True(await Eventually(() => Volatile.Read(ref frames) >= 2));
        Assert.Equal("fake screen", feed.GrabberKind);
        Assert.Single(screens.Opened);

        feed.Unsubscribe(id);
        Assert.True(await Eventually(() => !feed.IsCapturing));
        Assert.True(screens.Opened[0].Disposed);
        Assert.True(await Eventually(() => { lock (watching) return watching.Count == 2; }));
        lock (watching) Assert.Equal(new[] { true, false }, watching);

        // Watching again starts a new capture.
        feed.Subscribe(id, _ => { }, () => { });
        Assert.True(feed.IsCapturing);
        Assert.True(await Eventually(() => screens.Opened.Count == 2));
        feed.Unsubscribe(id);
    }

    [Fact]
    public async Task ANewWatcherGetsThePictureRightAwayEvenWhenTheScreenIsStill()
    {
        var screens = new FakeScreens { Changing = false };
        using var feed = new ScreenFeed(screens.Open, idleStop: TimeSpan.FromMilliseconds(300));
        var first = new TaskCompletionSource<byte[]>();
        feed.Subscribe(Guid.NewGuid(), jpeg => first.TrySetResult(jpeg), () => { });
        var got = await first.Task.WaitAsync(Soon);
        Assert.Equal(0xFF, got[0]);
        Assert.Equal(0xD8, got[1]);
        // A second watcher gets the latest one at once.
        byte[]? second = null;
        feed.Subscribe(Guid.NewGuid(), jpeg => second ??= jpeg, () => { });
        Assert.NotNull(second);
    }

    [Fact]
    public async Task TheLongPollGetsAPictureThenWaitsForANewerOne()
    {
        var screens = new FakeScreens { Changing = false };
        using var feed = new ScreenFeed(screens.Open, idleStop: TimeSpan.FromSeconds(3));
        var first = await feed.FrameAfter(0).WaitAsync(Soon);
        Assert.Equal(ScreenFeed.Result.Frame, first.Kind);
        Assert.True(first.Id > 0);
        Assert.Equal(0xFF, first.Jpeg![0]);

        // Nothing newer while the screen is still: after about 2 seconds, "unchanged".
        var started = DateTime.UtcNow;
        var still = await feed.FrameAfter(first.Id).WaitAsync(Soon);
        Assert.Equal(ScreenFeed.Result.Unchanged, still.Kind);
        Assert.InRange((DateTime.UtcNow - started).TotalSeconds, 1.5, 4.5);

        // A change answers the waiting poll with the newer picture.
        var waiting = feed.FrameAfter(first.Id);
        await Task.Delay(100);
        screens.Changing = true;
        var newer = await waiting.WaitAsync(Soon);
        Assert.Equal(ScreenFeed.Result.Frame, newer.Kind);
        Assert.True(newer.Id > first.Id);

        // An id the feed never gave (Lid Awake restarted) gets the latest picture at once.
        var restart = await feed.FrameAfter(newer.Id + 1000).WaitAsync(TimeSpan.FromSeconds(1));
        Assert.Equal(ScreenFeed.Result.Frame, restart.Kind);
    }

    [Fact]
    public async Task NoCaptureMeansUnavailableForPollsAndWatchers()
    {
        var screens = new FakeScreens { Broken = true };
        using var feed = new ScreenFeed(screens.Open, idleStop: TimeSpan.FromMilliseconds(300));
        var told = new TaskCompletionSource();
        feed.Subscribe(Guid.NewGuid(), _ => { }, () => told.TrySetResult());
        await told.Task.WaitAsync(Soon);
        var poll = await feed.FrameAfter(0).WaitAsync(Soon);
        Assert.Equal(ScreenFeed.Result.Unavailable, poll.Kind);
    }

    [Fact]
    public async Task ACaptureThatKeepsFailingFallsToTheNextKind()
    {
        var levels = new List<int>();
        using var feed = new ScreenFeed(level =>
        {
            lock (levels) levels.Add(level);
            return level == 0 ? new BrokenGrabber() : (IScreenGrabber)new FakeGrabber(() => true);
        }, idleStop: TimeSpan.FromMilliseconds(300));
        var got = new TaskCompletionSource();
        feed.Subscribe(Guid.NewGuid(), _ => got.TrySetResult(), () => { });
        await got.Task.WaitAsync(Soon);
        lock (levels) Assert.Equal(new[] { 0, 0, 0, 1 }, levels);
    }

    sealed class BrokenGrabber : IScreenGrabber
    {
        public string Kind => "broken";
        public ScreenImage? Grab(int maxWidth, int waitMs, bool force) => throw new InvalidOperationException("Access lost.");
        public void Dispose() { }
    }

    [Fact]
    public void PresetsAreTheMacs()
    {
        Assert.Equal((4096, 70, 6, 4096, 15, 12_000_000), Numbers(ScreenPreset.Max));
        Assert.Equal((1600, 70, 12, 1600, 20, 6_000_000), Numbers(ScreenPreset.Sharp));
        Assert.Equal((1200, 50, 20, 1280, 30, 4_000_000), Numbers(ScreenPreset.Balanced));
        Assert.Equal((640, 35, 25, 1024, 30, 2_500_000), Numbers(ScreenPreset.Fast));
        Assert.Same(ScreenPreset.Fast, ScreenPreset.Named("fast"));
        Assert.Null(ScreenPreset.Named("ultra"));

        using var feed = new ScreenFeed(_ => null);
        Assert.Equal("balanced", feed.PresetName);
        Assert.True(feed.SetPreset("sharp"));
        Assert.Equal("sharp", feed.PresetName);
        Assert.False(feed.SetPreset("ultra"));
        Assert.Equal("sharp", feed.PresetName);
    }

    static (int, int, int, int, int, int) Numbers(ScreenPreset p) => (p.MaxWidth, p.Quality, p.Fps, p.VideoWidth, p.VideoFps, p.VideoMaxBitrate);

    [Fact]
    public void ScreenSizesFitTheWidthAndAreEven()
    {
        Assert.Equal((1280, 720), ScreenSize.Fit(1920, 1080, 1280));
        Assert.Equal((1920, 1080), ScreenSize.Fit(1920, 1080, 4096));  // never bigger than the screen
        Assert.Equal((1280, 800), ScreenSize.Fit(2560, 1600, 1280));
        Assert.Equal((1200, 674), ScreenSize.Fit(1366, 768, 1200));  // 674.6 → even
    }

    [Fact]
    public async Task AVideoWatcherGetsTheSetupBeforeTheFirstKeyframeAndAgainAfterARestart()
    {
        var screens = new FakeScreens();
        var encoders = new FakeEncoders();
        using var feed = new ScreenFeed(screens.Open, encoders.Make, TimeSpan.FromMilliseconds(300));
        var packets = new List<VideoPacket>();
        var id = Guid.NewGuid();
        feed.Subscribe(id, _ => { }, () => { });
        feed.SetVideo(id, new VideoSink(p => { lock (packets) packets.Add(p); }, () => true));
        Assert.True(await Eventually(() => { lock (packets) return packets.Count >= 4; }));
        lock (packets)
        {
            var setup = Assert.IsType<VideoPacket.Setup>(packets[0]);
            Assert.Equal("avc1.4d0028", setup.Config.Codec);
            Assert.Equal(FakeGrabber.W, setup.Config.Width);
            Assert.Equal(FakeGrabber.H, setup.Config.Height);
            Assert.True(Assert.IsType<VideoPacket.Picture>(packets[1]).Chunk.Key);
            Assert.False(Assert.IsType<VideoPacket.Picture>(packets[2]).Chunk.Key);
            packets.Clear();
        }
        Assert.Equal("fake H.264", feed.EncoderName);

        feed.RestartVideo(id);
        int SetupAt() { lock (packets) return packets.FindIndex(p => p is VideoPacket.Setup); }
        Assert.True(await Eventually(() => { int at = SetupAt(); lock (packets) return at >= 0 && at + 1 < packets.Count; }));
        lock (packets) Assert.True(Assert.IsType<VideoPacket.Picture>(packets[SetupAt() + 1]).Chunk.Key);
        Assert.True(encoders.Made[0].ForcedKeys >= 2);

        // Back to JPEG: the encoder is let go.
        feed.SetVideo(id, null);
        Assert.True(await Eventually(() => encoders.Made[0].Disposed));
        feed.Unsubscribe(id);
    }

    [Fact]
    public async Task APhoneThatIsBehindGetsNoNewVideo()
    {
        var screens = new FakeScreens();
        var encoders = new FakeEncoders();
        using var feed = new ScreenFeed(screens.Open, encoders.Make, TimeSpan.FromMilliseconds(300));
        int pictures = 0;
        bool ready = true;
        var id = Guid.NewGuid();
        feed.Subscribe(id, _ => { }, () => { });
        feed.SetVideo(id, new VideoSink(p => { if (p is VideoPacket.Picture) Interlocked.Increment(ref pictures); }, () => Volatile.Read(ref ready)));
        Assert.True(await Eventually(() => Volatile.Read(ref pictures) >= 2));
        Volatile.Write(ref ready, false);
        await Task.Delay(200);
        int held = Volatile.Read(ref pictures);
        await Task.Delay(500);
        Assert.Equal(held, Volatile.Read(ref pictures));
        Volatile.Write(ref ready, true);
        Assert.True(await Eventually(() => Volatile.Read(ref pictures) > held));
        feed.Unsubscribe(id);
    }

    [Fact]
    public async Task WithoutAnEncoderVideoWatchersGetJpegInstead()
    {
        var screens = new FakeScreens();
        using var feed = new ScreenFeed(screens.Open, (_, _, _, _) => throw new InvalidOperationException("No H.264 encoder on this PC."),
                                        TimeSpan.FromMilliseconds(300));
        int jpegs = 0, packets = 0;
        var id = Guid.NewGuid();
        feed.Subscribe(id, _ => Interlocked.Increment(ref jpegs), () => { });
        feed.SetVideo(id, new VideoSink(_ => Interlocked.Increment(ref packets), () => true));
        Assert.True(await Eventually(() => feed.VideoProblem != null));
        int before = Volatile.Read(ref jpegs);
        Assert.True(await Eventually(() => Volatile.Read(ref jpegs) > before + 2));
        Assert.Equal(0, Volatile.Read(ref packets));
        Assert.Contains("No H.264 encoder", feed.VideoProblem);
        Assert.Null(feed.EncoderName);
        feed.Unsubscribe(id);
    }

    [Fact]
    public async Task AnEncoderThatFailsWhileEncodingFallsBackToJpeg()
    {
        var screens = new FakeScreens();
        using var feed = new ScreenFeed(screens.Open, (w, h, f, _) => new FailingEncoder(w, h, f), TimeSpan.FromMilliseconds(300));
        int jpegs = 0;
        var id = Guid.NewGuid();
        feed.Subscribe(id, _ => Interlocked.Increment(ref jpegs), () => { });
        feed.SetVideo(id, new VideoSink(_ => { }, () => true));
        Assert.True(await Eventually(() => feed.VideoProblem != null));
        int before = Volatile.Read(ref jpegs);
        Assert.True(await Eventually(() => Volatile.Read(ref jpegs) > before));
        feed.Unsubscribe(id);
    }

    sealed class FailingEncoder : IVideoEncoder
    {
        public FailingEncoder(int w, int h, int f) => (Width, Height, Fps) = (w, h, f);
        public string Name => "failing";
        public int Width { get; }
        public int Height { get; }
        public int Fps { get; }
        public IReadOnlyList<(VideoConfig Config, VideoChunk Chunk)> Encode(ScreenImage image, long timestamp, bool forceKey) =>
            throw new InvalidOperationException("The encoder broke.");
        public void SetBitrate(int bitsPerSecond) { }
        public void Dispose() { }
    }

    [Fact]
    public void TheBitRateFallsQuicklyAndRisesSlowly()
    {
        using var feed = new ScreenFeed(_ => null);
        Assert.Equal(1_000_000, feed.Bitrate);
        feed.VideoFeedback(0.5);
        Assert.Equal(800_000, feed.Bitrate);
        feed.VideoFeedback(0.5);  // too soon after the last change
        Assert.Equal(800_000, feed.Bitrate);
    }
}
