using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace LidAwake;

// Phone alerts for Claude Code sessions on this PC (the Mac's ClaudeAlerts.swift): "Claude
// finished" when a session is done replying, and "Claude needs you" when it asks for permission or
// waits for an answer. Claude Code's hooks run `LidAwake.exe --claude-event` (ClaudeHook.cs), which
// drops each event as a file into %LOCALAPPDATA%\LidAwake\claude-events (this user's only); this
// picks them up every 2 seconds, so nothing new listens on the network. Alerts only go out while
// the owner is away from the PC (no keyboard or mouse for 2 minutes, or the lid closed), at most
// one every 20 seconds per session. Every file is deleted once read.
public sealed class ClaudeAlerts : IDisposable
{
    public static readonly TimeSpan ScanEvery = TimeSpan.FromSeconds(2);
    public const double AwayAfterSeconds = 120;
    public static readonly TimeSpan PerSession = TimeSpan.FromSeconds(20);
    // "Still waiting" right after "finished" adds nothing.
    public static readonly TimeSpan WaitingAfterFinished = TimeSpan.FromMinutes(10);
    public const int MaxFileBytes = 64 * 1024;
    public static readonly TimeSpan Stale = TimeSpan.FromMinutes(5);
    public const int MaxPerScan = 50;
    // Only the end of a transcript is read.
    public const int TranscriptTail = 262_144;

