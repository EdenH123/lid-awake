using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LidAwake;
using Xunit;

// A real WebServer on a free port, driven with a real HttpClient. The Host, Tailscale and
// cookie headers are set by hand, the way Tailscale Serve and the phone would send them.
// The tests are in one class: each one starts its own server, with its own settings folder.
[Collection("WebServer")]
public partial class WebServerTests : IDisposable
{
    const string TailHost = "pc.example.ts.net";
    const string Owner = "owner@example.com";
    const string Good = "correct horse battery";
    const string OffText = "Lid Awake is off on your PC.";
    const string NotYours = "This device isn't signed in to the Tailscale account this PC uses.";
    const string IPhone = "Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)";
    const string UseTailscale = "Open Lid Awake at its Tailscale address.";

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly string site;
    readonly Settings settings;
    readonly Auth auth;
    readonly WebServer server;
    readonly HttpClient http;
    readonly int port;

    // What the tests control.
    bool on = true;
    int turnOffCalls;
    Func<object> statusAnswer = () => new { os = "windows", lidAwake = true };
    readonly List<(string Title, string Body)> announced = new();
    readonly AccessLog accessLog;
    readonly List<int> timerSets = new();
    AutoOff? realTimer;  // set by a test that wants /timer to change a real timer
    readonly List<(string Name, bool On)> limitSets = new();
    Limits? realLimits;  // set by a test that wants /limits to change the real switches
    // Phone alerts: the real WebPush, posting to a fake push service. Sign-ins are announced to the
    // phones as security alerts, as Program does.
    readonly FakePushService pushService = new();
    readonly WebPush push;

