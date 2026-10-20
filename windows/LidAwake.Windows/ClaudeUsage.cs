using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace LidAwake;

// Claude plan usage, as the Mac's SystemInfo.swift ("Claude plan usage"): the 5-hour and weekly
// limits, saved in %LOCALAPPDATA%\LidAwake\claude-usage.json (the same JSON as the Mac's file).
// Two things write it: `LidAwake.exe --claude-statusline` (ClaudeHook.cs) after each terminal
// reply, and Refresh below, which asks Claude Code itself ("/usage" sends no message and uses none
// of the plan). /status, the tray and the 90% alert read it. Called from the web server's threads,
// the tray and the safety check's timer: the scheduling is under a lock, the file is read whole.
public sealed class ClaudeUsage
{
    public const string FileName = "claude-usage.json";
    // A small, ordinary file only (as on the Mac): the numbers are checked so odd values can't crash anything.
    public const int MaxFileBytes = 64 * 1024;
    // How long one "/usage" question may take, as on the Mac; then the whole process tree is stopped.
    public static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(45);
    // Claude Code's flags for the question: skip the user's hooks and plugins (an empty folder has
    // no project settings) and save no session for it. The Mac's, exactly.
    public const string UsageArguments = "-p /usage --setting-sources local --no-session-persistence";

    // How often each caller may ask (main.swift): /status every 3 minutes, the tray's 30-second
    // timer every 5, the safety check's alerts every 10, opening the tray menu every minute.
    public static readonly TimeSpan AskForStatus = TimeSpan.FromMinutes(3);
    public static readonly TimeSpan AskForTray = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan AskForAlerts = TimeSpan.FromMinutes(10);
    public static readonly TimeSpan AskOnMenu = TimeSpan.FromMinutes(1);

    // One limit window: percent used, and when it resets (Unix seconds, 0 = unknown).
    public readonly record struct Window(double Pct, long ResetsAt);

    // The saved numbers, as /status sends them (docs/API.md 6.3). `Updated` is 0 when unknown.
    public sealed record Snapshot(long Updated, Window? FiveHour, Window? SevenDay)
    {
        public Dictionary<string, object?> ToStatus()
        {
            var s = new Dictionary<string, object?> { ["updated"] = Updated };
            if (FiveHour is { } f) s["fiveHour"] = new Dictionary<string, object?> { ["pct"] = f.Pct, ["resetsAt"] = f.ResetsAt };
            if (SevenDay is { } w) s["sevenDay"] = new Dictionary<string, object?> { ["pct"] = w.Pct, ["resetsAt"] = w.ResetsAt };
            return s;
        }
    }

    public static string DefaultFolder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LidAwake");

    readonly string path;
    readonly Func<string?> ask;
    readonly Func<DateTimeOffset> now;
    readonly TimeZoneInfo zone;
    readonly object gate = new();
    bool checking;
    DateTimeOffset checkedAt = DateTimeOffset.MinValue;

    // After Refresh has saved new numbers (on a thread-pool thread).
    public event Action? Saved;

    // `ask` runs Claude Code's "/usage" and returns what it printed, or null (by default
    // AskClaudeCode). The tests pass a fake, a folder and a clock.
    public ClaudeUsage(string? folder = null, Func<string?>? ask = null, Func<DateTimeOffset>? now = null, TimeZoneInfo? zone = null)
    {
        path = Path.Combine(folder ?? DefaultFolder, FileName);
        this.ask = ask ?? AskClaudeCode;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.zone = zone ?? TimeZoneInfo.Local;
    }

    public string FilePath => path;

    public Snapshot? Read() => ReadFile(path);

    // For /status: asks again at most every 3 minutes (in the background), and answers the saved
    // numbers now. Never throws: a broken file is only `null`.
    public Dictionary<string, object?>? ForStatus()
    {
        try
        {
            Refresh(AskForStatus);
            return Read()?.ToStatus();
        }
        catch (Exception) { return null; }
    }