    public static string DefaultFolder => Path.Combine(ClaudeUsage.DefaultFolder, "claude-events");
    public static string DefaultProjects =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "projects");

    readonly string folder;
    readonly string projects;
    readonly Func<string, string, Task> send;
    readonly Func<double> idleSeconds;
    readonly Func<bool> lidClosed;
    readonly Func<DateTimeOffset> now;
    readonly Action<string>? log;
    readonly object gate = new();
    readonly Dictionary<string, DateTimeOffset> lastAlert = new();     // per session
    readonly Dictionary<string, DateTimeOffset> lastFinished = new();  // per session, for "waiting" after "finished"
    System.Threading.Timer? timer;
    int scanning;

    // `send(title, body)` sends a claudeSession alert. `idleSeconds` is the time since the last
    // keyboard or mouse input (default GetLastInputInfo), `lidClosed` LidScreen.LidClosed. `log`
    // gets the alerts sent and the ones held back as too soon (not the many while the owner is there).
    public ClaudeAlerts(Func<string, string, Task> send, Func<bool> lidClosed, Func<double>? idleSeconds = null,
                        string? folder = null, string? projects = null, Func<DateTimeOffset>? now = null, Action<string>? log = null)
    {
        this.send = send;
        this.lidClosed = lidClosed;
        this.idleSeconds = idleSeconds ?? IdleTime.Seconds;
        this.folder = folder ?? DefaultFolder;
        this.projects = projects ?? DefaultProjects;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        this.log = log;
    }

    public void Start()
    {
        try { ClaudeHook.EnsurePrivateFolder(folder); }
        catch (Exception) { }
        timer = new System.Threading.Timer(_ => Scan(), null, ScanEvery, ScanEvery);
    }

    public void Dispose() => timer?.Dispose();

    // The oldest 50 files, each deleted once read. Too big, older than 5 minutes, or not a JSON
    // object: deleted unread. One scan at a time.
    public void Scan()
    {
        if (Interlocked.Exchange(ref scanning, 1) == 1) return;
        try
        {
            string[] files;
            try
            {
                files = Directory.GetFiles(folder, "*.json")
                    .Where(f => string.Equals(Path.GetExtension(f), ".json", StringComparison.OrdinalIgnoreCase))
                    .OrderBy(f => Path.GetFileName(f), StringComparer.Ordinal).Take(MaxPerScan).ToArray();
            }
            catch (Exception) { return; }
            foreach (var file in files)
            {
                try
                {
                    var info = new FileInfo(file);
                    if (info.Length >= MaxFileBytes || now() - new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero) >= Stale) continue;
                    using var doc = JsonDocument.Parse(File.ReadAllBytes(file));
                    if (doc.RootElement.ValueKind == JsonValueKind.Object) Handle(doc.RootElement);
                }
                catch (Exception) { }
                finally
                {
                    try { File.Delete(file); }
                    catch (Exception) { }
                }
            }
        }
        finally { Volatile.Write(ref scanning, 0); }
    }

    // One event, as on the Mac. Returns what was decided (for the tests).
    public string Handle(JsonElement e)
    {
        lock (gate)
        {
            var session = Str(e, "session_id") is { } id ? (id.Length > 64 ? id[..64] : id) : "?";
            var project = Str(e, "cwd") is { } cwd && Path.GetFileName(cwd.TrimEnd('\\', '/')) is { Length: > 0 } leaf ? leaf : "Claude";
            var t = now();
            string title, body;
            switch (Str(e, "hook_event_name"))
            {
                case "Stop":
                    if (e.TryGetProperty("stop_hook_active", out var active) && active.ValueKind == JsonValueKind.True) return "skipped: hook re-run";
                    lastFinished[session] = t;
                    title = "Claude finished";
                    body = LastReply(Str(e, "transcript_path")) is { } said ? $"{project}: {said}" : $"{project} is done.";
                    break;
                case "Notification":
                    var message = Clean(Str(e, "message") ?? "", 140);
                    var type = Str(e, "notification_type") ?? "";
                    bool waiting = type == "idle_prompt" || message.Contains("waiting for your input", StringComparison.OrdinalIgnoreCase);
                    if (waiting)
                    {
                        if (lastFinished.TryGetValue(session, out var done) && t - done < WaitingAfterFinished) return "skipped: already said finished";
                        title = "Claude is waiting for you";
                    }
                    else if (type == "permission_prompt" || message.Contains("permission", StringComparison.OrdinalIgnoreCase))
                        title = "Claude needs your OK";
                    else if (type.Length == 0 || type == "elicitation_dialog")
                        title = "Claude needs you";
                    else
                        return "skipped: not a call for you";  // e.g. "signed in"
                    body = message.Length == 0 ? project : $"{project}: {message}";
                    break;
                default:
                    return "skipped: other event";
            }
            if (!OwnerAway()) return "skipped: you were at the PC";
            if (lastAlert.TryGetValue(session, out var last) && t - last < PerSession)
            {
                log?.Invoke($"Claude alert held back: \"{title}\" came too soon after the last one");
                return "skipped: too soon after the last one";
            }
            lastAlert[session] = t;
            log?.Invoke($"Claude alert sent to the phones: \"{title}\"");
            try { _ = send(title, body); }
            catch (Exception) { }
            return "sent: " + title + " | " + body;
        }
    }

    // Away: no keyboard, mouse or touch for 2 minutes, or the lid closed.
    bool OwnerAway()
    {
        double idle;
        try { idle = idleSeconds(); }
        catch (Exception) { idle = 0; }
        bool closed;
        try { closed = lidClosed(); }
        catch (Exception) { closed = false; }
        return idle > AwayAfterSeconds || closed;
    }

    static string? Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // The start of Claude's last reply, from the session's own transcript: only a .jsonl file that
    // really is under %USERPROFILE%\.claude\projects (links followed, compared ignoring case), and
    // only the end of it. The file is read through the handle that was checked.
    internal string? LastReply(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        try
        {
            if (FinalPath.Of(projects, directory: true) is not { } root) return null;
            using var handle = FinalPath.Open(path, out var real);
            if (handle == null || real == null) return null;
            if (!real.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase)
                || !real.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase)) return null;
            using var stream = new FileStream(handle, FileAccess.Read);
            long size = stream.Length;
            stream.Seek(size > TranscriptTail ? size - TranscriptTail : 0, SeekOrigin.Begin);
            using var reader = new StreamReader(stream, new UTF8Encoding(false, false));
            var text = reader.ReadToEnd();
            foreach (var line in Enumerable.Reverse(text.Split('\n')))
            {
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var entry = doc.RootElement;
                    if (entry.ValueKind != JsonValueKind.Object || Str(entry, "type") != "assistant"
                        || !entry.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object
                        || !message.TryGetProperty("content", out var content) || content.ValueKind != JsonValueKind.Array) continue;
                    var said = string.Join(" ", content.EnumerateArray()
                        .Where(c => c.ValueKind == JsonValueKind.Object && Str(c, "type") == "text" && Str(c, "text") != null)
                        .Select(c => Str(c, "text")!));
                    var shortText = Clean(said.Replace("**", "").Replace("`", ""), 110);
                    if (shortText.Length > 0) return shortText;
                }
                catch (JsonException) { }
            }
        }
        catch (Exception) { }
        return null;
    }

    // One plain line, cut at a word near `limit` (counted in characters as a person sees them).
    public static string Clean(string text, int limit)
    {
        var flat = string.Join(' ', text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        var info = new StringInfo(flat);
        if (info.LengthInTextElements <= limit) return flat;
        var cut = info.SubstringByTextElements(0, limit);
        int space = cut.LastIndexOf(' ');
        return (space >= 0 ? cut[..space] : cut) + "…";
    }
}

