using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace LidAwake;

// What Claude Code runs (the Mac's ~/.claude/hooks/lidawake-claude-event.sh and
// ~/.claude/statusline-lidawake.py, which live outside the repo), as flags of LidAwake.exe itself,
// so nothing else (Python, a shell script) is needed. Main handles them before the single-instance
// check, so they never start a tray, and they always end quickly:
//   --claude-event        a hook (Stop, Notification): stdin (at most 64 KB) becomes a file in
//                         %LOCALAPPDATA%\LidAwake\claude-events for ClaudeAlerts. Always exit 0.
//   --claude-statusline   the status line: saves rate_limits to claude-usage.json and prints a
//                         short line ("Opus · session 59%"). Always exit 0.
//   --claude-setup on|off|status
//                         the same as the tray item "Claude alerts and usage from Claude Code…",
//                         for install.ps1 and uninstall.ps1 (ClaudeSettingsFile). Exit codes: see Setup.
public static class ClaudeHook
{
    public const string EventFlag = "--claude-event";
    public const string StatusLineFlag = "--claude-statusline";
    public const string SetupFlag = "--claude-setup";
    // Below the 64 KB the folder's reader accepts (ClaudeAlerts.MaxFileBytes).
    public const int MaxInputBytes = 64 * 1024 - 1;
    // Claude Code waits for its hooks; a stdin that never ends must not hold it up.
    public static readonly TimeSpan InputWait = TimeSpan.FromSeconds(5);

    // --claude-setup's exit codes.
    public const int SetUp = 0, NotSetUp = 1, NoClaudeCode = 2, Refused = 3;

    // null: not one of these flags (Lid Awake starts as usual). Otherwise the exit code.
    public static int? Run(string[] args)
    {
        if (args.Length == 0 || args[0] is not (EventFlag or StatusLineFlag or SetupFlag)) return null;
        EndStartingCursor();
        return Run(args, Console.OpenStandardInput, Console.OpenStandardOutput, ClaudeAlerts.DefaultFolder,
            Path.Combine(ClaudeUsage.DefaultFolder, ClaudeUsage.FileName), new ClaudeSettingsFile(), Environment.ProcessPath ?? "");
    }

    // LidAwake.exe is a windowed program, so each start shows the "app starting" pointer until it
    // reads its first message (or for some seconds). Claude Code runs the status line as often as
    // every 300 ms, which would keep the pointer busy all the time: post a WM_NULL to this thread
    // and read it once, and Windows drops the pointer at once. Costs a few microseconds.
    internal static void EndStartingCursor()
    {
        try
        {
            PeekMessageW(out _, IntPtr.Zero, 0, 0, PM_NOREMOVE);  // makes this thread's message queue
            if (PostThreadMessageW(GetCurrentThreadId(), WM_NULL, IntPtr.Zero, IntPtr.Zero))
                GetMessageW(out _, IntPtr.Zero, 0, 0);
        }
        catch (Exception) { }
    }

    const uint WM_NULL = 0, PM_NOREMOVE = 0;

