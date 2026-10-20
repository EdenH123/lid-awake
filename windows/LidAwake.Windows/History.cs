using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml;

namespace LidAwake;

// One minute of the PC's life, as the graphs on the phone need it (docs/API.md 5.7). Windows has no
// readable power log like the Mac's, so Lid Awake records these itself, while it runs.
// Percent: null on a PC with no battery. Temp: the battery's own sensor only (null: none).
// Cpu: percent busy over the Span seconds before T (null and 0: not measured, for example the
// first sample, or after the PC slept). Apps: processor-seconds of the busiest programs in that
// stretch, by CpuApp key (null or empty: none listed, for example rows from before per-app
// history). Backfilled: the battery level came from Windows' battery report afterwards (the PC
// was asleep or Lid Awake wasn't running), not from a reading of Lid Awake's own.
public readonly record struct HistorySample(long T, int? Percent, bool Plugged, bool Charging, double? Temp, double? Cpu, int Span,
                                            IReadOnlyDictionary<string, double>? Apps = null, bool Backfilled = false);

// Who used processor time: a program (Kind "app", Exe its exe, Key PhoneApps.Id of it), or Windows'
// own processes and those Windows won't name (Kind "other", Exe null). Color: the colour that
// stands out in the program's icon, "#rrggbb" (null: grey).
public sealed record CpuApp(string Key, string Name, string Kind, string? Exe = null, string? Color = null);

// A battery level from Windows' battery report (BatteryReport).
public readonly record struct BatteryPoint(long T, int Percent, bool Plugged, bool Charging);

// The recorded samples: the last 48 hours, in memory and in %LOCALAPPDATA%\LidAwake\history.json,
// and the answers of the history routes. Safe to use from any thread. It never throws: a file that
// can't be read starts empty, a write that fails is kept in memory only (tried again with the
// next sample).
public sealed class HistoryStore
{
    public const string FileName = "history.json";
    // What the routes keep and clamp to, as on the Mac (PowerHistory.query).
    public const int KeepHours = 48;
    public const double DefaultHours = 24;
    // A battery-report level within this many seconds of a row is not added (the row is there).
    public const int BackfillGapSeconds = 90;
    // /cpu-icon: the size the Mac sends, how many are kept, and the most one may weigh.
    public const int IconSize = 256;
    public const int MaxIcons = 64;
    public const int MaxIconBytes = 1 << 20;
    const int FileVersion = 2;
    const int MaxKeyLength = 64;
    const int MaxAppsPerRow = 32;
    const int FlagBackfilled = 1;

    readonly string path;
    readonly Func<long> clock;
    readonly object gate = new();
    readonly List<HistorySample> samples = new();
    readonly Dictionary<string, CpuApp> apps = new();  // every key the rows name; under `gate`
    readonly ConcurrentDictionary<string, byte[]> icons = new();

    // `clock` is Unix seconds (tests set their own). `cores` is what /cpu-history reports.
    public HistoryStore(string? folder = null, Func<long>? clock = null, int? cores = null)
    {
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LidAwake");
        path = Path.Combine(folder, FileName);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Cores = Math.Max(1, cores ?? Environment.ProcessorCount);
        try { Directory.CreateDirectory(folder); }
        catch (Exception) { }  // no folder: the history lives in memory only
        var (rows, table) = Load(path, Now() - KeepHours * 3600L);
        samples.AddRange(rows);
        foreach (var app in table.Values) apps[app.Key] = app;
    }

    public string FilePath => path;
    public int Cores { get; }
    public long Now() => clock();

    // Where /cpu-icon and the icon colours come from (tests set a fake).
    public IAppIcons Icons { get; init; } = new WindowsIcons();

    public int Count
    {
        get { lock (gate) return samples.Count; }
    }

    // The program or process behind a key of the rows, or null.
    public CpuApp? App(string key)
    {
        lock (gate) return apps.TryGetValue(key, out var app) ? app : null;
    }

    // ---- the file ----

