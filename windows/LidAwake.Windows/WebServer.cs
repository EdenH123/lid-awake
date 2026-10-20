using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LidAwake;

// The phone page and its requests on 127.0.0.1:8787, with the same rules as the Mac
// (docs/API.md). Every request goes through the checks of section 2.2 in that order.
public sealed partial class WebServer : IDisposable
{
    public const int DefaultPort = 8787;
    const int MaxBody = 64 * 1024;
    const string Forbidden = "Forbidden";

    readonly Auth auth;
    readonly Tailnet tailnet;
    readonly Func<bool> isOn;
    readonly Func<object> status;
    readonly Action turnOff;
    readonly Action<string, string> announce;
    readonly AccessLog? log;
    readonly Action<int>? setTimer;
    readonly Action<string, bool>? setLimit;
    readonly int port;
    readonly string baseDir;
    readonly string web;
    readonly string rawHtml;
    readonly string html;
    readonly string scriptHashes;
    volatile WebApplication? app;

    // `status` is what GET /status answers. `announce(title, body)` tells the owner about a
    // new sign-in. `baseDir` is where web\, sky\ and the icons are (next to the exe). `log` is the
    // activity log that GET /activity reads and sign-ins and turn-offs are written to (none: nothing
    // is written and the list is empty). `setTimer(minutes)` applies a length already checked against
    // AutoOff.Choices to the "Turn off after" timer (none: the length is accepted and dropped).
    // `setLimit(name, on)` turns a safety switch ("battery" or "heat", already checked) on or off
    // (none: accepted and dropped).
    public WebServer(Auth auth, Tailnet tailnet, Func<bool> isOn, Func<object> status, Action turnOff,
                     Action<string, string> announce, int port = DefaultPort, string? baseDir = null,
                     AccessLog? log = null, Action<int>? setTimer = null, Action<string, bool>? setLimit = null)
    {
        (this.auth, this.tailnet, this.isOn, this.status, this.turnOff, this.announce) = (auth, tailnet, isOn, status, turnOff, announce);
        (this.log, this.setTimer, this.setLimit) = (log, setTimer, setLimit);
        this.port = port;
        this.baseDir = baseDir ?? AppContext.BaseDirectory;
        web = Path.Combine(this.baseDir, "web");
        rawHtml = File.ReadAllText(Path.Combine(web, "index.html"));
        // Hashed before the launch-screen marker is replaced, once, so editing the page never needs a code change.
        scriptHashes = PageHeaders.ScriptHashes(rawHtml);
        html = rawHtml.Replace("<!--SPLASH-->", "");
    }

    public int Port => port;

    // The recorded samples behind /temp-history, /battery-history and /cpu-history (none: those
    // routes answer 404, as for a feature the PC doesn't have). Set before Start.
    public HistoryStore? History { get; set; }

    // True after Start succeeded, until Stop or Dispose. Tailscale Serve may only point at the
    // port while this is true (PhoneAccess).
    public bool IsRunning => app != null;