    [StructLayout(LayoutKind.Sequential)]
    struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam, lParam;
        public uint time;
        public int x, y;
        public uint lPrivate;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern bool PeekMessageW(out MSG msg, IntPtr hwnd, uint min, uint max, uint remove);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern bool PostThreadMessageW(uint thread, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern uint GetCurrentThreadId();

    // The same, with its streams and places given (the tests).
    internal static int? Run(string[] args, Func<Stream> stdin, Func<Stream> stdout, string eventsFolder, string usageFile,
                             ClaudeSettingsFile settingsFile, string exe)
    {
        if (args.Length == 0) return null;
        void Print(string text)
        {
            try
            {
                using var output = stdout();
                output.Write(Encoding.UTF8.GetBytes(text + "\n"));
            }
            catch (Exception) { }
        }
        switch (args[0])
        {
            case EventFlag:
                try { SaveEvent(ReadInput(stdin()), eventsFolder, DateTimeOffset.UtcNow); }
                catch (Exception) { }
                return 0;
            case StatusLineFlag:
                string line;
                try { line = StatusLine(ReadInput(stdin()), usageFile, DateTimeOffset.UtcNow); }
                catch (Exception) { line = "Claude"; }
                Print(line);
                return 0;
            case SetupFlag:
                try { return Setup(args.Length > 1 ? args[1] : "status", settingsFile, exe, Print); }
                catch (Exception e)
                {
                    Print(e.Message);
                    return Refused;
                }
            default:
                return null;
        }
    }

    // status: 0 set up (our hooks are there), 1 not set up, 2 no Claude Code on this PC, 3 the file
    // can't be read. on / off: 0 done (or nothing to do), 3 refused (the reason is printed).
    public static int Setup(string what, ClaudeSettingsFile file, string exe, Action<string> print, Func<bool>? claudeInstalled = null)
    {
        switch (what)
        {
            case "on":
            {
                var r = file.Install(exe);
                print(r.Message);
                return r.Ok ? SetUp : Refused;
            }
            case "off":
            {
                var r = file.Remove();
                print(r.Message);
                return r.Ok ? SetUp : Refused;
            }
            default:
            {
                var state = file.State();
                if (state == ClaudeSettingsFile.Setup.Unreadable) return Refused;
                if (state == ClaudeSettingsFile.Setup.On) return SetUp;
                return (claudeInstalled ?? ClaudeCodeInstalled)() ? NotSetUp : NoClaudeCode;
            }
        }
    }

    // Claude Code is there: its program (ClaudeFinder's places) or its folder in the profile.
    public static bool ClaudeCodeInstalled()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        return ClaudeFinder.Candidates(profile, appData).Any(File.Exists) || Directory.Exists(Path.Combine(profile, ".claude"));
    }

    // All of stdin, or null when it is over the limit or doesn't end within a few seconds.
    public static byte[]? ReadInput(Stream input, int max = MaxInputBytes, TimeSpan? wait = null)
    {
        var read = Task.Run(() =>
        {
            var all = new MemoryStream();
            var chunk = new byte[8192];
            int n;
            while ((n = input.Read(chunk, 0, chunk.Length)) > 0)
            {
                if (all.Length + n > max) return null;
                all.Write(chunk, 0, n);
            }
            return all.ToArray();
        });
        try { return read.Wait(wait ?? InputWait) ? read.Result : null; }
        catch (Exception) { return null; }
    }