    // A file that is missing, damaged or not ours gives an empty history. A single bad row is
    // skipped. Rows older than 48 hours (against `oldest`) are dropped, and the rest are put in
    // time order. Version 1 files (7 numbers a row, no "apps") load as they are, with no apps.
    static (List<HistorySample> Rows, Dictionary<string, CpuApp> Apps) Load(string path, long oldest)
    {
        var list = new List<HistorySample>();
        var table = new Dictionary<string, CpuApp>();
        try
        {
            if (!File.Exists(path)) return (list, table);
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("samples", out var rows)
                || rows.ValueKind != JsonValueKind.Array) return (list, table);
            if (doc.RootElement.TryGetProperty("apps", out var known) && known.ValueKind == JsonValueKind.Object)
                foreach (var entry in known.EnumerateObject())
                    if (ParseApp(entry.Name, entry.Value) is { } app) table[app.Key] = app;
            foreach (var row in rows.EnumerateArray())
                if (Parse(row) is { } s && s.T > oldest) list.Add(s);
            list.Sort((a, b) => a.T.CompareTo(b.T));
        }
        catch (Exception) { list.Clear(); table.Clear(); }  // JsonException, IOException, access denied: start empty
        return (list, table);
    }

    static readonly Regex ColorPattern = new("^#[0-9a-f]{6}$", RegexOptions.CultureInvariant);

    // "key": [name, kind, exe|null, color|null]
    static CpuApp? ParseApp(string key, JsonElement e)
    {
        if (key.Length == 0 || key.Length > MaxKeyLength || e.ValueKind != JsonValueKind.Array || e.GetArrayLength() != 4) return null;
        var a = e.EnumerateArray().ToArray();
        if (a[0].ValueKind != JsonValueKind.String || a[1].ValueKind != JsonValueKind.String) return null;
        var name = a[0].GetString()!;
        var kind = a[1].GetString()!;
        if (name.Length == 0 || name.Length > 256 || kind is not ("app" or "other")) return null;
        string? exe = a[2].ValueKind == JsonValueKind.String ? a[2].GetString() : null;
        string? color = a[3].ValueKind == JsonValueKind.String ? a[3].GetString() : null;
        if (exe != null && (exe.Length > 32767 || !exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))) exe = null;
        if (color != null && !ColorPattern.IsMatch(color)) color = null;
        // Only a program with its exe has an icon; anything else counts as "other".
        if (kind == "app" && exe == null) kind = "other";
        return new CpuApp(key, name, kind, kind == "app" ? exe : null, color);
    }

    // [t, percent|null, plugged 0/1, charging 0/1, temp|null, cpu|null, span] and optionally
    // [..., apps|null] and [..., apps|null, flags]. apps: {"key": processor-seconds}. flags: 1 =
    // backfilled from the battery report.
    static HistorySample? Parse(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() is < 7 or > 9) return null;
        var a = row.EnumerateArray().ToArray();
        if (a[0].ValueKind != JsonValueKind.Number || !a[0].TryGetInt64(out var t) || t <= 0) return null;
        if (!Optional(a[1], out var percent) || !Optional(a[4], out var temp) || !Optional(a[5], out var cpu)) return null;
        if (!Flag(a[2], out var plugged) || !Flag(a[3], out var charging)) return null;
        if (a[6].ValueKind != JsonValueKind.Number || !a[6].TryGetInt32(out var span) || span < 0) return null;
        if (percent is < 0 or > 100) return null;
        Dictionary<string, double>? perApp = null;
        if (a.Length >= 8)
        {
            if (a[7].ValueKind == JsonValueKind.Object)
            {
                perApp = new Dictionary<string, double>();
                foreach (var p in a[7].EnumerateObject())
                {
                    if (perApp.Count >= MaxAppsPerRow) break;
                    // A bad entry only leaves that program out.
                    if (p.Name.Length is 0 or > MaxKeyLength || p.Value.ValueKind != JsonValueKind.Number
                        || !p.Value.TryGetDouble(out var secs) || !double.IsFinite(secs) || secs < 0) continue;
                    perApp[p.Name] = secs;
                }
            }
            else if (a[7].ValueKind != JsonValueKind.Null) return null;
        }
        bool backfilled = false;
        if (a.Length == 9)
        {
            if (a[8].ValueKind != JsonValueKind.Number || !a[8].TryGetInt32(out var flags) || flags < 0) return null;
            backfilled = (flags & FlagBackfilled) != 0;
        }
        return new HistorySample(t, percent is { } p2 ? (int)Math.Round(p2) : null, plugged, charging, temp, cpu, span,
                                 perApp is { Count: > 0 } ? perApp : null, backfilled);
    }

    static bool Optional(JsonElement e, out double? value)
    {
        value = null;
        if (e.ValueKind == JsonValueKind.Null) return true;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetDouble(out var d) || double.IsNaN(d) || double.IsInfinity(d)) return false;
        value = d;
        return true;
    }

    static bool Flag(JsonElement e, out bool value)
    {
        value = false;
        if (e.ValueKind != JsonValueKind.Number || !e.TryGetInt32(out var n) || n is not (0 or 1)) return false;
        value = n == 1;
        return true;
    }

    // Records a sample, drops everything older than 48 hours before it, and saves the file. A
    // sample dated at or before the last one means the clock was set back: the rows that are now
    // "in the future" are dropped, so the history stays in order. `seen` names the programs the
    // sample's Apps list (a key with no name is left out of /cpu-history). False when the file
    // couldn't be written (the sample is still kept in memory).
    public bool Add(HistorySample s, IEnumerable<CpuApp>? seen = null)
    {
        string text;
        lock (gate)
        {
            if (seen != null)
                foreach (var app in seen)
                    if (app.Key.Length is > 0 and <= MaxKeyLength) apps[app.Key] = app;
            samples.RemoveAll(x => x.T >= s.T);
            samples.Add(s);
            long oldest = s.T - KeepHours * 3600L;
            samples.RemoveAll(x => x.T <= oldest);
            text = SaveText();
        }
        return WriteAtomic(path, text);
    }

    // Battery levels from Windows' battery report, for the times History has no row (the PC was
    // asleep, or Lid Awake wasn't running): only the last 48 hours, none in the future, none
    // within BackfillGapSeconds of a row (an earlier backfill's rows included, so running it again
    // adds nothing). They are marked Backfilled in the file. Answers how many were added.
    public int Backfill(IEnumerable<BatteryPoint> points)
    {
        string text;
        int added = 0;
        lock (gate)
        {
            long now = Now(), oldest = now - KeepHours * 3600L;
            foreach (var p in points.OrderBy(x => x.T))
            {
                if (p.T <= oldest || p.T > now || p.Percent is < 0 or > 100) continue;
                if (samples.Any(x => Math.Abs(x.T - p.T) <= BackfillGapSeconds)) continue;
                samples.Add(new HistorySample(p.T, p.Percent, p.Plugged, p.Charging, null, null, 0, null, Backfilled: true));
                added++;
            }
            if (added == 0) return 0;
            samples.Sort((a, b) => a.T.CompareTo(b.T));
            text = SaveText();
        }
        WriteAtomic(path, text);
        return added;
    }

    // Under `gate`. Programs no row names any more are forgotten.
    string SaveText()
    {
        var used = new HashSet<string>();
        foreach (var s in samples)
            if (s.Apps != null) used.UnionWith(s.Apps.Keys);
        foreach (var key in apps.Keys.Where(k => !used.Contains(k)).ToList()) apps.Remove(key);
        return Serialize(samples, apps);
    }

    // Compact rows, one array each (about 40 bytes, plus about 20 per program listed).
    static string Serialize(List<HistorySample> list, Dictionary<string, CpuApp> table)
    {
        var rows = new List<object?[]>(list.Count);
        foreach (var s in list)
        {
            var row = new List<object?> { s.T, s.Percent, s.Plugged ? 1 : 0, s.Charging ? 1 : 0, s.Temp, s.Cpu, s.Span };
            bool hasApps = s.Apps is { Count: > 0 };
            if (hasApps || s.Backfilled) row.Add(hasApps ? s.Apps : null);
            if (s.Backfilled) row.Add(FlagBackfilled);
            rows.Add(row.ToArray());
        }
        var known = new Dictionary<string, object?[]>();
        foreach (var app in table.Values) known[app.Key] = new object?[] { app.Name, app.Kind, app.Exe, app.Color };
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["v"] = FileVersion, ["apps"] = known, ["samples"] = rows });
    }

    // To a temp file first, then moved over the old one, so a crash mid-write can't leave a
    // truncated history. Never throws; false when it failed (the old file is left as it was).
    internal static bool WriteAtomic(string path, string text)
    {
        var tmp = path + ".tmp";
        try
        {
            File.WriteAllText(tmp, text);
            File.Move(tmp, path, overwrite: true);
            return true;
        }
        catch (Exception)
        {
            try { File.Delete(tmp); }
            catch (Exception) { }
            return false;
        }
    }

    // ---- the routes ----

    // The `hours` query: a number, clamped to 1 to 48 as on the Mac; anything else gives 24.
    public static double ClampHours(string? query)
    {
        if (!double.TryParse(query, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                             CultureInfo.InvariantCulture, out var hours) || double.IsNaN(hours))
            return DefaultHours;
        return Math.Clamp(hours, 1, KeepHours);
    }

    // The samples newer than `hours` ago (1 to 48), oldest first. Under `gate`.
    List<HistorySample> SinceLocked(double hours)
    {
        long since = Now() - (long)Math.Ceiling(Math.Clamp(hours, 1, KeepHours) * 3600);
        return samples.Where(s => s.T > since).ToList();
    }

    List<HistorySample> Since(double hours)
    {
        lock (gate) return SinceLocked(hours);
    }

    // GET /temp-history: {"points": [[unix seconds, degrees Celsius], ...], "now": ...}. Only
    // readings above 0 and below 100, to two decimals, like the Mac.
    public Dictionary<string, object> Temperature(double hours)
    {
        var points = new List<object[]>();
        foreach (var s in Since(hours))
            if (s.Temp is { } c && c > 0 && c < 100) points.Add(new object[] { s.T, Math.Round(c, 2) });
        return new() { ["points"] = points, ["now"] = Now() };
    }

    // GET /battery-history: {"points": [[unix seconds, percent, plugged in 0/1, charging 0/1], ...], "now": ...}.
    // Backfilled levels are points like any other.
    public Dictionary<string, object> Battery(double hours)
    {
        var points = new List<object[]>();
        foreach (var s in Since(hours))
            if (s.Percent is { } p) points.Add(new object[] { s.T, p, s.Plugged ? 1 : 0, s.Charging ? 1 : 0 });
        return new() { ["points"] = points, ["now"] = Now() };
    }

    // GET /cpu-history: {now, cores, apps: [{key, name, kind, color?}], intervals: [[start, end,
    // {"app index": seconds}, other seconds]]}, like the Mac. A stretch is one sample: the PC's
    // processor-seconds (percent busy x cores x seconds), of which the programs the row lists are
    // given by their index into `apps` and the rest is "other". Rows from before per-app history
    // list none ({}). The programs' share never adds up to more than the stretch (they are scaled
    // down when the two measurements disagree). To two decimals.
    public Dictionary<string, object> Cpu(double hours)
    {
        var intervals = new List<object[]>();
        var list = new List<object>();
        var index = new Dictionary<string, int>();
        lock (gate)
        {
            foreach (var s in SinceLocked(hours))
            {
                if (s.Cpu is not { } percent || s.Span <= 0) continue;
                double total = percent / 100.0 * Cores * s.Span;
                var named = new List<(string Key, double Secs)>();
                double sum = 0;
                if (s.Apps != null)
                    foreach (var (key, secs) in s.Apps)
                        if (secs > 0 && apps.ContainsKey(key)) { named.Add((key, secs)); sum += secs; }
                double scale = sum > total && sum > 0 ? total / sum : 1;
                var perApp = new Dictionary<string, double>();
                double listed = 0;
                foreach (var (key, secs) in named)
                {
                    double v = Math.Round(secs * scale, 2);
                    if (v <= 0) continue;
                    if (!index.TryGetValue(key, out var i))
                    {
                        i = list.Count;
                        index[key] = i;
                        var app = apps[key];
                        var entry = new Dictionary<string, object> { ["key"] = app.Key, ["name"] = app.Name, ["kind"] = app.Kind };
                        if (app.Color != null) entry["color"] = app.Color;
                        list.Add(entry);
                    }
                    perApp[i.ToString(CultureInfo.InvariantCulture)] = v;
                    listed += v;
                }
                intervals.Add(new object[] { s.T - s.Span, s.T, perApp, Math.Round(Math.Max(0, total - listed), 2) });
            }
        }
        return new() { ["now"] = Now(), ["cores"] = Cores, ["apps"] = list, ["intervals"] = intervals };
    }

    // GET /cpu-icon?key=: the icon of a program the processor history names (kind "app"), and
    // only those, so the page can't fetch any other file's icon. 256 pixels, kept (at most 64).
    // null: no such program, no icon, or a picture over 1 MB.
    public byte[]? IconPng(string key)
    {
        if (string.IsNullOrEmpty(key) || key.Length > MaxKeyLength) return null;
        string? exe;
        lock (gate) exe = apps.TryGetValue(key, out var app) && app.Kind == "app" ? app.Exe : null;
        if (exe == null) return null;
        if (icons.TryGetValue(key, out var cached)) return cached;
        byte[]? png;
        try { png = Icons.Png(exe, IconSize); }
        catch (Exception) { png = null; }
        if (png == null || png.Length > MaxIconBytes) return null;
        if (icons.Count >= MaxIcons) icons.Clear();
        icons[key] = png;
        return png;
    }
}

