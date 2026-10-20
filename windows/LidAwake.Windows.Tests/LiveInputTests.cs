using System.Text;
using System.Text.Json;
using LidAwake;
using Xunit;
using static LidAwake.InputInjector;

// Input commands on the live socket (docs/API.md 7.6), handed to the control's IInputSink in the
// Mac's order: the refusal, the 240-a-second cap, the text length, then the command (and the
// answer to `ping`). A phone that sent input lets go of held keys when it leaves.
[Collection("WebServer")]
public class LiveInputTests
{
    static readonly TimeSpan Soon = TimeSpan.FromSeconds(5);

    sealed class FakeSink : IInputSink
    {
        public volatile string? Refuse;
        public readonly List<string> Performed = new();
        public int Releases;
        public string? Refusal() => Refuse;
        public bool TooLong(JsonElement command) => InputInjector.TooLong(command);
        public void Perform(JsonElement command) { lock (Performed) Performed.Add(command.GetRawText()); }
        public void ReleaseHeld() => Interlocked.Increment(ref Releases);
        public int Count { get { lock (Performed) return Performed.Count; } }
    }

    static string Body((bool Text, byte[] Data) m) => Encoding.UTF8.GetString(m.Data);

    static Func<(bool Text, byte[] Data), bool> TextIs(string json) => m => m.Text && Body(m) == json;

    // Background work that should happen at once, with time enough for a busy runner.
    static Task<bool> Until(Func<bool> condition) => TestThreads.Until(condition);

    [Fact]
    public async Task ARefusalIsAnsweredAndNothingIsCarriedOut()
    {
        var sink = new FakeSink { Refuse = RemoteControl.ViewOnlyText };
        await using var h = new LiveHarness(video: false, input: sink);
        var c = await h.Connect();
        await c.Send("{\"type\":\"click\",\"x\":0.5,\"y\":0.5}");
        var error = await c.WaitFor(m => m.Text, Soon);
        Assert.NotNull(error);
        Assert.Equal(JsonSerializer.Serialize(new { error = RemoteControl.ViewOnlyText }), Body(error!.Value));
        // A ping too: view only gets no pong.
        await c.Send("{\"type\":\"ping\"}");
        var again = await c.WaitFor(m => m.Text, Soon);
        Assert.NotNull(again);
        Assert.Contains("error", Body(again!.Value));
        Assert.Equal(0, sink.Count);

        // Quality still works for a view-only phone (handled before the input checks).
        await c.Send("{\"quality\":\"fast\"}");
        Assert.True(await Until(() => h.Feed.PresetName == "fast"));
    }

    [Fact]
    public async Task PingIsCarriedOutThenAnswered()
    {
        var sink = new FakeSink();
        await using var h = new LiveHarness(video: false, input: sink);
        var c = await h.Connect();
        await c.Send("{\"type\":\"click\",\"x\":0.5,\"y\":0.5}");
        await c.Send("{\"type\":\"ping\"}");
        Assert.NotNull(await c.WaitFor(TextIs("{\"pong\":true}"), Soon));
        lock (sink.Performed)
        {
            Assert.Equal(2, sink.Performed.Count);
            Assert.Contains("click", sink.Performed[0]);
            Assert.Contains("ping", sink.Performed[1]);
        }
    }

    [Fact]
    public async Task TooMuchTextIsRefusedWithTheSentence()
    {
        var sink = new FakeSink();
        await using var h = new LiveHarness(video: false, input: sink);
        var c = await h.Connect();
        await c.Send(JsonSerializer.Serialize(new { type = "text", text = new string('x', InputInjector.MaxText + 1) }));
        var error = await c.WaitFor(m => m.Text, Soon);
        Assert.NotNull(error);
        Assert.Equal(JsonSerializer.Serialize(new { error = RemoteControl.TooMuchText }), Body(error!.Value));
        Assert.Equal(0, sink.Count);
        await c.Send(JsonSerializer.Serialize(new { type = "text", text = new string('x', InputInjector.MaxText) }));
        Assert.True(await Until(() => sink.Count == 1));
    }

    [Fact]
    public async Task AtMost240CommandsASecondReachTheSink()
    {
        var sink = new FakeSink();
        await using var h = new LiveHarness(video: false, input: sink);
        var c = await h.Connect();
        var sending = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 400; i++) await c.Send("{\"type\":\"move\",\"dx\":1,\"dy\":0}");
        var took = sending.Elapsed;
        await c.Collect(TimeSpan.FromSeconds(0.5));
        int done = sink.Count;
        Assert.InRange(done, 1, InputRateLimiter.PerSecond * ((int)Math.Ceiling(took.TotalSeconds) + 1));
        if (took < TimeSpan.FromSeconds(0.7)) Assert.Equal(InputRateLimiter.PerSecond, done);
        Assert.False(c.Closed);
    }

    [Fact]
    public async Task APhoneThatSentInputLetsGoWhenItLeaves()
    {
        var sink = new FakeSink();
        await using var h = new LiveHarness(video: false, input: sink);
        // A phone that only watched: nothing to let go of.
        var watcher = await h.Connect();
        Assert.True(await Until(() => h.Live.Viewers == 1));
        await watcher.DisposeAsync();
        Assert.True(await Until(() => h.Live.Viewers == 0));
        Assert.Equal(0, Volatile.Read(ref sink.Releases));

        var c = await h.Connect();
        await c.Send("{\"type\":\"mousedown\"}");
        Assert.True(await Until(() => sink.Count == 1));
        await c.DisposeAsync();
        Assert.True(await Until(() => Volatile.Read(ref sink.Releases) == 1));
    }

    [Fact]
    public async Task CloseAllLetsGoToo()
    {
        var sink = new FakeSink();
        await using var h = new LiveHarness(video: false, input: sink);
        var c = await h.Connect();
        Assert.True(await Until(() => h.Live.Viewers == 1));
        h.Live.CloseAll();
        Assert.True(await c.WaitClosed(Soon));
        Assert.True(Volatile.Read(ref sink.Releases) >= 1);
    }

    // The real RemoteControl behind the socket: a drag the phone left halfway is let go.
    [Fact]
    public async Task TheRealControlReleasesAHeldButton()
    {
        var fake = new FakeInput();
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        var settings = new Settings(folder);
        ScreenSwitch.Set(settings, true);
        var control = new RemoteControl(settings, () => true, new InputInjector(fake), new FakePower());
        try
        {
            await using var h = new LiveHarness(video: false, input: control);
            var c = await h.Connect();
            await c.Send("{\"type\":\"mousedown\"}");
            Assert.True(await Until(() => fake.All().Any(i => i.type == INPUT.MOUSE && (i.u.mi.dwFlags & MOUSEEVENTF_LEFTDOWN) != 0)));
            await c.DisposeAsync();
            Assert.True(await Until(() => fake.All().Any(i => i.type == INPUT.MOUSE && (i.u.mi.dwFlags & MOUSEEVENTF_LEFTUP) != 0)));

            // With the control switch off, the socket answers view only.
            control.SetControl(false);
            var d = await h.Connect();
            await d.Send("{\"type\":\"click\"}");
            var error = await d.WaitFor(m => m.Text, Soon);
            Assert.NotNull(error);
            Assert.Equal(JsonSerializer.Serialize(new { error = RemoteControl.ViewOnlyText }), Body(error!.Value));
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }
}
