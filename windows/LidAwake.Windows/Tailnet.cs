using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LidAwake;

// Who may use the page: this PC's own Tailscale name, from a device signed in to the PC's
// Tailscale account (docs/API.md section 9). Learned from `tailscale status --json` and
// remembered. Every call to Tailscale is safe when Tailscale is missing: nothing here throws.
public sealed class Tailnet
{
    public const string Exe = @"C:\Program Files\Tailscale\tailscale.exe";

    // Set while Serve holds a mapping that Lid Awake added, so turning off (or the next start,
    // after a crash) removes only that, and leaves a Serve setup of the owner's own alone.
    public const string ServedKey = "servedByLidAwake";
    public const string HostKey = "tailnetHost";
    public const string OwnerKey = "tailnetOwner";

    // This PC's Tailscale name and owner, and what Lid Awake added to its Serve setup, belong to
    // this PC (machine.json), not to the roaming settings another PC of the same user would read.
    // An older version kept them in settings.json: moved once, at start, like the saved lid values.
    public static void MoveToMachine(Settings roaming, Settings machine) =>
        roaming.MoveTo(machine, ServedKey, LiveServedKey, HostKey, OwnerKey);

    // How much of what Tailscale printed goes into the activity log.
    public const int MaxSaid = 300;

    // One run of the Tailscale CLI. Output is what it printed on stdout, Error on stderr. Ok is
    // false when it failed, did not finish in time (TimedOut) or is not installed (Missing).
    public readonly record struct CliResult(bool Ok, string Output, string Error = "", bool TimedOut = false, bool Missing = false);

    readonly Settings settings;
    readonly Func<string, CliResult> exec;
    readonly object applyGate = new();
    volatile int wantedPort;  // 0: not served
    // The port of our page, remembered even while off, to check `serve status` for after a failed
    // removal. 0 until known; then the default port is used.
    volatile int knownPort;
    // The live socket's port for /live (section 9), 0 for none. Set by PhoneAccess, and only while
    // our own LiveServer holds the port, for the same reason as the page. The last one used is
    // remembered, so a removal can be checked for it too.
    volatile int livePort;
    volatile int knownLivePort;

    public int LivePort
    {
        get => livePort;
        set
        {
            livePort = value;
            if (value != 0) knownLivePort = value;
        }
    }

    // `run` takes the arguments and returns the output, or null when the command could not be
    // run or failed. Tests pass a fake one.
    public Tailnet(Settings settings, Func<string, string?>? run = null)
    {
        this.settings = settings;
        exec = run is null ? new Func<string, CliResult>(RunExe) : FromOutput(run);
        peers = new(ReadPeers, PeersEvery);
    }

    static Func<string, CliResult> FromOutput(Func<string, string?> run) =>
        args => run(args) is { } output ? new CliResult(true, output) : new CliResult(false, "");

    Tailnet(Settings settings, Func<string, CliResult> exec)
    {
        this.settings = settings;
        this.exec = exec;
        peers = new(ReadPeers, PeersEvery);
    }

    // For tests that need what a failed or timed-out command printed.
    public static Tailnet WithCli(Settings settings, Func<string, CliResult> exec) => new(settings, exec);

    // Called (on a background thread) when Serve was asked for the page but the page is still not
    // published: with what Tailscale printed, cut to MaxSaid characters. Tailscale prints a link
    // when the owner must first allow Serve or HTTPS for the tailnet.
    public Action<string>? NotPublished { get; set; }

    // The output, or null when the command could not be run or failed.
    string? Run(string args)
    {
        var r = exec(args);
        return r.Ok ? r.Output : null;
    }

    public string? Host => settings.Get<string>(HostKey);
    public string? Owner => settings.Get<string>(OwnerKey);

    // Saves the name and the owner together or not at all, like Tailnet.update on the Mac, so
    // a status without the owner never pairs a new name with an old owner.
    public void Refresh()
    {
        try
        {
            using var doc = ReadStatus();
            if (doc != null) Learn(doc.RootElement);
        }
        catch (Exception) { }
    }

    // One `status --json`, parsed; null when Tailscale printed nothing (or failed). Throws on
    // output that isn't JSON.
    JsonDocument? ReadStatus()
    {
        var json = Run("status --json");
        return string.IsNullOrWhiteSpace(json) ? null : JsonDocument.Parse(json);
    }

    // ---- link in /status (6.6): how the phone that asks reaches this PC ----

    // A peer device as `status --json` shows it: CurAddr (its direct address and port, empty
    // while relayed), Relay (the relay's region code) and Active (traffic is flowing now).
    public readonly record struct PeerPath(string Direct, string Relay, bool Active);