    public void Start()
    {
        if (app != null) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions { ContentRootPath = baseDir });
        // No environment variable, appsettings.json or command line may add an address
        // (Kestrel:Endpoints, URLS, HTTP_PORTS): the one Listen below is the only one.
        ((IConfigurationBuilder)builder.Configuration).Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Listen(IPAddress.Loopback, port);
            o.Limits.MaxRequestBodySize = MaxBody;
            o.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
            // A body must keep coming: 64 KB takes at most about 8 seconds after a 5-second grace.
            // (Kestrel's default, 240 bytes a second, would let 64 KB dribble in for 4.5 minutes.)
            o.Limits.MinRequestBodyDataRate = new MinDataRate(8 * 1024, TimeSpan.FromSeconds(5));
            o.Limits.MaxConcurrentConnections = 64;
        });
        var built = builder.Build();
        built.Run(Handle);
        try
        {
            // Wait for the port to be taken, so "already in use" is reported to the caller.
            Task.Run(() => built.StartAsync()).GetAwaiter().GetResult();
            // Settings read before the sources above were cleared (ASPNETCORE_URLS with
            // PREFERHOSTINGURLS) could still move the server: refuse any address but ours.
            var urls = built.Urls.ToList();
            if (urls.Count == 0 || !urls.All(IsOurAddress))
                throw new InvalidOperationException($"The phone page would listen on {string.Join(", ", urls)} instead of 127.0.0.1:{port}.");
        }
        catch (Exception)
        {
            Shutdown(built);
            throw;
        }
        app = built;
    }

    bool IsOurAddress(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "http" && u.Host == "127.0.0.1" && u.Port == port;

    public void Stop()
    {
        var running = app;
        app = null;
        if (running != null) Shutdown(running);
    }

    static void Shutdown(WebApplication running)
    {
        try
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await running.StopAsync(cts.Token); }
                catch (Exception) { }
                await running.DisposeAsync();
            }).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { }
    }

    public void Dispose() => Stop();

    static readonly string[] StaticPaths =
        { "/sw.js", "/manifest.webmanifest", "/apple-touch-icon.png", "/apple-touch-icon-precomposed.png", "/icon-512.png" };

    // Answered before the "off" check and before sign-in (section 3).
    static bool IsStaticPath(string path) =>
        StaticPaths.Contains(path) || path.StartsWith("/sky/", StringComparison.Ordinal) || path.StartsWith("/splash/", StringComparison.Ordinal);

    async Task Handle(HttpContext c)
    {
        try
        {
            await Route(c);
        }
        catch (Microsoft.AspNetCore.Http.BadHttpRequestException)
        {
            // A body that is too big, or a broken one, while it was being read.
            await Fail(c, 400, "Bad request");
        }
        catch (Exception)
        {
            // Whatever went wrong stays inside this request.
            await Fail(c, 500, "Something went wrong.");
        }
    }

    // An error answer that replaces whatever the failed request had set (a cookie, the CSP).
    internal static async Task Fail(HttpContext c, int code, string message)
    {
        try
        {
            if (c.Response.HasStarted) return;
            c.Response.Headers.Clear();
            AddCommonHeaders(c);
            await SendText(c, code, message);
        }
        catch (Exception) { }
    }

    static void AddCommonHeaders(HttpContext c)
    {
        foreach (var (k, v) in PageHeaders.Common) c.Response.Headers[k] = v;
    }

    async Task Route(HttpContext c)
    {
        var req = c.Request;
        AddCommonHeaders(c);

        // Section 2.1: a request that is malformed or too big.
        if (req.Headers.ContainsKey("Transfer-Encoding") || req.ContentLength > MaxBody)
        { await SendText(c, 400, "Bad request"); return; }

        var host = (req.Host.Host ?? "").ToLowerInvariant();
        var path = req.Path.Value ?? "/";
        var method = req.Method;
        bool get = method == "GET" || method == "HEAD";
        bool isPage = path == "/" && get;
        bool isStatic = get && IsStaticPath(path);

        // 1: the request came from the public internet.
        if (req.Headers.ContainsKey("Tailscale-Funnel-Request")) { await SendText(c, 403, Forbidden); return; }

        // 2: this PC's own address. It only ever points to the Tailscale address (2.3).
        if (Tailnet.IsLoopback(host))
        {
            if (isPage) { await Page(c, host); return; }
            if (method == "GET" && path == "/auth")
            {
                await SendJson(c, new { loopback = true, url = tailnet.Host is { } h ? $"https://{h}/" : "", os = "windows" });
                return;
            }
            if (isStatic) { await Static(c, path); return; }
            await SendText(c, 403, "Open Lid Awake at its Tailscale address.");
            return;
        }

        // 3: any other Host must be this PC's Tailscale name, from the PC owner's account.
        if (!tailnet.IsOwner(host, req.Headers["Tailscale-User-Login"].ToString()))
        {
            await SendText(c, 403, "This device isn't signed in to the Tailscale account this PC uses.");
            return;
        }

        // 4: a request made by a page of another site.
        var site = req.Headers["Sec-Fetch-Site"].ToString();
        if (!isStatic && !isPage && site != "" && site != "same-origin" && site != "none")
        { await SendText(c, 403, Forbidden); return; }

        // 5: the page and static files.
        if (isPage) { await Page(c, host); return; }
        if (isStatic) { await Static(c, path); return; }

        // 6: while off, only the lock state answers (2.4).
        if (!isOn())
        {
            if (method == "GET" && path == "/auth") { await SendJson(c, new { off = true, os = "windows" }); return; }
            await SendText(c, 423, "Lid Awake is off on your PC.");
            return;
        }

        // 7: everything except the public routes needs a session.
        var tokens = CookieTokens(req).ToList();
        bool signedIn = tokens.Any(auth.IsValid);
        bool isPublic = path is "/" or "/auth" or "/login" or "/logout" or "/passkey/login-options" or "/passkey/login";
        if (!isPublic && !signedIn) { await SendText(c, 401, "Locked"); return; }

        // 8: a POST must carry the header a plain web form cannot add.
        if (method == "POST" && req.Headers["X-LidAwake"].ToString() != "1") { await SendText(c, 403, Forbidden); return; }

        // 9: the routes. Method and path must both match.
        switch (method, path)
        {
            case ("GET", "/auth"):
                await SendJson(c, new { hasPassword = auth.HasPassword, loggedIn = signedIn, faceID = auth.Passkeys.Count > 0, os = "windows" });
                return;
            case ("POST", "/login"):
                await Login(c);
                return;
            case ("POST", "/logout"):
                foreach (var token in tokens) auth.Logout(token);
                c.Response.Headers.Append("Set-Cookie", $"{Auth.CookieName}=; Path=/; Max-Age=0; HttpOnly; SameSite=Strict; Secure");
                Empty(c);
                return;
            case ("GET", "/status"):
                await SendJson(c, status());
                return;
            case ("POST", "/awake"):
            {
                var body = await ReadJson(c.Request);
                if (!body.TryGetValue("on", out var on) || on.ValueKind != JsonValueKind.False)
                { await SendText(c, 400, "Lid-awake can only be turned on from this PC."); return; }
                LogQuietly("Lid Awake turned off from the phone");
                turnOff();
                Empty(c);
                return;
            }
            case ("GET", "/sessions"):
            {
                var list = auth.Sessions(tokens).Select(x => new { id = x.Id, device = x.Device, created = x.Created, faceID = x.FaceID, current = x.Current }).ToList();
                await SendJson(c, new { sessions = list });
                return;
            }
            case ("POST", "/sessions/signout"):
                await SignOut(c, tokens);
                return;
            case ("GET", "/activity"):
                await SendJson(c, new { lines = log?.Recent(8) ?? new List<string>() });
                return;
            case ("POST", "/timer"):
            {
                var body = await ReadJson(c.Request);
                if (!body.TryGetValue("minutes", out var m) || !TryWhole(m, out var minutes) || !AutoOff.IsChoice(minutes))
                { await SendText(c, 400, "Unknown timer length."); return; }
                setTimer?.Invoke(minutes);
                Empty(c);
                return;
            }
            case ("POST", "/limits"):
            {
                // `heat` is accepted even on a PC with no temperature: the switch is stored.
                var body = await ReadJson(c.Request);
                if (!body.TryGetValue("name", out var n) || n.ValueKind != JsonValueKind.String || n.GetString() is not { } name
                    || !Limits.IsName(name)
                    || !body.TryGetValue("on", out var o) || o.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                { await SendText(c, 400, "Unknown limit."); return; }
                setLimit?.Invoke(name, o.ValueKind == JsonValueKind.True);
                Empty(c);
                return;
            }
            case ("GET", "/temp-history") when History is { } tempHistory:
                await SendJson(c, tempHistory.Temperature(HistoryStore.ClampHours(req.Query["hours"].FirstOrDefault())));
                return;
            case ("GET", "/battery-history") when History is { } batteryHistory:
                await SendJson(c, batteryHistory.Battery(HistoryStore.ClampHours(req.Query["hours"].FirstOrDefault())));
                return;
            case ("GET", "/cpu-history") when History is { } cpuHistory:
                await SendJson(c, cpuHistory.Cpu(HistoryStore.ClampHours(req.Query["hours"].FirstOrDefault())));
                return;
            case ("GET", "/cpu-icon") when History != null:
                // Windows sends no per-app processor use, so no app is ever listed to have an icon.
                await SendText(c, 404, "No such app.");
                return;
            // Face ID / fingerprint (passkeys): WebServer.Passkeys.cs.
            case ("GET", "/passkey/login-options"):
                await SendJson(c, new { challenge = auth.Passkeys.Challenge("get"), rpId = host });
                return;
            case ("POST", "/passkey/login"):
                await PasskeyLogin(c, host);
                return;
            case ("POST", "/passkey/register-options"):
                await PasskeyRegisterOptions(c, host);
                return;
            case ("POST", "/passkey/register"):
                await PasskeyRegister(c, host);
                return;
            default:
                await SendText(c, 404, "Not found");
                return;
        }
    }

    // POST /sessions/signout: `others: true` signs out every session but the caller's, `id` one
    // session (never the caller's own). Neither: 400. An id that matches nothing is still 204.
    async Task SignOut(HttpContext c, List<string> tokens)
    {
        var body = await ReadJson(c.Request);
        var device = Auth.DeviceName(c.Request.Headers.UserAgent.ToString());
        if (body.TryGetValue("others", out var o) && o.ValueKind == JsonValueKind.True)
        {
            if (auth.SignOutOthers(tokens) > 0) LogQuietly($"Signed out the other devices from the phone · {device}");
        }
        else if (body.TryGetValue("id", out var i) && i.ValueKind == JsonValueKind.String)
        {
            if (auth.SignOut(i.GetString(), tokens) > 0) LogQuietly($"Signed out a device from the phone · {device}");
        }
        else
        {
            await SendText(c, 400, "Which device?");
            return;
        }
        Empty(c);
    }

    // A whole number in a JSON body: 15 and 15.0 both count, 15.5 and "15" do not.
    static bool TryWhole(JsonElement e, out int value)
    {
        value = 0;
        if (e.ValueKind != JsonValueKind.Number) return false;
        if (e.TryGetInt32(out var whole)) { value = whole; return true; }
        if (!e.TryGetDouble(out var d) || d != Math.Floor(d) || Math.Abs(d) >= int.MaxValue) return false;
        value = (int)d;
        return true;
    }

    void LogQuietly(string text)
    {
        try { log?.Write(text); }
        catch (Exception) { }
    }

    async Task Login(HttpContext c)
    {
        var body = await ReadJson(c.Request);
        var password = body.TryGetValue("password", out var p) && p.ValueKind == JsonValueKind.String ? p.GetString() ?? "" : "";
        var device = Auth.DeviceName(c.Request.Headers.UserAgent.ToString());
        // If this throws (damaged settings), Handle answers 500 and no wrong try was counted:
        // Auth counts a try only after the password was compared.
        var r = auth.Login(password, device);
        switch (r.Result)
        {
            case LoginResult.Kind.Ok:
                c.Response.Headers.Append("Set-Cookie",
                    $"{Auth.CookieName}={r.Token}; Path=/; Max-Age={(int)(Auth.SessionDays * 86400)}; HttpOnly; SameSite=Strict; Secure");
                var announcement = device == "Browser" ? "With the password, on a browser." : $"With the password, on an {device}.";
                LogQuietly("New sign-in to Lid Awake · " + announcement);
                try { announce("New sign-in to Lid Awake", announcement); }
                catch (Exception) { }  // a notification that fails must not undo the sign-in
                Empty(c);
                return;
            case LoginResult.Kind.Wrong:
                await SendText(c, 401, "Wrong password.");
                return;
            case LoginResult.Kind.Locked:
                await SendText(c, 429, $"Too many wrong tries. Try again in {r.Seconds} seconds.");
                return;
            default:
                await SendText(c, 409, "No password is set yet.");
                return;
        }
    }

    // Every session token in the Cookie header, whichever way the header is split (2.5).
    static IEnumerable<string> CookieTokens(HttpRequest req)
    {
        foreach (var header in req.Headers.Cookie)
        {
            if (header == null) continue;
            foreach (var part in header.Split(';'))
            {
                var kv = part.Trim();
                if (kv.StartsWith(Auth.CookieName + "=", StringComparison.Ordinal))
                {
                    var value = kv[(Auth.CookieName.Length + 1)..];
                    if (value.Length > 0) yield return value;
                }
            }
        }
    }

    // A body that is not valid JSON, or not an object, counts as empty (section 5).
    static async Task<Dictionary<string, JsonElement>> ReadJson(HttpRequest req)
    {
        try
        {
            var body = await JsonSerializer.DeserializeAsync<Dictionary<string, JsonElement>>(req.Body);
            return body ?? new();
        }
        catch (JsonException) { return new(); }
    }

    Task Page(HttpContext c, string host)
    {
        c.Response.Headers["Content-Security-Policy"] = PageHeaders.CspFromHashes(scriptHashes, host);
        c.Response.ContentType = "text/html; charset=utf-8";
        return c.Response.WriteAsync(html);
    }

    async Task Static(HttpContext c, string path)
    {
        string? file = path switch
        {
            "/sw.js" => Path.Combine(web, "sw.js"),
            "/manifest.webmanifest" => Path.Combine(web, "manifest.webmanifest"),
            "/apple-touch-icon.png" or "/apple-touch-icon-precomposed.png" => Path.Combine(baseDir, "icon-180.png"),
            "/icon-512.png" => Path.Combine(baseDir, "icon-512.png"),
            "/sky/sunrise.jpg" or "/sky/day.jpg" or "/sky/sunset.jpg" or "/sky/night.jpg" =>
                Path.Combine(baseDir, "sky", "sky-" + path["/sky/".Length..]),
            _ => null,  // includes /splash/*: the Mac's iPhone launch screens are optional
        };
        string contentType = path switch
        {
            "/sw.js" => "text/javascript; charset=utf-8",
            "/manifest.webmanifest" => "application/manifest+json",
            _ when path.EndsWith(".png", StringComparison.Ordinal) => "image/png",
            _ => "image/jpeg",
        };
        if (file != null && File.Exists(file))
        {
            c.Response.ContentType = contentType;
            await c.Response.SendFileAsync(file);
        }
        else if (path == "/manifest.webmanifest")
        {
            c.Response.ContentType = contentType;
            await c.Response.WriteAsync("{}");  // the page still loads without a manifest file
        }
        else await SendText(c, 404, "Not found");
    }

    // Success with no data (2.7).
    static void Empty(HttpContext c)
    {
        c.Response.StatusCode = 204;
        c.Response.ContentType = "text/plain";
    }

    static Task SendText(HttpContext c, int code, string message)
    {
        c.Response.StatusCode = code;
        c.Response.ContentType = "text/plain; charset=utf-8";
        return c.Response.WriteAsync(message);
    }

    static Task SendJson(HttpContext c, object value)
    {
        c.Response.ContentType = "application/json";
        return c.Response.WriteAsync(JsonSerializer.Serialize(value));
    }
}
