using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using LidAwake;
using Xunit;

// Task 18's passkeys, as the camera routes use them.
public sealed class FakeCameraPasskeys : ICameraPasskeys
{
    public List<string> Ids = new() { "cred-1" };
    public bool Valid = true;
    public int Challenges;
    public (string Id, byte[] ClientData, byte[] AuthData, byte[] Signature, string Host)? Checked;

    public IReadOnlyList<string> CredentialIds() => Ids;
    public string CameraChallenge() => $"challenge-{++Challenges}";
    public bool VerifyCamera(string credentialId, byte[] clientData, byte[] authenticatorData, byte[] signature, string host)
    {
        Checked = (credentialId, clientData, authenticatorData, signature, host);
        return Valid;
    }
}

public class CameraTicketTests
{
    DateTimeOffset now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void ATicketWorksOnceAndOnlyForItsSession()
    {
        var t = new CameraTickets(() => now);
        var ticket = t.Issue("session-a");
        Assert.Equal(43, ticket.Length);  // 32 bytes, base64url without padding
        Assert.DoesNotContain('=', ticket);
        Assert.True(t.Redeem(ticket, "session-a"));
        Assert.False(t.Redeem(ticket, "session-a"));

        var other = t.Issue("session-a");
        Assert.False(t.Redeem(other, "session-b"));
        // Used up by the wrong session too, as on the Mac.
        Assert.False(t.Redeem(other, "session-a"));
    }

    [Fact]
    public void ATicketLastsSixtySeconds()
    {
        var t = new CameraTickets(() => now);
        var early = t.Issue("s");
        var late = t.Issue("s");
        now += TimeSpan.FromSeconds(59);
        Assert.True(t.Redeem(early, "s"));
        now += TimeSpan.FromSeconds(1);
        Assert.False(t.Redeem(late, "s"));
    }

    [Fact]
    public void AtMostSixteenLiveTicketsAreKept()
    {
        var t = new CameraTickets(() => now);
        var kept = Enumerable.Range(0, 16).Select(_ => t.Issue("s")).ToList();
        var extra = t.Issue("s");
        Assert.NotEmpty(extra);
        Assert.Equal(16, t.Count);
        Assert.False(t.Redeem(extra, "s"));
        Assert.True(t.Redeem(kept[0], "s"));
        // Expired ones make room again.
        now += TimeSpan.FromMinutes(2);
        var fresh = t.Issue("s");
        Assert.Equal(1, t.Count);
        Assert.True(t.Redeem(fresh, "s"));
    }

    [Fact]
    public void NothingRedeemsWithoutATicketOrASession()
    {
        var t = new CameraTickets(() => now);
        var ticket = t.Issue("s");
        Assert.False(t.Redeem(null, "s"));
        Assert.False(t.Redeem("made-up", "s"));
        Assert.False(t.Redeem(ticket, null));
    }
}

public class CameraSwitchTests
{
    [Fact]
    public void OffByDefaultAndOnlyWithTheScreenSwitch()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            var settings = new Settings(folder);
            var s = new CameraSwitch(settings);
            Assert.False(s.FromPhone);
            Assert.False(s.Allowed);
            s.Set(true);
            Assert.True(s.FromPhone);
            Assert.False(s.Allowed);  // the screen switch is still off
            settings.Set("remoteScreen", true);
            Assert.True(s.Allowed);
            s.Set(false);
            Assert.False(s.Allowed);
            Assert.False(new Settings(folder).Get<bool>("cameraFromPhone"));
            settings.Set("cameraFromPhone", "yes");  // not a boolean: off
            Assert.False(s.Allowed);
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
        }
    }
}

public class CameraAccessUnitTests
{
    readonly FakeCameraPasskeys passkeys = new();
    readonly CameraTickets tickets = new();
    bool allowed = true;

    CameraAccess Make() => new(passkeys, tickets, () => allowed);