    public static readonly TimeSpan PeersEvery = TimeSpan.FromSeconds(10);

    // Every peer by each of its Tailscale addresses, read in the background at most every 10
    // seconds. A read with no peers (Tailscale failed) keeps the last ones, as on the Mac.
    readonly BackgroundValue<Dictionary<string, PeerPath>> peers;

    Dictionary<string, PeerPath>? ReadPeers()
    {
        using var doc = ReadStatus();
        if (doc == null) return null;
        var found = Peers(doc.RootElement);
        return found.Count > 0 ? found : null;
    }

    // The Peer map of a parsed `status --json`, by each address in the peer's TailscaleIPs (in
    // IPAddress's own spelling, so the request's address compares however it was written).
    public static Dictionary<string, PeerPath> Peers(JsonElement root)
    {
        var found = new Dictionary<string, PeerPath>();
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Peer", out var list) || list.ValueKind != JsonValueKind.Object)
            return found;
        foreach (var entry in list.EnumerateObject())
        {
            var peer = entry.Value;
            if (peer.ValueKind != JsonValueKind.Object
                || !peer.TryGetProperty("TailscaleIPs", out var ips) || ips.ValueKind != JsonValueKind.Array) continue;
            var path = new PeerPath(Text(peer, "CurAddr"), Text(peer, "Relay"),
                peer.TryGetProperty("Active", out var a) && a.ValueKind == JsonValueKind.True);
            foreach (var ip in ips.EnumerateArray())
                if (ip.ValueKind == JsonValueKind.String && IPAddress.TryParse(ip.GetString(), out var address))
                    found[address.ToString()] = path;
        }
        return found;
    }

