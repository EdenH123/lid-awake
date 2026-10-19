using System.Diagnostics;
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

    // How much of what Tailscale printed goes into the activity log.
    public const int MaxSaid = 300;

    // One run of the Tailscale CLI. Output is what it printed on stdout, Error on stderr. Ok is
    // false when it failed, did not finish in time (TimedOut) or is not installed (Missing).
    public readonly record struct CliResult(bool Ok, string Output, string Error = "", bool TimedOut = false, bool Missing = false);

    readonly Settings settings;
    readonly Func<string, CliResult> exec;
    readonly object applyGate = new();
    volatile int wantedPort;  // 0: not served

    // `run` takes the arguments and returns the output, or null when the command could not be
    // run or failed. Tests pass a fake one.
    public Tailnet(Settings settings, Func<string, string?>? run = null)
    {
        this.settings = settings;
        exec = run is null ? new Func<string, CliResult>(RunExe) : FromOutput(run);
    }

    static Func<string, CliResult> FromOutput(Func<string, string?> run) =>
        args => run(args) is { } output ? new CliResult(true, output) : new CliResult(false, "");

    Tailnet(Settings settings, Func<string, CliResult> exec)
    {
        this.settings = settings;
        this.exec = exec;
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

    public string? Host => settings.Get<string>("tailnetHost");
    public string? Owner => settings.Get<string>("tailnetOwner");

    // Saves the name and the owner together or not at all, like Tailnet.update on the Mac, so
    // a status without the owner never pairs a new name with an old owner.
    public void Refresh()
    {
        try
        {
            var json = Run("status --json");
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
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
                settings.Set("tailnetHost", dns.TrimEnd('.').ToLowerInvariant());
                settings.Set("tailnetOwner", name.ToLowerInvariant());
            }
        }
        catch (Exception) { }
    }

    // Make https://<pc>.ts.net/ reach the page on `port` (section 9, the HTTP part). Only adds
    // what is missing, and never leaves Funnel (the public internet) on. The /live socket comes
    // later. Callers must make sure our own web server holds `port` (see PhoneAccess).
    // Returns null when the page is published (or Tailscale is not installed), otherwise what
    // Tailscale printed: after `serve --bg`, `serve status` must show our mapping, and a link in
    // what it printed means the owner must allow something first.
    public string? Serve(int port)
    {
        try
        {
            var first = exec("serve status --json");
            if (first.Missing) return null;
            var status = first.Ok ? first.Output : "";
            if (HasFunnel(status)) Run("funnel --https=443 off");
            if (Maps(status, port)) return null;
            // Remembered first, so a crash halfway still gets the mapping removed at the next start.
            settings.Set(ServedKey, true);
            var added = exec($"serve --bg {port}");
            var said = (added.Output + "\n" + added.Error).Trim();
            var after = Run("serve status --json") ?? "";
            if (!added.TimedOut && Maps(after, port) && !AsksForConsent(said)) return null;
            if (said.Length == 0) said = added.TimedOut ? "Tailscale did not answer in time." : "Tailscale printed nothing.";
            return Brief(said);
        }
        catch (Exception) { return null; }
    }

    // Take everything away, so nothing on the tailnet can reach the page.
    public void Unserve()
    {
        try { Run("serve --https=443 off"); }
        catch (Exception) { }
    }

    // Unserve, but only when Lid Awake added the mapping (ServedKey); then forget it.
    public void UnserveIfOurs()
    {
        try
        {
            if (settings.Get<bool?>(ServedKey) != true) return;
            Unserve();
            settings.Set(ServedKey, false);
        }
        catch (Exception) { }
    }

    // Serve (on `port`) or unserve in the background, so the tray never waits for Tailscale.
    // Calls are applied one at a time and each applies the latest wish, so a quick on/off/on
    // ends as "on". The task ends when this call's turn has been applied; it never fails.
    public Task SetServed(bool on, int port)
    {
        wantedPort = on ? port : 0;
        try { return Task.Run(Apply); }
        catch (Exception) { return Task.CompletedTask; }
    }

    // The check every 60 seconds while on (section 9): the name and owner are read again, and
    // Serve is added back if it went missing. Does nothing while the wish is "not served".
    public Task Recheck()
    {
        if (wantedPort == 0) return Task.CompletedTask;
        try { return Task.Run(Apply); }
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
