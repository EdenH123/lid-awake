using System.Globalization;
using System.Text.Json;

namespace LidAwake;

// One minute of the PC's life, as the graphs on the phone need it (docs/API.md 5.7). Windows has no
// readable power log like the Mac's, so Lid Awake records these itself, while it runs.
// Percent: null on a PC with no battery. Temp: the battery's own sensor only (null: none).
// Cpu: percent busy over the Span seconds before T (null and 0: not measured, for example the
// first sample, or after the PC slept).
public readonly record struct HistorySample(long T, int? Percent, bool Plugged, bool Charging, double? Temp, double? Cpu, int Span);

// The recorded samples: the last 48 hours, in memory and in %LOCALAPPDATA%\LidAwake\history.json,
// and the answers of the three history routes. Safe to use from any thread. It never throws: a
// file that can't be read starts empty, a write that fails is kept in memory only (tried again
// with the next sample).
public sealed class HistoryStore
{
    public const string FileName = "history.json";
    // What the routes keep and clamp to, as on the Mac (PowerHistory.query).
    public const int KeepHours = 48;
    public const double DefaultHours = 24;
    const int FileVersion = 1;

    readonly string path;
    readonly Func<long> clock;
    readonly object gate = new();
    readonly List<HistorySample> samples = new();

    // `clock` is Unix seconds (tests set their own). `cores` is what /cpu-history reports.
    public HistoryStore(string? folder = null, Func<long>? clock = null, int? cores = null)
    {
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LidAwake");
        path = Path.Combine(folder, FileName);
        this.clock = clock ?? (() => DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        Cores = Math.Max(1, cores ?? Environment.ProcessorCount);
        try { Directory.CreateDirectory(folder); }
        catch (Exception) { }  // no folder: the history lives in memory only
        samples.AddRange(Load(path, Now() - KeepHours * 3600L));
    }

    public string FilePath => path;
    public int Cores { get; }
    public long Now() => clock();

    public int Count
    {
        get { lock (gate) return samples.Count; }
    }

    // A file that is missing, damaged or not ours gives an empty history. A single bad row is
    // skipped. Rows older than 48 hours (against `oldest`) are dropped, and the rest are put in
    // time order.
    static List<HistorySample> Load(string path, long oldest)
    {
        var list = new List<HistorySample>();
        try
        {
            if (!File.Exists(path)) return list;
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            if (doc.RootElement.ValueKind != JsonValueKind.Object
                || !doc.RootElement.TryGetProperty("samples", out var rows)
                || rows.ValueKind != JsonValueKind.Array) return list;
            foreach (var row in rows.EnumerateArray())
                if (Parse(row) is { } s && s.T > oldest) list.Add(s);
            list.Sort((a, b) => a.T.CompareTo(b.T));
        }
        catch (Exception) { list.Clear(); }  // JsonException, IOException, access denied: start empty
        return list;
    }

    // [t, percent|null, plugged 0/1, charging 0/1, temp|null, cpu|null, span]
    static HistorySample? Parse(JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Array || row.GetArrayLength() != 7) return null;
        var a = row.EnumerateArray().ToArray();
        if (a[0].ValueKind != JsonValueKind.Number || !a[0].TryGetInt64(out var t) || t <= 0) return null;
        if (!Optional(a[1], out var percent) || !Optional(a[4], out var temp) || !Optional(a[5], out var cpu)) return null;
        if (!Flag(a[2], out var plugged) || !Flag(a[3], out var charging)) return null;
        if (a[6].ValueKind != JsonValueKind.Number || !a[6].TryGetInt32(out var span) || span < 0) return null;
        if (percent is < 0 or > 100) return null;
        return new HistorySample(t, percent is { } p ? (int)Math.Round(p) : null, plugged, charging, temp, cpu, span);
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
    // "in the future" are dropped, so the history stays in order. False when the file couldn't be
    // written (the sample is still kept in memory).
    public bool Add(HistorySample s)
    {
        string text;
        lock (gate)
        {
            samples.RemoveAll(x => x.T >= s.T);
            samples.Add(s);
            long oldest = s.T - KeepHours * 3600L;
            samples.RemoveAll(x => x.T <= oldest);
            text = Serialize(samples);
        }
        return WriteAtomic(path, text);
    }

    // Compact rows, one array each (about 40 bytes: 48 hours is roughly 115 KB).
    static string Serialize(List<HistorySample> list)
    {
        var rows = new List<object?[]>(list.Count);
        foreach (var s in list)
            rows.Add(new object?[] { s.T, s.Percent, s.Plugged ? 1 : 0, s.Charging ? 1 : 0, s.Temp, s.Cpu, s.Span });
        return JsonSerializer.Serialize(new Dictionary<string, object> { ["v"] = FileVersion, ["samples"] = rows });
    }

    // To a temp file first, then moved over the old one, so a crash mid-write can't leave a
    // truncated history. Never throws; false when it failed (the old file is left as it was).
    // `uniqueTemp`: a temp name of its own each time, for files several writers may write at once
    // (Claude's usage file and Claude Code's settings: status lines, hooks, the tray).
    internal static bool WriteAtomic(string path, string text, bool uniqueTemp = false)
    {
        var tmp = uniqueTemp ? path + "." + Guid.NewGuid().ToString("N") + ".tmp" : path + ".tmp";
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

    // The `hours` query: a number, clamped to 1 to 48 as on the Mac; anything else gives 24.
    public static double ClampHours(string? query)
    {
        if (!double.TryParse(query, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint | NumberStyles.AllowExponent,
                             CultureInfo.InvariantCulture, out var hours) || double.IsNaN(hours))
            return DefaultHours;
        return Math.Clamp(hours, 1, KeepHours);
    }

    // The samples newer than `hours` ago (1 to 48), oldest first.
    List<HistorySample> Since(double hours)
    {
        long since = Now() - (long)Math.Ceiling(Math.Clamp(hours, 1, KeepHours) * 3600);
        lock (gate) return samples.Where(s => s.T > since).ToList();
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
    public Dictionary<string, object> Battery(double hours)
    {
        var points = new List<object[]>();
        foreach (var s in Since(hours))
            if (s.Percent is { } p) points.Add(new object[] { s.T, p, s.Plugged ? 1 : 0, s.Charging ? 1 : 0 });
        return new() { ["points"] = points, ["now"] = Now() };
    }

    // GET /cpu-history. The PC's overall use only: no per-app numbers (Windows can't give them
    // without much heavier sampling), so `apps` is an empty list and every stretch is
    // [start, end, {}, processor-seconds]. The page draws that as one grey bar per slot and a
    // single "Everything else" row, the same as a Mac stretch whose apps were all folded away.
    // Processor-seconds = percent busy x cores x seconds, to two decimals (the page divides it
    // by cores and seconds again).
    public Dictionary<string, object> Cpu(double hours)
    {
        var intervals = new List<object[]>();
        foreach (var s in Since(hours))
        {
            if (s.Cpu is not { } percent || s.Span <= 0) continue;
            double seconds = Math.Round(percent / 100.0 * Cores * s.Span, 2);
            intervals.Add(new object[] { s.T - s.Span, s.T, new Dictionary<string, double>(), seconds });
        }
        return new() { ["now"] = Now(), ["cores"] = Cores, ["apps"] = new List<object>(), ["intervals"] = intervals };
    }
}

// Takes a sample every minute while the app runs, on a thread-pool timer (never the UI thread).
// Battery from GetSystemPowerStatus, the battery's temperature from the safety check's cached
// reading (it never waits for the hardware), and overall processor use between two samples.
public sealed class HistorySampler : IDisposable
{
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(60);
    // Longer than this since the last sample, and the PC slept or the app was stuck: the
    // processor use across that stretch would be an average over time nobody measured.
    public const int MaxSpanSeconds = 150;

    readonly HistoryStore store;
    readonly ISystemReadings readings;
    readonly Func<double?> batteryTemp;
    readonly Func<long> clock;
    readonly object gate = new();       // one sample at a time
    readonly object timerGate = new();  // Start and Dispose (never held while a sample runs)
    CpuMeter meter = new();
    long? lastT;
    System.Threading.Timer? timer;

    // `batteryTemp` gives the last good battery temperature (HeatSensor.Latest.Battery), or null.
    public HistorySampler(HistoryStore store, ISystemReadings readings, Func<double?> batteryTemp, Func<long>? clock = null)
    {
        this.store = store;
        this.readings = readings;
        this.batteryTemp = batteryTemp;
        this.clock = clock ?? (() => store.Now());
    }

    // The first sample is taken at once (it also starts the processor measurement).
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
            try
            {
                // After a long gap start the measurement again, so the first answer covers a minute.
                if (lastT is { } before && (t - before > MaxSpanSeconds || t <= before)) { meter = new CpuMeter(); lastT = null; }
                if (readings.Cpu() is { } times && meter.Percent(times, readings.Milliseconds()) is { } busy && lastT is { } since)
                {
                    cpu = busy;
                    span = (int)(t - since);
                }
            }
            catch (Exception) { }
            lastT = t;

            store.Add(new HistorySample(t, percent, plugged, charging, temp, cpu, span));
        }
        catch (Exception) { }
        finally { Monitor.Exit(gate); }
    }
}