    // One file per event: written as .tmp, then renamed to .json, so the scanner never reads half
    // of one. Named by time first, so they are handled in order. No folder (Lid Awake has never
    // run, which makes it): nothing is written, as on the Mac.
    public static string? SaveEvent(byte[]? data, string folder, DateTimeOffset now)
    {
        if (data == null || data.Length == 0 || !Directory.Exists(folder)) return null;
        var name = now.UtcTicks.ToString("D19", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N");
        var tmp = Path.Combine(folder, name + ".tmp");
        var done = Path.Combine(folder, name + ".json");
        File.WriteAllBytes(tmp, data);
        File.Move(tmp, done);
        return done;
    }

    // The events folder is this user's only: inheritance from %LOCALAPPDATA% is cut, and the one
    // entry left gives this user full control (of the folder and what is created in it).
    public static void EnsurePrivateFolder(string folder)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(folder))!);
        using var me = WindowsIdentity.GetCurrent();
        var user = me.User ?? throw new InvalidOperationException("no user");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var dir = new DirectoryInfo(folder);
        if (!dir.Exists) dir.Create(security);
        else dir.SetAccessControl(security);
    }

    // The status line: Claude Code's JSON on stdin. Its rate_limits (five_hour, seven_day; each a
    // used_percentage and a resets_at in Unix seconds or as an ISO date) are saved for Lid Awake,
    // checked like the reader checks them, but only when Lid Awake's folder is there (as on the
    // Mac). Prints the model and the session's percent.
    public static string StatusLine(byte[]? input, string usageFile, DateTimeOffset now)
    {
        string model = "Claude";
        double? session = null;
        try
        {
            if (input != null && JsonNode.Parse(input) is JsonObject root)
            {
                if (root["model"] is JsonObject m && Text(m["display_name"]) is { Length: > 0 } name) model = name;
                if (root["rate_limits"] is JsonObject limits)
                {
                    var kept = new JsonObject();
                    foreach (var key in new[] { "five_hour", "seven_day" })
                    {
                        if (limits[key] is not JsonObject w || Number(w["used_percentage"]) is not { } pct
                            || !double.IsFinite(pct) || pct < 0 || pct > 1000) continue;
                        var window = new JsonObject { ["used_percentage"] = pct };
                        if (ResetsAt(w["resets_at"]) is { } at) window["resets_at"] = at;
                        kept[key] = window;
                        if (key == "five_hour") session = pct;
                    }
                    if (kept.Count > 0) ClaudeUsage.Save(usageFile, kept, now);
                }
            }
        }
        catch (Exception) { }
        var flat = string.Join(' ', model.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        if (flat.Length > 60) flat = flat[..60];
        return session is { } s ? $"{flat} · session {Math.Round(s, MidpointRounding.AwayFromZero).ToString(CultureInfo.InvariantCulture)}%" : flat;
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    static double? Number(JsonNode? node)
    {
        if (node is not JsonValue v) return null;
        if (v.TryGetValue<double>(out var d)) return d;
        if (v.TryGetValue<JsonElement>(out var e) && e.ValueKind == JsonValueKind.Number && e.TryGetDouble(out d)) return d;
        return null;
    }

    static long? ResetsAt(JsonNode? node)
    {
        if (Number(node) is { } d) return double.IsFinite(d) && d > 0 && d < 1e11 ? (long)d : null;
        if (Text(node) is { } s && DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at))
        {
            var unix = at.ToUnixTimeSeconds();
            return unix > 0 && unix < 100_000_000_000 ? unix : null;
        }
        return null;
    }
}

// Lid Awake's entries in Claude Code's settings file, %USERPROFILE%\.claude\settings.json: a
// `Stop` and a `Notification` hook running "<LidAwake.exe>" --claude-event, and a `statusLine`
// running "<LidAwake.exe>" --claude-statusline (only when there is no status line yet). Changed
// only when the owner asks (the tray item, or the installer's question), by a JSON merge: every
// other key and hook stays exactly as it was. An entry is ours only when its command is exactly
// our form, "<path ending in LidAwake.exe>" and the flag, so an owner's own wrapper or another
// tool's hook that mentions the flag is never changed or removed. Before each change the file is
// backed up (settings.json.lidawake-backup, then -YYYYmmdd-HHMMSS ones, never overwritten); it
// is written to a temp file first, and left alone (with the reason) when it can't be read: not
// UTF-8, not valid JSON, a key twice, too big, or hooks of an unexpected shape.
public sealed class ClaudeSettingsFile
{
    public const string BackupSuffix = ".lidawake-backup";
    public static readonly string[] Events = { "Stop", "Notification" };
    // Seconds Claude Code waits for our hook (it takes a fraction of one).
    public const int HookTimeout = 10;
    // A settings file bigger than this is not one to rewrite.
    public const int MaxBytes = 4 * 1024 * 1024;

    public enum Setup { Off, On, Unreadable }

    // Ok false: nothing changed, and Message says why. TheirStatusLine: theirs was kept.
    public readonly record struct Result(bool Ok, bool Changed, string Message, bool TheirStatusLine = false);

    // Where install.ps1 puts Lid Awake. Only a Lid Awake running from there moves our entries to
    // itself at start (a build or a ZIP copy elsewhere never takes them over).
    public static string InstalledExe =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LidAwake", "LidAwake.exe");

