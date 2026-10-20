using System.Text.Json;
using LidAwake;
using Xunit;

// The apps routes, end to end through a real WebServer (the harness is in WebServerTests.cs).
// The programs, windows and icons are fakes: nothing here opens or closes a real program.
public partial class WebServerTests
{
    const string AppAlpha = PhoneAppsTests.Alpha;
    const string AppBeta = PhoneAppsTests.Beta;
    const string AppGamma = PhoneAppsTests.Gamma;
    const string AppOther = PhoneAppsTests.Delta;   // running, not picked
    static readonly byte[] PngSignature = { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A };

    readonly FakeAppProcesses appProcesses = new();
    readonly FakeAppActions appActions = new();
    readonly FakeAppIcons appIcons = new();
    PhoneApps phoneApps = null!;

    // Called by the constructor before the server starts: three picked apps, and one open app that is not picked.
    void WireApps()
    {
        phoneApps = new PhoneApps(settings, appProcesses, appActions, appIcons, accessLog, exists: _ => true);
        phoneApps.SetPicks(new[] { new StartApp("Alpha", AppAlpha), new StartApp("Beta", AppBeta), new StartApp("Gamma", AppGamma) });
        appProcesses.Apps.Add(new RunningApp(AppOther, "Other", true));
        server.Apps = phoneApps;
    }

    static string IdBody(string exe) => "{\"id\":\"" + PhoneApps.Id(exe) + "\"}";

    static string[] AppNames(JsonElement list) => list.EnumerateArray().Select(a => a.GetProperty("name").GetString()!).ToArray();

    // ---- the checks of section 2.2 apply to every route ----

    [Fact]
    public async Task AppsRoutesNeedASessionTheHeaderTheOwnerAndLidAwakeOn()
    {
        var cookie = await SignIn();
        var routes = new (string Method, string Path, string? Json)[]
        {
            ("GET", "/app-icon.png?id=" + PhoneApps.Id(AppAlpha), null),
            ("POST", "/open-app", IdBody(AppAlpha)),
            ("POST", "/focus-app", IdBody(AppOther)),
            ("POST", "/quit-app", IdBody(AppAlpha)),
            ("POST", "/apps-order", "{\"ids\":[]}"),
        };
        foreach (var (method, path, json) in routes)
        {
            // No session: 401 Locked.
            var noSession = await Send(method, path, lid: true, json: json);
            Assert.Equal(401, noSession.Code);
            Assert.Equal("Locked", noSession.Body);
            // A POST without X-LidAwake: 403.
            if (method == "POST") Assert.Equal(403, (await Send(method, path, cookie: cookie, json: json)).Code);
            // Another site's page: 403. Another Tailscale user: 403.
            Assert.Equal(403, (await Send(method, path, lid: true, cookie: cookie, json: json, fetchSite: "cross-site")).Code);
            Assert.Equal(403, (await Send(method, path, lid: true, cookie: cookie, json: json, owner: "someone@else.com")).Code);
            Assert.Equal(403, (await Send(method, path, lid: true, cookie: cookie, json: json, funnel: true)).Code);
        }
        // Nothing was opened, fronted, quit or read by any of those.
        Assert.Empty(appActions.Calls);
        Assert.Empty(appIcons.Asked);

        on = false;
        foreach (var (method, path, json) in routes)
        {
            var off = await Send(method, path, lid: true, cookie: cookie, json: json);
            Assert.Equal(423, off.Code);
            Assert.Equal(OffText, off.Body);
        }
        Assert.Empty(appActions.Calls);
    }

    [Fact]
    public async Task OnlyTheDocumentedMethodsReachTheAppsRoutes()
    {
        var cookie = await SignIn();
        Assert.Equal(404, (await Send("GET", "/open-app", cookie: cookie)).Code);
        Assert.Equal(404, (await Post("/app-icon.png", cookie, IdBody(AppAlpha))).Code);
        Assert.Equal(404, (await Send("GET", "/apps-order", cookie: cookie)).Code);
        Assert.Empty(appActions.Calls);
    }

    // ---- GET /app-icon.png ----