    static Dictionary<string, JsonElement> Body(string json) =>
        JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(json)!;

    [Fact]
    public void OptionsNeedAPasskeyAndATailscaleHost()
    {
        var a = Make();
        var off = a.Options("pc.example.ts.net");
        Assert.Equal(200, off.Code);
        allowed = false;
        Assert.Equal(new CameraAccess.Answer(403, null, CameraTexts.Off), a.Options("pc.example.ts.net"));
        allowed = true;
        Assert.Equal(409, a.Options("pc.local").Code);
        passkeys.Ids.Clear();
        Assert.Equal(new CameraAccess.Answer(409, null, CameraTexts.NeedsPasskeySetUp), a.Options("pc.example.ts.net"));
    }

    [Fact]
    public void UnlockNeedsASessionAndEveryField()
    {
        var a = Make();
        var good = Body("{\"id\":\"cred-1\",\"clientData\":\"AQI\",\"authenticatorData\":\"AwQ\",\"signature\":\"BQY\"}");
        Assert.Equal(401, a.Unlock("pc.example.ts.net", null, good).Code);
        Assert.Equal(401, a.Unlock("pc.example.ts.net", "", good).Code);
        Assert.Equal(401, a.Unlock("pc.example.ts.net", "s", Body("{\"id\":\"cred-1\",\"clientData\":\"AQI\",\"authenticatorData\":\"AwQ\"}")).Code);
        Assert.Equal(401, a.Unlock("pc.example.ts.net", "s", Body("{\"id\":\"cred-1\",\"clientData\":\"A+I\",\"authenticatorData\":\"AwQ\",\"signature\":\"BQY\"}")).Code);
        Assert.Equal(401, a.Unlock("pc.example.ts.net", "s", Body("{\"id\":7,\"clientData\":\"AQI\",\"authenticatorData\":\"AwQ\",\"signature\":\"BQY\"}")).Code);
        Assert.Null(passkeys.Checked);
        var ok = a.Unlock("pc.example.ts.net", "s", good);
        Assert.Equal(200, ok.Code);
        Assert.Equal(new byte[] { 1, 2 }, passkeys.Checked!.Value.ClientData);
        Assert.Equal(new byte[] { 3, 4 }, passkeys.Checked!.Value.AuthData);
        Assert.Equal(new byte[] { 5, 6 }, passkeys.Checked!.Value.Signature);
        Assert.Equal("pc.example.ts.net", passkeys.Checked!.Value.Host);
    }

    [Fact]
    public void Base64UrlBothWays()
    {
        var bytes = Enumerable.Range(0, 64).Select(i => (byte)(i * 7)).ToArray();
        var text = Passkeys.Base64Url(bytes);
        Assert.DoesNotContain('+', text);
        Assert.DoesNotContain('/', text);
        Assert.DoesNotContain('=', text);
        Assert.Equal(bytes, Passkeys.FromBase64Url(text));
        Assert.Equal(new byte[] { 0xFB, 0xFF }, Passkeys.FromBase64Url("-_8"));
        Assert.Equal(new byte[] { 0xFB, 0xFF }, Passkeys.FromBase64Url("-_8="));
        Assert.Null(Passkeys.FromBase64Url("+/8"));
        Assert.Null(Passkeys.FromBase64Url("abcde"));
        Assert.Null(Passkeys.FromBase64Url(null));
        Assert.Equal(Array.Empty<byte>(), Passkeys.FromBase64Url(""));
    }

    [Fact]
    public void OpenFailedSaysWhy()
    {
        Assert.Equal(CameraTexts.NotAllowed, CameraTexts.OpenFailed(true, true, unchecked((int)0x80070005)));
        Assert.Equal(CameraTexts.LidClosed, CameraTexts.OpenFailed(true, true, -1));
        Assert.Equal(CameraTexts.LidClosed, CameraTexts.OpenFailed(false, true, 0));
        Assert.Equal(CameraTexts.NoCamera, CameraTexts.OpenFailed(false, false, 0));
        Assert.Equal(CameraTexts.Busy, CameraTexts.OpenFailed(true, false, -1));
    }

