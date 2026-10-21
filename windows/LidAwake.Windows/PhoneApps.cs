using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LidAwake;

// A program: the name shown on the phone, and the exe that starts it. Also what is stored for the
// apps the owner picked ("Apps on phone…" in the tray), in the roaming settings.
public sealed record StartApp(string Name, string Exe);

// A program running right now. `Exe` is the full path of its exe, `HasWindow` is true when it has
// an ordinary visible window (the phone's dock), `IsSelf` is Lid Awake itself.
public sealed record RunningApp(string Exe, string Name, bool HasWindow, bool IsSelf = false);

// The Start menu's programs, resolved to their exe (see StartMenuScanner).
public interface IStartMenuSource
{
    IReadOnlyList<StartApp> Scan();
}

// What a .lnk shortcut points to (see ShortcutResolver). null: no target, or it can't be read.
public interface IShortcutResolver
{
    string? Target(string shortcutPath);
}

// The programs running now (see ProcessSource).
public interface IProcessSource
{
    IReadOnlyList<RunningApp> Running();
}

// NotOpen: no process of that exe. Asked: a close request went to its main window (it may still
// ask to save). NoWindow: running, but with no main window to ask (a tray-only program).
public enum QuitResult { NotOpen, Asked, NoWindow }

// What can be done to a program (see WindowActions). Quit only ever asks politely; nothing kills a process.
public interface IWindowActions
{
    // Starts the exe, as double-clicking it would. Throws when it can't.
    void Start(string exe);
    // Brings its window to the front (restoring it if minimized). false: it has no window.
    bool Focus(string exe);
    // Minimizes its window. false: it has no window.
    bool Hide(string exe);
    QuitResult Quit(string exe);
}

// An exe's icon as a square PNG of `size` pixels (see WindowsIcons). null: no icon.
public interface IAppIcons
{
    byte[]? Png(string exe, int size);
}

// The apps shown on the phone's Apps page (PhoneApps.swift). The owner picks them in the tray; the
// phone only ever sends an id, never a path (docs/API.md 5.6): every route looks the id up here, in
// the picked list or among the running programs, and an id that matches nothing is refused.
// Called from the web server's threads and from the tray, so the picked list is under a lock.
public sealed class PhoneApps
{
    public const string SettingsKey = "phoneApps";
    public const int SmallIcon = 256;
    public const int BigIcon = 512;
    // How long one look at the running programs is reused: several phones poll /status every 3
    // seconds, and a scan walks every process and every window.
    public const long ScanTtlMilliseconds = 2000;

    readonly Settings settings;
    readonly IProcessSource processes;
    readonly IWindowActions actions;
    readonly IAppIcons icons;
    readonly AccessLog? log;
    readonly Func<string, bool> exists;
    readonly Func<long> clock;
    readonly object gate = new();
    readonly object scanGate = new();
    IReadOnlyList<RunningApp>? scan;
    long scannedAt;
    readonly ConcurrentDictionary<string, byte[]> iconCache = new();

    // `exists` tells whether an exe is still there (apps that were uninstalled are left out).
    public PhoneApps(Settings settings, IProcessSource processes, IWindowActions actions, IAppIcons icons,
                     AccessLog? log = null, Func<string, bool>? exists = null, Func<long>? clock = null)
    {
        this.clock = clock ?? (() => Environment.TickCount64);
        this.settings = settings;
        this.processes = processes;
        this.actions = actions;
        this.icons = icons;
        this.log = log;
        this.exists = exists ?? File.Exists;
    }

    // ---- ids ----

    // The full path of an exe, so one program has one spelling.
    public static string Normalize(string exe)
    {
        try { return Path.GetFullPath(exe); }
        catch (Exception) { return exe; }
    }

