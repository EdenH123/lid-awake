using System.Collections.Concurrent;
using System.Management;

namespace LidAwake;

// The temperature sources in order (TemperatureSources.Read uses one of these for the whole app).
// First Windows' own: the battery's sensor (IOCTL), the ACPI thermal zones (MSAcpi_ThermalZoneTemperature,
// admin only on most PCs) and, when those zones say nothing, the Thermal Zone Information counters
// (readable by any user). Only when all of them give nothing: the laptop makers' own WMI sensors
// (VendorSensors.All, in that order), the first one with a sane reading. A vendor source that isn't
// on this PC (no namespace, no class, access refused, no temperature sensor in it), or that failed
// FailuresBeforeRest times in a row, is left alone for Recheck (10 minutes) before it is asked again.
// Each WMI query gets 3 seconds (Wmi). Never throws. Blocks: call it only off the UI thread.
// Which source gave the reading goes to the activity log, once per source per run.
public sealed class TemperatureChain
{
    public static readonly TimeSpan Recheck = TimeSpan.FromMinutes(10);
    public const int FailuresBeforeRest = 3;

    public const string FromBattery = "the battery's own sensor";
    public const string FromAcpiZones = "the ACPI thermal zones";
    public const string FromZoneCounters = "the thermal zone counters";

    readonly Func<double?> battery, acpiZones, zoneCounters;
    readonly IReadOnlyList<VendorSource> vendors;
    readonly IWmi wmi;
    readonly Func<long> clock;
    readonly Action<string>? log;
    readonly object gate = new();
    readonly long?[] restUntil;
    readonly int[] failures;
    readonly HashSet<string> logged = new();

    // `battery`, `acpiZones` and `zoneCounters` are Windows' own sources (°C or null); `clock` is
    // milliseconds that only go forward; `log` is the activity log's Write.
    public TemperatureChain(Func<double?> battery, Func<double?> acpiZones, Func<double?> zoneCounters,
        IReadOnlyList<VendorSource> vendors, IWmi wmi, Func<long>? clock = null, Action<string>? log = null)
    {
        (this.battery, this.acpiZones, this.zoneCounters) = (battery, acpiZones, zoneCounters);
        (this.vendors, this.wmi, this.log) = (vendors, wmi, log);
        this.clock = clock ?? (() => Environment.TickCount64);
        restUntil = new long?[vendors.Count];
        failures = new int[vendors.Count];
    }

    // What gave the last reading ("HP BIOS sensors", "the battery's own sensor"), null when nothing did.
    public string? Source { get; private set; }

    public TemperatureReading Read()
    {
        lock (gate)
        {
            var reading = ReadOnce(out var source);
            Source = source;
            if (source != null && logged.Add(source))
            {
                try { log?.Invoke("Temperature from " + source); }
                catch (Exception) { }
            }
            return reading;
        }
    }

    TemperatureReading ReadOnce(out string? source)
    {
        double? b = Sane(Try(battery)), z = Sane(Try(acpiZones));
        bool counters = false;
        if (z == null) counters = (z = Sane(Try(zoneCounters))) != null;
        if (b != null || z != null)
        {
            var parts = new List<string>();
            if (b != null) parts.Add(FromBattery);
            if (z != null) parts.Add(counters ? FromZoneCounters : FromAcpiZones);
            source = string.Join(" and ", parts);
            return new TemperatureReading(b, z);
        }
        for (int i = 0; i < vendors.Count; i++)
        {
            if (restUntil[i] is { } until && clock() < until) continue;
            restUntil[i] = null;
            var vendor = vendors[i];
            WmiResult result;
            try { result = wmi.Query(vendor.Scope, vendor.Query); }
            catch (Exception) { result = WmiResult.Failed; }
            switch (result.Outcome)
            {
                case WmiOutcome.Missing:
                    Rest(i);
                    continue;
                case WmiOutcome.Failed:
                    if (++failures[i] >= FailuresBeforeRest) Rest(i);
                    continue;
            }
            failures[i] = 0;
            var sensors = VendorSensors.Each(result.Rows, vendor.Parse);
            // The class is there but holds no temperature sensor (fans and voltages only, or none
            // with a reading): not on this PC either.
            if (sensors.Count == 0) { Rest(i); continue; }
            var found = VendorSensors.Hottest(sensors);
            if (found.Any)
            {
                source = vendor.Name;
                return found;
            }
            // Temperature sensors, but none with a sane value this time: asked again next round.
        }
        source = null;
        return TemperatureReading.None;
    }

    void Rest(int i)
    {
        restUntil[i] = clock() + (long)Recheck.TotalMilliseconds;
        failures[i] = 0;
    }

    static double? Try(Func<double?> read)
    {
        try { return read(); }
        catch (Exception) { return null; }
    }

    static double? Sane(double? celsius) => celsius is { } c && TemperatureMath.IsSane(c) ? c : null;
}

