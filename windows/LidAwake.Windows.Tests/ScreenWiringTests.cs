using System.Text.Json;
using LidAwake;
using Xunit;

// How screen viewing joins the rest: /status (remoteScreen, screenPermission, "screen" in
// features), the owner's switch, and Tailscale Serve's /live mapping (docs/API.md 9), which may
// only point at 8788 while our own live server holds it.
[Collection("WebServer")]
public class ScreenWiringTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly List<string> commands = new();

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static JsonElement Run(Dictionary<string, object?> status)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(status));
        return doc.RootElement.Clone();
    }

    static string?[] Features(JsonElement s) => s.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray();

    [Fact]
    public void StatusReportsTheScreenSwitchAndTheFeature()
    {
        bool allowed = false;
        var info = new StatusInfo(new FakeReadings(), screenAllowed: () => allowed);
        var off = Run(info.Build(true));
        Assert.False(off.GetProperty("remoteScreen").GetBoolean());
        Assert.True(off.GetProperty("screenPermission").GetBoolean());
        Assert.Equal(1920, off.GetProperty("screenWidth").GetInt32());
        Assert.Equal(new[] { "limits", "screen" }, Features(off));
        // Control is a later task: still false.
        Assert.False(off.GetProperty("remoteControl").GetBoolean());

        allowed = true;
        var on = Run(info.Build(true));
        Assert.True(on.GetProperty("remoteScreen").GetBoolean());

        // Without the switch (no screen viewing on this PC) nothing changes.
        var none = Run(new StatusInfo(new FakeReadings()).Build(true));
        Assert.False(none.GetProperty("remoteScreen").GetBoolean());
        Assert.False(none.GetProperty("screenPermission").GetBoolean());
        Assert.Equal(new[] { "limits" }, Features(none));
    }

    [Fact]
    public void TheScreenSwitchIsOffUntilTheOwnerTurnsItOn()
    {
        var settings = new Settings(folder);
        Assert.False(ScreenSwitch.IsOn(settings));
        ScreenSwitch.Set(settings, true);
        Assert.True(ScreenSwitch.IsOn(new Settings(folder)));
        Assert.True(settings.Get<bool?>("remoteScreen"));
        ScreenSwitch.Set(settings, false);
        Assert.False(ScreenSwitch.IsOn(settings));
    }

    Tailnet NewTailnet(Func<string, string?> answer, Settings? settings = null) =>
        new(settings ?? new Settings(folder), args => { lock (commands) commands.Add(args); return answer(args); });

    // A Tailscale that shows what was added with `serve --bg`.
    Tailnet ServingTailnet(bool pageAlready = false)
    {
        bool page = pageAlready, live = false;
        return NewTailnet(args =>
        {
            if (args == "serve --bg 8787") { page = true; return ""; }
            if (args == "serve --bg --set-path /live http://127.0.0.1:8788") { live = true; return ""; }
            if (args == "serve status --json")
            {
                var proxies = new List<string>();
                if (page) proxies.Add("\"/\":{\"Proxy\":\"http://127.0.0.1:8787\"}");
                if (live) proxies.Add("\"/live\":{\"Proxy\":\"http://127.0.0.1:8788\"}");
                return "{\"Web\":{\"pc.ts.net:443\":{\"Handlers\":{" + string.Join(",", proxies) + "}}}}";
            }
            return "";
        });
    }

    [Fact]
    public void ServeMapsTheLiveSocketWhenItsPortIsSet()
    {
        var t = ServingTailnet();
        t.LivePort = LiveServer.DefaultPort;
        Assert.Null(t.Serve(8787));
        lock (commands)
        {
            Assert.Equal(new[] { "serve status --json", "serve --bg 8787", "serve --bg --set-path /live http://127.0.0.1:8788", "serve status --json" }, commands);
            commands.Clear();
        }
        // Both there: nothing more to do.
        Assert.Null(t.Serve(8787));
        lock (commands) Assert.Equal(new[] { "serve status --json" }, commands);
    }

    [Fact]
    public void OnlyTheMissingLiveMappingIsAdded()
    {
        var t = ServingTailnet(pageAlready: true);
        t.LivePort = LiveServer.DefaultPort;
        Assert.Null(t.Serve(8787));
        lock (commands) Assert.Equal(new[] { "serve status --json", "serve --bg --set-path /live http://127.0.0.1:8788", "serve status --json" }, commands);
    }

    [Fact]
    public void WithoutALivePortOnlyThePageIsServed()
    {
        var t = ServingTailnet();
        Assert.Null(t.Serve(8787));
        lock (commands) Assert.DoesNotContain(commands, c => c.Contains("/live", StringComparison.Ordinal));
    }

    [Fact]
    public void ARemovalCountsOnlyOnceTheLiveMappingIsGoneToo()
    {
        var settings = new Settings(folder);
        settings.Set(Tailnet.ServedKey, true);
        // `serve --https=443 off` fails, and the status still shows /live.
        var t = NewTailnet(args => args == "serve status --json" ? "{\"Proxy\":\"http://127.0.0.1:8788\"}" : null, settings);
        t.LivePort = LiveServer.DefaultPort;
        t.UnserveIfOurs();
        Assert.True(settings.Get<bool?>(Tailnet.ServedKey));
    }

    [Fact]
    public async Task TheLiveSocketIsMappedOnlyWhileOurLiveServerRuns()
    {
        var settings = new Settings(folder);
        var tailnet = NewTailnet(args => args == "serve status --json" ? "{}" : null, settings);
        Directory.CreateDirectory(Path.Combine(folder, "web"));
        File.WriteAllText(Path.Combine(folder, "web", "index.html"), "<script>a()</script>");
        var auth = new Auth(settings, iterations: 1000);
        int port = LiveHarness.FreePort(), livePort = LiveHarness.FreePort();
        using var server = new WebServer(auth, tailnet, () => true, () => new { }, () => { }, (_, _) => { }, port, folder);
        server.Start();
        using var feed = new ScreenFeed(_ => null);
        using var live = new LiveServer(auth, tailnet, () => true, () => true, feed, livePort);
        var phone = new PhoneAccess(tailnet, server, live);
        string[] ServeCommands() { lock (commands) return commands.Where(c => c.StartsWith("serve --bg", StringComparison.Ordinal)).ToArray(); }

        // Not started: only the page.
        await phone.Set(true);
        Assert.Equal(new[] { $"serve --bg {port}" }, ServeCommands());

        live.Start();
        lock (commands) commands.Clear();
        await phone.Set(true);
        Assert.Equal(new[] { $"serve --bg {port}", $"serve --bg --set-path /live http://127.0.0.1:{livePort}" }, ServeCommands());
        lock (commands) commands.Clear();
        await phone.Recheck();
        Assert.Contains($"serve --bg --set-path /live http://127.0.0.1:{livePort}", ServeCommands());

        // Stopped (or its port taken by another program): /live is not pointed at it.
        live.Stop();
        lock (commands) commands.Clear();
        await phone.Set(true);
        Assert.Equal(new[] { $"serve --bg {port}" }, ServeCommands());
    }
}