    // Asks Claude Code for the numbers in the background, unless a question is running or one was
    // asked less than `every` ago. Returns the question's task (a finished one when skipped).
    public Task Refresh(TimeSpan every)
    {
        lock (gate)
        {
            var t = now();
            if (checking || (checkedAt != DateTimeOffset.MinValue && t - checkedAt <= every)) return Task.CompletedTask;
            checking = true;
            checkedAt = t;
        }
        return Task.Run(() =>
        {
            try
            {
                var text = ask();
                if (text != null && ParseUsage(text, now(), zone) is { } limits && Save(path, limits, now()))
                {
                    try { Saved?.Invoke(); }
                    catch (Exception) { }
                }
            }
            catch (Exception) { }
            finally
            {
                lock (gate) checking = false;
            }
        });
    }

    // {"rate_limits": {...}, "updated": <Unix seconds>}, to a temp file of its own first
    // (HistoryStore.WriteAtomic), so several status lines at once can't mix. Only into a folder
    // that is there (%LOCALAPPDATA%\LidAwake: no folder, no file, as on the Mac).
    public static bool Save(string path, JsonObject rateLimits, DateTimeOffset at)
    {
        try
        {
            if (!Directory.Exists(Path.GetDirectoryName(Path.GetFullPath(path)))) return false;
            var saved = new JsonObject { ["rate_limits"] = rateLimits.DeepClone(), ["updated"] = at.ToUnixTimeSeconds() };
            return HistoryStore.WriteAtomic(path, saved.ToJsonString(), uniqueTemp: true);
        }
        catch (Exception) { return false; }
    }

