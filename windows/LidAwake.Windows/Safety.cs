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

// The last temperature read, so /status and a turn-on never wait for the hardware. Only Refresh
// reads it (on the safety check's background timer, every 15 seconds while on), and never more
// than one read at a time. A read that fails or throws is "no reading": it never affects the rest.
public sealed class HeatSensor
{
    // While on, the safety check's timer reads every 15 seconds (CheckEvery). A reading younger
    // than Fresh is used again instead (a timer callback that came late, then one on time).
    public static readonly TimeSpan CheckEvery = TimeSpan.FromSeconds(15);
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);
    // While off, once a minute: enough for the tray's heat item and a turn-on's check.
    public static readonly TimeSpan WhileOff = TimeSpan.FromSeconds(60);

    readonly Func<double?> source;
    readonly Func<long> clock;
    readonly object gate = new();
    readonly object reading = new();
    double? last;
    long? readAt;

    // Raised (on the reading thread) when the first read is done, and whenever a temperature
    // starts or stops being available.
    public event Action? Changed;

    // `source` is TemperatureSources.Read on the PC; `clock` is milliseconds that only go forward.
    public HeatSensor(Func<double?> source, Func<long>? clock = null)
    {
        this.source = source;
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    // The last reading in °C, or null (none yet, or the last read failed). Never touches the hardware.
    public double? Latest
    {
        get { lock (gate) return last; }
    }

    // A temperature source works: the last read gave a value.
    public bool Available => Latest != null;

    // Whether any read has finished yet.
    public bool HasRead
    {
        get { lock (gate) return readAt != null; }
    }

    // Reads the source again unless the last reading is younger than `maxAge`. Blocks while the
    // source is read (up to about 3 seconds): call it only off the UI thread.
    public double? Refresh(TimeSpan maxAge)
    {
        lock (reading)
        {
            lock (gate)
            {
                if (readAt is { } at && clock() - at < (long)maxAge.TotalMilliseconds) return last;
            }
            double? value;
            try { value = source(); }
            catch (Exception) { value = null; }
            if (value is { } v && !TemperatureMath.IsSane(v)) value = null;
            bool changed;
            lock (gate)
            {
                changed = readAt == null || (last != null) != (value != null);
                last = value;
                readAt = clock();
            }
            if (changed)
            {
                try { Changed?.Invoke(); }
                catch (Exception) { }
            }
            return value;
        }
    }
}

// Why Lid Awake must not be on right now: the Mac's reasons (safetyStop), worded for Windows.
public sealed record SafetyReason(string Why, string Short)
{
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
    // The Mac's safetyStop: the battery limit first (on battery and below 10%), then heat (at or
    // above 40 °C). Plugged in, a low battery is no reason. No temperature: heat is no reason.
    public static SafetyReason? Decide(bool batteryGuard, bool heatGuard, PowerReading? power, double? celsius)
    {
        if (batteryGuard && power is { } p && p.AcLineStatus == 0 && StatusInfo.HasBattery(p) && p.BatteryLifePercent < Limits.BatteryLimit)
            return new SafetyReason($"Battery is at {p.BatteryLifePercent}%.", $"battery below {Limits.BatteryLimit}%");
        if (heatGuard && celsius is { } t && TemperatureMath.IsSane(t) && t >= Limits.MaxBatteryTemp)
        {
            var shown = TemperatureMath.OneDecimal(t).ToString("0.0", CultureInfo.InvariantCulture);
            return new SafetyReason($"Your PC is too hot ({shown}°C).", $"too hot ({shown}°C)");
        }
        return null;
    }
}

// The decision with this PC's readings: the switches, the power status (GetSystemPowerStatus, a
// quick call) and the last temperature read (HeatSensor.Latest, never the hardware).
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
