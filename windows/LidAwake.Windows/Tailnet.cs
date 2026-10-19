using System.Diagnostics;
using System.Text.Json;

namespace LidAwake;

// Who may use the page: this PC's own Tailscale name, from a device signed in to the PC's
// Tailscale account (docs/API.md section 9). Learned from `tailscale status --json` and
// remembered. Every call to Tailscale is safe when Tailscale is missing: nothing here throws.
public sealed class Tailnet
{
    public const string Exe = @"C:\Program Files\Tailscale\tailscale.exe";

    readonly Settings settings;
    readonly Func<string, string?> run;
    readonly object applyGate = new();
    volatile bool wanted;

    // `run` takes the arguments and returns the output, or null when the command could not be
    // run or failed. Tests pass a fake one.
    public Tailnet(Settings settings, Func<string, string?>? run = null)
    {
        this.settings = settings;
        this.run = run ?? RunExe;
    }

    public string? Host => settings.Get<string>("tailnetHost");
    public string? Owner => settings.Get<string>("tailnetOwner");

    public void Refresh()
    {
        try
        {
            var json = run("status --json");
            if (string.IsNullOrWhiteSpace(json)) return;
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("Self", out var self) || self.ValueKind != JsonValueKind.Object) return;
            var dns = self.TryGetProperty("DNSName", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() ?? "" : "";
            if (!dns.EndsWith(".ts.net.", StringComparison.OrdinalIgnoreCase)) return;
            settings.Set("tailnetHost", dns.TrimEnd('.').ToLowerInvariant());
            string? userId = null;
            if (self.TryGetProperty("UserID", out var uid))
                userId = uid.ValueKind == JsonValueKind.Number ? uid.GetRawText() : uid.ValueKind == JsonValueKind.String ? uid.GetString() : null;
            if (userId != null
                && root.TryGetProperty("User", out var users) && users.ValueKind == JsonValueKind.Object
                && users.TryGetProperty(userId, out var me) && me.ValueKind == JsonValueKind.Object
                && me.TryGetProperty("LoginName", out var login) && login.ValueKind == JsonValueKind.String
                && login.GetString() is { Length: > 0 } name)
                settings.Set("tailnetOwner", name.ToLowerInvariant());
        }
        catch (Exception) { }
    }

    // Make https://<pc>.ts.net/ reach the page (section 9, the HTTP part). Only adds what is
    // missing, and never leaves Funnel (the public internet) on. The /live socket comes later.
    public void Serve()
    {
        try
        {
            var status = run("serve status --json") ?? "";
            if (HasFunnel(status)) run("funnel --https=443 off");
            if (!status.Contains("127.0.0.1:8787", StringComparison.Ordinal)) run("serve --bg 8787");
        }
        catch (Exception) { }
    }

    // Take everything away, so nothing on the tailnet can reach the page.
    public void Unserve()
    {
        try { run("serve --https=443 off"); }
        catch (Exception) { }
    }

    // Serve or unserve in the background, so the tray never waits for Tailscale. Calls are
    // applied one at a time and each applies the latest wish, so a quick on/off/on ends as "on".
    public void SetServed(bool on)
    {
        wanted = on;
        try
        {
            Task.Run(() =>
            {
                lock (applyGate)
                {
                    if (wanted) { Refresh(); Serve(); }
                    else Unserve();
                }
            });
        }
        catch (Exception) { }
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

    static string? RunExe(string args)
    {
        try
        {
            if (!File.Exists(Exe)) return null;
            var info = new ProcessStartInfo(Exe, args)
            {
                RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true,
            };
            using var p = Process.Start(info);
            if (p == null) return null;
            var output = p.StandardOutput.ReadToEndAsync();
            _ = p.StandardError.ReadToEndAsync();
            if (!p.WaitForExit(15000))
            {
                try { p.Kill(true); } catch (Exception) { }
                return null;
            }
            p.WaitForExit();  // let the output finish arriving
            return p.ExitCode == 0 ? output.Result : null;
        }
        catch (Exception) { return null; }
    }
}