    // The Mac's claudeUsage(): a regular file under 64 KB (not a folder, a link or a device), a JSON
    // object with `rate_limits`; each window only with a finite percent from 0 to 1000, and times
    // only when finite, positive and below 1e11 (else 0). Anything else is null.
    public static Snapshot? ReadFile(string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists || (info.Attributes & (FileAttributes.Directory | FileAttributes.ReparsePoint | FileAttributes.Device)) != 0
                || info.Length >= MaxFileBytes) return null;
            byte[] data;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                // It may have grown since: never more than the limit.
                data = new byte[MaxFileBytes];
                int total = 0, n;
                while (total < data.Length && (n = stream.Read(data, total, data.Length - total)) > 0) total += n;
                if (total >= MaxFileBytes) return null;
                Array.Resize(ref data, total);
            }
            using var doc = JsonDocument.Parse(data);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("rate_limits", out var limits)
                || limits.ValueKind != JsonValueKind.Object) return null;
            return new Snapshot(Stamp(root, "updated"), WindowOf(limits, "five_hour"), WindowOf(limits, "seven_day"));
        }
        catch (Exception) { return null; }
    }

    static Window? WindowOf(JsonElement limits, string key)
    {
        if (!limits.TryGetProperty(key, out var w) || w.ValueKind != JsonValueKind.Object) return null;
        if (!w.TryGetProperty("used_percentage", out var p) || p.ValueKind != JsonValueKind.Number || !p.TryGetDouble(out var pct)
            || !double.IsFinite(pct) || pct < 0 || pct > 1000) return null;
        return new Window(pct, Stamp(w, "resets_at"));
    }

    static long Stamp(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d)
            && double.IsFinite(d) && d > 0 && d < 1e11 ? (long)d : 0;

    // ---- What "claude -p /usage" prints (the Mac's parseUsage) ----

    // "Current session: 74% used · resets Oct 8 at 3:09pm (Asia/Jerusalem)" and
    // "Current week (all models): 84% used · resets Oct 10 at 10:59pm (…)" → the status line's
    // format: {"five_hour": {"used_percentage": 74, "resets_at": …}, "seven_day": {…}}. Windows line
    // endings are fine, and so is a "resets" without the time zone in brackets. Null: neither line.
    public static JsonObject? ParseUsage(string text, DateTimeOffset now, TimeZoneInfo zone)
    {
        var limits = new JsonObject();
        var lines = text.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();
        foreach (var (label, key) in new[] { ("Current session", "five_hour"), ("Current week (all models)", "seven_day") })
        {
            var line = lines.FirstOrDefault(l => l.StartsWith(label + ":", StringComparison.Ordinal));
            if (line == null) continue;
            var pctMatch = PercentUsed.Match(line);
            if (!pctMatch.Success || !double.TryParse(pctMatch.Groups[1].Value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var pct))
                continue;
            var window = new JsonObject { ["used_percentage"] = pct };
            var resets = ResetsAt.Match(line);
            if (resets.Success && ResetDate(resets.Groups[1].Value, now, zone) is { } date)
                window["resets_at"] = date.ToUnixTimeSeconds();
            limits[key] = window;
        }
        return limits.Count == 0 ? null : limits;
    }

    static readonly Regex PercentUsed = new(@"([0-9]+(\.[0-9]+)?)% used", RegexOptions.CultureInvariant);
    // Up to " (" (the time zone) or the end of the line.
    static readonly Regex ResetsAt = new(@"resets (.+?)(?: \(|\s*$)", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    // "Oct 8 at 3:09pm", "Oct 10 at 11pm" (the Mac's two forms); also "3:09pm" or "3pm" alone (today,
    // or tomorrow once past), and a 24-hour "15:09".
    static readonly Regex DateAndTime = new(@"^(?<mon>[a-z]{3})[a-z]*\.? (?<day>[0-9]{1,2}),? at (?<time>.+)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly Regex TimeOnly = new(@"^(?<h>[0-9]{1,2})(?::(?<m>[0-9]{2}))? ?(?<ap>am|pm)?$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    static readonly string[] Months = { "jan", "feb", "mar", "apr", "may", "jun", "jul", "aug", "sep", "oct", "nov", "dec" };

    // This year, local time (the Mac's resetDate): a date more than 30 days ago is next year's.
    internal static DateTimeOffset? ResetDate(string s, DateTimeOffset now, TimeZoneInfo zone)
    {
        var text = s.Trim();
        var local = TimeZoneInfo.ConvertTime(now, zone);
        try
        {
            var dated = DateAndTime.Match(text);
            if (dated.Success)
            {
                int month = Array.IndexOf(Months, dated.Groups["mon"].Value.ToLowerInvariant()) + 1;
                if (month == 0 || Clock(dated.Groups["time"].Value) is not { } at) return null;
                int day = int.Parse(dated.Groups["day"].Value, CultureInfo.InvariantCulture);
                var d = At(new DateTime(local.Year, month, day, at.Hour, at.Minute, 0), zone);
                if (d < now - TimeSpan.FromDays(30)) d = At(new DateTime(local.Year + 1, month, day, at.Hour, at.Minute, 0), zone);
                return d;
            }
            if (Clock(text) is { } time)
            {
                var d = At(local.Date + new TimeSpan(time.Hour, time.Minute, 0), zone);
                if (d <= now) d = At(local.Date.AddDays(1) + new TimeSpan(time.Hour, time.Minute, 0), zone);
                return d;
            }
        }
        catch (ArgumentException) { }  // Feb 30 and the like
        return null;
    }

    static (int Hour, int Minute)? Clock(string text)
    {
        var m = TimeOnly.Match(text.Trim());
        if (!m.Success) return null;
        int h = int.Parse(m.Groups["h"].Value, CultureInfo.InvariantCulture);
        int min = m.Groups["m"].Success ? int.Parse(m.Groups["m"].Value, CultureInfo.InvariantCulture) : 0;
        if (min > 59) return null;
        if (m.Groups["ap"].Success)
        {
            if (h < 1 || h > 12) return null;
            bool pm = m.Groups["ap"].Value.Equals("pm", StringComparison.OrdinalIgnoreCase);
            h = h % 12 + (pm ? 12 : 0);
        }
        else if (!m.Groups["m"].Success || h > 23) return null;  // "15" alone is not a time
        return (h, min);
    }

    static DateTimeOffset At(DateTime local, TimeZoneInfo zone) =>
        new(DateTime.SpecifyKind(local, DateTimeKind.Unspecified), zone.GetUtcOffset(local));

    // ---- Asking Claude Code ----

    // Finds Claude Code (ClaudeFinder), runs "/usage" in a fresh empty temp folder that is deleted
    // afterwards, hidden, with nothing to read, inheriting no open files, for at most 45 seconds.
    public const string TempPrefix = "lidawake-usage-";

    public static string? AskClaudeCode()
    {
        var claude = ClaudeFinder.Find();
        if (claude == null || ClaudeFinder.CommandFor(claude, UsageArguments) is not { } command) return null;
        var temp = Path.GetTempPath();
        CleanUp(temp, DateTime.UtcNow);
        var root = Path.Combine(temp, TempPrefix + Guid.NewGuid().ToString("N")[..12]);
        var dir = Path.Combine(root, "run");
        try
        {
            Directory.CreateDirectory(dir);
            return HiddenRun.Run(command.App, command.CommandLine, dir, Path.Combine(root, "out.txt"), AskTimeout).Output;
        }
        catch (Exception) { return null; }
        finally
        {
            // Something it left running may hold the folder a moment longer (when no job could be used).
            for (int i = 0; i < 3 && Directory.Exists(root); i++)
            {
                try { Directory.Delete(root, true); }
                catch (Exception) { Thread.Sleep(500); }
            }
        }
    }

    // Folders an earlier question couldn't delete, once they are 10 minutes old.
    internal static void CleanUp(string temp, DateTime utcNow)
    {
        try
        {
            foreach (var old in Directory.GetDirectories(temp, TempPrefix + "*"))
            {
                try
                {
                    if (utcNow - Directory.GetCreationTimeUtc(old) > TimeSpan.FromMinutes(10)) Directory.Delete(old, true);
                }
                catch (Exception) { }
            }
        }
        catch (Exception) { }
    }

    // ---- Words for the tray and the alert (main.swift) ----

    // "1h40m", "35m", "<1m" (main.swift shortDuration).
    public static string ShortDuration(TimeSpan span)
    {
        var minutes = (long)(span.TotalSeconds / 60);
        if (minutes < 1) return "<1m";
        long h = minutes / 60, m = minutes % 60;
        return h == 0 ? $"{m}m" : $"{h}h{m:00}m";
    }

    // "15:10" today, "Fri 22:59" another day (the time in the PC's own format).
    public static string When(DateTimeOffset at, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture, string weekdaySeparator = " ")
    {
        var local = TimeZoneInfo.ConvertTime(at, zone);
        var time = local.ToString("t", culture);
        return local.Date == TimeZoneInfo.ConvertTime(now, zone).Date ? time : local.ToString("ddd", culture) + weekdaySeparator + time;
    }
}

// The `claudeUsage` alert (main.swift checkAlerts): once per limit window when it reaches 90%,
// remembered as claudeAlerted.<fiveHour|sevenDay> = that window's resetsAt in the settings.
// Checked with the other early warnings (PhoneAlerts.Check, every 15 seconds while on), asking
// Claude Code at most every 10 minutes even with no phone watching.
public sealed class ClaudeUsageAlert
{
    public const double At = 90;
    public const string KeyPrefix = "claudeAlerted.";

    readonly ClaudeUsage usage;
    readonly Settings settings;
    readonly Func<DateTimeOffset> now;
    readonly TimeZoneInfo zone;
    readonly CultureInfo? culture;
    readonly bool ask;
    // Also kept here, so a settings file that can't be written never means an alert every 15 seconds.
    readonly HashSet<(string, long)> sent = new();
    readonly object gate = new();

    // `ask` false: only read the saved numbers (the tests).
    public ClaudeUsageAlert(ClaudeUsage usage, Settings settings, Func<DateTimeOffset>? now = null, TimeZoneInfo? zone = null,
                            CultureInfo? culture = null, bool ask = true)
    {
        (this.usage, this.settings, this.ask) = (usage, settings, ask);
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.zone = zone ?? TimeZoneInfo.Local;
        this.culture = culture;
    }

    // A reset time to the nearest minute.
    public static long WindowKey(long resetsAt) => (long)Math.Round(resetsAt / 60.0, MidpointRounding.AwayFromZero) * 60;

    public void Check(Func<string, string, string, Task> send)
    {
        if (ask) usage.Refresh(ClaudeUsage.AskForAlerts);
        if (usage.Read() is not { } u) return;
        var t = now();
        lock (gate)
        {
            foreach (var (key, label, window) in new[] { ("fiveHour", "5-hour", u.FiveHour), ("sevenDay", "weekly", u.SevenDay) })
            {
                if (window is not { } w || w.Pct < At || w.ResetsAt <= t.ToUnixTimeSeconds()) continue;
                // To the minute: the status line and "/usage" give the same window a few seconds apart.
                long resets = WindowKey(w.ResetsAt);
                long? alerted;
                try { alerted = settings.Get<long?>(KeyPrefix + key); }
                catch (Exception) { alerted = null; }
                if ((alerted is { } a && WindowKey(a) == resets) || !sent.Add((key, resets))) continue;
                try { settings.Set(KeyPrefix + key, resets); }
                catch (Exception) { }
                var reset = DateTimeOffset.FromUnixTimeSeconds(w.ResetsAt);
                var when = ClaudeUsage.When(reset, t, zone, culture ?? CultureInfo.CurrentCulture, " at ");
                if (!when.Contains(" at ", StringComparison.Ordinal)) when = "at " + when;
                var body = $"{char.ToUpperInvariant(label[0])}{label[1..]} limit, resets {when} (in {PhoneAlerts.Spoken(reset - t)}).";
                try { _ = send(AlertKind.ClaudeUsage, $"Claude usage at {(int)w.Pct}%", body); }
                catch (Exception) { }
            }
        }
    }
}

// Where Claude Code is, and whether it is safe to run (the Mac's candidate list and ownedSafely):
// %USERPROFILE%\.local\bin\claude.exe (the native installer) first, then %APPDATA%\npm\claude.cmd
// (npm), which runs through cmd.exe. The file (links followed) and its folder must both be owned
// by this user, Administrators, SYSTEM or TrustedInstaller, with no one else allowed to write.
public static class ClaudeFinder
{
    public const string SystemSid = "S-1-5-18";
    public const string AdministratorsSid = "S-1-5-32-544";
    public const string TrustedInstallerSid = "S-1-5-80-956008885-3418522649-1831038044-1853292631-2271478464";
    public const string OwnerRightsSid = "S-1-3-4";
    // Changing the file or folder, or what is in it: write and append data (add a file or folder),
    // delete, delete children, change permissions, take ownership, and generic write or all.
    public const int WriteMask = 0x2 | 0x4 | 0x10000 | 0x40 | 0x40000 | 0x80000 | 0x40000000 | 0x10000000;

    public static string[] Candidates(string userProfile, string appData) => new[]
    {
        Path.Combine(userProfile, ".local", "bin", "claude.exe"),
        Path.Combine(appData, "npm", "claude.cmd"),
    };

    public static string? Find(IOwnershipReader? reader = null, string? userSid = null)
    {
        try
        {
            reader ??= new WindowsOwnership();
            using var me = WindowsIdentity.GetCurrent();
            userSid ??= me.User?.Value;
            if (userSid == null) return null;
            var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            foreach (var path in Candidates(profile, appData))
                if (File.Exists(path) && SafePath(path, reader, userSid) is { } real) return real;
        }
        catch (Exception) { }
        return null;
    }

    // The file, with links followed, and the folder it is really in.
    public static bool Safe(string path, IOwnershipReader reader, string userSid) => SafePath(path, reader, userSid) != null;

    // The real path (links followed) when it and its folder are safe, else null. That real path is
    // what is run, so a link can't be pointed elsewhere between the check and the start.
    public static string? SafePath(string path, IOwnershipReader reader, string userSid)
    {
        try
        {
            var real = Resolve(path);
            var folder = Path.GetDirectoryName(real);
            if (folder == null) return null;
            return reader.Read(real, directory: false) is { } file && Trusted(file, userSid)
                && reader.Read(folder, directory: true) is { } dir && Trusted(dir, userSid) ? real : null;
        }
        catch (Exception) { return null; }
    }

    static string Resolve(string path)
    {
        try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path); }
        catch (Exception) { return Path.GetFullPath(path); }
    }

    // Owned by the user or the system, and no allow entry (that applies to this object itself)
    // lets anyone else change it.
    public static bool Trusted(FileOwnership o, string userSid)
    {
        bool Ours(string sid) => sid == userSid || sid == SystemSid || sid == AdministratorsSid || sid == TrustedInstallerSid;
        if (o.OwnerSid == null || !Ours(o.OwnerSid)) return false;
        foreach (var e in o.Entries)
        {
            if (!e.Allow || e.InheritOnly || (e.Mask & WriteMask) == 0) continue;
            if (!Ours(e.Sid) && e.Sid != OwnerRightsSid) return false;
        }
        return true;
    }

    // claude.exe directly; claude.cmd through cmd.exe ("/d" skips AutoRun, "/v:off" keeps "!"
    // plain, "/s" keeps everything inside the outer quotes as it is). A path with a quote, or a
    // "%" that cmd would expand, is never run.
    // A program (the real file a link points to may have no ".exe") runs directly; a ".bat" or
    // ".com" never does (Windows would run a batch file through cmd.exe without our quoting).
    public static (string App, string CommandLine)? CommandFor(string claude, string arguments)
    {
        if (claude.Contains('"')) return null;
        var ext = Path.GetExtension(claude);
        if (ext.Equals(".bat", StringComparison.OrdinalIgnoreCase) || ext.Equals(".com", StringComparison.OrdinalIgnoreCase)) return null;
        if (!ext.Equals(".cmd", StringComparison.OrdinalIgnoreCase)) return (claude, $"\"{claude}\" {arguments}");
        if (claude.Contains('%')) return null;
        var cmd = Path.Combine(Environment.SystemDirectory, "cmd.exe");
        return (cmd, $"\"{cmd}\" /d /v:off /s /c \"\"{claude}\" {arguments}\"");
    }
}