// Seconds since the last keyboard, mouse or touch input in this session (GetLastInputInfo).
public static class IdleTime
{
    [StructLayout(LayoutKind.Sequential)]
    struct LASTINPUTINFO
    {
        public uint cbSize;
        public uint dwTime;
    }

    [DllImport("user32.dll", ExactSpelling = true)]
    static extern bool GetLastInputInfo(ref LASTINPUTINFO info);

    // 0 when Windows can't tell (then the owner counts as present).
    public static double Seconds()
    {
        var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
        if (!GetLastInputInfo(ref info)) return 0;
        uint idle = unchecked((uint)Environment.TickCount - info.dwTime);
        return idle / 1000.0;
    }
}

// Where a file or folder really is, links and junctions followed (the Mac's realpath), from the
// open handle (GetFinalPathNameByHandle).
static class FinalPath
{
    const uint GENERIC_READ = 0x80000000, FILE_READ_ATTRIBUTES = 0x80;
    const uint FILE_SHARE_ALL = 1 | 2 | 4, OPEN_EXISTING = 3, FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint creation, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern uint GetFinalPathNameByHandleW(SafeFileHandle file, [Out] char[] path, uint length, uint flags);

    // A file opened for reading (not a folder), and its real path; null when it can't be opened.
    public static SafeFileHandle? Open(string path, out string? real)
    {
        real = null;
        var handle = CreateFileW(path, GENERIC_READ, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING, 0, IntPtr.Zero);
        if (handle.IsInvalid) { handle.Dispose(); return null; }
        real = Name(handle);
        if (real == null) { handle.Dispose(); return null; }
        return handle;
    }

    public static string? Of(string path, bool directory)
    {
        using var handle = CreateFileW(path, FILE_READ_ATTRIBUTES, FILE_SHARE_ALL, IntPtr.Zero, OPEN_EXISTING,
            directory ? FILE_FLAG_BACKUP_SEMANTICS : 0, IntPtr.Zero);
        return handle.IsInvalid ? null : Name(handle);
    }

    static string? Name(SafeFileHandle handle)
    {
        var buffer = new char[1024];
        uint n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
        if (n == 0) return null;
        if (n >= buffer.Length)
        {
            buffer = new char[n + 1];
            n = GetFinalPathNameByHandleW(handle, buffer, (uint)buffer.Length, 0);
            if (n == 0 || n >= buffer.Length) return null;
        }
        var s = new string(buffer, 0, (int)n);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.Ordinal)) return @"\\" + s[8..];
        return s.StartsWith(@"\\?\", StringComparison.Ordinal) ? s[4..] : s;
    }
}