    static string Text(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    // `link` for the device at `forwardedFor`, the X-Forwarded-For header Tailscale Serve adds (its
    // Tailscale address, which a device can't fake). Null when unknown. Never waits for Tailscale:
    // each call only starts a background read when the last one is older than 10 seconds, so the
    // first status after a while has no link yet, as on the Mac.
    public string? Link(string? forwardedFor)
    {
        var known = peers.Latest;
        if (known == null || ForwardedAddress(forwardedFor) is not { } ip) return null;
        return known.TryGetValue(ip, out var peer) ? LinkText(peer) : null;
    }

    // The last address in X-Forwarded-For (the one Serve itself added, after any the device sent),
    // in IPAddress's spelling; null when there is none.
    public static string? ForwardedAddress(string? forwardedFor)
    {
        if (string.IsNullOrWhiteSpace(forwardedFor)) return null;
        var last = forwardedFor.Split(',')[^1].Trim();
        return IPAddress.TryParse(last, out var address) ? address.ToString() : null;
    }

    static readonly Dictionary<string, string> RelayCities = new()
    {
        ["par"] = "Paris", ["fra"] = "Frankfurt", ["ams"] = "Amsterdam", ["lhr"] = "London", ["mad"] = "Madrid",
        ["waw"] = "Warsaw", ["tlv"] = "Tel Aviv", ["nyc"] = "New York", ["sfo"] = "San Francisco", ["sin"] = "Singapore",
    };

    // The Mac's words (Tailnet.path): "Direct · same Wi-Fi", "Direct · iPhone hotspot", "Direct ·
    // over the internet", "Through a relay in Paris" or "Through a relay server". Null for an idle
    // device with no live path yet (the page is about to make one).
    public static string? LinkText(PeerPath peer)
    {
        if (!peer.Active && peer.Direct.Length == 0) return null;
        if (peer.Direct.Length == 0)
            return RelayCities.TryGetValue(peer.Relay, out var city) ? "Through a relay in " + city : "Through a relay server";
        // The address part of "1.2.3.4:41641" or "[fe80::1]:41641".
        var address = peer.Direct;
        int colon = address.LastIndexOf(':');
        if (colon >= 0) address = address[..colon];
        address = address.Trim('[', ']');
        if (address.StartsWith("172.20.10.", StringComparison.Ordinal)) return "Direct · iPhone hotspot";
        var octets = address.Split('.')
            .Select(p => int.TryParse(p, NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : (int?)null)
            .Where(n => n != null).Select(n => n!.Value).ToArray();
        bool privateV4 = octets.Length == 4 && (octets[0] == 10 || (octets[0] == 192 && octets[1] == 168)
            || (octets[0] == 172 && octets[1] >= 16 && octets[1] <= 31) || (octets[0] == 169 && octets[1] == 254));
        var lower = address.ToLowerInvariant();
        bool privateV6 = lower.StartsWith("fe80", StringComparison.Ordinal) || lower.StartsWith("fd", StringComparison.Ordinal);
        return privateV4 || privateV6 ? "Direct · same Wi-Fi" : "Direct · over the internet";
    }

    // What the "Set up Lid Awake" window shows about Tailscale, from one `status --json` (the
    // same read as Refresh, which it also does): installed, signed in (BackendState "Running"),
    // and HTTPS certificates on for the account (CertDomains not empty), as on the Mac.
    public readonly record struct SetupState(bool Installed, bool SignedIn, bool HttpsOn);

    // Slow (it runs tailscale.exe): never on the UI thread. Never throws.
    public SetupState CheckSetup()
    {
        try
        {
            var r = exec("status --json");
            if (r.Missing) return new(false, false, false);
            if (!r.Ok || string.IsNullOrWhiteSpace(r.Output)) return new(true, false, false);
            using var doc = JsonDocument.Parse(r.Output);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(true, false, false);
            // A settings file that can't be written doesn't change what Tailscale said.
            try { Learn(root); }
            catch (Exception) { }
            bool running = root.TryGetProperty("BackendState", out var b) && b.ValueKind == JsonValueKind.String && b.GetString() == "Running";
            bool https = root.TryGetProperty("CertDomains", out var c) && c.ValueKind == JsonValueKind.Array && c.GetArrayLength() > 0;
            return new(true, running, https);
        }
        catch (Exception) { return new(true, false, false); }
    }

    // The name and owner from a parsed `status --json`, saved together or not at all.
    void Learn(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !root.TryGetProperty("Self", out var self) || self.ValueKind != JsonValueKind.Object) return;
        var dns = self.TryGetProperty("DNSName", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
        if (!dns.EndsWith(".ts.net.", StringComparison.OrdinalIgnoreCase)) return;
        string? userId = null;
        if (self.TryGetProperty("UserID", out var uid))
            userId = uid.ValueKind == JsonValueKind.Number ? uid.GetRawText() : uid.ValueKind == JsonValueKind.String ? uid.GetString() : null;
        if (userId != null
            && root.TryGetProperty("User", out var users) && users.ValueKind == JsonValueKind.Object
            && users.TryGetProperty(userId, out var me) && me.ValueKind == JsonValueKind.Object
            && me.TryGetProperty("LoginName", out var login) && login.ValueKind == JsonValueKind.String
            && login.GetString() is { Length: > 0 } name)
        {
            settings.Set(HostKey, dns.TrimEnd('.').ToLowerInvariant());
            settings.Set(OwnerKey, name.ToLowerInvariant());
        }
    }

    // The Tailscale app itself (the window and tray icon), next to the CLI.
    public static string AppExe => Path.Combine(Path.GetDirectoryName(Exe)!, "tailscale-ipn.exe");

    // Make https://<pc>.ts.net/ reach the page on `port` (section 9), and /live the live socket
    // on LivePort when it is set. Only adds what is missing, and never leaves Funnel (the public
    // internet) on. Callers must make sure our own servers hold the ports (see PhoneAccess).
    // Returns null when the page is published (or Tailscale is not installed), otherwise what
    // Tailscale printed: after `serve --bg`, `serve status` must show our mapping, and a link in
    // what it printed means the owner must allow something first.
    public string? Serve(int port)
    {
        try
        {
            knownPort = port;
            var first = exec("serve status --json");
            if (first.Missing) return null;
            var status = first.Ok ? first.Output : "";
            if (HasFunnel(status)) Run("funnel --https=443 off");
            var live = livePort;
            // No live socket of ours (it isn't running, or another program holds its port): a
            // /live mapping to that port left from before (a crash) goes, so the phone's cookie
            // can't reach whatever listens there.
            if (live == 0 && MapsLive(status, LastLivePort) && exec(LiveOff).Ok && settings.Get<bool?>(LiveServedKey) == true)
                settings.Set(LiveServedKey, false);
            bool pageMapped = Maps(status, port), liveMapped = live == 0 || MapsLive(status, live);
            if (pageMapped && liveMapped) { lastLiveProblem = null; return null; }
            // Remembered first, so a crash halfway still gets the mapping removed at the next start.
            // When the page is the owner's own mapping, only /live is ours to take away later.
            if (!pageMapped) settings.Set(ServedKey, true);
            else if (settings.Get<bool?>(ServedKey) != true) settings.Set(LiveServedKey, true);
            string saidPage = "", saidLive = "";
            bool pageTimedOut = false, liveTimedOut = false;
            if (!pageMapped)
            {
                var added = exec($"serve --bg {port}");
                saidPage = (added.Output + "\n" + added.Error).Trim();
                pageTimedOut = added.TimedOut;
            }
            if (!liveMapped)
            {
                // The live socket for the Screen tab: wss://<pc>.ts.net/live.
                var added = exec($"serve --bg --set-path /live http://127.0.0.1:{live}");
                saidLive = (added.Output + "\n" + added.Error).Trim();
                liveTimedOut = added.TimedOut;
            }
            var after = Run("serve status --json") ?? "";
            // A page that was already mapped was left alone: it is still published, even when this
            // second status failed (then `after` is empty and only /live is in doubt).
            bool pageOk = pageMapped || (!pageTimedOut && Maps(after, port) && !AsksForConsent(saidPage));
            bool liveOk = live == 0 || (!liveTimedOut && MapsLive(after, live) && !AsksForConsent(saidLive));
            if (pageOk)
            {
                // The page works and only the screen's socket isn't published: that is said apart
                // (LiveNotPublished), not as "the phone page isn't published".
                if (!liveOk) TellLive(Brief(saidLive.Length > 0 ? saidLive : liveTimedOut ? "Tailscale did not answer in time." : "Tailscale printed nothing."));
                else lastLiveProblem = null;
                return null;
            }
            var said = (saidPage + "\n" + saidLive).Trim();
            if (said.Length == 0) said = pageTimedOut || liveTimedOut ? "Tailscale did not answer in time." : "Tailscale printed nothing.";
            return Brief(said);
        }
        catch (Exception) { return null; }
    }

    // Removes only Lid Awake's /live, leaving the rest of Serve as it is.
    const string LiveOff = "serve --set-path /live off";

    // Set while Serve holds a /live mapping Lid Awake added to a Serve setup of the owner's own
    // (the owner already mapped the page): then turning off removes only /live.
    public const string LiveServedKey = "liveServedByLidAwake";

    // Called (on a background thread) when the page is published but /live, the screen's live
    // socket, is not: with what Tailscale printed. Once per problem, not every minute.
    public Action<string>? LiveNotPublished { get; set; }
    string? lastLiveProblem;

    void TellLive(string said)
    {
        if (said == lastLiveProblem) return;
        lastLiveProblem = said;
        try { LiveNotPublished?.Invoke(said); }
        catch (Exception) { }
    }

    int LastLivePort => knownLivePort != 0 ? knownLivePort : LiveServer.DefaultPort;

    // `serve status` maps the path /live to 127.0.0.1:<port> exactly.
    public static bool MapsLive(string status, int port) =>
        Regex.IsMatch(status ?? "", "\"/live\"\\s*:\\s*\\{[^{}]*(?<![\\d.])127\\.0\\.0\\.1:"
                                    + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + "(?!\\d)");

    // Take everything away, so nothing on the tailnet can reach the page.
    public void Unserve()
    {
        try { TryUnserve(); }
        catch (Exception) { }
    }

    // True when the mapping is gone: the off command succeeded, or it failed but `serve status`
    // (which must itself have worked) no longer maps our port. Otherwise false, and the caller
    // keeps what it remembered so a later attempt tries again.
    bool TryUnserve()
    {
        if (exec("serve --https=443 off").Ok) return true;
        var status = exec("serve status --json");
        if (!status.Ok) return false;
        var port = knownPort != 0 ? knownPort : WebServer.DefaultPort;
        var live = knownLivePort;
        return !Maps(status.Output, port) && (live == 0 || !MapsLive(status.Output, live));
    }

    // Only /live, the same way: the off command worked, or the status no longer shows it.
    bool TryUnserveLive()
    {
        if (exec(LiveOff).Ok) return true;
        var status = exec("serve status --json");
        return status.Ok && !MapsLive(status.Output, LastLivePort);
    }

    // Unserve, but only when Lid Awake added the mapping (ServedKey); then forget it, but only
    // once the mapping is really gone. A failed removal keeps the flag, so the next start, the
    // next turn-off and the minute check try again. When Lid Awake only added /live to the
    // owner's own Serve setup (LiveServedKey), only /live is removed.
    public void UnserveIfOurs()
    {
        try
        {
            if (settings.Get<bool?>(ServedKey) == true)
            {
                if (TryUnserve())
                {
                    settings.Set(ServedKey, false);
                    if (settings.Get<bool?>(LiveServedKey) == true) settings.Set(LiveServedKey, false);
                }
                return;
            }
            if (settings.Get<bool?>(LiveServedKey) == true && TryUnserveLive()) settings.Set(LiveServedKey, false);
        }
        catch (Exception) { }
    }

    // Serve (on `port`) or unserve in the background, so the tray never waits for Tailscale.
    // Calls are applied one at a time and each applies the latest wish, so a quick on/off/on
    // ends as "on". The task ends when this call's turn has been applied; it never fails.
    public Task SetServed(bool on, int port)
    {
        wantedPort = on ? port : 0;
        knownPort = port;
        try { return Task.Run(Apply); }
        catch (Exception) { return Task.CompletedTask; }
    }

    // The check every 60 seconds while on (section 9): the name and owner are read again, and
    // Serve is added back if it went missing. While the wish is "not served" it does nothing,
    // except trying again to remove a mapping whose earlier removal failed (the flag is still set).
    public Task Recheck()
    {
        try
        {
            if (wantedPort == 0 && settings.Get<bool?>(ServedKey) != true && settings.Get<bool?>(LiveServedKey) != true)
                return Task.CompletedTask;
            return Task.Run(Apply);
        }
        catch (Exception) { return Task.CompletedTask; }
    }

    void Apply()
    {
        lock (applyGate)
        {
            if (wantedPort == 0) { UnserveIfOurs(); return; }
            Refresh();
            // Turned off while Tailscale was being asked: do not add Serve after all.
            var port = wantedPort;
            if (port == 0) { UnserveIfOurs(); return; }
            if (Serve(port) is { } said)
            {
                try { NotPublished?.Invoke(said); }
                catch (Exception) { }
            }
        }
    }

    // `serve status` maps our port: 127.0.0.1:8787 exactly, so 8787 never matches 87870.
    public static bool Maps(string status, int port) =>
        Regex.IsMatch(status ?? "", @"(?<![\d.])127\.0\.0\.1:" + port.ToString(System.Globalization.CultureInfo.InvariantCulture) + @"(?!\d)");

    static readonly Regex Url = new(@"https?://([^\s/:""'<>]+)", RegexOptions.IgnoreCase);

    // A link to anywhere but this tailnet's own addresses and the PC itself: Tailscale asks the
    // owner to open it, to allow Serve or HTTPS certificates. (When Serve works it prints the
    // page's own https://<pc>.ts.net/ address and the 127.0.0.1 it points to; those are fine.)
    public static bool AsksForConsent(string said) =>
        Url.Matches(said ?? "").Any(m =>
        {
            var host = m.Groups[1].Value.ToLowerInvariant();
            return !host.EndsWith(".ts.net", StringComparison.Ordinal) && !IsLoopback(host);
        });

    // On one line, cut to MaxSaid characters, for the activity log.
    public static string Brief(string said)
    {
        var line = Regex.Replace(said ?? "", @"\s+", " ").Trim();
        return line.Length <= MaxSaid ? line : line.Substring(0, MaxSaid);
    }

    static bool HasFunnel(string statusJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(statusJson);
            return doc.RootElement.ValueKind == JsonValueKind.Object
                && doc.RootElement.TryGetProperty("AllowFunnel", out var f) && f.ValueKind == JsonValueKind.Object
                && f.EnumerateObject().Any(p => p.Value.ValueKind == JsonValueKind.True);
        }
        catch (JsonException) { return false; }
    }

    // Loopback means the PC itself (docs/API.md 2.3). The port is already removed.
    public static bool IsLoopback(string host) => host is "127.0.0.1" or "localhost";

    public bool IsOwner(string host, string? login) =>
        Host is { } h && Owner is { } o && host == h && login != null && string.Equals(login, o, StringComparison.OrdinalIgnoreCase);

    // Runs tailscale.exe for at most 15 seconds. `serve --bg` can wait for the owner to allow
    // Serve in the browser: then it is stopped, and what it printed so far is kept.
    static CliResult RunExe(string args)
    {
        try
        {
            if (!File.Exists(Exe)) return new CliResult(false, "", Missing: true);
            var info = new ProcessStartInfo(Exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(info);
            if (p == null) return new CliResult(false, "");
            var output = p.StandardOutput.ReadToEndAsync();
            var error = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch (Exception) { }
                // Killed, its streams close; wait a little for what it printed.
                return new CliResult(false, Finished(output), Finished(error), TimedOut: true);
            }
            p.WaitForExit();  // let the output finish arriving
            return new CliResult(p.ExitCode == 0, output.Result, error.Result);
        }
        catch (Exception) { return new CliResult(false, ""); }
    }

    static string Finished(Task<string> read)
    {
        try { return read.Wait(2000) ? read.Result : ""; }
        catch (Exception) { return ""; }
    }
}
