using System.Net;
using System.Net.Sockets;
using LidAwake;
using Xunit;

// Tailscale Serve may only point at the port while our own web server holds it: otherwise the
// owner's phone would send its password and cookie to whatever program has the port.
[Collection("WebServer")]
public class PhoneAccessTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly List<string> commands = new();
    readonly Settings settings;
    readonly Tailnet tailnet;
    // Whether this fake Tailscale accepts `serve --https=443 off`. When it does not, the app
    // checks `serve status` afterwards, to see whether the mapping is gone anyway.
    volatile bool offWorks = true;

    public PhoneAccessTests()
    {
        Directory.CreateDirectory(Path.Combine(folder, "web"));
        File.WriteAllText(Path.Combine(folder, "web", "index.html"), "<script>a()</script>");
        settings = new Settings(Path.Combine(folder, "settings"));
        tailnet = new Tailnet(settings, args =>
        {
            lock (commands) commands.Add(args);
            if (args == "serve --https=443 off") return offWorks ? "" : null;
            return args == "serve status --json" ? "{}" : null;
        });
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    WebServer NewServer(int port) =>
        new(new Auth(settings, iterations: 1000), tailnet, () => true, () => new { }, () => { }, (_, _) => { }, port, folder);

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    string[] ServeCommands()
    {
        lock (commands) return commands.Where(c => c.StartsWith("serve --bg", StringComparison.Ordinal)).ToArray();
    }

    [Fact]
    public async Task NothingIsServedWhenTheServerWasNeverStarted()
    {
        using var server = NewServer(FreePort());
        var phone = new PhoneAccess(tailnet, server);
        Assert.False(phone.PageRunning);
        await phone.Set(true);
        Assert.Empty(ServeCommands());
        // Lid Awake never added Serve, so it leaves Serve as it is.
        lock (commands) Assert.Empty(commands);
    }

    [Fact]
    public async Task ServeLeftFromBeforeIsRemovedWhenTheServerWasNeverStarted()
    {
        settings.Set(Tailnet.ServedKey, true);
        using var server = NewServer(FreePort());
        var phone = new PhoneAccess(tailnet, server);
        await phone.Set(true);
        Assert.Empty(ServeCommands());
        // The removal worked, so nothing else is asked.
        lock (commands) Assert.Equal(new[] { "serve --https=443 off" }, commands);
        Assert.False(settings.Get<bool?>(Tailnet.ServedKey));
    }

    [Fact]
    public async Task AFailedRemovalIsFollowedByAStatusCheck()
    {
        offWorks = false;
        settings.Set(Tailnet.ServedKey, true);
        using var server = NewServer(FreePort());
        var phone = new PhoneAccess(tailnet, server);
        await phone.Set(true);
        lock (commands) Assert.Equal(new[] { "serve --https=443 off", "serve status --json" }, commands);
        // The status shows no mapping of ours, so it is gone anyway.
        Assert.False(settings.Get<bool?>(Tailnet.ServedKey));
    }

    [Fact]
    public async Task NothingIsServedWhenAnotherProgramHasThePort()
    {
        var other = new TcpListener(IPAddress.Loopback, 0) { ExclusiveAddressUse = true };
        other.Start();
        try
        {
            var port = ((IPEndPoint)other.LocalEndpoint).Port;
            using var server = NewServer(port);
            Assert.ThrowsAny<Exception>(() => server.Start());
            Assert.False(server.IsRunning);
            var phone = new PhoneAccess(tailnet, server);
            await phone.Set(true);
            await phone.Set(true);
            Assert.Empty(ServeCommands());
        }
        finally { other.Stop(); }
    }

    [Fact]
    public async Task OurPortIsServedOnlyWhileTheServerRuns()
    {
        var port = FreePort();
        using var server = NewServer(port);
        server.Start();
        Assert.True(server.IsRunning);
        var phone = new PhoneAccess(tailnet, server);
        await phone.Set(true);
        Assert.Equal(new[] { $"serve --bg {port}" }, ServeCommands());

        server.Stop();
        Assert.False(server.IsRunning);
        lock (commands) commands.Clear();
        await phone.Set(true);
        Assert.Empty(ServeCommands());
    }

    // The 8-second wait after turning off, finished by the test itself (FakeWait).
    static readonly Func<TimeSpan, CancellationToken, Task> NoWait = (_, _) => Task.CompletedTask;

    [Fact]
    public async Task TurningOffRemovesServe()
    {
        var port = FreePort();
        using var server = NewServer(port);
        server.Start();
        var phone = new PhoneAccess(tailnet, server, delay: NoWait);
        await phone.Set(true);
        lock (commands) commands.Clear();
        await phone.Set(false);
        // The removal worked, so no status check follows.
        lock (commands) Assert.Equal(new[] { "serve --https=443 off" }, commands);
    }

    [Fact]
    public async Task TurningOffChecksTheStatusWhenTheRemovalFails()
    {
        var port = FreePort();
        using var server = NewServer(port);
        server.Start();
        var phone = new PhoneAccess(tailnet, server, delay: NoWait);
        await phone.Set(true);
        offWorks = false;
        lock (commands) commands.Clear();
        await phone.Set(false);
        lock (commands) Assert.Equal(new[] { "serve --https=443 off", "serve status --json" }, commands);
    }

    [Fact]
    public async Task TheMinuteCheckServesAgainOnlyWhileOn()
    {
        var port = FreePort();
        using var server = NewServer(port);
        server.Start();
        var phone = new PhoneAccess(tailnet, server);
        await phone.Recheck();
        Assert.Empty(ServeCommands());
        await phone.Set(true);
        await phone.Recheck();
        // This fake Tailscale never shows the mapping, so each check adds it again.
        Assert.Equal(new[] { $"serve --bg {port}", $"serve --bg {port}" }, ServeCommands());
    }

    // ---- The 8 seconds after turning off (the Mac's refresh) ----

    // A wait the test ends: Waits are the lengths asked for, Finish lets the oldest one end.
    sealed class FakeWait
    {
        readonly List<(TimeSpan Length, TaskCompletionSource Done, CancellationToken Cancel)> waits = new();
        public Func<TimeSpan, CancellationToken, Task> Delay => (length, cancel) =>
        {
            var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (waits) waits.Add((length, done, cancel));
            return done.Task.WaitAsync(cancel);
        };
        public TimeSpan[] Lengths { get { lock (waits) return waits.Select(w => w.Length).ToArray(); } }
        public bool Cancelled(int i) { lock (waits) return waits[i].Cancel.IsCancellationRequested; }
        public void Finish(int i) { lock (waits) waits[i].Done.TrySetResult(); }
    }

    string[] OffCommands()
    {
        lock (commands) return commands.Where(c => c == "serve --https=443 off").ToArray();
    }

    [Fact]
    public async Task TurningOffWaitsEightSecondsBeforeRemovingServe()
    {
        var port = FreePort();
        using var server = NewServer(port);
        server.Start();
        var wait = new FakeWait();
        var phone = new PhoneAccess(tailnet, server, delay: wait.Delay);
        await phone.Set(true);
        var off = phone.Set(false);
        Assert.Equal(new[] { TimeSpan.FromSeconds(8) }, wait.Lengths);
        Assert.Equal(TimeSpan.FromSeconds(8), PhoneAccess.OffDelay);
        // Still published: the phone keeps hearing 423 "Lid Awake is off" for those seconds.
        await Task.Delay(200);
        Assert.False(off.IsCompleted);
        Assert.Empty(OffCommands());
        Assert.True(settings.Get<bool?>(Tailnet.ServedKey));
        // Turned off again meanwhile (a timer and a limit together): the same wait, not a new one.
        Assert.Same(off, phone.Set(false));
        Assert.Single(wait.Lengths);
        wait.Finish(0);
        await off;
        Assert.Single(OffCommands());
        Assert.False(settings.Get<bool?>(Tailnet.ServedKey));
    }

    [Fact]
    public async Task TurningOnWithinTheEightSecondsKeepsServe()
    {
        var port = FreePort();
        using var server = NewServer(port);
        server.Start();
        var wait = new FakeWait();
        var phone = new PhoneAccess(tailnet, server, delay: wait.Delay);
        await phone.Set(true);
        var off = phone.Set(false);
        await phone.Set(true);
        Assert.True(wait.Cancelled(0));
        await off;  // ends: replaced by the turn-on
        wait.Finish(0);  // a wait that ends late anyway changes nothing
        await Task.Delay(200);
        Assert.Empty(OffCommands());
        Assert.True(settings.Get<bool?>(Tailnet.ServedKey));
        // Off again: a fresh wait of its own.
        var again = phone.Set(false);
        Assert.Equal(2, wait.Lengths.Length);
        wait.Finish(1);
        await again;
        Assert.Single(OffCommands());
    }

    [Fact]
    public async Task OffRemovesServeAtOnceAndCancelsAWaitingRemoval()
    {
        var port = FreePort();
        using var server = NewServer(port);
        server.Start();
        var wait = new FakeWait();
        var phone = new PhoneAccess(tailnet, server, delay: wait.Delay);
        await phone.Set(true);
        // Quit, or signing out: SetAwake(false) starts the wait, then Off removes it now.
        var off = phone.Set(false);
        // Off must not wait the eight seconds (WaitAsync throws if it does).
        await phone.Off().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Single(OffCommands());
        Assert.True(wait.Cancelled(0));
        await off;
        wait.Finish(0);
        await Task.Delay(200);
        Assert.Single(OffCommands());
        // Within SessionEnd's own limit, with the real wait: Off never waits for it.
        var real = new PhoneAccess(tailnet, server);
        await real.Set(true);
        lock (commands) commands.Clear();
        _ = real.Set(false);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        await real.Off().WaitAsync(SessionEnd.MaxWait);
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
        Assert.Single(OffCommands());
    }

    [Fact]
    public async Task TurningOffWhenItWasNotOnRemovesAtOnce()
    {
        // Nothing was published by this run (start-up, or a server that couldn't start): no wait.
        settings.Set(Tailnet.ServedKey, true);
        using var server = NewServer(FreePort());
        var wait = new FakeWait();
        var phone = new PhoneAccess(tailnet, server, delay: wait.Delay);
        await phone.Set(false);
        Assert.Empty(wait.Lengths);
        Assert.Single(OffCommands());
    }
}
