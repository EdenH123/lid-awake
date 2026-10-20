using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LidAwake;
using Xunit;

// The real routes: a real WebServer on a free port with a real HttpClient, signed in the way the
// phone does it. Its own small harness (the main one belongs to WebServerTests).
[Collection("WebServer")]
public sealed class HistoryRouteTests : IDisposable
{
    const string TailHost = "pc.example.ts.net";
    const string Owner = "owner@example.com";
    const string Good = "correct horse battery";
    const long T0 = 1_760_003_600;
    const long Hour = 3600;

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Auth auth;
    readonly WebServer server;
    readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly HistoryStore history;
    readonly int port;

    public HistoryRouteTests()
    {
        var site = Path.Combine(folder, "site");
        Directory.CreateDirectory(Path.Combine(site, "web"));
        File.Copy(Path.Combine(RepoRoot(), "web", "index.html"), Path.Combine(site, "web", "index.html"));
        var settings = new Settings(Path.Combine(folder, "settings"));
        settings.Set("tailnetHost", TailHost);
        settings.Set("tailnetOwner", Owner);
        auth = new Auth(settings, iterations: 1000);
        auth.SetPassword(Good);
        history = new HistoryStore(Path.Combine(folder, "history"), () => T0, 8);
        port = FreePort();
        server = new WebServer(auth, new Tailnet(settings, _ => null), () => true, () => new { os = "windows" },
            () => { }, (_, _) => { }, port, site) { History = history };
        server.Start();
    }

    public void Dispose()
    {
        http.Dispose();
        server.Dispose();
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // Both markers: the test's own bin folder also holds a copy of web\index.html, but only the
    // repository root has the app project folder.
    static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null
               && !(File.Exists(Path.Combine(dir.FullName, "web", "index.html"))
                    && File.Exists(Path.Combine(dir.FullName, "windows", "LidAwake.Windows", "LidAwake.Windows.csproj"))))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("The repository root (web\\index.html and windows\\LidAwake.Windows) was not found above the test folder.");
    }

    static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    async Task<string> SignIn()
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}/login");
        req.Headers.Host = TailHost;
        req.Headers.TryAddWithoutValidation("Tailscale-User-Login", Owner);
        req.Headers.TryAddWithoutValidation("X-LidAwake", "1");
        req.Content = new StringContent($"{{\"password\":\"{Good}\"}}", Encoding.UTF8, "application/json");
        var r = await http.SendAsync(req);
        Assert.Equal(HttpStatusCode.NoContent, r.StatusCode);
        return r.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    async Task<(int Code, string Type, string Body)> Get(string path, string? cookie)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}{path}");
        req.Headers.Host = TailHost;
        req.Headers.TryAddWithoutValidation("Tailscale-User-Login", Owner);
        if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        var r = await http.SendAsync(req);
        return ((int)r.StatusCode, r.Content.Headers.ContentType?.ToString() ?? "", await r.Content.ReadAsStringAsync());
    }

    static HistorySample Sample(long t, int percent = 50, double? temp = null, double? cpu = null, int span = 0)
        => new(t, percent, true, false, temp, cpu, span);

    [Fact]
    public async Task EmptyHistoryAnswersEmptyListsOverHttp()
    {
        var cookie = await SignIn();
        var temp = await Get("/temp-history", cookie);
        Assert.Equal(200, temp.Code);
        Assert.StartsWith("application/json", temp.Type);
        Assert.Equal("{\"points\":[],\"now\":" + T0 + "}", temp.Body);
        Assert.Equal("{\"points\":[],\"now\":" + T0 + "}", (await Get("/battery-history", cookie)).Body);
        Assert.Equal("{\"now\":" + T0 + ",\"cores\":8,\"apps\":[],\"intervals\":[]}", (await Get("/cpu-history", cookie)).Body);
    }

    [Fact]
    public async Task RoutesAnswerWithTheApiShapes()
    {
        history.Add(Sample(T0 - 600, 87, temp: 31.25, cpu: 25, span: 60));
        history.Add(Sample(T0 - 540, 86, temp: 31.5, cpu: 50, span: 60));
        var cookie = await SignIn();

        var temp = JsonDocument.Parse((await Get("/temp-history", cookie)).Body).RootElement;
        Assert.Equal(T0, temp.GetProperty("now").GetInt64());
        Assert.Equal($"[[{T0 - 600},31.25],[{T0 - 540},31.5]]", temp.GetProperty("points").GetRawText());

        var battery = JsonDocument.Parse((await Get("/battery-history?hours=24", cookie)).Body).RootElement;
        Assert.Equal($"[[{T0 - 600},87,1,0],[{T0 - 540},86,1,0]]", battery.GetProperty("points").GetRawText());

        var cpu = JsonDocument.Parse((await Get("/cpu-history?hours=6", cookie)).Body).RootElement;
        Assert.Equal(8, cpu.GetProperty("cores").GetInt32());
        Assert.Equal("[]", cpu.GetProperty("apps").GetRawText());
        // 25% of 8 cores for 60 s = 120 processor-seconds; 50% = 240.
        Assert.Equal($"[[{T0 - 660},{T0 - 600},{{}},120],[{T0 - 600},{T0 - 540},{{}},240]]", cpu.GetProperty("intervals").GetRawText());
    }

    [Fact]
    public async Task TheHoursQueryIsClampedAndDefaultsTo24()
    {
        foreach (var ago in new[] { 47 * Hour, 30 * Hour, 10 * Hour, 90 * 60L, 1800L })
            history.Add(Sample(T0 - ago));
        var cookie = await SignIn();
        async Task<int> Points(string query) =>
            JsonDocument.Parse((await Get("/battery-history" + query, cookie)).Body).RootElement.GetProperty("points").GetArrayLength();
        Assert.Equal(3, await Points(""));
        Assert.Equal(3, await Points("?hours=abc"));
        Assert.Equal(3, await Points("?hours="));
        Assert.Equal(1, await Points("?hours=1"));
        Assert.Equal(1, await Points("?hours=0"));
        Assert.Equal(1, await Points("?hours=-8"));
        Assert.Equal(2, await Points("?hours=2"));
        Assert.Equal(5, await Points("?hours=48"));
        Assert.Equal(5, await Points("?hours=100000"));
    }

    [Fact]
    public async Task CpuIconAnswers404NoSuchApp()
    {
        history.Add(Sample(T0 - 60, cpu: 10, span: 60));
        var cookie = await SignIn();
        var icon = await Get("/cpu-icon?key=com.google.Chrome", cookie);
        Assert.Equal(404, icon.Code);
        Assert.Equal("No such app.", icon.Body);
        Assert.Equal(404, (await Get("/cpu-icon", cookie)).Code);
    }

    [Fact]
    public async Task HistoryRoutesNeedASession()
    {
        foreach (var path in new[] { "/temp-history", "/battery-history", "/cpu-history", "/cpu-icon?key=x" })
        {
            var r = await Get(path, null);
            Assert.Equal(401, r.Code);
            Assert.Equal("Locked", r.Body);
        }
    }

    [Fact]
    public async Task WithoutAHistoryTheRoutesAreNotFound()
    {
        server.History = null;
        var cookie = await SignIn();
        foreach (var path in new[] { "/temp-history", "/battery-history", "/cpu-history", "/cpu-icon?key=x" })
        {
            var r = await Get(path, cookie);
            Assert.Equal(404, r.Code);
            Assert.Equal("Not found", r.Body);
        }
    }
}
