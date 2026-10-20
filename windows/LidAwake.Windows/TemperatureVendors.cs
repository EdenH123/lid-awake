using System.Collections.Concurrent;
using System.Globalization;
using System.Management;
using System.Text.RegularExpressions;

namespace LidAwake;

// Every temperature source, put together into one reading per round (TemperatureSources.Read uses
// one chain for the whole app). Never throws. Blocks: call it only off the UI thread.
//
// The battery: its own sensor (IOCTL_BATTERY_QUERY_INFORMATION), else a maker's sensor labelled for
// the battery. The PC itself (the zone), the first group that has a live reading:
//   1. the ACPI thermal zones (MSAcpi_ThermalZoneTemperature, admin only on most PCs),
//   2. a maker's board-type sensors (board, chassis, ambient, skin, memory, chipset...),
//   3. the Thermal Zone Information counters (readable by any user),
// all three with the 90 °C limit (Limits.MaxPcTemp); and only when none of them has one, a maker's
// processor die sensors (cpu, core, package, gpu, "internal") with their own 100 °C limit
// (Limits.MaxDieTemp), since a die runs far hotter than the board around it.
// The makers' sources (VendorSensors.All, the first with a sane reading) are asked whenever the
// battery or the ACPI zones have nothing, within MakersBudget (about 6 seconds) per round.
//
// A zone value is checked for life, per source and per zone: one that has not changed for
// StuckAfter (30 minutes) while the processor load moved by LoadSwing (25) points or more is not a
// real sensor (a fixed value the firmware reports) and is left out until it changes; the activity
// log says so once per source. A zone may only turn Lid Awake off (ZoneConfirmed) once it has been
// at or above its limit on HotReads (2) reads in a row and has been seen at two different values in
// this run. Windows holding the processor to ThrottledLimit (50) percent or less on HotReads reads
// in a row (PercentPassiveLimit) is Throttled.
//
// A WMI source that isn't on this PC (no namespace or class, access refused, no temperature sensor
// in it), or that failed FailuresBeforeRest (3) times in a row, is left alone for Recheck (10
// minutes). Which sources gave the reading goes to the activity log, once per combination per run.
public sealed class TemperatureChain
{
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(10);
    public const int FailuresBeforeRest = 3;
    public static readonly TimeSpan StuckAfter = TimeSpan.FromMinutes(30);
    public const double LoadSwing = 25;
    public const int HotReads = 2;
    public const int ThrottledLimit = 50;
    public static readonly TimeSpan MakersBudget = TimeSpan.FromSeconds(6);

    public const string FromBattery = "the battery's own sensor";
    public const string FromAcpiZones = "the ACPI thermal zones";
    public const string FromZoneCounters = "the thermal zone counters";

    readonly Func<double?> battery;
    readonly Func<ThermalZoneReading?> counters;
    readonly IReadOnlyList<VendorSource> vendors;
    readonly IWmi wmi;
    readonly Func<double?> load;
    readonly Func<long> clock;
    readonly Action<string>? log;
    readonly object gate = new();
    readonly Dictionary<string, Resting> resting = new();
    readonly Dictionary<string, Track> tracks = new();
    readonly HashSet<string> logged = new();
    int hotStreak, throttledStreak;

    sealed class Resting { public long? Until; public int Failures; }

    // One zone's history: its value, since when, and the processor load's range since then.
    sealed class Track
    {
        public double Value;
        public long Since;
        public double? LowLoad, HighLoad;
        public bool Moved, Stuck;
    }

    // A zone that counts this round: its value and whether it has moved in this run.
    readonly record struct Live(double Celsius, bool Moved);

