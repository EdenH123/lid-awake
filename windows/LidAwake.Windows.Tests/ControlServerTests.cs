using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LidAwake;
using Xunit;

// POST /input and POST /power end to end: a real WebServer on a free port, with fakes for
// SendInput and the power actions (nothing touches the real mouse, keyboard or screen). In the
// "WebServer" collection, so it never runs at the same time as the other server tests.
[Collection("WebServer")]
public class ControlServerTests : IDisposable
{
    const string TailHost = "pc.example.ts.net";
    const string Owner = "owner@example.com";
    const string Good = "correct horse battery";
    const string IPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)";
    const string ScreenOff = "Screen access is off.";
    const string ViewOnly = "View only: control from the phone is off in the Lid Awake menu on your PC.";
    const string TooMuch = "That's too much text to type at once.";

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Settings settings;
    readonly Auth auth;
    readonly AccessLog accessLog;
    readonly FakeInput input = new();
    readonly FakePower power = new();
    readonly RemoteControl control;
    readonly StatusInfo status;
    readonly WebServer server;
    readonly HttpClient http;
    readonly int port;
    bool lid;  // the lid, as the lid-closed screen would report it

    public ControlServerTests()
    {
        var site = Path.Combine(folder, "site");
        MakeSite(site);
        settings = new Settings(Path.Combine(folder, "settings"));
        settings.Set("tailnetHost", TailHost);
        settings.Set("tailnetOwner", Owner);
        auth = new Auth(settings, iterations: 1000);
        accessLog = new AccessLog(Path.Combine(folder, "logs"));
        // A clock that stands still: the first accepted input restarts the display timer, no other.
        control = new RemoteControl(settings, () => true, new InputInjector(input), power, () => 0, () => lid);
        status = new StatusInfo(new FakeReadings()) { Control = control };
        port = FreePort();
        server = new WebServer(auth, new Tailnet(settings, _ => null), () => true, () => status.Build(true),
            () => { }, (_, _) => { }, port, site, accessLog, control: control);
        server.Start();
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(60) };
    }

    public void Dispose()
    {
        http.Dispose();
        server.Dispose();
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // Both markers: the test's own bin folder also holds a copy of web\index.html, but only the
        // repository root has the app project folder.
        while (dir != null
               && !(File.Exists(Path.Combine(dir.FullName, "web", "index.html"))
                    && File.Exists(Path.Combine(dir.FullName, "windows", "LidAwake.Windows", "LidAwake.Windows.csproj"))))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root (web\\index.html and windows\\LidAwake.Windows) was not found above the test folder.");
    }

    static void MakeSite(string dir)
    {
        var root = RepoRoot();
        Directory.CreateDirectory(Path.Combine(dir, "web"));
        foreach (var f in new[] { "index.html", "sw.js", "manifest.webmanifest" })
            File.Copy(Path.Combine(root, "web", f), Path.Combine(dir, "web", f));
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    async Task<(int Code, string Body)> Send(string path, string? json, string? cookie, bool lid = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}");
        req.Headers.Host = TailHost;
        req.Headers.TryAddWithoutValidation("Tailscale-User-Login", Owner);
        req.Headers.TryAddWithoutValidation("User-Agent", IPhone);
        if (lid) req.Headers.TryAddWithoutValidation("X-LidAwake", "1");
        if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.SendAsync(req);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    async Task<string> SignIn()
    {
        if (!auth.HasPassword) auth.SetPassword(Good);
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/login");
        req.Headers.Host = TailHost;
        req.Headers.TryAddWithoutValidation("Tailscale-User-Login", Owner);
        req.Headers.TryAddWithoutValidation("X-LidAwake", "1");
        req.Content = new StringContent($"{{\"password\":\"{Good}\"}}", Encoding.UTF8, "application/json");
        var r = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return r.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    void ScreenOn() => settings.Set("remoteScreen", true);

    static string Family(int n) => string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", n));

    // JSON with the text as raw UTF-8 (not \u escapes), as the page's JSON.stringify sends it.
    // The texts here have no quotes, backslashes or control characters to escape.
    static string TextBody(string text) => "{\"type\":\"text\",\"text\":\"" + text + "\"}";

    [Fact]
    public async Task InputIsRunAndAnswered204()
    {
        var cookie = await SignIn();
        ScreenOn();
        var r = await Send("/input", """{"type":"move","dx":10,"dy":5}""", cookie);
        Assert.Equal(204, r.Code);
        Assert.Equal("", r.Body);
        var sent = input.All();
        Assert.Single(sent);
        Assert.Equal(InputInjector.MOUSEEVENTF_MOVE | InputInjector.MOUSEEVENTF_ABSOLUTE, sent[0].u.mi.dwFlags);
        Assert.Equal(InputInjector.ToAbsolute(110, 1920), sent[0].u.mi.dx);
        Assert.Equal(1, power.Wakes);  // accepted input wakes the screen
    }

    // What the page sends from its trackpad, screen picture and keyboard, in one go.
    [Fact]
    public async Task ThePagesCommandsAllWork()
    {
        var cookie = await SignIn();
        ScreenOn();
        foreach (var json in new[]
        {
            """{"type":"click","button":"left","count":1,"x":0.25,"y":0.75}""",
            """{"type":"click","button":"right","x":0.5,"y":0.5}""",
            """{"type":"mousedown"}""", """{"type":"move","dx":-3,"dy":4}""", """{"type":"mouseup"}""",
            """{"type":"scroll","dx":0,"dy":-30,"x":0.5,"y":0.5}""",
            """{"type":"text","text":"Hi"}""",
            """{"type":"key","key":"return","mods":[]}""",
            """{"type":"key","char":"c","mods":["cmd"]}""",
            """{"type":"key","key":"left","mods":["ctrl"]}""",
        })
            Assert.Equal(204, (await Send("/input", json, cookie)).Code);
        Assert.Equal(3 + 3 + 1 + 1 + 1 + 2 + 4 + 2 + 4 + 6, input.All().Count);
    }

    [Fact]
    public async Task UnknownCommandsAreAcceptedAndDoNothing()
    {
        var cookie = await SignIn();
        ScreenOn();
        foreach (var json in new[] { """{"type":"jump"}""", """{}""", """{"type":"ping"}""", """{"type":"key","key":"f13"}""" })
            Assert.Equal(204, (await Send("/input", json, cookie)).Code);
        Assert.Empty(input.All());
    }

    [Fact]
    public async Task ABodyThatIsNotAJsonObjectIs400()
    {
        var cookie = await SignIn();
        ScreenOn();
        foreach (var json in new[] { "not json", "[1,2]", "\"move\"", "", "null", "{\"type\":" })
        {
            var r = await Send("/input", json, cookie);
            Assert.Equal(400, r.Code);
            Assert.Equal("Bad request", r.Body);
        }
        Assert.Empty(input.All());
    }

    [Fact]
    public async Task InputWithTheScreenSwitchOffIs403()
    {
        var cookie = await SignIn();
        var r = await Send("/input", """{"type":"click"}""", cookie);
        Assert.Equal((403, ScreenOff), r);
        settings.Set("remoteControl", true);  // control on but screen off: still refused
        Assert.Equal((403, ScreenOff), await Send("/input", """{"type":"click"}""", cookie));
        Assert.Empty(input.All());
    }

    [Fact]
    public async Task InputWhileViewOnlyIs403()
    {
        var cookie = await SignIn();
        ScreenOn();
        settings.Set("remoteControl", false);
        var r = await Send("/input", """{"type":"click"}""", cookie);
        Assert.Equal((403, ViewOnly), r);
        Assert.Empty(input.All());
        Assert.Equal(0, power.Wakes);
    }

    // The checks come in the Mac's order: the switches before the body.
    [Fact]
    public async Task TheSwitchesAreCheckedBeforeTheBody()
    {
        var cookie = await SignIn();
        Assert.Equal((403, ScreenOff), await Send("/input", "not json", cookie));
        ScreenOn();
        settings.Set("remoteControl", false);
        Assert.Equal((403, ViewOnly), await Send("/input", TextBody(new string('a', 2001)), cookie));
    }

    [Fact]
    public async Task TextOver2000CharactersIs413And2000FamilyEmojiAreTyped()
    {
        var cookie = await SignIn();
        ScreenOn();
        var tooLong = await Send("/input", TextBody(new string('a', 2001)), cookie);
        Assert.Equal((413, TooMuch), tooLong);
        Assert.Equal((413, TooMuch), await Send("/input", TextBody(Family(2001)), cookie));
        Assert.Empty(input.All());

        // 2000 family emoji: 22,000 UTF-16 units, about 50 KB of UTF-8, still one character each.
        var ok = await Send("/input", TextBody(Family(2000)), cookie);
        Assert.Equal(204, ok.Code);
        Assert.Equal(2000, input.Calls.Count);
        Assert.Equal(2000 * 11 * 2, input.All().Count);
    }

    [Fact]
    public async Task InputNeedsASessionAndTheHeader()
    {
        ScreenOn();
        Assert.Equal((401, "Locked"), await Send("/input", """{"type":"click"}""", null));
        var cookie = await SignIn();
        Assert.Equal((403, "Forbidden"), await Send("/input", """{"type":"click"}""", cookie, lid: false));
        Assert.Equal((403, "Forbidden"), await Send("/power", """{"action":"lock"}""", cookie, lid: false));
        Assert.Empty(input.All());
        Assert.Equal(0, power.Locks);
    }

    [Fact]
    public async Task PowerNeedsTheScreenSwitch()
    {
        var cookie = await SignIn();
        foreach (var action in new[] { "lock", "displayoff", "wake", "nonsense" })
            Assert.Equal((403, ScreenOff), await Send("/power", $$"""{"action":"{{action}}"}""", cookie));
        Assert.Equal((0, 0, 0), (power.Locks, power.Offs, power.Wakes));
    }

    [Fact]
    public async Task LockNeedsTheControlSwitchAndIsLogged()
    {
        var cookie = await SignIn();
        ScreenOn();
        settings.Set("remoteControl", false);
        Assert.Equal((403, ViewOnly), await Send("/power", """{"action":"lock"}""", cookie));
        Assert.Equal(0, power.Locks);

        settings.Set("remoteControl", true);
        Assert.Equal((204, ""), await Send("/power", """{"action":"lock"}""", cookie));
        Assert.Equal(1, power.Locks);
        Assert.Contains(accessLog.Recent(5), l => l.EndsWith("  PC locked from the phone · iPhone", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DisplayOffAndWakeNeedOnlyTheScreenSwitch()
    {
        var cookie = await SignIn();
        ScreenOn();
        settings.Set("remoteControl", false);  // view only
        Assert.Equal((204, ""), await Send("/power", """{"action":"displayoff"}""", cookie));
        Assert.True(SpinWait.SpinUntil(() => power.Offs == 1, 5000));  // broadcast in the background
        Assert.True(control.ScreenOff);
        Assert.Contains(accessLog.Recent(5), l => l.EndsWith("  Display turned off from the phone · iPhone", StringComparison.Ordinal));
        Assert.True(JsonDocument.Parse(JsonSerializer.Serialize(status.Build(true))).RootElement.GetProperty("screenOff").GetBoolean());

        Assert.Equal((204, ""), await Send("/power", """{"action":"wake"}""", cookie));
        Assert.Equal(1, power.Wakes);
        Assert.False(control.ScreenOff);
    }

    // With the lid closed the screen couldn't be turned back on from the phone: refused, not logged.
    [Fact]
    public async Task DisplayOffWithTheLidClosedIs409()
    {
        var cookie = await SignIn();
        ScreenOn();
        lid = true;
        var r = await Send("/power", """{"action":"displayoff"}""", cookie);
        Assert.Equal((409, "The lid is closed: the screen couldn't be turned back on from your phone until you open the lid, so it stays on (at the lowest brightness)."), r);
        Assert.False(control.ScreenOff);
        await Task.Delay(100);
        Assert.Equal(0, power.Offs);
        Assert.DoesNotContain(accessLog.Recent(5), l => l.Contains("Display turned off", StringComparison.Ordinal));
        // Lock and wake don't care about the lid.
        Assert.Equal((204, ""), await Send("/power", """{"action":"wake"}""", cookie));
        Assert.Equal((204, ""), await Send("/power", """{"action":"lock"}""", cookie));
        lid = false;
        Assert.Equal((204, ""), await Send("/power", """{"action":"displayoff"}""", cookie));
        Assert.True(SpinWait.SpinUntil(() => power.Offs == 1, 5000));
    }

    [Fact]
    public async Task AnUnknownPowerActionIs400()
    {
        var cookie = await SignIn();
        ScreenOn();
        foreach (var json in new[] { """{"action":"reboot"}""", """{"action":5}""", """{}""", "nope", "[]", "" })
            Assert.Equal((400, "Bad request"), await Send("/power", json, cookie));
        Assert.Equal((0, 0, 0), (power.Locks, power.Offs, power.Wakes));
    }

    [Fact]
    public async Task StatusTellsThePageControlIsThere()
    {
        var cookie = await SignIn();
        ScreenOn();
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/status");
        req.Headers.Host = TailHost;
        req.Headers.TryAddWithoutValidation("Tailscale-User-Login", Owner);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        var r = await http.SendAsync(req);
        var s = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
        Assert.True(s.GetProperty("remoteControl").GetBoolean());
        Assert.True(s.GetProperty("controlPermission").GetBoolean());
        Assert.Contains("control", s.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }

    // A server built without control has no such routes.
    [Fact]
    public async Task WithoutControlTheRoutesAreNotFound()
    {
        var site = Path.Combine(folder, "site");
        int other = FreePort();
        using var plain = new WebServer(auth, new Tailnet(settings, _ => null), () => true, () => new { },
            () => { }, (_, _) => { }, other, site, accessLog);
        plain.Start();
        var cookie = await SignIn();
        ScreenOn();
        foreach (var path in new[] { "/input", "/power" })
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{other}{path}");
            req.Headers.Host = TailHost;
            req.Headers.TryAddWithoutValidation("Tailscale-User-Login", Owner);
            req.Headers.TryAddWithoutValidation("X-LidAwake", "1");
            req.Headers.TryAddWithoutValidation("Cookie", cookie);
            req.Content = new StringContent("""{"type":"click"}""", Encoding.UTF8, "application/json");
            var r = await http.SendAsync(req);
            Assert.Equal(404, (int)r.StatusCode);
        }
        Assert.Empty(input.All());
    }
}