public sealed record AccessEntry(string Sid, bool Allow, int Mask, bool InheritOnly);

public sealed record FileOwnership(string? OwnerSid, IReadOnlyList<AccessEntry> Entries);

// The owner and the access entries of a file or folder (the tests pass a fake).
public interface IOwnershipReader
{
    FileOwnership? Read(string path, bool directory);
}

public sealed class WindowsOwnership : IOwnershipReader
{
    public FileOwnership? Read(string path, bool directory)
    {
        try
        {
            const AccessControlSections sections = AccessControlSections.Owner | AccessControlSections.Access;
            FileSystemSecurity security = directory
                ? new DirectoryInfo(path).GetAccessControl(sections)
                : new FileInfo(path).GetAccessControl(sections);
            var owner = (security.GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier)?.Value;
            var entries = new List<AccessEntry>();
            foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            {
                if (rule.IdentityReference is not SecurityIdentifier sid) continue;
                entries.Add(new AccessEntry(sid.Value, rule.AccessControlType == AccessControlType.Allow, (int)rule.FileSystemRights,
                    (rule.PropagationFlags & PropagationFlags.InheritOnly) != 0));
            }
            return new FileOwnership(owner, entries);
        }
        catch (Exception) { return null; }
    }
}

public readonly record struct RunResult(bool Started, bool TimedOut, string? Output);