    // `battery` is the battery's own sensor (°C or null), `counters` the Thermal Zone Information
    // counters (ZoneCounters.Read), `wmi` answers the ACPI zones' and the makers' queries, `load` the
    // processor load in percent since the last call (null: unknown), `clock` milliseconds that only
    // go forward, `log` the activity log's Write.
    public TemperatureChain(Func<double?> battery, Func<ThermalZoneReading?> counters, IReadOnlyList<VendorSource> vendors,
        IWmi wmi, Func<double?>? load = null, Func<long>? clock = null, Action<string>? log = null)
    {
        (this.battery, this.counters, this.vendors, this.wmi, this.log) = (battery, counters, vendors, wmi, log);
        this.load = load ?? (() => null);
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    // What gave the last reading ("HP BIOS sensors", "the battery's own sensor and the thermal
    // zone counters"), null when nothing did.
    public string? Source { get; private set; }

    public TemperatureReading Read()
    {
        lock (gate)
        {
            var reading = ReadOnce(out var source);
            Source = source;
            if (source != null && logged.Add("from " + source)) Note("Temperature from " + source);
            return reading;
        }
    }

    TemperatureReading ReadOnce(out string? source)
    {
        double? cpu = Try(load);
        double? b = Sane(Try(battery));
        string? batteryFrom = b != null ? FromBattery : null;

        // 1. The ACPI zones.
        List<Live>? acpi = null;
        if (Ask(ThermalZones.Scope, ThermalZones.Query) is { } found)
        {
            var zones = ThermalZones.Each(found.Rows);
            if (zones.Count == 0) Rest(ThermalZones.Scope, ThermalZones.Query);
            else acpi = Alive(FromAcpiZones, zones);
        }
        // The counters, every round: their PercentPassiveLimit is the throttling signal (one shared
        // reading with thermalState, so this is no extra query).
        ThermalZoneReading? counted = null;
        try { counted = counters(); }
        catch (Exception) { }
        var counterZones = counted is { Zones.Count: > 0 } ? Alive(FromZoneCounters, counted.Zones) : new List<Live>();

        // 2. The makers', for what Windows leaves out.
        VendorSource? maker = null;
        List<VendorSensor> sensors = new();
        if (b == null || acpi is not { Count: > 0 }) (maker, sensors) = AskMakers();
        var makerBoard = maker == null ? new List<Live>() : Alive(maker.Name, Group(sensors, SensorKind.Board));
        var makerDie = maker == null ? new List<Live>() : Alive(maker.Name, Group(sensors, SensorKind.Die));
        if (b == null && maker != null && NamedZone.Hottest(Group(sensors, SensorKind.Battery)) is { } makerBattery)
            (b, batteryFrom) = (makerBattery, maker.Name);

        // The zone: the first group with a live reading.
        List<Live> zone = new();
        string? zoneFrom = null;
        int limit = Limits.MaxPcTemp;
        if (acpi is { Count: > 0 }) (zone, zoneFrom) = (acpi, FromAcpiZones);
        else if (makerBoard.Count > 0) (zone, zoneFrom) = (makerBoard, maker!.Name);
        else if (counterZones.Count > 0) (zone, zoneFrom) = (counterZones, FromZoneCounters);
        else if (makerDie.Count > 0) (zone, zoneFrom, limit) = (makerDie, maker!.Name, Limits.MaxDieTemp);

        double? z = null;
        bool moved = false;
        foreach (var live in zone)
            if (z == null || live.Celsius > z) (z, moved) = (live.Celsius, live.Moved);
        hotStreak = z >= limit && moved ? hotStreak + 1 : 0;
        throttledStreak = counted?.PassiveLimit <= ThrottledLimit ? throttledStreak + 1 : 0;

        var parts = new List<string>();
        if (batteryFrom != null) parts.Add(batteryFrom);
        if (zoneFrom != null && zoneFrom != batteryFrom) parts.Add(zoneFrom);
        source = parts.Count == 0 ? null : string.Join(" and ", parts);
        return new TemperatureReading(b, z, limit, ZoneConfirmed: hotStreak >= HotReads, Throttled: throttledStreak >= HotReads);

        // The zones that are alive this round, each tracked by its source and name.
        List<Live> Alive(string from, IEnumerable<NamedZone> named)
        {
            var alive = new List<Live>();
            foreach (var n in named)
            {
                if (n.Celsius is not { } c || !TemperatureMath.IsSane(c)) continue;
                if (Watch(from, n.Name, c, cpu) is { } live) alive.Add(live);
            }
            return alive;
        }
    }

    // The first maker's source with a sane reading, and its temperature sensors.
    (VendorSource?, List<VendorSensor>) AskMakers()
    {
        long start = clock();
        foreach (var vendor in vendors)
        {
            if (clock() - start >= (long)MakersBudget.TotalMilliseconds) break;
            if (Ask(vendor.Scope, vendor.Query) is not { } found) continue;
            var sensors = VendorSensors.Each(found.Rows, vendor.Parse);
            // The class is there but holds no temperature sensor (fans and voltages only, or none
            // with a reading): not on this PC either.
            if (sensors.Count == 0) { Rest(vendor.Scope, vendor.Query); continue; }
            if (sensors.Any(s => s.Celsius is { } c && TemperatureMath.IsSane(c))) return (vendor, sensors);
            // Temperature sensors, but none with a sane value this time: asked again next round.
        }
        return (null, new List<VendorSensor>());
    }

    static IEnumerable<NamedZone> Group(List<VendorSensor> sensors, SensorKind kind) =>
        sensors.Where(s => s.Kind == kind).Select(s => new NamedZone(s.Label, s.Celsius));

    // Null when that source rests, or when it isn't on this PC or failed (then it may rest).
    WmiResult? Ask(string scope, string query)
    {
        var state = State(scope, query);
        if (state.Until is { } until && clock() < until) return null;
        state.Until = null;
        WmiResult result;
        try { result = wmi.Query(scope, query); }
        catch (Exception) { result = WmiResult.Failed; }
        switch (result.Outcome)
        {
            case WmiOutcome.Missing:
                Rest(scope, query);
                return null;
            case WmiOutcome.Failed:
                if (++state.Failures >= FailuresBeforeRest) Rest(scope, query);
                return null;
        }
        state.Failures = 0;
        return result;
    }

    Resting State(string scope, string query)
    {
        string key = scope + "\n" + query;
        if (!resting.TryGetValue(key, out var state)) resting[key] = state = new Resting();
        return state;
    }

    void Rest(string scope, string query)
    {
        var state = State(scope, query);
        state.Until = clock() + (long)Recheck.TotalMilliseconds;
        state.Failures = 0;
    }

    // The zone `name` of `from` read `celsius`: null while it is stuck.
    Live? Watch(string from, string name, double celsius, double? cpu)
    {
        long now = clock();
        string key = from + "\n" + name;
        if (!tracks.TryGetValue(key, out var t)) tracks[key] = t = new Track { Value = celsius, Since = now };
        else if (t.Value != celsius)
        {
            (t.Value, t.Since, t.LowLoad, t.HighLoad) = (celsius, now, null, null);
            t.Moved = true;
            t.Stuck = false;
        }
        if (cpu is { } l)
        {
            if (t.LowLoad == null || l < t.LowLoad) t.LowLoad = l;
            if (t.HighLoad == null || l > t.HighLoad) t.HighLoad = l;
        }
        if (!t.Stuck && now - t.Since >= (long)StuckAfter.TotalMilliseconds && t.HighLoad - t.LowLoad >= LoadSwing)
        {
            t.Stuck = true;
            if (logged.Add("stuck " + from))
                Note($"{char.ToUpperInvariant(from[0])}{from[1..]} have said {TemperatureMath.OneDecimal(celsius).ToString("0.0", CultureInfo.InvariantCulture)}°C " +
                     $"for {StuckAfter.TotalMinutes:0} minutes while the processor load changed: not used");
        }
        return t.Stuck ? null : new Live(celsius, t.Moved);
    }

    void Note(string text)
    {
        try { log?.Invoke(text); }
        catch (Exception) { }
    }

    static double? Try(Func<double?> read)
    {
        try { return read(); }
        catch (Exception) { return null; }
    }

    static double? Sane(double? celsius) => celsius is { } c && TemperatureMath.IsSane(c) ? c : null;
}

// One vendor's sensor class: where it is, and how one of its objects reads (null: not a
// temperature sensor, or one with no reading at all). Name says it in the activity log, after
// "Temperature from" and "The".
public sealed record VendorSource(string Name, string Scope, string Query, Func<IReadOnlyDictionary<string, object?>, VendorSensor?> Parse);

// What a maker's sensor measures, from its label: the battery (the 40 °C limit, sent as
// batteryTemp), the board around the processor (the 90 °C limit), or the processor's die itself
// (the 100 °C limit, used only on a PC with no board-type sensor).
public enum SensorKind { Battery, Board, Die }

// One temperature sensor: its label, its temperature in °C (null: garbage this time), and its kind.
public readonly record struct VendorSensor(string Label, double? Celsius, SensorKind Kind);

public static class VendorSensors
{
    // CIM_Sensor.SensorType: 2 = Temperature (HP's MOF and DMTF CIM_Sensor alike).
    public const int SensorTypeTemperature = 2;
    // CIM_NumericSensor.BaseUnits: 2 = Degrees C, 3 = Degrees F, 4 = Degrees K.
    public const int DegreesC = 2, DegreesF = 3, DegreesK = 4;
    // HP's OperationalStatus 12 = "No Contact" (the Linux driver treats it as disconnected).
    public const int NoContact = 12;
    // A UnitModifier beyond this is garbage, not a scale.
    public const int MaxModifier = 6;

