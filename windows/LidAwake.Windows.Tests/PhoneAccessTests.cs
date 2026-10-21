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

    [Fact]
    public async Task TurningOffRemovesServe()
    {
        var port = FreePort();
        using var server = NewServer(port);
        server.Start();
        var phone = new PhoneAccess(tailnet, server);
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
        var phone = new PhoneAccess(tailnet, server);
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
}
