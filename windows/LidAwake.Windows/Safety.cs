using System.Globalization;
using System.Text.Json;

namespace LidAwake;

// The two safety switches, as on the Mac ("batteryGuard" and "heatGuard" in UserDefaults, both on
// by default): kept in the roaming settings. Read from any thread (the web server's, the safety
// check's timer); a change is saved before it is used.
public sealed class Limits
{
    // The same numbers as main.swift (batteryLimit, maxBatteryTemp) and docs/API.md 6.1.
    public const int BatteryLimit = 10;
    public const int MaxBatteryTemp = 40;
    // The ACPI thermal zones measure the processor or the board, where 40 to 60 °C is normal: they
    // have their own, higher limit (Windows only; the Mac uses the system's thermal state instead).
    public const int MaxPcTemp = 90;

    public const string Battery = "battery";
    public const string Heat = "heat";

    readonly Settings settings;
    volatile bool batteryGuard;
    volatile bool heatGuard;

    public Limits(Settings settings)
    {
        this.settings = settings;
        batteryGuard = Saved("batteryGuard");
        heatGuard = Saved("heatGuard");
    }

    // A damaged value (not true or false) counts as the default, on.
    bool Saved(string key)
    {
        try { return settings.Get<bool?>(key) ?? true; }
        catch (JsonException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    public bool BatteryGuard => batteryGuard;
    public bool HeatGuard => heatGuard;

    // The names POST /limits accepts.
    public static bool IsName(string? name) => name is Battery or Heat;

    // Turn one switch on or off. False for a name that isn't one of the two.
    public bool Set(string name, bool on)
    {
        switch (name)
        {
            case Battery:
                settings.Set("batteryGuard", on);
                batteryGuard = on;
                return true;
            case Heat:
                settings.Set("heatGuard", on);
                heatGuard = on;
                return true;
            default:
                return false;
        }
    }
}

// The last temperature readings, so /status and a turn-on never wait for the hardware. Only
// Refresh reads them (the safety check's background timer, every 15 seconds while on; the tray
// menu opening; a refused turn-on), never more than one read at a time. A read that fails, throws
// or gives garbage is a miss for that source: its last good value is kept through
// MissesKept - 1 misses and dropped by the next one (so it is used for at most about 45 seconds
// while on). It never affects anything else.
public sealed class HeatSensor
{
    // While on, the safety check's timer reads every 15 seconds (CheckEvery). A reading younger
    // than Fresh is used again instead (a late timer callback, the menu opened twice).
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);
    // While off, once a minute: enough for the tray's heat item and a turn-on's check.
    public static readonly TimeSpan WhileOff = TimeSpan.FromSeconds(60);
    // The third miss in a row drops a source's last good value (3 x 15 s = 45 s while on).
    public const int MissesKept = 3;

    readonly Func<TemperatureReading> source;
    readonly Func<long> clock;
    readonly object gate = new();
    readonly object reading = new();
    double? battery, zone;
    int batteryMisses, zoneMisses;
    long? readAt;

    // Raised (on the reading thread) when the first read is done, and whenever a temperature
    // starts or stops being available.
    public event Action? Changed;

    // `source` is TemperatureSources.Read on the PC; `clock` is milliseconds that only go forward.
    public HeatSensor(Func<TemperatureReading> source, Func<long>? clock = null)
    {
        this.source = source;
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    // The last good readings (each null: none yet, or dropped). Never touches the hardware.
    public TemperatureReading Latest
    {
        get { lock (gate) return new TemperatureReading(battery, zone); }
    }

    // Some temperature source works: the page shows the heat switch, the tray item is available.
    public bool Available => Latest.Any;

    // Whether any read has finished yet.
    public bool HasRead
    {
        get { lock (gate) return readAt != null; }
    }

    // Reads the sources again unless the last read is younger than `maxAge`. Blocks while they
    // are read (up to about 3 seconds): call it only off the UI thread.
    public TemperatureReading Refresh(TimeSpan maxAge)
    {
        lock (reading)
        {
            lock (gate)
            {
                if (readAt is { } at && clock() - at < (long)maxAge.TotalMilliseconds)
                    return new TemperatureReading(battery, zone);
            }
            TemperatureReading now;
            try { now = source(); }
            catch (Exception) { now = TemperatureReading.None; }
            bool changed;
            TemperatureReading kept;
            lock (gate)
            {
                bool before = battery != null || zone != null;
                bool first = readAt == null;
                Keep(ref battery, ref batteryMisses, Sane(now.Battery));
                Keep(ref zone, ref zoneMisses, Sane(now.Zone));
                readAt = clock();
                kept = new TemperatureReading(battery, zone);
                changed = first || before != kept.Any;
            }
            if (changed)
            {
                try { Changed?.Invoke(); }
                catch (Exception) { }
            }
            return kept;
        }
    }

    static double? Sane(double? celsius) => celsius is { } c && TemperatureMath.IsSane(c) ? c : null;

    // A good value replaces the kept one; a miss keeps it until the MissesKept-th miss in a row.
    static void Keep(ref double? kept, ref int misses, double? now)
    {
        if (now != null) { kept = now; misses = 0; return; }
        if (kept == null) { misses = 0; return; }
        if (++misses >= MissesKept) { kept = null; misses = 0; }
    }
}

// Why Lid Awake must not be on right now: the Mac's reasons (safetyStop), worded for Windows.
// `Alert` is the phone alert's wording (safetyStopAlertReason): "Battery reached 9%".
public sealed record SafetyReason(string Why, string Short, string? Alert = null)
{
    // The phone alert, "<reason> after 2 h 5 min. Your PC can sleep now." (PhoneAlerts.TurnedOff).
    public string AlertReason => Alert ?? Why.TrimEnd('.');
    // The activity log, when it turns off: "Turned off: battery below 10%".
    public string LogOff => "Turned off: " + Short;
    // The activity log, when a turn-on is refused.
    public string LogRefused => "Not turned on: " + Short;
    // The tray balloons.
    public string OffBody => Why + " Turned off so your PC can sleep.";
    public string RefusedBody => Why + " Can't keep your PC awake right now.";
}

public static class Safety
{
    // The Mac's safetyStop order: the battery limit first (on battery and below 10%), then heat:
    // the battery at or above 40 °C ("Battery is too hot", the Mac's words), then the thermal
    // zones at or above 90 °C (the PC itself). Plugged in, a low battery is no reason. No
    // temperature: heat is no reason.
    public static SafetyReason? Decide(bool batteryGuard, bool heatGuard, PowerReading? power, TemperatureReading heat)
    {
        if (batteryGuard && power is { } p && p.AcLineStatus == 0 && StatusInfo.HasBattery(p) && p.BatteryLifePercent < Limits.BatteryLimit)
            return new SafetyReason($"Battery is at {p.BatteryLifePercent}%.", $"battery below {Limits.BatteryLimit}%",
                $"Battery reached {p.BatteryLifePercent}%");
        if (!heatGuard) return null;
        if (heat.Battery is { } b && TemperatureMath.IsSane(b) && b >= Limits.MaxBatteryTemp)
            return new SafetyReason($"Battery is too hot ({Shown(b)}°C).", $"battery too hot ({Shown(b)}°C)",
                $"Battery got too hot ({Shown(b)}°C)");
        if (heat.Zone is { } z && TemperatureMath.IsSane(z) && z >= Limits.MaxPcTemp)
            return new SafetyReason($"Your PC is too hot ({Shown(z)}°C).", $"PC too hot ({Shown(z)}°C)",
                $"Your PC got too hot ({Shown(z)}°C)");
        return null;
    }

    static string Shown(double celsius) => TemperatureMath.OneDecimal(celsius).ToString("0.0", CultureInfo.InvariantCulture);
}

// The decision with this PC's readings: the switches, the power status (GetSystemPowerStatus, a
// quick call) and the last good temperatures (HeatSensor.Latest, never the hardware).
public sealed class SafetyCheck
{
    readonly Limits limits;
    readonly HeatSensor heat;
    readonly Func<PowerReading?> power;

    public SafetyCheck(Limits limits, HeatSensor heat, Func<PowerReading?> power)
    {
        (this.limits, this.heat, this.power) = (limits, heat, power);
    }

    // Quick: safe on the UI thread (a turn-on from the tray).
    public SafetyReason? Now()
    {
        PowerReading? p;
        try { p = power(); }
        catch (Exception) { p = null; }
        return Safety.Decide(limits.BatteryGuard, limits.HeatGuard, p, heat.Latest);
    }
}

// The tray's turn-off for a safety reason, run on the UI thread. It decides again when it runs,
// so a second post queued behind the first (two checks that found the same reason) does nothing:
// the first one already turned Lid Awake off.
public sealed class SafetyStop
{
    readonly Func<bool> isOn;
    readonly SafetyCheck safety;
    readonly Action<SafetyReason> turnOff;

    public SafetyStop(Func<bool> isOn, SafetyCheck safety, Action<SafetyReason> turnOff)
    {
        (this.isOn, this.safety, this.turnOff) = (isOn, safety, turnOff);
    }

    // True when it turned off. `turnOff` must leave isOn false.
    public bool Run()
    {
        if (!isOn() || safety.Now() is not { } why) return false;
        turnOff(why);
        return true;
    }
}

// Runs `run(read)` for every Request, never two at once, and never drops one: a request made while
// a run is going gets one more run after it (several such requests share it), and `read` asked by
// any of them is carried into that run. The run happens on the thread of whichever request finds
// nothing running. An exception from `run` is swallowed (it is the safety check's timer).
public sealed class Coalescer
{
    readonly Action<bool> run;
    int running;      // 1 while some thread is running
    int pending;      // 1 when a run is owed
    int pendingRead;  // 1 when the owed run must read

    public Coalescer(Action<bool> run) => this.run = run;

    public void Request(bool read)
    {
        if (read) Interlocked.Exchange(ref pendingRead, 1);
        Interlocked.Exchange(ref pending, 1);
        // Whoever gets `running` runs everything owed. After letting go it looks again: a request
        // that came just before it let go (and found it still running) is run here.
        while (Interlocked.CompareExchange(ref running, 1, 0) == 0)
        {
            try
            {
                while (Interlocked.Exchange(ref pending, 0) == 1)
                {
                    bool r = Interlocked.Exchange(ref pendingRead, 0) == 1;
                    try { run(r); }
                    catch (Exception) { }
                }
            }
            finally { Interlocked.Exchange(ref running, 0); }
            if (Volatile.Read(ref pending) == 0) return;
        }
    }
}