    // HP business PCs (EliteBook, ProBook, ZBook, Elite/Z desktops): the BIOS's own sensors, through
    // HP's Client Management Interface, which the BIOS itself provides (no HP software needed).
    // root\HP\InstrumentedBIOS HP_BIOSNumericSensor : HP_BIOSSensor, and the same sensors in root\WMI
    // as HPBIOS_BIOSNumericSensor (often admin only). Properties: Name, Description, SensorType
    // (uint32, 2 = Temperature), OtherSensorType, OperationalStatus (uint32, 12 = No Contact),
    // CurrentState (string, e.g. "Normal", "Not Present"), BaseUnits (uint32, 2 = Degrees C,
    // 3 = F, 4 = K), UnitModifier (sint32, a power of ten), CurrentReading (uint32).
    // A real HP Z440 answers Name "CPU0 Temperature", BaseUnits 2, UnitModifier 0, CurrentReading 39.
    // Sources: the Linux hp-wmi-sensors driver, whose header has both MOF variants and the
    // conversion (https://kernelsources.org/source/xref/linux/drivers/hwmon/hp-wmi-sensors.c,
    // https://docs.kernel.org/hwmon/hp-wmi-sensors.html), its patch with the InstrumentedBIOS
    // namespace pragma (https://lkml.rescloud.iu.edu/2304.0/06211.html), HP's "HP Client
    // Management Interface Technical White Paper" (2005) that both cite, and the Z440 output at
    // https://thwack.solarwinds.com/discussion/comment/43045.
    public static readonly VendorSource HpInstrumentedBios =
        new("HP BIOS sensors", @"root\HP\InstrumentedBIOS", "SELECT * FROM HP_BIOSNumericSensor", Hp);
    public static readonly VendorSource HpAcpiWmi =
        new("HP BIOS sensors", @"root\WMI", "SELECT * FROM HPBIOS_BIOSNumericSensor", Hp);