// One vendor's sensor class: where it is, and how one of its objects reads (null: not a
// temperature sensor, or one with no reading at all).
public sealed record VendorSource(string Name, string Scope, string Query, Func<IReadOnlyDictionary<string, object?>, VendorSensor?> Parse);

// One temperature sensor: its label, its temperature in °C (null: garbage this time) and whether
// it measures the battery (then it gets the battery's 40 °C limit and is sent as batteryTemp;
// everything else is the processor, the board or a zone, with the 90 °C limit).
public readonly record struct VendorSensor(string Label, double? Celsius, bool Battery);

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

    // Any PC: root\cimv2 Win32_TemperatureProbe, CurrentReading sint32 in tenths of a degree C.
    // Microsoft says WMI doesn't fill CurrentReading today (SMBIOS has no live reading), so this
    // almost never gives anything; it costs one quick query every 10 minutes.
    // https://learn.microsoft.com/en-us/windows/win32/cimwin32prov/win32-temperatureprobe
    public static readonly VendorSource WindowsProbes =
        new("Windows temperature probes", @"root\cimv2", "SELECT * FROM Win32_TemperatureProbe", Probe);

    // Left out, as nothing could be confirmed: Lenovo (root\Lenovo Lenovo_Battery only exists when
    // Commercial Vantage's policy writes it, and no temperature property is documented; Vantage
    // shows the battery temperature, but not through a documented class), ASUS (AsusAtkWmi_WMNB in
    // root\WMI is a method, DSTS, that needs admin rights and a per-model device id, and the Linux
    // asus-wmi driver found models answering 1 deci-kelvin), and root\WMI BatteryTemperature (the
    // battery class driver's WMI copy of the same IOCTL value Lid Awake already reads, and no
    // documentation of the class).
    public static readonly IReadOnlyList<VendorSource> All = new[] { HpInstrumentedBios, HpAcpiWmi, DellCommandMonitor, WindowsProbes };

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

    public static VendorSensor? Probe(IReadOnlyDictionary<string, object?> row)
    {
        if (Whole(row, "CurrentReading") is not { } tenths) return null;
        string label = Text(row, "Name") ?? Text(row, "Description") ?? Text(row, "DeviceID") ?? "";
        return new VendorSensor(label, Degrees(tenths, DegreesC, -1), IsBattery(label));
    }

    // A CIM_NumericSensor-style reading: CurrentReading x 10^UnitModifier, in BaseUnits. Not a
    // sensor when the unit isn't a temperature (an HP "Thermal Index" in "Other" units) or there
    // is no reading at all.
    static VendorSensor? Numeric(IReadOnlyDictionary<string, object?> row, string label)
    {
        if (Whole(row, "BaseUnits") is not { } units || units is not (DegreesC or DegreesF or DegreesK)
            || Whole(row, "CurrentReading") is not { } reading)
            return null;
        return new VendorSensor(label, Degrees(reading, units, Whole(row, "UnitModifier") ?? 0), IsBattery(label));
    }

    // °C from a raw reading, its base unit and its power of ten; null for a raw 0 (no reading: no
    // laptop is at exactly 0 °C, as the kelvin sources treat a raw 0), any other unit, a modifier
    // that is garbage, or a value outside the sane range.
    public static double? Degrees(long reading, long baseUnits, long unitModifier)
    {
        if (reading == 0 || unitModifier is < -MaxModifier or > MaxModifier) return null;
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

    // A sensor labelled for the battery ("Battery Temperature", "BATT Temp"); anything else is the
    // processor, the board or a zone.
    public static bool IsBattery(string? label) => Mentions(label, "batt");

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

    // The hottest battery sensor and the hottest of the others, each null without a sane one.
    public static TemperatureReading Hottest(IEnumerable<VendorSensor> sensors)
    {
        double? battery = null, zone = null;
        foreach (var s in sensors)
        {
            if (s.Celsius is not { } c || !TemperatureMath.IsSane(c)) continue;
            if (s.Battery) { if (battery == null || c > battery) battery = c; }
            else if (zone == null || c > zone) zone = c;
        }
        return new TemperatureReading(battery, zone);
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

// One WMI query, so the vendor sources can be tested with made-up objects.
public interface IWmi
{
    WmiResult Query(string scope, string query);
}

// The real WMI: each query on the thread pool, given ThermalZones.QueryTimeout (3 s); one still
// stuck from an earlier call is left to finish and counts as Failed, without starting a second.
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

// Runs a slow call on the thread pool and waits for it up to a timeout; `fallback` when it takes
// longer or throws, or while the previous call is still running (that one is left to finish, and
// no second one is started).
public sealed class OneAtATime<T>
{
    readonly object gate = new();
    Task<T>? pending;

    public T Run(Func<T> call, TimeSpan timeout, T fallback)
    {
        try
        {
            Task<T> task;
            lock (gate)
            {
                if (pending is { IsCompleted: false }) return fallback;
                task = pending = Task.Run(call);
            }
            return task.Wait(timeout) ? task.Result : fallback;
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