    [Fact]
    public async Task IconIsAPngForAPickedAppAndForAnOpenAppAndNotFoundOtherwise()
    {
        var cookie = await SignIn();
        foreach (var exe in new[] { AppAlpha, AppOther })
        {
            var r = await Send("GET", "/app-icon.png?id=" + PhoneApps.Id(exe), cookie: cookie);
            Assert.Equal(200, r.Code);
            Assert.Equal("image/png", r.Header("Content-Type"));
            Assert.Equal(PngSignature, r.Bytes.Take(8).ToArray());
        }
        foreach (var path in new[] { "/app-icon.png", "/app-icon.png?id=", "/app-icon.png?id=0123456789ab" })
        {
            var r = await Send("GET", path, cookie: cookie);
            Assert.Equal(404, r.Code);
            Assert.Equal("text/plain; charset=utf-8", r.Header("Content-Type"));
            Assert.Equal("No such app.", r.Body);
        }
    }

    [Fact]
    public async Task IconSizeIs512ForFiveHundredTwelveOrMoreAnd256ForAnythingElse()
    {
        var cookie = await SignIn();
        var id = PhoneApps.Id(AppBeta);
        foreach (var size in new[] { "512", "1024", "100", "abc", "" })
            Assert.Equal(200, (await Send("GET", $"/app-icon.png?id={id}&size={size}", cookie: cookie)).Code);
        Assert.Equal(new[] { 512, 256 }, appIcons.Asked.Select(a => a.Size).ToArray());
    }

    [Fact]
    public async Task APathAsTheIconIdIsNotFound()
    {
        var cookie = await SignIn();
        foreach (var path in new[] { AppAlpha, AppOther, @"C:\Windows\System32\cmd.exe" })
        {
            var r = await Send("GET", "/app-icon.png?id=" + Uri.EscapeDataString(path), cookie: cookie);
            Assert.Equal(404, r.Code);
            Assert.Equal("No such app.", r.Body);
        }
        Assert.Empty(appIcons.Asked);
    }

    // ---- POST /open-app ----

    [Fact]
    public async Task OpenStartsAPickedAppAndLogsIt()
    {
        var cookie = await SignIn();
        var r = await Post("/open-app", cookie, IdBody(AppAlpha), ua: IPhone);
        Assert.Equal(204, r.Code);
        Assert.Equal(new[] { "start " + AppAlpha }, appActions.Calls.ToArray());
        Assert.Contains("Opened Alpha from the phone · iPhone", accessLog.Recent(1)[0]);
    }

    [Fact]
    public async Task OpenAnIdNotOnTheListIs404AndStartsNothing()
    {
        var cookie = await SignIn();
        foreach (var body in new[] { IdBody(AppOther), "{\"id\":\"0123456789ab\"}", "{\"id\":\"\"}", "{}", "{\"id\":5}", "not json" })
        {
            var r = await Post("/open-app", cookie, body);
            Assert.Equal(404, r.Code);
            Assert.Equal("That app isn't on your list any more.", r.Body);
        }
        Assert.Empty(appActions.Calls);
    }