    // Dell: root\dcim\sysman DCIM_NumericSensor (a CIM_NumericSensor), which only Dell Command |
    // Monitor (formerly OMCI) installs; without it the namespace isn't there and this is skipped.
    // SensorType 2 = Temperature, BaseUnits 2 = Degrees C, UnitModifier -1 (tenths), CurrentReading
    // sint32, ElementName e.g. "Temperature Sensor:CPU Internal Temperature". Many Dell laptops and
    // OptiPlexes leave CurrentReading empty: then nothing. Sources: Dell's community answers with
    // `wmic /namespace:\\root\dcim\sysman path DCIM_NumericSensor where SensorType="2"`
    // (https://dell.com/community/Systems-Management-General/CPU-Temperature-of-Dell-PC/td-p/4200279,
    // https://www.dell.com/community/en/conversations/enterprise-client/temperature-mibs-optiplex-745/647f367ff4ccf8a8ded324b4),
    // Dell Command | Monitor's release notes (all root\DCIM\SYSMAN classes are named DCIM_*), and
    // DMTF CIM_NumericSensor for the units (https://openlmi.readthedocs.io/en/latest/mof/CIM_NumericSensor.html).
    public static readonly VendorSource DellCommandMonitor =
        new("Dell Command | Monitor sensors", @"root\dcim\sysman", "SELECT * FROM DCIM_NumericSensor", Dell);

    // Left out, as nothing could be confirmed: Lenovo (root\Lenovo Lenovo_Battery only exists when
    // Commercial Vantage's policy writes it, and no temperature property is documented), ASUS
    // (AsusAtkWmi_WMNB in root\WMI is a method, DSTS, that needs admin rights and a per-model device
    // id, and the Linux asus-wmi driver found models answering 1 deci-kelvin), root\WMI
    // BatteryTemperature (the battery class driver's WMI copy of the same IOCTL value Lid Awake
    // already reads, and no documentation of the class), and root\cimv2 Win32_TemperatureProbe
    // (Microsoft: WMI doesn't fill CurrentReading, the SMBIOS values are static;
    // https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-temperatureprobe).
    public static readonly IReadOnlyList<VendorSource> All = new[] { HpInstrumentedBios, HpAcpiWmi, DellCommandMonitor };