// Who used the processor between two process snapshots (ProcessTable), as the Mac's power log
// tells it: by program, the small users folded into "other".
public static class AppCpu
{
    // The Mac's rule (PowerHistory.cpu): a program is listed when it used at least 0.2 s or 0.5%
    // of the stretch. At most this many per row, to keep the file small.
    public const double MinSeconds = 0.2;
    public const double MinShare = 0.005;
    public const int KeepPerRow = 8;

    // Windows' kernel processes, which have no exe.
    static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase) { "System", "Registry", "Memory Compression", "Secure System" };

    // A program by its exe (kind "app"); Windows' own processes as "Windows core", "Screen &
    // windows" (dwm.exe) and "Windows (other)"; a process Windows won't name by its image name
    // (kind "other"). The idle process: null (its time is idle time).
    public static CpuApp? Who(ProcessEntry p, string windowsFolder, Func<string, string> nameOf)
    {
        if (p.Pid == 0) return null;
        if (p.Pid == 4 || (p.Path == null && Core.Contains(p.Name))) return new CpuApp("other.core", "Windows core", "other");
        if (p.Path == null)
        {
            var stem = Path.GetFileNameWithoutExtension(p.Name);
            if (stem.Length > 48) stem = stem[..48];
            return stem.Length == 0 ? new CpuApp("other.unnamed", "Unnamed process", "other")
                                    : new CpuApp("other." + stem.ToLowerInvariant(), stem, "other");
        }
        if (ProcessTable.IsSystem(p.Path, windowsFolder))
            return string.Equals(Path.GetFileName(p.Path), "dwm.exe", StringComparison.OrdinalIgnoreCase)
                ? new CpuApp("other.screen", "Screen & windows", "other")
                : new CpuApp("other.windows", "Windows (other)", "other");
        var exe = PhoneApps.Normalize(p.Path);
        string name;
        try { name = nameOf(exe); }
        catch (Exception) { name = Path.GetFileNameWithoutExtension(exe); }
        return new CpuApp(PhoneApps.Id(exe), name, "app", exe);
    }

    // Processor-seconds per CpuApp key between two snapshots. A process in both counts the
    // difference of its processor time; one that started after the first snapshot counts all of
    // its time; one that ended in between is lost (its time stays in "other").
    public static Dictionary<string, (CpuApp App, double Seconds)> Diff(ProcessSnapshot before, ProcessSnapshot after, Func<ProcessEntry, CpuApp?> who)
    {
        var old = new Dictionary<(int, long), long>();
        foreach (var p in before.Processes) old.TryAdd((p.Pid, p.CreateTime), p.CpuTime);
        var result = new Dictionary<string, (CpuApp App, double Seconds)>();
        foreach (var p in after.Processes)
        {
            long delta;
            if (old.TryGetValue((p.Pid, p.CreateTime), out var was)) delta = p.CpuTime - was;
            else if (p.CreateTime > 0 && p.CreateTime >= before.FileTime) delta = p.CpuTime;
            else continue;
            if (delta <= 0) continue;
            CpuApp? app;
            try { app = who(p); }
            catch (Exception) { app = null; }
            if (app == null) continue;
            double secs = delta / 1e7;
            result[app.Key] = result.TryGetValue(app.Key, out var had) ? (had.App, had.Seconds + secs) : (app, secs);
        }
        return result;
    }

    // The ones a row keeps: at least 0.2 s or 0.5% of everyone's time, the busiest first, at most
    // `keep`, to two decimals. The rest is "other" (what the stretch used minus these).
    public static Dictionary<string, double> Fold(IReadOnlyDictionary<string, double> seconds, int keep = KeepPerRow)
    {
        double total = seconds.Values.Where(v => v > 0).Sum();
        var kept = new Dictionary<string, double>();
        foreach (var (key, secs) in seconds.Where(kv => kv.Value > 0).OrderByDescending(kv => kv.Value).ThenBy(kv => kv.Key, StringComparer.Ordinal))
        {
            if (kept.Count >= keep) break;
            if (secs < MinSeconds && secs < total * MinShare) continue;
            double v = Math.Round(secs, 2);
            if (v > 0) kept[key] = v;
        }
        return kept;
    }

    // The colour that stands out in an icon, as on the Mac (PowerHistory.iconColor): its most
    // common bright hue at 32 x 32, averaged. Grey or black-and-white icons give null.
    public static string? IconColor(byte[] png)
    {
        try
        {
            using var stream = new MemoryStream(png);
            using var source = new Bitmap(stream);
            using var small = new Bitmap(source, new Size(32, 32));
            var buckets = new (double W, double R, double G, double B)[12];
            for (int x = 0; x < 32; x++)
                for (int y = 0; y < 32; y++)
                {
                    var c = small.GetPixel(x, y);
                    if (c.A < 204) continue;
                    double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
                    double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
                    double saturation = max == 0 ? 0 : (max - min) / max;
                    if (saturation <= 0.35 || max <= 0.3) continue;
                    int i = Math.Min(11, (int)(c.GetHue() / 360.0 * 12));
                    buckets[i].W += saturation;
                    buckets[i].R += r * saturation;
                    buckets[i].G += g * saturation;
                    buckets[i].B += b * saturation;
                }
            var best = buckets.MaxBy(k => k.W);
            if (best.W <= 8) return null;
            int To255(double v) => Math.Clamp((int)(v / best.W * 255), 0, 255);
            return $"#{To255(best.R):x2}{To255(best.G):x2}{To255(best.B):x2}";
        }
        catch (Exception) { return null; }
    }
}