    [Fact]
    public void TheSentencesAreTheMacsForAPc()
    {
        Assert.Equal("The camera closed by itself after 10 minutes. Open it again to keep watching.", CameraTexts.TenMinutes);
        Assert.Equal("The camera was turned off on the PC.", CameraTexts.SwitchedOff);
        Assert.Equal("Face ID is needed to open the camera. Try again.", CameraTexts.NeedsPasskey);
        Assert.Equal("Face ID didn't work, so the camera stays closed.", CameraTexts.PasskeyFailed);
        Assert.Equal("The camera needs Face ID. Turn it on first in Settings → Sign-in.", CameraTexts.NeedsPasskeySetUp);
        Assert.Equal("The camera is off. On your PC, tick “Allow camera & microphone from phone” in the Lid Awake menu.", CameraTexts.Off);
        foreach (var text in new[] { CameraTexts.Off, CameraTexts.SwitchedOff, CameraTexts.LidClosed, CameraTexts.Busy, CameraTexts.Stalled, CameraTexts.NoCamera })
            Assert.DoesNotContain("Mac", text);
    }
}

// The two routes over HTTP: a real WebServer with a fake ICameraPasskeys, signed in as the phone.
[Collection("WebServer")]
public sealed class CameraRouteTests : IDisposable
{
    const string TailHost = "pc.example.ts.net";
    const string Owner = "owner@example.com";
    const string Good = "correct horse battery";

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Auth auth;
    readonly WebServer server;
    readonly HttpClient http = new(new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly FakeCameraPasskeys passkeys = new();
    readonly CameraTickets tickets = new();
    readonly int port;
    bool allowed = true;

    public CameraRouteTests()
    {
        var site = Path.Combine(folder, "site");
        Directory.CreateDirectory(Path.Combine(site, "web"));
        File.Copy(Path.Combine(RepoRoot(), "web", "index.html"), Path.Combine(site, "web", "index.html"));
        var settings = new Settings(Path.Combine(folder, "settings"));
        settings.Set("tailnetHost", TailHost);
        settings.Set("tailnetOwner", Owner);
        auth = new Auth(settings, iterations: 1000);
        auth.SetPassword(Good);
        port = FreePort();
        server = new WebServer(auth, new Tailnet(settings, _ => null), () => true, () => new { os = "windows" },
            () => { }, (_, _) => { }, port, site)
        {
            Camera = new CameraAccess(passkeys, tickets, () => allowed),
        };
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

    // The session cookie ("name=token").
    async Task<string> SignIn()
    {
        var r = await Post("/login", null, $"{{\"password\":\"{Good}\"}}");
        Assert.Equal(204, r.Code);
        return r.Cookie!;
    }

    sealed record Reply(int Code, string Type, string Body, string? Cookie);

    async Task<Reply> Post(string path, string? cookie, string? json = null, bool lid = true)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, $"http://127.0.0.1:{port}{path}");
        req.Headers.Host = TailHost;
        req.Headers.TryAddWithoutValidation("Tailscale-User-Login", Owner);
        if (lid) req.Headers.TryAddWithoutValidation("X-LidAwake", "1");
        if (cookie != null) req.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (json != null) req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        var r = await http.SendAsync(req);
        var cookieOut = r.Headers.TryGetValues("Set-Cookie", out var v) ? v.First().Split(';')[0] : null;
        return new((int)r.StatusCode, r.Content.Headers.ContentType?.ToString() ?? "", await r.Content.ReadAsStringAsync(), cookieOut);
    }

    const string Assertion = "{\"id\":\"cred-1\",\"clientData\":\"AQI\",\"authenticatorData\":\"AwQ\",\"signature\":\"BQY\"}";