    readonly string path;
    readonly Func<DateTime> clock;

    public ClaudeSettingsFile(string? path = null, Func<DateTime>? clock = null)
    {
        this.path = path ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "settings.json");
        this.clock = clock ?? (() => DateTime.Now);
    }

    public string FilePath => path;

    // The command Claude Code runs: the absolute path in quotes, with forward slashes, which both
    // cmd.exe and Git Bash read the same way (PowerShell would need "& " before it: unverified).
    public static string Command(string exe, string flag) => "\"" + exe.Replace('\\', '/') + "\" " + flag;

    static readonly Regex OurEvent = OurForm(ClaudeHook.EventFlag);
    static readonly Regex OurStatusLine = OurForm(ClaudeHook.StatusLineFlag);

    // "<anything without a quote>\LidAwake.exe" (or /LidAwake.exe), a space, the flag, nothing else.
    static Regex OurForm(string flag) =>
        new("^\"[^\"]*[\\\\/](?i:LidAwake\\.exe)\" " + Regex.Escape(flag) + "$", RegexOptions.CultureInvariant);

    public static bool IsOurs(JsonNode? entry, string flag) =>
        entry is JsonObject o && o["command"] is JsonValue v && v.TryGetValue<string>(out var c)
        && (flag == ClaudeHook.StatusLineFlag ? OurStatusLine : OurEvent).IsMatch(c);

    // A path the shells would read differently ($, `, %, !, a quote) is never written.
    public static bool UsablePath(string exe) =>
        Path.IsPathFullyQualified(exe) && exe.IndexOfAny(new[] { '"', '$', '`', '%', '!' }) < 0;

    // On: both hooks of ours are there.
    public Setup State()
    {
        if (Load(out var root, out _) is { } failed) return failed.Ok ? Setup.Off : Setup.Unreadable;
        var hooks = root!["hooks"] as JsonObject;
        return Events.All(e => Ours(hooks?[e] as JsonArray, ClaudeHook.EventFlag).Any()) ? Setup.On : Setup.Off;
    }

    public Result Install(string exe)
    {
        if (!UsablePath(exe)) return new Result(false, false, $"Lid Awake's place ({exe}) can't be written into Claude Code's settings safely. Nothing was changed.");
        if (Load(out var root, out var raw) is { } failed && !failed.Ok) return failed;
        root ??= new JsonObject();
        var before = root.ToJsonString();
        var hooks = root["hooks"] as JsonObject;
        if (hooks == null) root["hooks"] = hooks = new JsonObject();
        var command = Command(exe, ClaudeHook.EventFlag);
        foreach (var e in Events)
        {
            var list = hooks[e] as JsonArray;
            if (list == null) hooks[e] = list = new JsonArray();
            var ours = Ours(list, ClaudeHook.EventFlag).ToList();
            foreach (var entry in ours) entry["command"] = command;  // an older place of LidAwake.exe
            if (ours.Count == 0)
                list.Add((JsonNode)new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command, ["timeout"] = HookTimeout }) });
        }
        bool theirs = false;
        var statusCommand = Command(exe, ClaudeHook.StatusLineFlag);
        if (!root.ContainsKey("statusLine")) root["statusLine"] = new JsonObject { ["type"] = "command", ["command"] = statusCommand };
        else if (IsOurs(root["statusLine"], ClaudeHook.StatusLineFlag)) root["statusLine"]!["command"] = statusCommand;
        else theirs = true;
        var note = theirs
            ? " Your own status line is kept, so the usage numbers come from Lid Awake's /usage check instead."
            : "";
        if (root.ToJsonString() == before)
            return new Result(true, false, "Claude Code is already set up for Lid Awake." + note, theirs);
        if (Save(root, raw, out var backup) is { } notSaved) return notSaved;
        var copy = backup != null ? $" (a copy of the old file is in {backup})" : "";
        return new Result(true, true, $"Added Lid Awake's hooks and status line to {path}{copy}." + note, theirs);
    }

    // At start: our entries follow LidAwake.exe to a new place, but only for the installed copy
    // (`installed`, by default InstalledExe), and only entries exactly of our form. Nothing is
    // added: what the owner removed stays removed.
    public Result Update(string exe, string? installed = null)
    {
        if (!UsablePath(exe)) return new Result(false, false, "unusable path");
        if (!string.Equals(Path.GetFullPath(exe), Path.GetFullPath(installed ?? InstalledExe), StringComparison.OrdinalIgnoreCase))
            return new Result(true, false, "Not the installed Lid Awake: nothing changed.");
        if (Load(out var root, out var raw) is { } failed) return failed;
        var before = root!.ToJsonString();
        var command = Command(exe, ClaudeHook.EventFlag);
        if (root["hooks"] is JsonObject hooks)
            foreach (var e in Events)
                foreach (var entry in Ours(hooks[e] as JsonArray, ClaudeHook.EventFlag)) entry["command"] = command;
        if (IsOurs(root["statusLine"], ClaudeHook.StatusLineFlag)) root["statusLine"]!["command"] = Command(exe, ClaudeHook.StatusLineFlag);
        if (root.ToJsonString() == before) return new Result(true, false, "Nothing to change.");
        return Save(root, raw, out _) ?? new Result(true, true, $"Lid Awake's place updated in {path}.");
    }

    // Exactly what Install added: our hooks, a group or list they leave empty, an empty "hooks",
    // and our status line. Anything else stays.
    public Result Remove()
    {
        if (Load(out var root, out var raw) is { } failed) return failed.Ok ? new Result(true, false, "Claude Code has no settings file: nothing to remove.") : failed;
        var before = root!.ToJsonString();
        if (root["hooks"] is JsonObject hooks)
        {
            bool removedAny = false;
            foreach (var e in Events)
            {
                if (hooks[e] is not JsonArray list) continue;
                bool removedHere = false;
                for (int g = list.Count - 1; g >= 0; g--)
                {
                    if (list[g] is not JsonObject group || group["hooks"] is not JsonArray entries) continue;
                    bool fromGroup = false;
                    for (int i = entries.Count - 1; i >= 0; i--)
                        if (IsOurs(entries[i], ClaudeHook.EventFlag)) { entries.RemoveAt(i); fromGroup = true; }
                    if (!fromGroup) continue;
                    removedHere = true;
                    if (entries.Count == 0) list.RemoveAt(g);
                }
                if (removedHere && list.Count == 0) hooks.Remove(e);
                removedAny |= removedHere;
            }
            if (removedAny && hooks.Count == 0) root.Remove("hooks");
        }
        if (IsOurs(root["statusLine"], ClaudeHook.StatusLineFlag)) root.Remove("statusLine");
        if (root.ToJsonString() == before) return new Result(true, false, "Claude Code has none of Lid Awake's hooks: nothing to remove.");
        return Save(root, raw, out _) ?? new Result(true, true, $"Removed Lid Awake's hooks and status line from {path}.");
    }

    static IEnumerable<JsonObject> Ours(JsonArray? list, string flag)
    {
        if (list == null) yield break;
        foreach (var group in list)
            if (group is JsonObject g && g["hooks"] is JsonArray entries)
                foreach (var entry in entries)
                    if (IsOurs(entry, flag)) yield return (JsonObject)entry!;
    }

    // A backup of the file as it was (when there was one), then the new text. null: saved.
    Result? Save(JsonObject root, byte[]? raw, out string? backup)
    {
        backup = null;
        var real = RealPath();
        if (raw != null)
        {
            try { backup = Backup(raw); }
            catch (Exception e) { return new Result(false, false, $"Couldn't back up {path}: {e.Message} Nothing was changed."); }
        }
        return Write(real, root) ? null : new Result(false, false, $"Couldn't write {path}. Nothing was changed.");
    }

    // settings.json.lidawake-backup the first time, then settings.json.lidawake-backup-YYYYmmdd-HHMMSS
    // (the Mac's setup.py); a file that is already there is never overwritten.
    string Backup(byte[] raw)
    {
        var first = path + BackupSuffix;
        var stamped = first + clock().ToString("-yyyyMMdd-HHmmss", CultureInfo.InvariantCulture);
        foreach (var candidate in new[] { first, stamped }.Concat(Enumerable.Range(2, 98).Select(n => stamped + "-" + n)))
        {
            try
            {
                using var f = new FileStream(candidate, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                f.Write(raw);
                return candidate;
            }
            catch (IOException) when (File.Exists(candidate) || Directory.Exists(candidate)) { }
        }
        throw new IOException("too many backups already");
    }

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // null with `root` set: loaded (and `raw`, the bytes as they were). A Result with Ok true:
    // there is no file (root null). Ok false: it can't be used, and why.
    Result? Load(out JsonObject? root, out byte[]? raw)
    {
        root = null;
        raw = null;
        var real = RealPath();
        if (!File.Exists(real)) return new Result(true, false, "no file");
        byte[] bytes;
        try
        {
            if (new FileInfo(real).Length > MaxBytes) return new Result(false, false, $"{path} is too big to change, so Lid Awake left it alone.");
            bytes = File.ReadAllBytes(real);
        }
        catch (Exception e) { return new Result(false, false, $"Couldn't read {path}: {e.Message}"); }
        string text;
        try
        {
            int start = bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF ? 3 : 0;
            text = StrictUtf8.GetString(bytes, start, bytes.Length - start);
        }
        catch (DecoderFallbackException) { return new Result(false, false, $"{path} isn't UTF-8 text, so Lid Awake left it alone."); }
        JsonNode? node;
        try
        {
            using (var doc = JsonDocument.Parse(text))
                if (HasKeyTwice(doc.RootElement)) return new Result(false, false, $"{path} has a key twice, so Lid Awake left it alone.");
            node = JsonNode.Parse(text);
        }
        catch (JsonException e)
        {
            return new Result(false, false, $"{path} isn't valid JSON ({e.Message}), so Lid Awake left it alone. Fix it in Claude Code (or an editor) and try again.");
        }
        catch (ArgumentException) { return new Result(false, false, $"{path} has a key twice, so Lid Awake left it alone."); }
        catch (InvalidOperationException) { return new Result(false, false, $"{path} has a key twice, so Lid Awake left it alone."); }
        if (node is not JsonObject o) return new Result(false, false, $"{path} isn't a JSON object, so Lid Awake left it alone.");
        if (o.ContainsKey("hooks") && o["hooks"] is not JsonObject)
            return new Result(false, false, $"\"hooks\" in {path} isn't an object, so Lid Awake left the file alone.");
        if (o["hooks"] is JsonObject h)
            foreach (var e in Events)
                if (h.ContainsKey(e) && h[e] is not JsonArray)
                    return new Result(false, false, $"\"hooks\".\"{e}\" in {path} isn't a list, so Lid Awake left the file alone.");
        root = o;
        raw = bytes;
        return null;
    }

    // The same key twice in one object (JSON allows it; which one counts is anyone's guess).
    static bool HasKeyTwice(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var p in e.EnumerateObject())
                if (!seen.Add(p.Name) || HasKeyTwice(p.Value)) return true;
        }
        else if (e.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in e.EnumerateArray())
                if (HasKeyTwice(item)) return true;
        }
        return false;
    }

    // A settings file that is a link (dotfiles) is changed where it really is.
    string RealPath()
    {
        try { return new FileInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? path; }
        catch (Exception) { return path; }
    }

    static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    static bool Write(string file, JsonObject root)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
            return HistoryStore.WriteAtomic(file, root.ToJsonString(Pretty) + Environment.NewLine);
        }
        catch (Exception) { return false; }
    }
}