// Windows' battery report (powercfg /batteryreport /xml), which keeps the battery's level at each
// change of state (sleep, wake, plugged in) for the last days, also while Lid Awake wasn't running.
public interface IBatteryReport
{
    // The report's XML, or null when it couldn't be made.
    string? ReadXml();
}

public static class BatteryReport
{
    // The RecentUsage entries: when (Timestamp, UTC; LocalTimestamp if that is missing), on AC
    // (Ac), and the charge (ChargeCapacity / FullChargeCapacity, as a percent). Charging: on AC
    // and the next entry has more charge. Entries that can't be read are skipped; a report that
    // can't be read gives none. Oldest first.
    public static List<BatteryPoint> Parse(string xml)
    {
        var raw = new List<(long T, bool Ac, double Charge, double Full)>();
        try
        {
            var settings = new XmlReaderSettings
            {
                DtdProcessing = DtdProcessing.Prohibit, IgnoreComments = true, IgnoreWhitespace = true,
            };
            using var reader = XmlReader.Create(new StringReader(xml), settings);
            int depth = -1;  // the RecentUsage element's depth while inside it
            while (reader.Read())
            {
                if (reader.NodeType == XmlNodeType.EndElement && reader.LocalName == "RecentUsage") { depth = -1; continue; }
                if (reader.NodeType != XmlNodeType.Element) continue;
                if (reader.LocalName == "RecentUsage") { depth = reader.IsEmptyElement ? -1 : reader.Depth; continue; }
                if (depth < 0 || reader.LocalName != "UsageEntry") continue;
                if (Entry(reader) is { } e) raw.Add(e);
            }
        }
        catch (Exception) { return new List<BatteryPoint>(); }  // XmlException and the like: no levels

        raw.Sort((a, b) => a.T.CompareTo(b.T));
        var points = new List<BatteryPoint>();
        for (int i = 0; i < raw.Count; i++)
        {
            var e = raw[i];
            int percent = (int)Math.Round(Math.Clamp(e.Charge / e.Full * 100, 0, 100));
            bool charging = e.Ac && i + 1 < raw.Count && raw[i + 1].Charge > e.Charge;
            points.Add(new BatteryPoint(e.T, percent, e.Ac, charging));
        }
        return points;
    }