    [Fact]
    public async Task OptionsAnswerAChallengeWithTheIds()
    {
        var cookie = await SignIn();
        var r = await Post("/camera/options", cookie);
        Assert.Equal(200, r.Code);
        Assert.StartsWith("application/json", r.Type);
        var o = JsonDocument.Parse(r.Body).RootElement;
        Assert.Equal("challenge-1", o.GetProperty("challenge").GetString());
        Assert.Equal(TailHost, o.GetProperty("rpId").GetString());
        Assert.Equal(new[] { "cred-1" }, o.GetProperty("ids").EnumerateArray().Select(x => x.GetString()).ToArray());
    }

    [Fact]
    public async Task OptionsWithTheSwitchOffOrNoPasskey()
    {
        var cookie = await SignIn();
        allowed = false;
        var off = await Post("/camera/options", cookie);
        Assert.Equal(403, off.Code);
        Assert.Equal("text/plain; charset=utf-8", off.Type);
        Assert.Equal(CameraTexts.Off, off.Body);
        allowed = true;
        passkeys.Ids.Clear();
        var none = await Post("/camera/options", cookie);
        Assert.Equal(409, none.Code);
        Assert.Equal(CameraTexts.NeedsPasskeySetUp, none.Body);
        Assert.Equal(0, passkeys.Challenges);
    }

    [Fact]
    public async Task TheRoutesNeedASessionAndTheHeader()
    {
        var locked = await Post("/camera/options", null);
        Assert.Equal(401, locked.Code);
        Assert.Equal("Locked", locked.Body);
        var cookie = await SignIn();
        var noHeader = await Post("/camera/unlock", cookie, Assertion, lid: false);
        Assert.Equal(403, noHeader.Code);
        Assert.Equal("Forbidden", noHeader.Body);
        Assert.Null(passkeys.Checked);
    }

    [Fact]
    public async Task UnlockGivesATicketForThisSessionOnly()
    {
        var cookie = await SignIn();
        var r = await Post("/camera/unlock", cookie, Assertion);
        Assert.Equal(200, r.Code);
        Assert.StartsWith("application/json", r.Type);
        var ticket = JsonDocument.Parse(r.Body).RootElement.GetProperty("ticket").GetString();
        Assert.Equal("cred-1", passkeys.Checked!.Value.Id);
        Assert.Equal(TailHost, passkeys.Checked!.Value.Host);
        var token = cookie[(Auth.CookieName.Length + 1)..];
        Assert.False(tickets.Redeem(ticket, "another-session"));

        var second = JsonDocument.Parse((await Post("/camera/unlock", cookie, Assertion)).Body).RootElement.GetProperty("ticket").GetString();
        Assert.True(tickets.Redeem(second, token));
        Assert.False(tickets.Redeem(second, token));
    }

    [Fact]
    public async Task UnlockRefusesABadPasskeyOrTheSwitchOff()
    {
        var cookie = await SignIn();
        passkeys.Valid = false;
        var bad = await Post("/camera/unlock", cookie, Assertion);
        Assert.Equal(401, bad.Code);
        Assert.Equal(CameraTexts.PasskeyFailed, bad.Body);
        passkeys.Valid = true;
        allowed = false;
        var off = await Post("/camera/unlock", cookie, Assertion);
        Assert.Equal(401, off.Code);
        Assert.Equal(CameraTexts.PasskeyFailed, off.Body);
        var notJson = await Post("/camera/unlock", cookie, "not json");
        Assert.Equal(401, notJson.Code);
        Assert.Equal(0, tickets.Count);
    }

    [Fact]
    public async Task WithoutTheCameraWiredTheRoutesAreNotFound()
    {
        server.Camera = null;
        var cookie = await SignIn();
        foreach (var path in new[] { "/camera/options", "/camera/unlock" })
        {
            var r = await Post(path, cookie, Assertion);
            Assert.Equal(404, r.Code);
            Assert.Equal("Not found", r.Body);
        }
    }
}
