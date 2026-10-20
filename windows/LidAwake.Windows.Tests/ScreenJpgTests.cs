using System.Text;
using LidAwake;
using Xunit;

// GET /screen.jpg, the slow path for the screen picture (docs/API.md 5.9): a long poll with
// `after`, through the real web server.
[Collection("WebServer")]
public class ScreenJpgTests : IAsyncLifetime
{
    const string Good = "correct horse battery";
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly FakeScreens screens = new();
    readonly HttpClient http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    Settings settings = null!;
    Auth auth = null!;
    ScreenFeed feed = null!;
    WebServer server = null!;
    WebServer plain = null!;
    int port, plainPort;
    volatile bool allowed = true;
    string cookie = "";

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(Path.Combine(folder, "web"));
        File.WriteAllText(Path.Combine(folder, "web", "index.html"), "<script>a()</script>");
        settings = new Settings(Path.Combine(folder, "settings"));
        settings.Set("tailnetHost", LiveClient.TailHost);
        settings.Set("tailnetOwner", LiveClient.Owner);
        auth = new Auth(settings, iterations: 1000);
        auth.SetPassword(Good);
        feed = new ScreenFeed(screens.Open, idleStop: TimeSpan.FromSeconds(3));
        var tailnet = new Tailnet(settings, _ => null);
        port = LiveHarness.FreePort();
        server = new WebServer(auth, tailnet, () => true, () => new { }, () => { }, (_, _) => { }, port, folder,
                               screen: feed, screenAllowed: () => allowed);
        server.Start();
        plainPort = LiveHarness.FreePort();
        plain = new WebServer(auth, tailnet, () => true, () => new { }, () => { }, (_, _) => { }, plainPort, folder);
        plain.Start();
        var login = await Send(port, "POST", "/login", json: $"{{\"password\":\"{Good}\"}}", signedIn: false);
        Assert.Equal(204, (int)login.StatusCode);
        cookie = login.Headers.GetValues("Set-Cookie").Single().Split(';')[0];
    }

    public Task DisposeAsync()
    {
        http.Dispose();
        server.Dispose();
        plain.Dispose();
        feed.Dispose();
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        return Task.CompletedTask;
    }

    async Task<HttpResponseMessage> Send(int to, string method, string path, string? json = null, bool signedIn = true)
    {
        using var req = new HttpRequestMessage(new HttpMethod(method), $"http://127.0.0.1:{to}{path}");
        req.Headers.Host = LiveClient.TailHost;
        req.Headers.TryAddWithoutValidation("Tailscale-User-Login", LiveClient.Owner);
        req.Headers.TryAddWithoutValidation("X-LidAwake", "1");
        if (signedIn) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var response = await http.SendAsync(req);
        await response.Content.LoadIntoBufferAsync();
        return response;
    }

    [Fact]
    public async Task APictureThenNothingNewThenTheNextOne()
    {
        screens.Changing = false;
        var first = await Send(port, "GET", "/screen.jpg?after=0");
        Assert.Equal(200, (int)first.StatusCode);
        Assert.Equal("image/jpeg", first.Content.Headers.ContentType?.MediaType);
        var bytes = await first.Content.ReadAsByteArrayAsync();
        Assert.Equal(new byte[] { 0xFF, 0xD8 }, bytes[..2]);
        var id = int.Parse(first.Headers.GetValues("X-Frame-Id").Single());
        Assert.True(id > 0);
        Assert.Equal("no-store", first.Headers.CacheControl?.ToString());

        // The screen is still: 204 after about 2 seconds.
        var started = DateTime.UtcNow;
        var still = await Send(port, "GET", $"/screen.jpg?after={id}");
        Assert.Equal(204, (int)still.StatusCode);
        Assert.True((DateTime.UtcNow - started).TotalSeconds >= 1.5);

        // It changes: the waiting request gets the newer picture.
        var waiting = Send(port, "GET", $"/screen.jpg?after={id}");
        await Task.Delay(200);
        screens.Changing = true;
        var next = await waiting;
        Assert.Equal(200, (int)next.StatusCode);
        Assert.True(int.Parse(next.Headers.GetValues("X-Frame-Id").Single()) > id);
    }

    [Fact]
    public async Task TheOwnersSwitchOffAnswers403()
    {
        allowed = false;
        var r = await Send(port, "GET", "/screen.jpg?after=0");
        Assert.Equal(403, (int)r.StatusCode);
        Assert.StartsWith("Screen access is off.", await r.Content.ReadAsStringAsync());
        Assert.False(feed.IsCapturing);
    }

    [Fact]
    public async Task NoCaptureAnswers503()
    {
        screens.Broken = true;
        var r = await Send(port, "GET", "/screen.jpg?after=0");
        Assert.Equal(503, (int)r.StatusCode);
        Assert.Equal(LiveServer.WakingText, await r.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ItNeedsASession()
    {
        var r = await Send(port, "GET", "/screen.jpg?after=0", signedIn: false);
        Assert.Equal(401, (int)r.StatusCode);
    }

    [Fact]
    public async Task WithoutScreenViewingTheRouteDoesNotExist()
    {
        var login = await Send(plainPort, "GET", "/screen.jpg?after=0");
        Assert.Equal(404, (int)login.StatusCode);
    }
}