    public WebServerTests()
    {
        site = Path.Combine(folder, "site");
        MakeSite(site);
        settings = new Settings(Path.Combine(folder, "settings"));
        settings.Set("tailnetHost", TailHost);
        settings.Set("tailnetOwner", Owner);
        auth = new Auth(settings, iterations: 1000);
        port = FreePort();
        push = new WebPush(Settings.ForMachine(Path.Combine(folder, "machine")), pushService);
        accessLog = new AccessLog(Path.Combine(folder, "logs"));
        server = new WebServer(auth, new Tailnet(settings, _ => null), () => on, () => statusAnswer(),
            () => turnOffCalls++, (title, body) => { announced.Add((title, body)); _ = push.SendSecurity(title, body); }, port, site,
            accessLog, minutes => { lock (timerSets) timerSets.Add(minutes); realTimer?.Set(minutes); },
            setLimit: (name, value) => { lock (limitSets) limitSets.Add((name, value)); realLimits?.Set(name, value); },
            push: push);
        WireApps();
        server.Start();
        http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false })
        {
            Timeout = TimeSpan.FromSeconds(30),
        };
    }

    public void Dispose()
    {
        http.Dispose();
        server.Dispose();
        push.Dispose();
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

    // The folder layout the app has next to its exe, built from the repository's own files.
    static void MakeSite(string dir)
    {
        var root = RepoRoot();
        Directory.CreateDirectory(Path.Combine(dir, "web"));
        Directory.CreateDirectory(Path.Combine(dir, "sky"));
        foreach (var f in new[] { "index.html", "sw.js", "manifest.webmanifest" })
            File.Copy(Path.Combine(root, "web", f), Path.Combine(dir, "web", f));
        foreach (var f in Directory.GetFiles(Path.Combine(root, "assets"), "sky-*.jpg"))
            File.Copy(f, Path.Combine(dir, "sky", Path.GetFileName(f)));
        foreach (var f in new[] { "icon-512.png", "icon-180.png" })
            File.Copy(Path.Combine(root, "windows", "LidAwake.Windows", f), Path.Combine(dir, f));
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    sealed record Reply(HttpResponseMessage Response, byte[] Bytes)
    {
        public int Code => (int)Response.StatusCode;
        public string Body => Encoding.UTF8.GetString(Bytes);
        public string? Header(string name) =>
            Response.Headers.TryGetValues(name, out var v) || Response.Content.Headers.TryGetValues(name, out v) ? string.Join(", ", v) : null;
        public bool Has(string name) => Header(name) != null;
    }

    // Defaults are a request from the owner's phone through Tailscale Serve. Pass a null
    // `owner`, or another `host`, to change that.
    async Task<Reply> Send(string method, string path, string? host = TailHost, string? owner = Owner, string? fetchSite = null,
                           bool lid = false, string? cookie = null, string? json = null, string? ua = null, bool funnel = false)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{port}{path}");
        if (host != null) req.Headers.Host = host;
        if (owner != null) req.Headers.TryAddWithoutValidation("Tailscale-User-Login", owner);
        if (fetchSite != null) req.Headers.TryAddWithoutValidation("Sec-Fetch-Site", fetchSite);
        if (lid) req.Headers.TryAddWithoutValidation("X-LidAwake", "1");
        if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (ua != null) req.Headers.TryAddWithoutValidation("User-Agent", ua);
        if (funnel) req.Headers.TryAddWithoutValidation("Tailscale-Funnel-Request", "?1");
        if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.SendAsync(req);
        return new Reply(response, await response.Content.ReadAsByteArrayAsync());
    }

    // Sets the password if needed, signs in, and returns the "name=token" cookie to send back.
    async Task<string> SignIn(string? ua = null)
    {
        if (!auth.HasPassword) auth.SetPassword(Good);
        var r = await Send("POST", "/login", lid: true, json: $"{{\"password\":\"{Good}\"}}", ua: ua);
        Assert.Equal(204, r.Code);
        return r.Response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    static JsonElement Json(Reply r) => JsonDocument.Parse(r.Body).RootElement.Clone();

    // 2.8: every inline script of the real page is hashed into the CSP.
    [Fact]
    public async Task PageHasCspWithAHashForEveryInlineScript()
    {
        var r = await Send("GET", "/");
        Assert.Equal(200, r.Code);
        Assert.Equal("text/html; charset=utf-8", r.Header("Content-Type"));
        var raw = File.ReadAllText(Path.Combine(site, "web", "index.html"));
        var scripts = Regex.Matches(raw, "<script>([\\s\\S]*?)</script>");
        Assert.True(scripts.Count >= 1);
        var csp = r.Header("Content-Security-Policy")!;
        foreach (Match m in scripts)
        {
            var hash = "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(m.Groups[1].Value))) + "'";
            Assert.Contains(hash, csp);
        }
        Assert.Contains("connect-src 'self' wss://pc.example.ts.net", csp);
        Assert.DoesNotContain("<!--SPLASH-->", r.Body);
        Assert.Contains("<script>", r.Body);
    }

    [Fact]
    public async Task EveryResponseHasTheCommonHeadersAndNoServerHeader()
    {
        foreach (var r in new[] { await Send("GET", "/"), await Send("GET", "/nope"), await Send("GET", "/sw.js") })
        {
            Assert.Equal("no-store", r.Header("Cache-Control"));
            Assert.Equal("nosniff", r.Header("X-Content-Type-Options"));
            Assert.Equal("DENY", r.Header("X-Frame-Options"));
            Assert.Equal("same-origin", r.Header("Cross-Origin-Resource-Policy"));
            Assert.Equal("no-referrer", r.Header("Referrer-Policy"));
            Assert.False(r.Has("Server"));
        }
    }

    [Fact]
    public async Task StaticFilesAreServed()
    {
        var sw = await Send("GET", "/sw.js");
        Assert.Equal(200, sw.Code);
        Assert.Equal("text/javascript; charset=utf-8", sw.Header("Content-Type"));
        Assert.Equal(File.ReadAllBytes(Path.Combine(site, "web", "sw.js")), sw.Bytes);

        var manifest = await Send("GET", "/manifest.webmanifest");
        Assert.Equal(200, manifest.Code);
        Assert.Equal("application/manifest+json", manifest.Header("Content-Type"));

        var sky = await Send("GET", "/sky/day.jpg");
        Assert.Equal(200, sky.Code);
        Assert.Equal("image/jpeg", sky.Header("Content-Type"));

        var evil = await Send("GET", "/sky/evil.jpg");
        Assert.Equal(404, evil.Code);
        Assert.Equal("Not found", evil.Body);

        // Launch screens are optional on Windows (section 3), and never need a session.
        foreach (var path in new[] { "/splash/x.png", "/splash/1179x2556.png" })
        {
            var splash = await Send("GET", path);
            Assert.Equal(404, splash.Code);
            Assert.Equal("Not found", splash.Body);
        }

        foreach (var path in new[] { "/apple-touch-icon.png", "/apple-touch-icon-precomposed.png" })
        {
            var small = await Send("GET", path);
            Assert.Equal(200, small.Code);
            Assert.Equal("image/png", small.Header("Content-Type"));
            Assert.Equal(File.ReadAllBytes(Path.Combine(site, "icon-180.png")), small.Bytes);
        }
        var big = await Send("GET", "/icon-512.png");
        Assert.Equal(200, big.Code);
        Assert.Equal(File.ReadAllBytes(Path.Combine(site, "icon-512.png")), big.Bytes);
    }

    [Fact]
    public async Task StaticFilesAndPageAreAnsweredWhileOffAndToOtherSites()
    {
        on = false;
        Assert.Equal(200, (await Send("GET", "/")).Code);
        Assert.Equal(200, (await Send("GET", "/sw.js")).Code);
        // Step 4 does not apply to the page and static files.
        Assert.Equal(200, (await Send("GET", "/sw.js", fetchSite: "cross-site")).Code);
        Assert.Equal(200, (await Send("GET", "/", fetchSite: "cross-site")).Code);
    }

    // 2.3
    [Fact]
    public async Task LoopbackOnlyPointsToTheTailscaleAddress()
    {
        var page = await Send("GET", "/", host: $"127.0.0.1:{port}", owner: null);
        Assert.Equal(200, page.Code);
        // The port is removed and nothing else is added: exactly the loopback address.
        Assert.Contains("connect-src 'self' wss://127.0.0.1; worker-src 'self';", page.Header("Content-Security-Policy"));

        var authReply = await Send("GET", "/auth", host: "127.0.0.1", owner: null);
        Assert.Equal(200, authReply.Code);
        Assert.Equal("application/json", authReply.Header("Content-Type"));
        var json = Json(authReply);
        Assert.True(json.GetProperty("loopback").GetBoolean());
        Assert.Equal("https://pc.example.ts.net/", json.GetProperty("url").GetString());
        Assert.Equal("windows", json.GetProperty("os").GetString());

        Assert.Equal(200, (await Send("GET", "/sw.js", host: "localhost", owner: null)).Code);

        var status = await Send("GET", "/status", host: "127.0.0.1", owner: null);
        Assert.Equal(403, status.Code);
        Assert.Equal(UseTailscale, status.Body);
        var login = await Send("POST", "/login", host: "localhost", owner: null, lid: true, json: "{}");
        Assert.Equal(403, login.Code);
        Assert.Equal(UseTailscale, login.Body);
    }

    // A local program can claim to be the owner, but 127.0.0.1 never gets past 2.3.
    [Fact]
    public async Task LoopbackWithAForgedOwnerIsStillRefused()
    {
        var cookie = await SignIn();
        foreach (var host in new[] { "127.0.0.1", "localhost", $"127.0.0.1:{port}" })
        {
            var status = await Send("GET", "/status", host: host, owner: Owner, cookie: cookie);
            Assert.Equal(403, status.Code);
            Assert.Equal(UseTailscale, status.Body);

            var login = await Send("POST", "/login", host: host, owner: Owner, lid: true, json: $"{{\"password\":\"{Good}\"}}");
            Assert.Equal(403, login.Code);
            Assert.Equal(UseTailscale, login.Body);
            Assert.False(login.Has("Set-Cookie"));
        }
        // Only the sign-in above was announced.
        Assert.Single(announced);
    }

    [Fact]
    public async Task LoopbackAnswersEvenWhileOffAndUrlIsEmptyBeforeTheNameIsKnown()
    {
        on = false;
        Assert.True(Json(await Send("GET", "/auth", host: "127.0.0.1", owner: null)).GetProperty("loopback").GetBoolean());
        settings.Set<string>("tailnetHost", null);
        Assert.Equal("", Json(await Send("GET", "/auth", host: "127.0.0.1", owner: null)).GetProperty("url").GetString());
    }

    // 2.2 steps 1 to 3
    [Fact]
    public async Task OnlyTheOwnerThroughTailscaleGetsIn()
    {
        var funnel = await Send("GET", "/status", funnel: true);
        Assert.Equal(403, funnel.Code);
        Assert.Equal("Forbidden", funnel.Body);
        var funnelPage = await Send("GET", "/", host: "127.0.0.1", owner: null, funnel: true);
        Assert.Equal(403, funnelPage.Code);
        Assert.Equal("Forbidden", funnelPage.Body);

        var wrongHost = await Send("GET", "/auth", host: "evil.example.com");
        Assert.Equal(403, wrongHost.Code);
        Assert.Equal(NotYours, wrongHost.Body);

        var wrongOwner = await Send("GET", "/auth", owner: "someone@example.com");
        Assert.Equal(403, wrongOwner.Code);
        Assert.Equal(NotYours, wrongOwner.Body);

        var noOwner = await Send("GET", "/auth", owner: null);
        Assert.Equal(403, noOwner.Code);
        Assert.Equal(NotYours, noOwner.Body);
        var otherPage = await Send("GET", "/", owner: "someone@example.com");
        Assert.Equal(403, otherPage.Code);
        Assert.Equal(NotYours, otherPage.Body);

        Assert.Equal(200, (await Send("GET", "/auth", owner: "OWNER@Example.com")).Code);
        Assert.Equal(200, (await Send("GET", "/auth", host: "PC.Example.TS.net:443")).Code);
    }

    // The Host must be exactly this PC's name: no suffix, prefix or trailing dot.
    [Fact]
    public async Task NamesThatOnlyLookLikeThisPcAreRefused()
    {
        foreach (var host in new[] { "pc.example.ts.net.evil.com", "xpc.example.ts.net", "pc.example.ts.net.", "example.ts.net" })
        {
            foreach (var path in new[] { "/", "/auth", "/sw.js" })
            {
                var r = await Send("GET", path, host: host);
                Assert.Equal(403, r.Code);
                Assert.Equal(NotYours, r.Body);
            }
        }
    }

    // 2.2 step 4
    [Fact]
    public async Task RequestsFromOtherSitesAreRefused()
    {
        var cookie = await SignIn();
        var cross = await Send("GET", "/status", fetchSite: "cross-site", cookie: cookie);
        Assert.Equal(403, cross.Code);
        Assert.Equal("Forbidden", cross.Body);
        var sameSite = await Send("GET", "/auth", fetchSite: "same-site");
        Assert.Equal(403, sameSite.Code);
        Assert.Equal("Forbidden", sameSite.Body);
        Assert.Equal(200, (await Send("GET", "/status", fetchSite: "same-origin", cookie: cookie)).Code);
        Assert.Equal(200, (await Send("GET", "/status", fetchSite: "none", cookie: cookie)).Code);
    }

    [Fact]
    public async Task SignInFromAnotherSiteIsRefusedWithoutATry()
    {
        auth.SetPassword(Good);
        var r = await Send("POST", "/login", fetchSite: "cross-site", lid: true, json: $"{{\"password\":\"{Good}\"}}");
        Assert.Equal(403, r.Code);
        Assert.Equal("Forbidden", r.Body);
        Assert.False(r.Has("Set-Cookie"));
        Assert.Empty(announced);
        Assert.Equal(0, settings.Get<int?>("loginFailures") ?? 0);
    }

    // 2.4
    [Fact]
    public async Task WhileOffOnlyTheLockStateAnswers()
    {
        var cookie = await SignIn();
        on = false;
        var authReply = await Send("GET", "/auth", cookie: cookie);
        Assert.Equal(200, authReply.Code);
        Assert.Equal("{\"off\":true,\"os\":\"windows\"}", authReply.Body);

        // Even a signed-in phone gets 423, not 401, and a made-up route does too.
        var status = await Send("GET", "/status", cookie: cookie);
        Assert.Equal(423, status.Code);
        Assert.Equal(OffText, status.Body);
        Assert.Equal(423, (await Send("GET", "/status")).Code);
        Assert.Equal(423, (await Send("GET", "/nope", cookie: cookie)).Code);
        var login = await Send("POST", "/login", lid: true, json: $"{{\"password\":\"{Good}\"}}");
        Assert.Equal(423, login.Code);
        Assert.Equal(OffText, login.Body);
        Assert.Equal(423, (await Send("POST", "/awake", lid: true, cookie: cookie, json: "{\"on\":false}")).Code);
        Assert.Equal(0, turnOffCalls);
    }

    [Fact]
    public async Task WithNoPasswordTheLockStateSaysSo()
    {
        var r = await Send("GET", "/auth");
        Assert.Equal(200, r.Code);
        Assert.Equal("application/json", r.Header("Content-Type"));
        var json = Json(r);
        Assert.False(json.GetProperty("hasPassword").GetBoolean());
        Assert.False(json.GetProperty("loggedIn").GetBoolean());
        Assert.False(json.GetProperty("faceID").GetBoolean());
        Assert.Equal("windows", json.GetProperty("os").GetString());

        var login = await Send("POST", "/login", lid: true, json: $"{{\"password\":\"{Good}\"}}");
        Assert.Equal(409, login.Code);
        Assert.Equal("No password is set yet.", login.Body);
        Assert.Equal("text/plain; charset=utf-8", login.Header("Content-Type"));
    }

    [Fact]
    public async Task WrongPasswordIsRefusedAndRightOneSignsIn()
    {
        auth.SetPassword(Good);
        var wrong = await Send("POST", "/login", lid: true, json: "{\"password\":\"not the password\"}");
        Assert.Equal(401, wrong.Code);
        Assert.Equal("Wrong password.", wrong.Body);
        Assert.False(wrong.Has("Set-Cookie"));
        Assert.Empty(announced);

        var ok = await Send("POST", "/login", lid: true, json: $"{{\"password\":\"{Good}\"}}");
        Assert.Equal(204, ok.Code);
        Assert.Equal("", ok.Body);
        var setCookie = ok.Response.Headers.GetValues("Set-Cookie").Single();
        Assert.Matches("^__Host-la_session=[0-9a-f]{64}; Path=/; Max-Age=604800; HttpOnly; SameSite=Strict; Secure$", setCookie);

        var state = Json(await Send("GET", "/auth", cookie: setCookie.Split(';')[0]));
        Assert.True(state.GetProperty("hasPassword").GetBoolean());
        Assert.True(state.GetProperty("loggedIn").GetBoolean());
        Assert.Equal("windows", state.GetProperty("os").GetString());

        // Announced once, for a browser that is none of the known phones.
        var note = Assert.Single(announced);
        Assert.Equal("New sign-in to Lid Awake", note.Title);
        Assert.Equal("With the password, on a browser.", note.Body);
    }

    [Fact]
    public async Task SignInIsAnnouncedWithTheDevice()
    {
        await SignIn(IPhone);
        var note = Assert.Single(announced);
        Assert.Equal("New sign-in to Lid Awake", note.Title);
        Assert.Equal("With the password, on an iPhone.", note.Body);
    }

    [Fact]
    public async Task FailingAnnouncementDoesNotUndoSignIn()
    {
        var otherPort = FreePort();
        var throwing = new WebServer(auth, new Tailnet(settings, _ => null), () => on, () => statusAnswer(),
            () => turnOffCalls++, (_, _) => throw new InvalidOperationException("no balloon"), otherPort, site);
        throwing.Start();
        try
        {
            auth.SetPassword(Good);
            using var client = new HttpClient();
            using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{otherPort}/login");
            req.Headers.Host = TailHost;
            req.Headers.TryAddWithoutValidation("Tailscale-User-Login", Owner);
            req.Headers.TryAddWithoutValidation("X-LidAwake", "1");
            req.Content = new StringContent($"{{\"password\":\"{Good}\"}}", Encoding.UTF8, "application/json");
            using var r = await client.SendAsync(req);
            Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        }
        finally { throwing.Dispose(); }
    }

    [Fact]
    public async Task TooManyWrongTriesLockSignIn()
    {
        auth.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries - 1; i++)
            Assert.Equal(401, (await Send("POST", "/login", lid: true, json: "{\"password\":\"nope\"}")).Code);
        var locked = await Send("POST", "/login", lid: true, json: "{\"password\":\"nope\"}");
        Assert.Equal(429, locked.Code);
        Assert.Equal("Too many wrong tries. Try again in 60 seconds.", locked.Body);
        // Even the right password is refused while locked.
        Assert.Equal(429, (await Send("POST", "/login", lid: true, json: $"{{\"password\":\"{Good}\"}}")).Code);
    }

    [Fact]
    public async Task LoginBodyThatIsNotJsonCountsAsEmpty()
    {
        auth.SetPassword(Good);
        var r = await Send("POST", "/login", lid: true, json: "this is not json");
        Assert.Equal(401, r.Code);
        Assert.Equal("Wrong password.", r.Body);
    }

    // Sends exactly these bytes and reads until the server closes the connection.
    async Task<string> SendRaw(string request)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        using var stream = tcp.GetStream();
        var bytes = Encoding.ASCII.GetBytes(request);
        await stream.WriteAsync(bytes);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        return await reader.ReadToEndAsync(cts.Token);
    }

    // 2.1
    [Fact]
    public async Task ChunkedBodyIsRefused()
    {
        auth.SetPassword(Good);
        var body = $"{{\"password\":\"{Good}\"}}";
        var reply = await SendRaw(
            "POST /login HTTP/1.1\r\n" +
            $"Host: {TailHost}\r\n" +
            $"Tailscale-User-Login: {Owner}\r\n" +
            "X-LidAwake: 1\r\n" +
            "Content-Type: application/json\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Connection: close\r\n" +
            "\r\n" +
            $"{body.Length:x}\r\n{body}\r\n0\r\n\r\n");
        Assert.StartsWith("HTTP/1.1 400 ", reply);
        Assert.Contains("Bad request", reply);
        Assert.DoesNotContain("Set-Cookie", reply);
        Assert.Empty(announced);
    }

    [Fact]
    public async Task BodyOverSixtyFourKilobytesIsRefused()
    {
        auth.SetPassword(Good);
        var r = await Send("POST", "/login", lid: true, json: "{\"password\":\"" + new string('a', 70_000) + "\"}");
        Assert.Equal(400, r.Code);
        Assert.Equal("Bad request", r.Body);
    }

    [Fact]
    public async Task StatusAnswersWithASession()
    {
        var cookie = await SignIn();
        var r = await Send("GET", "/status", cookie: cookie);
        Assert.Equal(200, r.Code);
        Assert.Equal("application/json", r.Header("Content-Type"));
        var json = Json(r);
        Assert.Equal("windows", json.GetProperty("os").GetString());
        Assert.True(json.GetProperty("lidAwake").GetBoolean());
    }

    // Task 12: GET /status, signed in, carries the full status (docs/API.md section 6).
    [Fact]
    public async Task StatusAnswersTheFullStatusFromStatusInfo()
    {
        var fake = new FakeReadings
        {
            PowerNow = new PowerReading(0, 1, 80, 7200),
            CpuNow = new CpuTimes(1000, 2000, 500),
            NetNow = new NetCounters("Ethernet", true, 1000, 500),
            Ms = 0,
        };
        var info = new StatusInfo(fake);
        statusAnswer = () => info.Build(true);
        var cookie = await SignIn();

        var first = await Send("GET", "/status", cookie: cookie);
        Assert.Equal(200, first.Code);
        Assert.Equal("application/json", first.Header("Content-Type"));
        var s = Json(first);
        StatusInfoTests.AssertEveryKeyWithItsKind(s);
        Assert.Equal("windows", s.GetProperty("os").GetString());
        Assert.Equal("DESKTOP-TEST", s.GetProperty("host").GetString());
        Assert.True(s.GetProperty("lidAwake").GetBoolean());
        Assert.Equal(80, s.GetProperty("battery").GetInt32());
        Assert.Equal(120, s.GetProperty("minutesToEmpty").GetInt32());
        Assert.Equal(1920, s.GetProperty("screenWidth").GetInt32());
        Assert.Equal(16UL << 30, s.GetProperty("mac").GetProperty("memTotal").GetUInt64());
        Assert.Equal("Ethernet", s.GetProperty("net").GetProperty("iface").GetString());
        Assert.True(s.GetProperty("net").GetProperty("wired").GetBoolean());
        Assert.False(s.GetProperty("mac").TryGetProperty("cpu", out _));

        // The second request measures the load and the network speed since the first.
        fake.CpuNow = new CpuTimes(1750, 3000, 1000);
        fake.NetNow = new NetCounters("Ethernet", true, 3000, 1500);
        fake.Ms = 2000;
        var second = Json(await Send("GET", "/status", cookie: cookie));
        Assert.Equal(50.0, second.GetProperty("mac").GetProperty("cpu").GetDouble());
        Assert.Equal(1000.0, second.GetProperty("net").GetProperty("down").GetDouble());
        Assert.Equal(500.0, second.GetProperty("net").GetProperty("up").GetDouble());
    }

    [Fact]
    public async Task StatusWithTheRealReadingsIsWhole()
    {
        var info = new StatusInfo(new SystemReadings());
        statusAnswer = () => info.Build(true);
        var cookie = await SignIn();
        var r = await Send("GET", "/status", cookie: cookie);
        Assert.Equal(200, r.Code);
        var s = Json(r);
        StatusInfoTests.AssertEveryKeyWithItsKind(s);
        Assert.Equal("windows", s.GetProperty("os").GetString());
        Assert.True(s.GetProperty("mac").GetProperty("memTotal").GetUInt64() > 0);
    }

    [Fact]
    public async Task AnyValidCookieOfSeveralCounts()
    {
        var cookie = await SignIn();
        var r = await Send("GET", "/status", cookie: "__Host-la_session=" + new string('0', 64) + "; other=1; " + cookie);
        Assert.Equal(200, r.Code);
    }

    [Fact]
    public async Task OnlyTurningOffIsAllowedFromThePhone()
    {
        var cookie = await SignIn();
        var off = await Send("POST", "/awake", lid: true, cookie: cookie, json: "{\"on\":false}");
        Assert.Equal(204, off.Code);
        Assert.Equal(1, turnOffCalls);

        var turnOn = await Send("POST", "/awake", lid: true, cookie: cookie, json: "{\"on\":true}");
        Assert.Equal(400, turnOn.Code);
        Assert.Equal("Lid-awake can only be turned on from this PC.", turnOn.Body);
        Assert.Equal(400, (await Send("POST", "/awake", lid: true, cookie: cookie, json: "{}")).Code);
        Assert.Equal(400, (await Send("POST", "/awake", lid: true, cookie: cookie, json: "nonsense")).Code);
        Assert.Equal(400, (await Send("POST", "/awake", lid: true, cookie: cookie)).Code);
        Assert.Equal(1, turnOffCalls);
    }

    [Fact]
    public async Task LogoutClearsTheCookieAndTheSession()
    {
        var cookie = await SignIn();
        var r = await Send("POST", "/logout", lid: true, cookie: cookie);
        Assert.Equal(204, r.Code);
        Assert.Equal("__Host-la_session=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict; Secure",
            r.Response.Headers.GetValues("Set-Cookie").Single());

        var after = await Send("GET", "/status", cookie: cookie);
        Assert.Equal(401, after.Code);
        Assert.Equal("Locked", after.Body);
        Assert.False(Json(await Send("GET", "/auth", cookie: cookie)).GetProperty("loggedIn").GetBoolean());
    }

    [Fact]
    public async Task LogoutWithTwoCookiesEndsBothSessions()
    {
        var first = await SignIn();
        var second = await SignIn(IPhone);
        Assert.NotEqual(first, second);
        var r = await Send("POST", "/logout", lid: true, cookie: first + "; " + second);
        Assert.Equal(204, r.Code);
        Assert.Equal(401, (await Send("GET", "/status", cookie: first)).Code);
        Assert.Equal(401, (await Send("GET", "/status", cookie: second)).Code);
    }

    [Fact]
    public async Task LogoutWithAMadeUpTokenChangesNothing()
    {
        var cookie = await SignIn();
        var r = await Send("POST", "/logout", lid: true, cookie: "__Host-la_session=" + new string('1', 64));
        Assert.Equal(204, r.Code);
        Assert.Equal(200, (await Send("GET", "/status", cookie: cookie)).Code);
    }

    // 2.2 steps 7 and 8
    [Fact]
    public async Task SessionIsCheckedBeforeTheHeader()
    {
        var cookie = await SignIn();
        var signedIn = await Send("POST", "/awake", cookie: cookie, json: "{\"on\":false}");
        Assert.Equal(403, signedIn.Code);
        Assert.Equal("Forbidden", signedIn.Body);
        Assert.Equal(0, turnOffCalls);

        var anonymous = await Send("POST", "/awake", json: "{\"on\":false}");
        Assert.Equal(401, anonymous.Code);
        Assert.Equal("Locked", anonymous.Body);

        var status = await Send("GET", "/status");
        Assert.Equal(401, status.Code);
        Assert.Equal("Locked", status.Body);

        // Public routes need no session, but a POST still needs the header.
        var logout = await Send("POST", "/logout");
        Assert.Equal(403, logout.Code);
        Assert.Equal("Forbidden", logout.Body);
        var login = await Send("POST", "/login", json: "{}");
        Assert.Equal(403, login.Code);
        Assert.Equal("Forbidden", login.Body);
    }

    // 2.2 step 9
    [Fact]
    public async Task MethodAndPathMustBothMatch()
    {
        var cookie = await SignIn();
        var getLogin = await Send("GET", "/login");
        Assert.Equal(404, getLogin.Code);
        Assert.Equal("Not found", getLogin.Body);
        Assert.Equal(404, (await Send("GET", "/login", cookie: cookie)).Code);
        Assert.Equal(404, (await Send("POST", "/status", lid: true, cookie: cookie)).Code);
        Assert.Equal(404, (await Send("GET", "/awake", cookie: cookie)).Code);

        var unknown = await Send("GET", "/nope", cookie: cookie);
        Assert.Equal(404, unknown.Code);
        Assert.Equal("Not found", unknown.Body);
        // A route that is not built yet: 404 after the session check, like any route that does not exist.
        Assert.Equal(404, (await Send("GET", "/memory", cookie: cookie)).Code);
        Assert.Equal(401, (await Send("GET", "/memory")).Code);
        Assert.Equal(401, (await Send("GET", "/sessions")).Code);
        // The routes that exist answer only their own method.
        Assert.Equal(404, (await Send("POST", "/sessions", lid: true, cookie: cookie)).Code);
        Assert.Equal(404, (await Send("GET", "/sessions/signout", cookie: cookie)).Code);
        Assert.Equal(404, (await Send("POST", "/activity", lid: true, cookie: cookie)).Code);
        Assert.Equal(404, (await Send("GET", "/timer", cookie: cookie)).Code);
        Assert.Equal(404, (await Send("POST", "/passkey/login-options", lid: true, cookie: cookie)).Code);
        Assert.Equal(404, (await Send("GET", "/passkey/login")).Code);
        Assert.Equal(401, (await Send("GET", "/passkey/register-options")).Code);
        Assert.False(unknown.Has("Server"));
    }

    // P2-9
    [Fact]
    public async Task AnUnexpectedErrorAnswers500AndTheServerKeepsRunning()
    {
        var cookie = await SignIn();
        statusAnswer = () => throw new InvalidOperationException("boom");
        var r = await Send("GET", "/status", cookie: cookie);
        Assert.Equal(500, r.Code);
        Assert.Equal("Something went wrong.", r.Body);
        Assert.Equal("no-store", r.Header("Cache-Control"));

        statusAnswer = () => new { os = "windows", lidAwake = true };
        Assert.Equal(200, (await Send("GET", "/status", cookie: cookie)).Code);
    }

    [Fact]
    public async Task DamagedPasswordSettingsAnswer500WithoutCountingAWrongTry()
    {
        auth.SetPassword(Good);
        settings.Set("pwSalt", "this is not base64!");
        var r = await Send("POST", "/login", lid: true, json: $"{{\"password\":\"{Good}\"}}");
        Assert.Equal(500, r.Code);
        Assert.Equal("Something went wrong.", r.Body);
        Assert.Equal(0, settings.Get<int?>("loginFailures") ?? 0);
        Assert.Equal(200, (await Send("GET", "/")).Code);
    }

    // An error replaces whatever the failed request had already set.
    [Fact]
    public async Task AnErrorAnswerDropsHeadersSetBeforeIt()
    {
        var c = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        var body = new MemoryStream();
        c.Response.Body = body;
        c.Response.Headers["Set-Cookie"] = "__Host-la_session=abc; Path=/";
        c.Response.Headers["Content-Security-Policy"] = "default-src 'none'";
        c.Response.ContentType = "application/json";
        await WebServer.Fail(c, 500, "Something went wrong.");
        Assert.Equal(500, c.Response.StatusCode);
        Assert.False(c.Response.Headers.ContainsKey("Set-Cookie"));
        Assert.False(c.Response.Headers.ContainsKey("Content-Security-Policy"));
        Assert.Equal("text/plain; charset=utf-8", c.Response.ContentType);
        foreach (var (name, value) in PageHeaders.Common)
            Assert.Equal(value, c.Response.Headers[name].ToString());
        Assert.Equal("Something went wrong.", Encoding.UTF8.GetString(body.ToArray()));
    }

    // Only the one loopback address in the code is listened on, whatever the environment says.
    [Fact]
    public async Task TheEnvironmentCannotAddAnAddress()
    {
        var extra = FreePort();
        var otherPort = FreePort();
        const string name = "Kestrel__Endpoints__Extra__Url";
        Environment.SetEnvironmentVariable(name, $"http://127.0.0.1:{extra}");
        try
        {
            using var other = new WebServer(auth, new Tailnet(settings, _ => null), () => on, () => statusAnswer(),
                () => turnOffCalls++, (_, _) => { }, otherPort, site);
            other.Start();
            Assert.True(other.IsRunning);
            using var probe = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, extra));
        }
        finally { Environment.SetEnvironmentVariable(name, null); }
    }

    // URLS read when the server is built could move it off 127.0.0.1:<port>: then it refuses to run.
    [Fact]
    public async Task HostingUrlsFromTheEnvironmentCannotMoveTheServer()
    {
        var extra = FreePort();
        var otherPort = FreePort();
        Environment.SetEnvironmentVariable("ASPNETCORE_URLS", $"http://127.0.0.1:{extra}");
        Environment.SetEnvironmentVariable("ASPNETCORE_PREFERHOSTINGURLS", "true");
        try
        {
            using var other = new WebServer(auth, new Tailnet(settings, _ => null), () => on, () => statusAnswer(),
                () => turnOffCalls++, (_, _) => { }, otherPort, site);
            try { other.Start(); }
            catch (InvalidOperationException) { Assert.False(other.IsRunning); }
            using var probe = new TcpClient();
            await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, extra));
        }
        finally
        {
            Environment.SetEnvironmentVariable("ASPNETCORE_URLS", null);
            Environment.SetEnvironmentVariable("ASPNETCORE_PREFERHOSTINGURLS", null);
        }
    }

    // ---- Task 13b: /sessions, /sessions/signout, /activity, /timer (docs/API.md 5.2) ----

    Task<Reply> Post(string path, string cookie, string? json = null, string? ua = null) =>
        Send("POST", path, lid: true, cookie: cookie == "" ? null : cookie, json: json, ua: ua);

    static string[] Lines(Reply r) => Json(r).GetProperty("lines").EnumerateArray().Select(l => l.GetString()!).ToArray();

    static JsonElement[] SessionList(Reply r) => Json(r).GetProperty("sessions").EnumerateArray().ToArray();

    // Every session but the caller's own, by its handle.
    static string[] OtherIds(Reply r) => SessionList(r).Where(x => !x.GetProperty("current").GetBoolean()).Select(x => x.GetProperty("id").GetString()!).ToArray();

    [Fact]
    public async Task SessionsListsEachSignedInDeviceNewestFirst()
    {
        var phone = await SignIn(IPhone);
        await Task.Delay(1100);  // sessions are told apart by the second they were made
        var pc = await SignIn("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");

        var r = await Send("GET", "/sessions", cookie: phone);
        Assert.Equal(200, r.Code);
        Assert.Equal("application/json", r.Header("Content-Type"));
        var list = SessionList(r);
        Assert.Equal(2, list.Length);
        Assert.Equal(new[] { "Windows PC", "iPhone" }, list.Select(x => x.GetProperty("device").GetString()));
        Assert.Equal(new[] { false, true }, list.Select(x => x.GetProperty("current").GetBoolean()));
        foreach (var x in list)
        {
            Assert.Equal(new[] { "id", "device", "created", "faceID", "current" }, x.EnumerateObject().Select(p => p.Name));
            Assert.Matches("^[0-9a-f]{12}$", x.GetProperty("id").GetString()!);
            Assert.Equal(JsonValueKind.Number, x.GetProperty("created").ValueKind);
            Assert.False(x.GetProperty("faceID").GetBoolean());
        }
        Assert.True(list[0].GetProperty("created").GetInt64() > list[1].GetProperty("created").GetInt64());
        Assert.InRange(list[0].GetProperty("created").GetInt64(), DateTimeOffset.UtcNow.ToUnixTimeSeconds() - 60, DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 1);

        // The same list from the other device has the flag the other way round.
        Assert.Equal(new[] { true, false }, SessionList(await Send("GET", "/sessions", cookie: pc)).Select(x => x.GetProperty("current").GetBoolean()));
    }

    [Fact]
    public async Task SessionsNeverShowsTheTokenOrItsHash()
    {
        var cookie = await SignIn();
        var token = cookie[(cookie.IndexOf('=') + 1)..];
        var stored = settings.Get<List<Auth.Session>>("sessions")!.Single().Hash;
        var r = await Send("GET", "/sessions", cookie: cookie);
        Assert.DoesNotContain(token, r.Body);
        Assert.DoesNotContain(stored, r.Body);
        Assert.Contains(stored[..12], r.Body);
    }

    [Fact]
    public async Task SessionsAndSignOutNeedASessionAndTheRightHeaderAndAreClosedWhileOff()
    {
        var cookie = await SignIn();
        var anonymous = await Send("GET", "/sessions");
        Assert.Equal(401, anonymous.Code);
        Assert.Equal("Locked", anonymous.Body);
        Assert.Equal(401, (await Post("/sessions/signout", "", "{\"others\":true}")).Code);
        // Signed in, but a POST without X-LidAwake: 403.
        var noHeader = await Send("POST", "/sessions/signout", cookie: cookie, json: "{\"others\":true}");
        Assert.Equal(403, noHeader.Code);
        Assert.Equal("Forbidden", noHeader.Body);
        Assert.Equal(403, (await Send("GET", "/sessions", cookie: cookie, fetchSite: "cross-site")).Code);

        on = false;
        var off = await Send("GET", "/sessions", cookie: cookie);
        Assert.Equal(423, off.Code);
        Assert.Equal(OffText, off.Body);
        Assert.Equal(423, (await Post("/sessions/signout", cookie, "{\"others\":true}")).Code);
    }

    [Fact]
    public async Task SignOutOthersKeepsTheCallersOwnSession()
    {
        var mine = await SignIn(IPhone);
        var second = await SignIn();
        var third = await SignIn("Mozilla/5.0 (Windows NT 10.0)");

        var r = await Post("/sessions/signout", mine, "{\"others\":true}");
        Assert.Equal(204, r.Code);
        Assert.Equal("", r.Body);
        Assert.Equal(200, (await Send("GET", "/status", cookie: mine)).Code);
        Assert.Equal(401, (await Send("GET", "/status", cookie: second)).Code);
        Assert.Equal(401, (await Send("GET", "/status", cookie: third)).Code);
        var list = SessionList(await Send("GET", "/sessions", cookie: mine));
        Assert.Single(list);
        Assert.True(list[0].GetProperty("current").GetBoolean());
        // Nothing left to sign out: still 204.
        Assert.Equal(204, (await Post("/sessions/signout", mine, "{\"others\":true}")).Code);
    }

    [Fact]
    public async Task SignOutByIdEndsThatDeviceOnly()
    {
        var mine = await SignIn(IPhone);
        var other = await SignIn();
        var third = await SignIn();
        var ids = OtherIds(await Send("GET", "/sessions", cookie: mine));
        Assert.Equal(2, ids.Length);

        var stored = settings.Get<List<Auth.Session>>("sessions")!;
        var otherHash = stored[1].Hash;  // the second one signed in
        var otherId = otherHash[..12];
        Assert.Contains(otherId, ids);

        var r = await Post("/sessions/signout", mine, $"{{\"id\":\"{otherId}\"}}");
        Assert.Equal(204, r.Code);
        Assert.Equal(401, (await Send("GET", "/status", cookie: other)).Code);
        Assert.Equal(200, (await Send("GET", "/status", cookie: third)).Code);
        Assert.Equal(200, (await Send("GET", "/status", cookie: mine)).Code);
        Assert.Equal(2, SessionList(await Send("GET", "/sessions", cookie: mine)).Length);
    }

    [Fact]
    public async Task SignOutByIdNeverEndsTheCallersOwnSession()
    {
        var mine = await SignIn();
        var ownId = SessionList(await Send("GET", "/sessions", cookie: mine)).Single().GetProperty("id").GetString();
        var r = await Post("/sessions/signout", mine, $"{{\"id\":\"{ownId}\"}}");
        Assert.Equal(204, r.Code);
        Assert.Equal(200, (await Send("GET", "/status", cookie: mine)).Code);
        Assert.Single(SessionList(await Send("GET", "/sessions", cookie: mine)));
    }

    [Theory]
    [InlineData("{\"id\":\"\"}")]
    [InlineData("{\"id\":\"abc\"}")]
    [InlineData("{\"id\":\"0123456789abcdef\"}")]
    [InlineData("{\"id\":\"000000000000\"}")]
    [InlineData("{\"others\":false,\"id\":\"000000000000\"}")]
    public async Task AnIdOfTheWrongLengthOrMatchingNothingAnswers204AndChangesNothing(string json)
    {
        var mine = await SignIn();
        var other = await SignIn();
        var r = await Post("/sessions/signout", mine, json);
        Assert.Equal(204, r.Code);
        Assert.Equal(200, (await Send("GET", "/status", cookie: other)).Code);
        Assert.Equal(2, SessionList(await Send("GET", "/sessions", cookie: mine)).Length);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{\"others\":false}")]
    [InlineData("{\"others\":\"true\"}")]
    [InlineData("{\"others\":1}")]
    [InlineData("{\"id\":5}")]
    [InlineData("{\"id\":null}")]
    public async Task SignOutWithNeitherOthersNorAnIdIs400WhichDevice(string? json)
    {
        var mine = await SignIn();
        var other = await SignIn();
        var r = await Post("/sessions/signout", mine, json);
        Assert.Equal(400, r.Code);
        Assert.Equal("Which device?", r.Body);
        Assert.Equal("text/plain; charset=utf-8", r.Header("Content-Type"));
        Assert.Equal(200, (await Send("GET", "/status", cookie: other)).Code);
    }

    [Fact]
    public async Task OthersWinsOverAnIdInTheSameBody()
    {
        var mine = await SignIn();
        var other = await SignIn();
        var r = await Post("/sessions/signout", mine, "{\"others\":true,\"id\":\"000000000000\"}");
        Assert.Equal(204, r.Code);
        Assert.Equal(401, (await Send("GET", "/status", cookie: other)).Code);
    }

    [Fact]
    public async Task SignOutCountsEveryCookieOfTheRequestAsTheCallers()
    {
        var first = await SignIn();
        var second = await SignIn();
        var third = await SignIn();
        var r = await Post("/sessions/signout", first + "; " + second, "{\"others\":true}");
        Assert.Equal(204, r.Code);
        Assert.Equal(200, (await Send("GET", "/status", cookie: first)).Code);
        Assert.Equal(200, (await Send("GET", "/status", cookie: second)).Code);
        Assert.Equal(401, (await Send("GET", "/status", cookie: third)).Code);
    }

    [Fact]
    public async Task ActivityIsAnEmptyListWhenThereIsNoLog()
    {
        var cookie = await SignIn();
        File.Delete(accessLog.FilePath);
        var r = await Send("GET", "/activity", cookie: cookie);
        Assert.Equal(200, r.Code);
        Assert.Equal("application/json", r.Header("Content-Type"));
        Assert.Equal("{\"lines\":[]}", r.Body);
    }

    [Fact]
    public async Task ActivityHasTheNewestLinesFirstAsTimeTwoSpacesText()
    {
        accessLog.Write("older event");
        var cookie = await SignIn(IPhone);
        var lines = Lines(await Send("GET", "/activity", cookie: cookie));
        Assert.Equal(2, lines.Length);
        Assert.EndsWith("  New sign-in to Lid Awake · With the password, on an iPhone.", lines[0]);
        Assert.EndsWith("  older event", lines[1]);
        foreach (var line in lines)
        {
            var space = line.IndexOf("  ", StringComparison.Ordinal);
            Assert.True(DateTimeOffset.TryParse(line[..space], System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out _), line);
            Assert.Matches(@"^\d{4}-\d\d-\d\dT\d\d:\d\d:\d\d[+-]\d\d:\d\d  ", line);
        }
    }

    [Fact]
    public async Task ActivityAnswersTheNewestEightOnly()
    {
        var cookie = await SignIn();
        for (int i = 1; i <= 12; i++) accessLog.Write("event " + i);
        var lines = Lines(await Send("GET", "/activity", cookie: cookie));
        Assert.Equal(8, lines.Length);
        Assert.EndsWith("  event 12", lines[0]);
        Assert.EndsWith("  event 5", lines[7]);
    }

    [Fact]
    public async Task ActivityNeedsASessionAndIsClosedWhileOff()
    {
        var cookie = await SignIn();
        var anonymous = await Send("GET", "/activity");
        Assert.Equal(401, anonymous.Code);
        Assert.Equal("Locked", anonymous.Body);
        Assert.Equal(403, (await Send("GET", "/activity", cookie: cookie, fetchSite: "cross-site")).Code);
        on = false;
        var off = await Send("GET", "/activity", cookie: cookie);
        Assert.Equal(423, off.Code);
        Assert.Equal(OffText, off.Body);
    }

    [Fact]
    public async Task SignInsSignOutsAndTurningOffFromThePhoneAreLogged()
    {
        var mine = await SignIn(IPhone);
        await SignIn();
        var id = OtherIds(await Send("GET", "/sessions", cookie: mine)).Single();
        Assert.Equal(204, (await Post("/sessions/signout", mine, $"{{\"id\":\"{id}\"}}", ua: IPhone)).Code);
        await SignIn();
        Assert.Equal(204, (await Post("/sessions/signout", mine, "{\"others\":true}", ua: IPhone)).Code);
        Assert.Equal(204, (await Post("/awake", mine, "{\"on\":false}")).Code);

        var lines = Lines(await Send("GET", "/activity", cookie: mine));
        string Text(string line) => line[(line.IndexOf("  ", StringComparison.Ordinal) + 2)..];
        Assert.Equal(new[]
        {
            "Lid Awake turned off from the phone",
            "Signed out the other devices from the phone · iPhone",
            "New sign-in to Lid Awake · With the password, on a browser.",
            "Signed out a device from the phone · iPhone",
            "New sign-in to Lid Awake · With the password, on a browser.",
            "New sign-in to Lid Awake · With the password, on an iPhone.",
        }, lines.Select(Text));
    }

    [Fact]
    public async Task RefusedRequestsAreNotLogged()
    {
        var cookie = await SignIn();
        var before = Lines(await Send("GET", "/activity", cookie: cookie)).Length;
        Assert.Equal(401, (await Send("POST", "/login", lid: true, json: "{\"password\":\"wrong password!\"}")).Code);
        Assert.Equal(400, (await Post("/awake", cookie, "{\"on\":true}")).Code);
        Assert.Equal(400, (await Post("/sessions/signout", cookie, "{}")).Code);
        Assert.Equal(204, (await Post("/sessions/signout", cookie, "{\"others\":true}")).Code);  // nobody else was signed in
        Assert.Equal(before, Lines(await Send("GET", "/activity", cookie: cookie)).Length);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(15)]
    [InlineData(30)]
    [InlineData(60)]
    [InlineData(120)]
    [InlineData(240)]
    [InlineData(480)]
    public async Task EachTimerChoiceIsSetWith204(int minutes)
    {
        var cookie = await SignIn();
        var r = await Post("/timer", cookie, $"{{\"minutes\":{minutes}}}");
        Assert.Equal(204, r.Code);
        Assert.Equal("", r.Body);
        Assert.Equal(new[] { minutes }, timerSets);
    }

    [Theory]
    [InlineData("{\"minutes\":1}")]
    [InlineData("{\"minutes\":10}")]
    [InlineData("{\"minutes\":45}")]
    [InlineData("{\"minutes\":-15}")]
    [InlineData("{\"minutes\":481}")]
    [InlineData("{\"minutes\":1440}")]
    [InlineData("{\"minutes\":15.5}")]
    [InlineData("{\"minutes\":1e20}")]
    [InlineData("{\"minutes\":\"15\"}")]
    [InlineData("{\"minutes\":true}")]
    [InlineData("{\"minutes\":false}")]
    [InlineData("{\"minutes\":null}")]
    [InlineData("{\"minutes\":[15]}")]
    [InlineData("{\"minute\":15}")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[15]")]
    [InlineData("15")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnyOtherTimerValueIs400UnknownTimerLength(string? json)
    {
        var cookie = await SignIn();
        var r = await Post("/timer", cookie, json);
        Assert.Equal(400, r.Code);
        Assert.Equal("Unknown timer length.", r.Body);
        Assert.Equal("text/plain; charset=utf-8", r.Header("Content-Type"));
        Assert.Empty(timerSets);
    }

    [Fact]
    public async Task AWholeNumberWrittenWithADecimalPointIsAccepted()
    {
        var cookie = await SignIn();
        Assert.Equal(204, (await Post("/timer", cookie, "{\"minutes\":60.0}")).Code);
        Assert.Equal(new[] { 60 }, timerSets);
    }

    [Fact]
    public async Task TimerNeedsASessionTheHeaderAndIsClosedWhileOff()
    {
        var cookie = await SignIn();
        var anonymous = await Post("/timer", "", "{\"minutes\":15}");
        Assert.Equal(401, anonymous.Code);
        Assert.Equal("Locked", anonymous.Body);
        var noHeader = await Send("POST", "/timer", cookie: cookie, json: "{\"minutes\":15}");
        Assert.Equal(403, noHeader.Code);
        Assert.Equal("Forbidden", noHeader.Body);
        // The header check comes before the body is looked at.
        Assert.Equal(403, (await Send("POST", "/timer", cookie: cookie, json: "{\"minutes\":7}")).Code);
        Assert.Equal(403, (await Send("POST", "/timer", cookie: cookie, lid: true, fetchSite: "cross-site", json: "{\"minutes\":15}")).Code);
        on = false;
        var off = await Post("/timer", cookie, "{\"minutes\":15}");
        Assert.Equal(423, off.Code);
        Assert.Equal(OffText, off.Body);
        Assert.Empty(timerSets);
    }

    // The page posts a length, then asks /status: the answer must already show it.
    [Fact]
    public async Task TheTimerChosenOverHttpShowsInTheNextStatus()
    {
        var timer = new AutoOff(settings);
        realTimer = timer;
        var info = new StatusInfo(new FakeReadings());
        statusAnswer = () => info.Build(true, timer);
        timer.Start();
        var cookie = await SignIn();

        var before = Json(await Send("GET", "/status", cookie: cookie));
        Assert.Equal(0, before.GetProperty("timerMinutes").GetInt32());
        Assert.False(before.TryGetProperty("timerRemaining", out _));

        Assert.Equal(204, (await Post("/timer", cookie, "{\"minutes\":30}")).Code);
        var after = Json(await Send("GET", "/status", cookie: cookie));
        Assert.Equal(30, after.GetProperty("timerMinutes").GetInt32());
        var left = after.GetProperty("timerRemaining").GetDouble();
        Assert.InRange(left, 1700, 1800);
        StatusInfoTests.AssertEveryKeyWithItsKind(after);

        Assert.Equal(204, (await Post("/timer", cookie, "{\"minutes\":0}")).Code);
        var never = Json(await Send("GET", "/status", cookie: cookie));
        Assert.Equal(0, never.GetProperty("timerMinutes").GetInt32());
        Assert.False(never.TryGetProperty("timerRemaining", out _));
        Assert.Equal(0, settings.Get<int?>("timerMinutes"));
    }

    [Fact]
    public async Task ARefusedTimerLeavesTheTimerAlone()
    {
        var timer = new AutoOff(settings);
        timer.Set(60);
        realTimer = timer;
        var cookie = await SignIn();
        Assert.Equal(400, (await Post("/timer", cookie, "{\"minutes\":61}")).Code);
        Assert.Equal(60, timer.Minutes);
    }

    // ---- Task 17: POST /limits (docs/API.md 5.2) and the switches in /status ----

    [Theory]
    [InlineData("battery", true)]
    [InlineData("battery", false)]
    [InlineData("heat", true)]
    [InlineData("heat", false)]
    public async Task EachLimitIsSetWith204(string name, bool value)
    {
        var cookie = await SignIn();
        var r = await Post("/limits", cookie, $"{{\"name\":\"{name}\",\"on\":{(value ? "true" : "false")}}}");
        Assert.Equal(204, r.Code);
        Assert.Equal("", r.Body);
        Assert.Equal(new[] { (name, value) }, limitSets);
    }

    [Theory]
    [InlineData("{\"name\":\"battery\"}")]
    [InlineData("{\"on\":true}")]
    [InlineData("{\"name\":\"Battery\",\"on\":true}")]
    [InlineData("{\"name\":\"heat \",\"on\":false}")]
    [InlineData("{\"name\":\"cpu\",\"on\":true}")]
    [InlineData("{\"name\":\"\",\"on\":true}")]
    [InlineData("{\"name\":null,\"on\":true}")]
    [InlineData("{\"name\":[\"battery\"],\"on\":true}")]
    [InlineData("{\"name\":1,\"on\":true}")]
    [InlineData("{\"name\":\"battery\",\"on\":1}")]
    [InlineData("{\"name\":\"battery\",\"on\":0}")]
    [InlineData("{\"name\":\"battery\",\"on\":\"true\"}")]
    [InlineData("{\"name\":\"battery\",\"on\":null}")]
    [InlineData("{\"name\":\"heat\",\"on\":[true]}")]
    [InlineData("{\"name\":\"heat\",\"value\":true}")]
    [InlineData("{}")]
    [InlineData("not json")]
    [InlineData("[\"battery\",true]")]
    [InlineData("true")]
    [InlineData("")]
    [InlineData(null)]
    public async Task AnyOtherLimitBodyIs400UnknownLimit(string? json)
    {
        var cookie = await SignIn();
        var r = await Post("/limits", cookie, json);
        Assert.Equal(400, r.Code);
        Assert.Equal("Unknown limit.", r.Body);
        Assert.Equal("text/plain; charset=utf-8", r.Header("Content-Type"));
        Assert.Empty(limitSets);
    }

    [Fact]
    public async Task LimitsNeedsASessionTheHeaderAndIsClosedWhileOff()
    {
        var cookie = await SignIn();
        var anonymous = await Post("/limits", "", "{\"name\":\"battery\",\"on\":false}");
        Assert.Equal(401, anonymous.Code);
        Assert.Equal("Locked", anonymous.Body);
        var noHeader = await Send("POST", "/limits", cookie: cookie, json: "{\"name\":\"battery\",\"on\":false}");
        Assert.Equal(403, noHeader.Code);
        Assert.Equal("Forbidden", noHeader.Body);
        // The header check comes before the body is looked at.
        Assert.Equal(403, (await Send("POST", "/limits", cookie: cookie, json: "{}")).Code);
        Assert.Equal(403, (await Send("POST", "/limits", cookie: cookie, lid: true, fetchSite: "cross-site", json: "{\"name\":\"heat\",\"on\":false}")).Code);
        // Only POST.
        Assert.Equal(404, (await Send("GET", "/limits", cookie: cookie)).Code);
        on = false;
        var off = await Post("/limits", cookie, "{\"name\":\"battery\",\"on\":false}");
        Assert.Equal(423, off.Code);
        Assert.Equal(OffText, off.Body);
        Assert.Empty(limitSets);
    }

    // The page posts a switch, then asks /status: the answer must already show it. `heat` is
    // accepted on a PC with no temperature, because the setting is stored.
    [Fact]
    public async Task ALimitChangedOverHttpShowsInTheNextStatusAndIsStored()
    {
        realLimits = new Limits(settings);
        var info = new StatusInfo(new FakeReadings(), realLimits, new HeatSensor(() => TemperatureReading.None));
        statusAnswer = () => info.Build(true);
        var cookie = await SignIn();

        var before = Json(await Send("GET", "/status", cookie: cookie));
        Assert.True(before.GetProperty("batteryGuard").GetBoolean());
        Assert.True(before.GetProperty("heatGuard").GetBoolean());

        Assert.Equal(204, (await Post("/limits", cookie, "{\"name\":\"battery\",\"on\":false}")).Code);
        var after = Json(await Send("GET", "/status", cookie: cookie));
        Assert.False(after.GetProperty("batteryGuard").GetBoolean());
        Assert.True(after.GetProperty("heatGuard").GetBoolean());
        StatusInfoTests.AssertEveryKeyWithItsKind(after);

        Assert.Equal(204, (await Post("/limits", cookie, "{\"name\":\"heat\",\"on\":false}")).Code);
        var noHeat = Json(await Send("GET", "/status", cookie: cookie));
        Assert.False(noHeat.GetProperty("heatGuard").GetBoolean());
        Assert.DoesNotContain("heat", noHeat.GetProperty("features").EnumerateArray().Select(f => f.GetString()));

        // Stored, so a restart keeps them.
        Assert.False(settings.Get<bool?>("batteryGuard"));
        Assert.False(settings.Get<bool?>("heatGuard"));
        var again = new Limits(settings);
        Assert.False(again.BatteryGuard);
        Assert.False(again.HeatGuard);

        Assert.Equal(204, (await Post("/limits", cookie, "{\"name\":\"battery\",\"on\":true}")).Code);
        Assert.True(Json(await Send("GET", "/status", cookie: cookie)).GetProperty("batteryGuard").GetBoolean());
    }

    [Fact]
    public async Task StatusOverHttpListsHeatAndBatteryTempOnlyWithATemperature()
    {
        var source = TemperatureReading.None;
        var heat = new HeatSensor(() => source);
        var info = new StatusInfo(new FakeReadings { PowerNow = new PowerReading(0, 1, 80, 7200) }, new Limits(settings), heat);
        statusAnswer = () => info.Build(true);
        var cookie = await SignIn();

        heat.Refresh(TimeSpan.Zero);
        var noTemperature = Json(await Send("GET", "/status", cookie: cookie));
        Assert.Equal(new[] { "limits", "history", "passkeys", "push" }, noTemperature.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray());
        Assert.False(noTemperature.TryGetProperty("batteryTemp", out _));

        // Only a thermal zone: the heat switch shows, but batteryTemp is the battery's alone.
        source = new TemperatureReading(null, 55.0);
        heat.Refresh(TimeSpan.Zero);
        var zoneOnly = Json(await Send("GET", "/status", cookie: cookie));
        Assert.Equal(new[] { "limits", "history", "heat", "passkeys", "push" }, zoneOnly.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray());
        Assert.False(zoneOnly.TryGetProperty("batteryTemp", out _));

        source = new TemperatureReading(36.66, 55.0);
        heat.Refresh(TimeSpan.Zero);
        var withHeat = Json(await Send("GET", "/status", cookie: cookie));
        Assert.Equal(new[] { "limits", "history", "heat", "passkeys", "push" }, withHeat.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray());
        Assert.Equal(36.7, withHeat.GetProperty("batteryTemp").GetDouble());
        StatusInfoTests.AssertEveryKeyWithItsKind(withHeat);
    }

    // ---- 5.5 push ----

    const string PushEndpoint = "https://web.push.apple.com/QGphone";

    static string Subscription(FakePhone phone, string endpoint = PushEndpoint, string off = "[\"warm\"]") =>
        $"{{\"endpoint\":\"{endpoint}\",\"expirationTime\":null,\"keys\":{{\"p256dh\":\"{phone.P256dh}\",\"auth\":\"{phone.AuthText}\"}},\"off\":{off}}}";

    Task<Reply> PostPush(string path, string cookie, string json) => Send("POST", path, lid: true, cookie: cookie, json: json);

    [Fact]
    public async Task PushRoutesNeedASessionAndThePostHeader()
    {
        using var phone = new FakePhone();
        Assert.Equal(401, (await Send("GET", "/push/key")).Code);
        Assert.Equal(401, (await Send("POST", "/push/subscribe", lid: true, json: Subscription(phone))).Code);
        var cookie = await SignIn();
        Assert.Equal(403, (await Send("POST", "/push/subscribe", cookie: cookie, json: Subscription(phone))).Code);
        Assert.Equal(403, (await Send("POST", "/push/settings", cookie: cookie, json: "{}")).Code);
        Assert.Equal(404, (await Send("POST", "/push/key", lid: true, cookie: cookie)).Code);
        Assert.Equal(404, (await Send("GET", "/push/subscribe", cookie: cookie)).Code);
        Assert.Empty(push.Phones);
    }

    [Fact]
    public async Task PushKeyIsTheSame65ByteKeyEveryTime()
    {
        var cookie = await SignIn();
        var r = await Send("GET", "/push/key", cookie: cookie);
        Assert.Equal(200, r.Code);
        Assert.Equal("application/json", r.Header("Content-Type"));
        var key = Json(r).GetProperty("key").GetString()!;
        var bytes = WebPush.FromBase64Url(key)!;
        Assert.Equal(65, bytes.Length);
        Assert.Equal(4, bytes[0]);
        Assert.DoesNotContain("=", key);
        Assert.Equal(key, Json(await Send("GET", "/push/key", cookie: cookie)).GetProperty("key").GetString());
        Assert.Equal(push.PublicKey, key);
    }

    [Fact]
    public async Task PushSubscribeStoresThePhoneAndRemembersThePageAsTheContact()
    {
        using var phone = new FakePhone();
        var cookie = await SignIn();
        var r = await PostPush("/push/subscribe", cookie, Subscription(phone, off: "[\"warm\",\"bogus\",\"security\"]"));
        Assert.Equal(204, r.Code);
        Assert.Empty(r.Bytes);
        var stored = Assert.Single(push.Phones);
        Assert.Equal(PushEndpoint, stored.Endpoint);
        Assert.Equal(phone.P256dh, stored.P256dh);
        Assert.Equal(phone.AuthText, stored.Auth);
        Assert.Equal(new[] { "warm", "security" }, stored.Off);
        Assert.Equal("https://" + TailHost, push.Contact);

        // The same endpoint again replaces it; an `off` with anything but strings counts as none.
        Assert.Equal(204, (await PostPush("/push/subscribe", cookie, Subscription(phone, off: "[\"warm\", 3]"))).Code);
        Assert.Empty(Assert.Single(push.Phones).Off);
        Assert.Equal(204, (await PostPush("/push/subscribe", cookie, Subscription(phone, off: "\"warm\""))).Code);
        Assert.Empty(Assert.Single(push.Phones).Off);
    }

    [Fact]
    public async Task PushSubscribeRefusesAnythingButARealSubscription()
    {
        using var phone = new FakePhone();
        var cookie = await SignIn();
        foreach (var body in new[]
        {
            Subscription(phone, endpoint: "https://evil.example/push"),
            Subscription(phone, endpoint: "http://web.push.apple.com/x"),
            Subscription(phone, endpoint: "https://web.push.apple.com:8443/x"),
            $"{{\"endpoint\":\"{PushEndpoint}\",\"keys\":{{\"p256dh\":\"{phone.P256dh}\"}}}}",
            $"{{\"endpoint\":\"{PushEndpoint}\",\"keys\":{{\"p256dh\":\"AAAA\",\"auth\":\"{phone.AuthText}\"}}}}",
            $"{{\"endpoint\":\"{PushEndpoint}\",\"keys\":\"nope\"}}",
            $"{{\"keys\":{{\"p256dh\":\"{phone.P256dh}\",\"auth\":\"{phone.AuthText}\"}}}}",
            "{}",
            "not json",
            "[1,2]",
        })
        {
            var r = await PostPush("/push/subscribe", cookie, body);
            Assert.Equal(400, r.Code);
            Assert.Equal("Couldn't turn on alerts.", r.Body);
        }
        Assert.Empty(push.Phones);
    }

    [Fact]
    public async Task PushSettingsChangesOrRemovesAPhone()
    {
        using var phone = new FakePhone();
        var cookie = await SignIn();
        Assert.Equal(204, (await PostPush("/push/subscribe", cookie, Subscription(phone))).Code);

        Assert.Equal(204, (await PostPush("/push/settings", cookie, $"{{\"endpoint\":\"{PushEndpoint}\",\"off\":[\"lowBattery\",\"nope\",\"turnedOff\"]}}")).Code);
        Assert.Equal(new[] { "lowBattery", "turnedOff" }, Assert.Single(push.Phones).Off);
        // Neither enabled:false nor a list: every alert back on.
        Assert.Equal(204, (await PostPush("/push/settings", cookie, $"{{\"endpoint\":\"{PushEndpoint}\"}}")).Code);
        Assert.Empty(Assert.Single(push.Phones).Off);
        Assert.Equal(204, (await PostPush("/push/settings", cookie, $"{{\"endpoint\":\"{PushEndpoint}\",\"enabled\":true,\"off\":[\"warm\"]}}")).Code);
        Assert.Equal(new[] { "warm" }, Assert.Single(push.Phones).Off);
        // An unknown endpoint is ignored.
        Assert.Equal(204, (await PostPush("/push/settings", cookie, "{\"endpoint\":\"https://web.push.apple.com/other\",\"enabled\":false}")).Code);
        Assert.Single(push.Phones);

        var missing = await PostPush("/push/settings", cookie, "{\"off\":[]}");
        Assert.Equal(400, missing.Code);
        Assert.Equal("Missing phone.", missing.Body);
        Assert.Equal(400, (await PostPush("/push/settings", cookie, "{\"endpoint\":5}")).Code);

        Assert.Equal(204, (await PostPush("/push/settings", cookie, $"{{\"endpoint\":\"{PushEndpoint}\",\"enabled\":false}}")).Code);
        Assert.Empty(push.Phones);
    }

    [Fact]
    public async Task ANewSignInIsASecurityAlertEvenForAPhoneThatTurnedEverythingOff()
    {
        using var phone = new FakePhone();
        var cookie = await SignIn();
        var everything = "[\"turnedOff\",\"lowBattery\",\"claudeUsage\",\"claudeSession\",\"warm\",\"security\"]";
        Assert.Equal(204, (await PostPush("/push/subscribe", cookie, Subscription(phone, off: everything))).Code);
        Assert.Empty(pushService.Seen);

        await SignIn(ua: IPhone);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (pushService.Seen.Count == 0 && DateTime.UtcNow < deadline) await Task.Delay(50);
        var seen = Assert.Single(pushService.Seen);
        Assert.Equal(PushEndpoint, seen.Uri);
        var alert = JsonDocument.Parse(PushReceiver.Decrypt(seen.Body, phone.Key, phone.Auth).Text).RootElement;
        Assert.Equal("New sign-in to Lid Awake", alert.GetProperty("title").GetString());
        Assert.Equal("With the password, on an iPhone.", alert.GetProperty("body").GetString());
        Assert.Equal("security", alert.GetProperty("tag").GetString());
    }
}