    static (long T, bool Ac, double Charge, double Full)? Entry(XmlReader reader)
    {
        long? t = Time(reader.GetAttribute("Timestamp"), DateTimeStyles.AssumeUniversal)
                  ?? Time(reader.GetAttribute("LocalTimestamp"), DateTimeStyles.AssumeLocal);
        if (t is not { } at || at <= 0) return null;
        var ac = reader.GetAttribute("Ac");
        if (!Number(reader.GetAttribute("ChargeCapacity"), out var charge) || !Number(reader.GetAttribute("FullChargeCapacity"), out var full)
            || full <= 0 || charge < 0) return null;
        return (at, ac == "1" || string.Equals(ac, "true", StringComparison.OrdinalIgnoreCase), charge, full);
    }

    static long? Time(string? text, DateTimeStyles style)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, style, out var parsed) ? parsed.ToUnixTimeSeconds() : null;
    }

    static bool Number(string? text, out double value) =>
        double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) && double.IsFinite(value);
}

// The real report: powercfg.exe from System32 (no administrator needed), written to a temp file
// that is deleted again. At most 30 seconds; a report over 8 MB is not read.
public sealed class PowercfgReport : IBatteryReport
{
    const int MaxBytes = 8 << 20;
    static readonly TimeSpan Limit = TimeSpan.FromSeconds(30);