// Runs a helper program the way the Mac's runOnItsOwn does: hidden (no window, no console), stdin
// from NUL, stderr to NUL, and only its three standard handles inherited (PROC_THREAD_ATTRIBUTE_
// HANDLE_LIST), never Lid Awake's other open files. Its output goes to a file (so a stray
// grandchild holding it can't make us wait). It starts suspended inside a job object, so a timeout
// stops the whole process tree, and whatever it leaves running is stopped when it exits.
public static class HiddenRun
{
    public const int MaxOutputBytes = 1024 * 1024;

    public static RunResult Run(string app, string commandLine, string dir, string outputFile, TimeSpan timeout)
    {
        SafeFileHandle? input = null, output = null, error = null;
        IntPtr attributes = IntPtr.Zero, handles = IntPtr.Zero, job = IntPtr.Zero;
        bool attributesReady = false;
        var pi = default(PROCESS_INFORMATION);
        try
        {
            var inherit = new SECURITY_ATTRIBUTES { nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(), bInheritHandle = 1 };
            input = CreateFileW(@"\\.\NUL", GENERIC_READ, FILE_SHARE_READ | FILE_SHARE_WRITE, ref inherit, OPEN_EXISTING, 0, IntPtr.Zero);
            error = CreateFileW(@"\\.\NUL", GENERIC_WRITE, FILE_SHARE_READ | FILE_SHARE_WRITE, ref inherit, OPEN_EXISTING, 0, IntPtr.Zero);
            output = CreateFileW(outputFile, GENERIC_WRITE, FILE_SHARE_READ, ref inherit, CREATE_ALWAYS, FILE_ATTRIBUTE_NORMAL, IntPtr.Zero);
            if (input.IsInvalid || error.IsInvalid || output.IsInvalid) return new RunResult(false, false, null);

            IntPtr size = IntPtr.Zero;
            InitializeProcThreadAttributeList(IntPtr.Zero, 1, 0, ref size);
            attributes = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(attributes, 1, 0, ref size)) return new RunResult(false, false, null);
            attributesReady = true;
            handles = Marshal.AllocHGlobal(3 * IntPtr.Size);
            Marshal.WriteIntPtr(handles, 0, input.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, IntPtr.Size, output.DangerousGetHandle());
            Marshal.WriteIntPtr(handles, 2 * IntPtr.Size, error.DangerousGetHandle());
            if (!UpdateProcThreadAttribute(attributes, 0, PROC_THREAD_ATTRIBUTE_HANDLE_LIST, handles, (IntPtr)(3 * IntPtr.Size), IntPtr.Zero, IntPtr.Zero))
                return new RunResult(false, false, null);

            var si = new STARTUPINFOEX();
            si.StartupInfo.cb = Marshal.SizeOf<STARTUPINFOEX>();
            si.StartupInfo.dwFlags = STARTF_USESTDHANDLES | STARTF_USESHOWWINDOW;
            si.StartupInfo.wShowWindow = SW_HIDE;
            si.StartupInfo.hStdInput = input.DangerousGetHandle();
            si.StartupInfo.hStdOutput = output.DangerousGetHandle();
            si.StartupInfo.hStdError = error.DangerousGetHandle();
            si.lpAttributeList = attributes;

            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job != IntPtr.Zero)
            {
                var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
                limits.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
                // A job that can't be set up is no job: the tree is stopped by walking it instead.
                if (!SetInformationJobObject(job, JobObjectExtendedLimitInformation, ref limits, Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>()))
                {
                    CloseHandle(job);
                    job = IntPtr.Zero;
                }
            }