    // Whole words, so "Battery Temperature" and "BATT Temp" count but "Battery Charger IC" (the
    // charging circuit, part of the board) doesn't.
    static readonly Regex BatteryWord = new(@"\bbatt(ery)?\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex NotTheCells = new(@"\b(charg\w*|ic|fet|circuit|pmic|vr|regulator)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex DieWord = new(@"\b(cpu|gpu|core)\d*\b|\b(processor|package|internal)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static SensorKind Kind(string? label)
    {
        label ??= "";
        if (BatteryWord.IsMatch(label) && !NotTheCells.IsMatch(label)) return SensorKind.Battery;
        return DieWord.IsMatch(label) ? SensorKind.Die : SensorKind.Board;
    }

    public static VendorSensor? Hp(IReadOnlyDictionary<string, object?> row)
    {
        string label = Text(row, "Name") ?? Text(row, "Description") ?? "";
        long? type = Whole(row, "SensorType");
        bool temperature = type == SensorTypeTemperature
            || ((type is null or 0 or 1) && (Mentions(label, "temperature") || Mentions(Text(row, "Description"), "temperature")
                || Mentions(Text(row, "OtherSensorType"), "temperature")));
        if (!temperature) return null;
        if (Whole(row, "OperationalStatus") == NoContact) return null;
        if (string.Equals(Text(row, "CurrentState"), "Not Present", StringComparison.OrdinalIgnoreCase)) return null;
        return Numeric(row, label);
    }

    public static VendorSensor? Dell(IReadOnlyDictionary<string, object?> row)
    {
        if (Whole(row, "SensorType") != SensorTypeTemperature) return null;
        string label = Text(row, "ElementName") ?? Text(row, "Name") ?? Text(row, "DeviceID") ?? "";
        return Numeric(row, label);
    }

    // A CIM_NumericSensor-style reading: CurrentReading x 10^UnitModifier, in BaseUnits. Not a
    // sensor when the unit isn't a temperature (an HP "Thermal Index" in "Other" units) or there
    // is no reading at all.
    static VendorSensor? Numeric(IReadOnlyDictionary<string, object?> row, string label)
    {
        if (Whole(row, "BaseUnits") is not { } units || units is not (DegreesC or DegreesF or DegreesK)
            || Whole(row, "CurrentReading") is not { } reading)
            return null;
        return new VendorSensor(label, Degrees(reading, units, Whole(row, "UnitModifier") ?? 0), Kind(label));
    }

    // A raw value that means "no reading": 0 (no laptop is at exactly 0 °C, as the kelvin sources
    // treat a raw 0), and the usual "not there" fillers, rejected before any scaling (0x7FFF at
    // UnitModifier -3 would otherwise read as a plausible 32.8 °C).
    public static bool IsSentinel(long reading) =>
        reading is 0 or -1 or 0x7FFF or 0xFFFF or 0xFFFFFFFF or int.MinValue or int.MaxValue;

    // °C from a raw reading, its base unit and its power of ten; null for a sentinel, any other unit,
    // a modifier that is garbage, or a value outside the sane range.
    public static double? Degrees(long reading, long baseUnits, long unitModifier)
    {
        if (IsSentinel(reading) || unitModifier is < -MaxModifier or > MaxModifier) return null;
        // Divided for a negative power, so tenths come out as the nearest double (452 -> 45.2).
        double value = unitModifier < 0 ? reading / Math.Pow(10, -unitModifier) : reading * Math.Pow(10, unitModifier);
        double celsius = baseUnits switch
        {
            DegreesC => value,
            DegreesF => (value - 32) * 5 / 9,
            DegreesK => value - 273.15,
            _ => double.NaN,
        };
        return TemperatureMath.IsSane(celsius) ? celsius : null;
    }

    // Every row that is a temperature sensor; a row that can't be read is skipped.
    public static List<VendorSensor> Each(IEnumerable<IReadOnlyDictionary<string, object?>> rows, Func<IReadOnlyDictionary<string, object?>, VendorSensor?> parse)
    {
        var sensors = new List<VendorSensor>();
        foreach (var row in rows)
        {
            try
            {
                if (parse(row) is { } sensor) sensors.Add(sensor);
            }
            catch (Exception) { }
        }
        return sensors;
    }

    static bool Mentions(string? text, string word) => text?.Contains(word, StringComparison.OrdinalIgnoreCase) == true;

    static string? Text(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) && v is string s && s.Trim().Length > 0 ? s.Trim() : null;

    static long? Whole(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) ? WmiValues.Whole(v) : null;
}

// What one WMI query found: its objects' properties (Rows), or that the namespace or class isn't on
// this PC or may not be read here (Missing), or that it failed or took too long this time (Failed).
public enum WmiOutcome { Rows, Missing, Failed }

public sealed record WmiResult(WmiOutcome Outcome, IReadOnlyList<IReadOnlyDictionary<string, object?>> Rows)
{
    public static readonly WmiResult Missing = new(WmiOutcome.Missing, Array.Empty<IReadOnlyDictionary<string, object?>>());
    public static readonly WmiResult Failed = new(WmiOutcome.Failed, Array.Empty<IReadOnlyDictionary<string, object?>>());
    public static WmiResult Of(IReadOnlyList<IReadOnlyDictionary<string, object?>> rows) => new(WmiOutcome.Rows, rows);
}

// One WMI query, so the sources can be tested with made-up objects.
public interface IWmi
{
    WmiResult Query(string scope, string query);
}

// The real WMI: each query on the thread pool, given ThermalZones.QueryTimeout (3 s), through
// OneAtATime: callers of the same query share it, and one stuck past the timeout counts as Failed
// without a second one being started.
public sealed class Wmi : IWmi
{
    public static readonly Wmi Shared = new();
    // More objects than any sensor class has; the rest is ignored.
    public const int MaxRows = 256;

    readonly ConcurrentDictionary<string, OneAtATime<WmiResult>> queries = new();

    public WmiResult Query(string scope, string query) =>
        queries.GetOrAdd(scope + "\n" + query, _ => new OneAtATime<WmiResult>())
            .Run(() => Fetch(scope, query), ThermalZones.QueryTimeout, WmiResult.Failed);

    static WmiResult Fetch(string scope, string query)
    {
        try
        {
            // System.Management's options, not System.IO's (the implicit usings bring in both names).
            var options = new System.Management.EnumerationOptions { Timeout = ThermalZones.QueryTimeout, ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(scope), new ObjectQuery(query), options);
            using var found = searcher.Get();
            var rows = new List<IReadOnlyDictionary<string, object?>>();
            foreach (ManagementBaseObject o in found)
            {
                using (o)
                {
                    var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
                    foreach (PropertyData p in o.Properties) row[p.Name] = p.Value;
                    rows.Add(row);
                }
                if (rows.Count >= MaxRows) break;
            }
            return WmiResult.Of(rows);
        }
        catch (ManagementException e) when (IsMissing(e.ErrorCode)) { return WmiResult.Missing; }
        catch (UnauthorizedAccessException) { return WmiResult.Missing; }
        catch (Exception) { return WmiResult.Failed; }
    }

    // Not on this PC, or not for this user: no namespace (Dell Command | Monitor not installed, not
    // an HP), no class, refused (root\WMI without admin rights), or a provider that won't load.
    public static bool IsMissing(ManagementStatus status) => status is ManagementStatus.InvalidNamespace
        or ManagementStatus.InvalidClass or ManagementStatus.NotFound or ManagementStatus.AccessDenied
        or ManagementStatus.ProviderLoadFailure;
}

// Runs a slow call on the thread pool and waits for it up to a timeout. A caller that comes while a
// call is running waits for that same call, up to the timeout counted from when it started, so
// two users of one query share it; once a call has run past the timeout it is left to finish and
// every caller gets `fallback` at once, without a second call being started. `fallback` also when
// the call throws.
public sealed class OneAtATime<T>
{
    readonly object gate = new();
    Task<T>? pending;
    long startedAt;

    public T Run(Func<T> call, TimeSpan timeout, T fallback)
    {
        try
        {
            Task<T> task;
            long wait = (long)timeout.TotalMilliseconds;
            lock (gate)
            {
                long now = Environment.TickCount64;
                if (pending is { IsCompleted: false })
                {
                    wait -= now - startedAt;
                    if (wait <= 0) return fallback;
                    task = pending;
                }
                else
                {
                    task = pending = Task.Run(call);
                    startedAt = now;
                }
            }
            return task.Wait(TimeSpan.FromMilliseconds(wait)) ? task.Result : fallback;
        }
        catch (Exception) { return fallback; }
    }
}

public static class WmiValues
{
    // A whole-number WMI value (uint8 to uint64 and their signed kinds, as System.Management
    // gives them); anything else (missing, a string, an array) is no value.
    public static long? Whole(object? value) => value switch
    {
        byte b => b,
        sbyte sb => sb,
        ushort us => us,
        short s => s,
        uint u => u,
        int i => i,
        long l => l,
        ulong ul when ul <= long.MaxValue => (long)ul,
        _ => null,
    };
}