    public string? ReadXml()
    {
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "powercfg.exe");
        if (!File.Exists(exe)) return null;
        var output = Path.Combine(Path.GetTempPath(), $"LidAwake-battery-{Guid.NewGuid():N}.xml");
        try
        {
            var start = new ProcessStartInfo(exe)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
                WorkingDirectory = Path.GetTempPath(),
            };
            foreach (var arg in new[] { "/batteryreport", "/xml", "/output", output }) start.ArgumentList.Add(arg);
            using var process = Process.Start(start);
            if (process == null) return null;
            // Read both, so a full pipe can't stop powercfg.
            _ = process.StandardOutput.ReadToEndAsync();
            _ = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit((int)Limit.TotalMilliseconds))
            {
                try { process.Kill(entireProcessTree: true); }
                catch (Exception) { }
                return null;
            }
            if (process.ExitCode != 0) return null;
            var info = new FileInfo(output);
            if (!info.Exists || info.Length == 0 || info.Length > MaxBytes) return null;
            return File.ReadAllText(output);
        }
        catch (Exception) { return null; }
        finally
        {
            try { File.Delete(output); }
            catch (Exception) { }
        }
    }
}

// Takes a sample every minute while the app runs, on a thread-pool timer (never the UI thread).
// Battery from GetSystemPowerStatus, the battery's temperature from the safety check's cached
// reading (it never waits for the hardware), overall processor use between two samples, and
// (with a process table) each program's share of it. With a battery report, the battery levels
// of the time History has no rows for are filled in at start and once a day after (a PC with no
// battery skips it).
public sealed class HistorySampler : IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    // Longer than this since the last sample, and the PC slept or the app was stuck: the
    // processor use across that stretch would be an average over time nobody measured.
    public const int MaxSpanSeconds = 150;
    public const long BackfillEverySeconds = 24 * 3600;

    readonly HistoryStore store;
    readonly ISystemReadings readings;
    readonly Func<double?> batteryTemp;
    readonly Func<long> clock;
    readonly IProcessTable? processes;
    readonly IBatteryReport? report;
    readonly string windowsFolder = ProcessTable.WindowsFolder();
    readonly object gate = new();       // one sample at a time
    readonly object timerGate = new();  // Start and Dispose (never held while a sample runs)
    readonly Dictionary<string, string?> colors = new();  // icon colour per program key; under `gate`
    CpuMeter meter = new();
    long? lastT;
    ProcessSnapshot? lastProcesses;
    long? backfilledAt;
    int backfilling;
    System.Threading.Timer? timer;

    // `batteryTemp` gives the last good battery temperature (HeatSensor.Latest.Battery), or null.
    // `processes`: each program's processor use (none: the PC's overall use only). `report`:
    // the battery report for the backfill (none: no backfill).
    public HistorySampler(HistoryStore store, ISystemReadings readings, Func<double?> batteryTemp, Func<long>? clock = null,
                          IProcessTable? processes = null, IBatteryReport? report = null)
    {
        this.store = store;
        this.readings = readings;
        this.batteryTemp = batteryTemp;
        this.clock = clock ?? (() => store.Now());
        this.processes = processes;
        this.report = report;
    }

    // What counts as Windows itself for the processor history (tests set their own).
    public string WindowsFolder { get => windowsFolder; init => windowsFolder = value; }

    // A program's name from its exe (tests set their own).
    public Func<string, string> NameOf { get; init; } = ProcessSource.NameOf;

    // The first sample is taken at once (it also starts the processor measurement and the backfill).
    public void Start()
    {
        lock (timerGate) timer ??= new System.Threading.Timer(_ => Sample(), null, TimeSpan.Zero, Every);
    }

    public void Dispose()
    {
        lock (timerGate)
        {
            timer?.Dispose();
            timer = null;
        }
    }

    // One sample, now. Safe to call from any thread; one at a time (a call that arrives while
    // another runs is dropped). A failing reading only leaves its part out.
    public void Sample()
    {
        if (!Monitor.TryEnter(gate)) return;
        try
        {
            long t = clock();
            int? percent = null;
            bool plugged = false, charging = false;
            try
            {
                if (readings.Power() is { } p && StatusInfo.HasBattery(p))
                {
                    percent = p.BatteryLifePercent;
                    plugged = p.AcLineStatus == 1;
                    charging = (p.BatteryFlag & 8) != 0;
                }
            }
            catch (Exception) { }

            double? temp = null;
            try { temp = batteryTemp(); }
            catch (Exception) { }

            double? cpu = null;
            int span = 0;
            Dictionary<string, double>? apps = null;
            List<CpuApp>? seen = null;
            try
            {
                // After a long gap start the measurement again, so the first answer covers a minute.
                if (lastT is { } before && (t - before > MaxSpanSeconds || t <= before))
                {
                    meter = new CpuMeter();
                    lastT = null;
                    lastProcesses = null;
                }
                ProcessSnapshot? now = null;
                try { now = processes?.Snapshot(); }
                catch (Exception) { }
                if (readings.Cpu() is { } times && meter.Percent(times, readings.Milliseconds()) is { } busy && lastT is { } since)
                {
                    cpu = busy;
                    span = (int)(t - since);
                    if (lastProcesses != null && now != null) (apps, seen) = Programs(lastProcesses, now);
                }
                lastProcesses = now;
            }
            catch (Exception) { }
            lastT = t;

            store.Add(new HistorySample(t, percent, plugged, charging, temp, cpu, span, apps is { Count: > 0 } ? apps : null), seen);
            MaybeBackfill(t);
        }
        catch (Exception) { }
        finally { Monitor.Exit(gate); }
    }

    // Under `gate`: the programs a row keeps, with their names (and icon colours).
    (Dictionary<string, double>, List<CpuApp>) Programs(ProcessSnapshot before, ProcessSnapshot after)
    {
        var used = AppCpu.Diff(before, after, p => AppCpu.Who(p, windowsFolder, NameOf));
        var kept = AppCpu.Fold(used.ToDictionary(kv => kv.Key, kv => kv.Value.Seconds));
        var seen = new List<CpuApp>();
        foreach (var key in kept.Keys)
        {
            var app = used[key].App;
            if (app.Kind == "app" && app.Exe != null)
            {
                if (!colors.TryGetValue(key, out var color))
                {
                    try { color = store.Icons.Png(app.Exe, 32) is { } png ? AppCpu.IconColor(png) : null; }
                    catch (Exception) { color = null; }
                    if (colors.Count >= 512) colors.Clear();
                    colors[key] = color;
                }
                app = app with { Color = color };
            }
            seen.Add(app);
        }
        return (kept, seen);
    }

    // At start, then once a day: on the thread pool, as powercfg takes a few seconds.
    void MaybeBackfill(long t)
    {
        if (report == null) return;
        if (backfilledAt is { } at && t >= at && t - at < BackfillEverySeconds) return;
        if (Interlocked.CompareExchange(ref backfilling, 1, 0) != 0) return;
        backfilledAt = t;
        Task.Run(() =>
        {
            try { Backfill(); }
            catch (Exception) { }
            finally { Volatile.Write(ref backfilling, 0); }
        });
    }

    // Fills in the battery levels History has no rows for, from the battery report. Answers how
    // many were added (0 on a PC with no battery, or when the report couldn't be made or read).
    public int Backfill()
    {
        if (report == null) return 0;
        try
        {
            if (readings.Power() is not { } p || !StatusInfo.HasBattery(p)) return 0;
            if (report.ReadXml() is not { } xml) return 0;
            return store.Backfill(BatteryReport.Parse(xml));
        }
        catch (Exception) { return 0; }
    }
}