    [Fact]
    public async Task APathSentByThePhoneIsRejectedAndNeverStarted()
    {
        var cookie = await SignIn();
        var picked = AppAlpha.Replace("\\", "\\\\");
        foreach (var body in new[]
        {
            "{\"id\":\"" + picked + "\"}",                                   // a listed app's path as the id
            "{\"id\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\"}",             // some other program's path
            "{\"path\":\"" + picked + "\"}",                                 // a path field instead
            "{\"id\":\"" + PhoneApps.Id(AppAlpha) + "\",\"path\":\"C:\\\\Windows\\\\System32\\\\cmd.exe\"}",
        })
        {
            var r = await Post("/open-app", cookie, body);
            // Only an id from the list counts: the extra "path" is ignored, so the last body opens Alpha, not cmd.exe.
            Assert.Contains(r.Code, new[] { 204, 404 });
        }
        Assert.DoesNotContain(appActions.Calls, c => c.Contains("cmd.exe", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(new[] { "start " + AppAlpha }, appActions.Calls.ToArray());   // only the one real id
        Assert.Equal(404, (await Post("/focus-app", cookie, "{\"id\":\"" + picked + "\"}")).Code);
        Assert.Equal(404, (await Post("/quit-app", cookie, "{\"id\":\"" + picked + "\"}")).Code);
        Assert.Equal(new[] { "start " + AppAlpha }, appActions.Calls.ToArray());
    }

    // ---- POST /focus-app ----

    [Fact]
    public async Task FocusFrontsHidesAndQuitsAnOpenApp()
    {
        var cookie = await SignIn();
        appActions.WithWindow.Add(AppOther);
        appActions.QuitAnswers[AppOther] = QuitResult.Asked;
        var id = PhoneApps.Id(AppOther);
        Assert.Equal(204, (await Post("/focus-app", cookie, "{\"id\":\"" + id + "\"}")).Code);                          // action defaults to focus
        Assert.Equal(204, (await Post("/focus-app", cookie, "{\"id\":\"" + id + "\",\"action\":\"focus\"}")).Code);
        Assert.Equal(204, (await Post("/focus-app", cookie, "{\"id\":\"" + id + "\",\"action\":\"hide\"}")).Code);
        Assert.Equal(204, (await Post("/focus-app", cookie, "{\"id\":\"" + id + "\",\"action\":\"quit\"}", ua: IPhone)).Code);
        Assert.Equal(new[] { "focus " + AppOther, "focus " + AppOther, "hide " + AppOther, "quit " + AppOther }, appActions.Calls.ToArray());
        Assert.Contains("Quit Other from the phone · iPhone", accessLog.Recent(1)[0]);
    }

    [Fact]
    public async Task FocusWithAnUnknownActionIs400BeforeAnythingIsLookedUp()
    {
        var cookie = await SignIn();
        var r = await Post("/focus-app", cookie, "{\"id\":\"" + PhoneApps.Id(AppOther) + "\",\"action\":\"kill\"}");
        Assert.Equal(400, r.Code);
        Assert.Equal("Unknown action.", r.Body);
        Assert.Equal(400, (await Post("/focus-app", cookie, "{\"id\":\"nothing\",\"action\":\"kill\"}")).Code);
        Assert.Empty(appActions.Calls);
    }

    [Fact]
    public async Task FocusOnAnAppThatIsNotOpenIs404()
    {
        var cookie = await SignIn();
        foreach (var body in new[] { IdBody(AppAlpha), "{\"id\":\"0123456789ab\"}", "{}" })   // Alpha is picked but not running
        {
            var r = await Post("/focus-app", cookie, body);
            Assert.Equal(404, r.Code);
            Assert.Equal("That app isn't open any more.", r.Body);
        }
        // Open, but with no window to show or close: also "not open".
        Assert.Equal(404, (await Post("/focus-app", cookie, IdBody(AppOther))).Code);
        Assert.Equal(404, (await Post("/focus-app", cookie, "{\"id\":\"" + PhoneApps.Id(AppOther) + "\",\"action\":\"quit\"}")).Code);
    }

    // ---- POST /quit-app ----

    [Fact]
    public async Task QuitAsksAPickedAppAndLogsIt()
    {
        var cookie = await SignIn();
        appActions.QuitAnswers[AppAlpha] = QuitResult.Asked;
        var r = await Post("/quit-app", cookie, IdBody(AppAlpha), ua: IPhone);
        Assert.Equal(204, r.Code);
        Assert.Equal(new[] { "quit " + AppAlpha }, appActions.Calls.ToArray());
        Assert.Contains("Quit Alpha from the phone · iPhone", accessLog.Recent(1)[0]);
    }

    [Fact]
    public async Task QuitOfAnIdNotOnTheListIs404()
    {
        var cookie = await SignIn();
        foreach (var body in new[] { IdBody(AppOther), "{\"id\":\"0123456789ab\"}", "{}" })
        {
            var r = await Post("/quit-app", cookie, body);
            Assert.Equal(404, r.Code);
            Assert.Equal("That app isn't on your list any more.", r.Body);
        }
        Assert.Empty(appActions.Calls);
    }

    [Fact]
    public async Task QuitOfAListedAppThatIsNotOpenOrWillNotCloseIs409()
    {
        var cookie = await SignIn();
        // Not running at all.
        var notOpen = await Post("/quit-app", cookie, IdBody(AppBeta));
        Assert.Equal(409, notOpen.Code);
        Assert.Equal("That app isn't open.", notOpen.Body);
        // Running, but with no window to ask to close: it does not close, so the same answer.
        appActions.QuitAnswers[AppGamma] = QuitResult.NoWindow;
        var noWindow = await Post("/quit-app", cookie, IdBody(AppGamma));
        Assert.Equal(409, noWindow.Code);
        Assert.Equal("That app isn't open.", noWindow.Body);
        // Nothing but quit requests were made: no kill, no focus.
        Assert.All(appActions.Calls, c => Assert.StartsWith("quit ", c));
        Assert.DoesNotContain(accessLog.Recent(10), line => line.Contains("Quit "));
    }

    // ---- POST /apps-order ----

    [Fact]
    public async Task OrderIsSavedAndShownInTheNextStatus()
    {
        var info = new StatusInfo(new FakeReadings(), apps: phoneApps);
        statusAnswer = () => info.Build(true);
        var cookie = await SignIn();
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, AppNames(Json(await Send("GET", "/status", cookie: cookie)).GetProperty("apps")));

        var ids = new[] { PhoneApps.Id(AppGamma), "unknown", PhoneApps.Id(AppAlpha) };
        Assert.Equal(204, (await Post("/apps-order", cookie, JsonSerializer.Serialize(new { ids }))).Code);
        // Unknown ids are ignored; Beta, not mentioned, keeps its place at the end.
        Assert.Equal(new[] { "Gamma", "Alpha", "Beta" }, AppNames(Json(await Send("GET", "/status", cookie: cookie)).GetProperty("apps")));
        // Stored in the settings.
        Assert.Equal(new[] { "Gamma", "Alpha", "Beta" }, new PhoneApps(settings, appProcesses, appActions, appIcons).Picks().Select(p => p.Name).ToArray());
    }

    [Fact]
    public async Task OrderWithoutAListOfStringsIs400()
    {
        var cookie = await SignIn();
        foreach (var body in new[] { "{}", "{\"ids\":\"abc\"}", "{\"ids\":[1,2]}", "{\"ids\":[\"a\",null]}", "{\"ids\":{}}", "[]", "not json" })
        {
            var r = await Post("/apps-order", cookie, body);
            Assert.Equal(400, r.Code);
            Assert.Equal("Missing app order.", r.Body);
        }
        Assert.Equal(new[] { "Alpha", "Beta", "Gamma" }, phoneApps.Picks().Select(p => p.Name).ToArray());
        Assert.Equal(204, (await Post("/apps-order", cookie, "{\"ids\":[]}")).Code);
    }

    // ---- /status ----

    [Fact]
    public async Task StatusListsAppsOtherAppsAndTheAppsFeature()
    {
        appProcesses.Apps.Add(new RunningApp(AppBeta, "Beta", true));
        var info = new StatusInfo(new FakeReadings(), apps: phoneApps);
        statusAnswer = () => info.Build(true);
        var cookie = await SignIn();
        var s = Json(await Send("GET", "/status", cookie: cookie));
        var apps = s.GetProperty("apps").EnumerateArray().ToArray();
        Assert.Equal(3, apps.Length);
        Assert.Equal(PhoneApps.Id(AppBeta), apps[1].GetProperty("id").GetString());
        Assert.False(apps[0].GetProperty("running").GetBoolean());
        Assert.True(apps[1].GetProperty("running").GetBoolean());
        var other = Assert.Single(s.GetProperty("otherApps").EnumerateArray().ToArray());
        Assert.Equal(PhoneApps.Id(AppOther), other.GetProperty("id").GetString());
        Assert.Equal("Other", other.GetProperty("name").GetString());
        Assert.Contains("apps", s.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        StatusInfoTests.AssertEveryKeyWithItsKind(s);
    }
}