    // A short id that stays the same for the same program, so a tap can't open the wrong one: the
    // first 6 bytes of SHA-256 of its path as 12 hex characters, like the Mac. Windows paths ignore
    // upper and lower case, so the path is made lower case first.
    public static string Id(string exe)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(Normalize(exe).ToLowerInvariant()));
        return Convert.ToHexString(hash.AsSpan(0, 6)).ToLowerInvariant();
    }

    static bool SamePath(string a, string b) =>
        string.Equals(Normalize(a), Normalize(b), StringComparison.OrdinalIgnoreCase);

    // ---- the owner's picks ----

    // The picked apps, in order. A damaged entry is ignored.
    public IReadOnlyList<StartApp> Picks()
    {
        lock (gate) return Load();
    }

    List<StartApp> Load()
    {
        try
        {
            var stored = settings.Get<List<StartApp>>(SettingsKey) ?? new List<StartApp>();
            return stored.Where(a => a != null && !string.IsNullOrWhiteSpace(a.Exe)).ToList();
        }
        catch (JsonException) { return new List<StartApp>(); }
    }

    // Replaces the list (the tray's checklist). The same program twice is kept once.
    public void SetPicks(IEnumerable<StartApp> picks)
    {
        var list = new List<StartApp>();
        var seen = new HashSet<string>();
        foreach (var p in picks)
        {
            var exe = Normalize(p.Exe);
            var name = string.IsNullOrWhiteSpace(p.Name) ? Path.GetFileNameWithoutExtension(exe) : p.Name;
            if (seen.Add(Id(exe))) list.Add(new StartApp(name, exe));
        }
        lock (gate) settings.Set(SettingsKey, list);
    }

    StartApp? Listed(string id)
    {
        if (id.Length == 0) return null;
        return Picks().FirstOrDefault(p => Id(p.Exe) == id);
    }

    // ---- what is running ----

    // One look at the running programs, reused for ScanTtlMilliseconds. The lock is held during the
    // scan, so phones asking at the same time share one scan instead of each making their own.
    IReadOnlyList<RunningApp> Running()
    {
        lock (scanGate)
        {
            var now = clock();
            if (scan != null && now - scannedAt >= 0 && now - scannedAt < ScanTtlMilliseconds) return scan;
            IReadOnlyList<RunningApp> found;
            try { found = processes.Running(); }
            catch (Exception) { found = Array.Empty<RunningApp>(); }
            scan = found;
            scannedAt = now;
            return found;
        }
    }

    // After an open, front, hide or quit: the next status looks again.
    void Rescan()
    {
        lock (scanGate) scan = null;
    }

    // A running program with an ordinary visible window, by its id, never Lid Awake itself. The Mac
    // matches every running app; here a program with no window (explorer's background processes,
    // services, shell hosts) is not reachable, so the phone cannot compute an id for any process.
    RunningApp? RunningFor(string id)
    {
        if (id.Length == 0) return null;
        return Running().FirstOrDefault(r => r.HasWindow && !r.IsSelf && Id(r.Exe) == id);
    }

    // ---- /status ----

    // `apps`: the picked apps, in order, with whether each is running. One that was uninstalled
    // since is left out.
    public List<Dictionary<string, object>> List() => Lists().Apps;

    // Both lists of one status from a single look at the running programs.
    public (List<Dictionary<string, object>> Apps, List<Dictionary<string, object>> Others) Lists()
    {
        var running = Running();
        var picks = Picks();
        return (BuildList(picks, running), BuildOthers(picks, running));
    }

    List<Dictionary<string, object>> BuildList(IReadOnlyList<StartApp> picks, IReadOnlyList<RunningApp> now)
    {
        var running = new HashSet<string>(now.Select(r => Normalize(r.Exe)), StringComparer.OrdinalIgnoreCase);
        return picks
            .Where(p => Safe(() => exists(p.Exe)))
            .Select(p => new Dictionary<string, object>
            {
                ["id"] = Id(p.Exe),
                ["name"] = p.Name ?? "",
                ["running"] = running.Contains(Normalize(p.Exe)),
            })
            .ToList();
    }

    // `otherApps`: programs with an ordinary visible window that are not picked (the phone's dock),
    // once each, never Lid Awake itself.
    public List<Dictionary<string, object>> Others() => Lists().Others;

    static List<Dictionary<string, object>> BuildOthers(IReadOnlyList<StartApp> picks, IReadOnlyList<RunningApp> now)
    {
        var mine = new HashSet<string>(picks.Select(p => Id(p.Exe)));
        var seen = new HashSet<string>();
        var result = new List<Dictionary<string, object>>();
        foreach (var r in now)
        {
            if (!r.HasWindow || r.IsSelf) continue;
            var id = Id(r.Exe);
            if (mine.Contains(id) || !seen.Add(id)) continue;
            result.Add(new Dictionary<string, object> { ["id"] = id, ["name"] = r.Name });
        }
        return result;
    }

    static bool Safe(Func<bool> check)
    {
        try { return check(); }
        catch (Exception) { return false; }
    }

    // ---- the routes ----

    // GET /app-icon.png: for a picked app, or any program running now. `size` of 512 or more gives
    // 512, anything else 256. null: no such app (404).
    public byte[]? IconPng(string id, int size)
    {
        var exe = Listed(id)?.Exe ?? RunningFor(id)?.Exe;
        if (exe == null) return null;
        int px = size >= BigIcon ? BigIcon : SmallIcon;
        var key = Normalize(exe).ToLowerInvariant() + "#" + px;
        if (iconCache.TryGetValue(key, out var cached)) return cached;
        byte[]? png;
        try { png = icons.Png(exe, px); }
        catch (Exception) { png = null; }
        if (png == null) return null;
        iconCache[key] = png;
        return png;
    }

    // POST /open-app: only a picked app. false: not on the list (404). A program that is already
    // open with a window is brought forward instead of started a second time.
    public bool Open(string id, string device)
    {
        var app = Listed(id);
        if (app == null) return false;
        bool shown = Running().Any(r => r.HasWindow && SamePath(r.Exe, app.Exe)) && Try(() => actions.Focus(app.Exe));
        if (!shown)
        {
            try { actions.Start(app.Exe); }
            catch (Exception e)
            {
                Rescan();
                log?.Write($"Couldn't open {app.Name}: {e.Message} · {device}");
                return true;
            }
        }
        Rescan();
        log?.Write($"Opened {app.Name} from the phone · {device}");
        return true;
    }

    // POST /focus-app: any program running now. false: not open (404). "focus" fronts it, "hide"
    // minimizes it, "quit" asks it to close.
    public bool Focus(string id, string action, string device)
    {
        var app = RunningFor(id);
        if (app == null) return false;
        var name = Listed(id)?.Name ?? app.Name;
        bool done = action switch
        {
            "hide" => Try(() => actions.Hide(app.Exe)),
            "quit" => Try(() => actions.Quit(app.Exe) == QuitResult.Asked),
            _ => Try(() => actions.Focus(app.Exe)),
        };
        Rescan();
        if (!done) return false;
        var what = action == "hide" ? $"Hid {name}" : action == "quit" ? $"Quit {name}" : $"Brought {name} to the front";
        log?.Write($"{what} from the phone · {device}");
        return true;
    }

    // POST /quit-app: only a picked app. null: not on the list (404). false: it isn't open, or it
    // has no window to ask (409). true: asked to close, the polite way (CloseMainWindow, never a kill).
    public bool? Quit(string id, string device)
    {
        var app = Listed(id);
        if (app == null) return null;
        QuitResult result;
        try { result = actions.Quit(app.Exe); }
        catch (Exception) { result = QuitResult.NoWindow; }
        Rescan();
        if (result != QuitResult.Asked) return false;
        log?.Write($"Quit {app.Name} from the phone · {device}");
        return true;
    }

    static bool Try(Func<bool> action)
    {
        try { return action(); }
        catch (Exception) { return false; }
    }

    // POST /apps-order: unknown ids are ignored, and picked apps not mentioned keep their place at the end.
    public void Reorder(IEnumerable<string> ids)
    {
        lock (gate)
        {
            var rest = Load();
            var ordered = new List<StartApp>();
            foreach (var id in ids)
            {
                var i = rest.FindIndex(p => Id(p.Exe) == id);
                if (i < 0) continue;
                ordered.Add(rest[i]);
                rest.RemoveAt(i);
            }
            ordered.AddRange(rest);
            settings.Set(SettingsKey, ordered);
        }
    }
}