            var line = new StringBuilder(commandLine);
            const uint flags = EXTENDED_STARTUPINFO_PRESENT | CREATE_NO_WINDOW | CREATE_SUSPENDED | CREATE_UNICODE_ENVIRONMENT;
            if (!CreateProcessW(app, line, IntPtr.Zero, IntPtr.Zero, true, flags, IntPtr.Zero, dir, ref si, out pi))
                return new RunResult(false, false, null);
            // In the job before it runs a single instruction, so nothing it starts can escape it.
            // A job that can't be used (an old Windows that refuses nested jobs): the tree is
            // stopped by walking it instead.
            if (job != IntPtr.Zero && !AssignProcessToJobObject(job, pi.hProcess))
            {
                CloseHandle(job);
                job = IntPtr.Zero;
            }
            ResumeThread(pi.hThread);
            // Its copies are its own now; ours go, so the output file is only the child's.
            input.Dispose();
            error.Dispose();
            output.Dispose();

            uint ms = (uint)Math.Clamp(timeout.TotalMilliseconds, 0, uint.MaxValue - 1);
            bool timedOut = WaitForSingleObject(pi.hProcess, ms) != WAIT_OBJECT_0;
            // Whatever is left: on a timeout the program itself, otherwise anything it left running.
            if (job == IntPtr.Zero || !TerminateJobObject(job, 1))
            {
                if (timedOut) KillTree(pi.dwProcessId);
            }
            if (timedOut) WaitForSingleObject(pi.hProcess, 5000);
            return new RunResult(true, timedOut, ReadOutput(outputFile));
        }
        catch (Exception) { return new RunResult(false, false, null); }
        finally
        {
            input?.Dispose();
            error?.Dispose();
            output?.Dispose();
            if (attributesReady) DeleteProcThreadAttributeList(attributes);
            if (attributes != IntPtr.Zero) Marshal.FreeHGlobal(attributes);
            if (handles != IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if (pi.hThread != IntPtr.Zero) CloseHandle(pi.hThread);
            if (pi.hProcess != IntPtr.Zero) CloseHandle(pi.hProcess);
            if (job != IntPtr.Zero) CloseHandle(job);
        }
    }

    static void KillTree(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
        }
        catch (Exception) { }
    }

    static string? ReadOutput(string file)
    {
        try
        {
            using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var data = new byte[(int)Math.Min(stream.Length, MaxOutputBytes)];
            int total = 0, n;
            while (total < data.Length && (n = stream.Read(data, total, data.Length - total)) > 0) total += n;
            return Encoding.UTF8.GetString(data, 0, total);
        }
        catch (Exception) { return null; }
    }

    const uint GENERIC_READ = 0x80000000, GENERIC_WRITE = 0x40000000;
    const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2;
    const uint OPEN_EXISTING = 3, CREATE_ALWAYS = 2, FILE_ATTRIBUTE_NORMAL = 0x80;
    const int STARTF_USESHOWWINDOW = 0x1, STARTF_USESTDHANDLES = 0x100;
    const short SW_HIDE = 0;
    const uint EXTENDED_STARTUPINFO_PRESENT = 0x80000, CREATE_NO_WINDOW = 0x08000000, CREATE_SUSPENDED = 0x4, CREATE_UNICODE_ENVIRONMENT = 0x400;
    static readonly IntPtr PROC_THREAD_ATTRIBUTE_HANDLE_LIST = (IntPtr)0x20002;
    const uint WAIT_OBJECT_0 = 0;
    const int JobObjectExtendedLimitInformation = 9;
    const uint JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE = 0x2000;

    [StructLayout(LayoutKind.Sequential)]
    struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        public int bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct STARTUPINFO
    {
        public int cb;
        public IntPtr lpReserved, lpDesktop, lpTitle;
        public int dwX, dwY, dwXSize, dwYSize, dwXCountChars, dwYCountChars, dwFillAttribute, dwFlags;
        public short wShowWindow, cbReserved2;
        public IntPtr lpReserved2, hStdInput, hStdOutput, hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct PROCESS_INFORMATION
    {
        public IntPtr hProcess, hThread;
        public int dwProcessId, dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct IO_COUNTERS
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount, ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, ref SECURITY_ATTRIBUTES security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list, int count, int flags, ref IntPtr size);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list, uint flags, IntPtr attribute, IntPtr value, IntPtr size, IntPtr previous, IntPtr returnSize);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern void DeleteProcThreadAttributeList(IntPtr list);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern bool CreateProcessW(string? application, StringBuilder commandLine, IntPtr processAttributes, IntPtr threadAttributes,
        bool inheritHandles, uint flags, IntPtr environment, string? directory, ref STARTUPINFOEX startup, out PROCESS_INFORMATION info);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern IntPtr CreateJobObjectW(IntPtr attributes, string? name);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref JOBOBJECT_EXTENDED_LIMIT_INFORMATION info, int length);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool TerminateJobObject(IntPtr job, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern uint WaitForSingleObject(IntPtr handle, uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    static extern bool CloseHandle(IntPtr handle);
}
