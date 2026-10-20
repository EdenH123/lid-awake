namespace LidAwake;

// What Windows tells us about the PC, as plain numbers. The real answers come from SystemReadings
// (Win32 calls); the tests give StatusInfo a fake, so the rules below can be checked anywhere.
public interface ISystemReadings
{
    PowerReading? Power();      // null: the call failed
    MemoryReading? Memory();
    CpuTimes? Cpu();
    DiskReading? Disk();
    NetCounters? Network();     // null: no network interface in use
    // The connected Wi-Fi, preferring the adapter with this NetworkInterface.Id. null: no reading.
    WifiReading? Wifi(string? adapterId);
    long UptimeSeconds();
    int ScreenWidth();          // 0 when unknown
    string HostName();
    long Milliseconds();        // a clock that only goes forward, for measuring rates
}

// GetSystemPowerStatus, as it comes: AC 0 = battery, 1 = plugged in, 255 = unknown. Flag bit 8 =
// charging, 128 = no battery, 255 = unknown. Percent 0 to 100, 255 = unknown. LifeSeconds -1 = unknown.
public readonly record struct PowerReading(byte AcLineStatus, byte BatteryFlag, byte BatteryLifePercent, long LifeSeconds);

public readonly record struct MemoryReading(ulong Total, ulong Available);

// GetSystemTimes, in 100-nanosecond units since boot. Kernel time includes idle time.
public readonly record struct CpuTimes(ulong Idle, ulong Kernel, ulong User);

public readonly record struct DiskReading(long Free, long Total);

// Bytes through the network interface in use since it came up. Id is NetworkInterface.Id (the
// adapter GUID in braces), used to read that adapter's Wi-Fi signal; null when unknown.
public readonly record struct NetCounters(string Iface, bool Wired, ulong Received, ulong Sent, string? Id = null);

// Processor load between two calls, like the Mac: the first call has no answer.
public sealed class CpuMeter
{
    public const long MinMilliseconds = 200;
    CpuTimes? last;
    long lastMs;

    // Percent busy (0 to 100, one decimal) since the previous accepted call, or null.
    public double? Percent(CpuTimes now, long nowMs)
    {
        if (last is not { } before) { last = now; lastMs = nowMs; return null; }
        // Asked again too soon (or the counters went backwards): keep the old starting point
        // when it is only too soon, so the next answer covers a longer, steadier stretch.
        if (nowMs - lastMs < MinMilliseconds) return null;
        if (now.Idle < before.Idle || now.Kernel < before.Kernel || now.User < before.User)
        { last = now; lastMs = nowMs; return null; }
        ulong total = (now.Kernel - before.Kernel) + (now.User - before.User);
        if (total == 0) return null;
        ulong idle = now.Idle - before.Idle;
        ulong busy = total > idle ? total - idle : 0;
        last = now;
        lastMs = nowMs;
        return Math.Round(Math.Clamp(busy * 100.0 / total, 0.0, 100.0), 1);
    }
}

// Network speed between two calls on the same interface.
public sealed class NetMeter
{
    public const long MinMilliseconds = 200;
    NetCounters? last;
    long lastMs;

    public void Reset() => last = null;

    // Bytes per second received and sent since the previous accepted call, or null.
    public (double Down, double Up)? Rates(NetCounters now, long nowMs)
    {
        if (last is not { } before || before.Iface != now.Iface) { last = now; lastMs = nowMs; return null; }
        if (nowMs - lastMs < MinMilliseconds) return null;
        // A counter that went down (the adapter was reset): start again from here.
        bool backwards = now.Received < before.Received || now.Sent < before.Sent;
        double seconds = (nowMs - lastMs) / 1000.0;
        last = now;
        lastMs = nowMs;
        if (backwards) return null;
        return (Math.Round((now.Received - before.Received) / seconds, 1), Math.Round((now.Sent - before.Sent) / seconds, 1));
    }
}

// GET /status on Windows: every field the Mac sends (docs/API.md section 6), with the value
// Windows can measure, and the documented safe default for what it can't. One instance serves
// all requests, because processor load and network speed are measured between two requests.
public sealed class StatusInfo
{
    // Section 4. "limits" (POST /limits and the low-battery switch) and "history" (the graphs,
    // recorded by Lid Awake itself) always; "heat" (the too-hot switch) only while a temperature
    // source works, re-checked with each status. No /memory route yet, so "memory" is not listed.
    // Grows as features land.
    // "passkeys": Face ID / fingerprint sign-in (/passkey/*). "push": phone alerts (/push/*).
    public static readonly string[] Features = { "limits", "history", "passkeys", "push" };
    public static readonly string[] FeaturesWithHeat = { "limits", "history", "heat", "passkeys", "push" };

    readonly ISystemReadings readings;
    readonly Limits? limits;
    readonly HeatSensor? heat;
    readonly PhoneApps? apps;
    readonly object gate = new();
    readonly CpuMeter cpu = new();
    readonly NetMeter net = new();

    // `limits` are the two safety switches (none: both on, the documented default). `heat` holds
    // the last good temperatures (none: no temperature, so no "heat" and no batteryTemp). `apps` is
    // the owner's list for the phone's Apps page (none: empty lists, and no "apps" in features).
    public StatusInfo(ISystemReadings readings, Limits? limits = null, HeatSensor? heat = null, PhoneApps? apps = null)
    {
        this.apps = apps;
        this.readings = readings;
        this.limits = limits;
        this.heat = heat;
    }

    // `timer` is the "Turn off after" setting: timerMinutes is its length and timerRemaining the
    // time left (6.1). Without one (tests, before the tray exists) the length is 0, never.
    public Dictionary<string, object?> Build(bool lidAwake, AutoOff? timer = null)
    {
        // The cached reading only: /status never waits for the hardware.
        var temperature = heat?.Latest ?? TemperatureReading.None;
        // One look at the running programs for both lists.
        var appLists = AppLists();
        var s = new Dictionary<string, object?>
        {
            // 6.1
            ["host"] = readings.HostName(),
            ["lidAwake"] = lidAwake,
            ["timerMinutes"] = timer?.Minutes ?? 0,
            // The stored switches. Without a temperature heatGuard is still the stored setting
            // (the page hides its row: no "heat" in features).
            ["batteryGuard"] = limits?.BatteryGuard ?? true,
            ["heatGuard"] = limits?.HeatGuard ?? true,
            ["batteryLimit"] = Limits.BatteryLimit,
            ["maxBatteryTemp"] = Limits.MaxBatteryTemp,
            // "unknown", not the documented "nominal": Windows has no system thermal state like
            // the Mac's, and the page shows a dash for a value it doesn't know, instead of "Normal".
            ["thermalState"] = "unknown",
            // 6.2
            ["remoteScreen"] = false,
            ["remoteControl"] = false,
            ["cameraAllowed"] = false,
            ["screenPermission"] = false,
            ["controlPermission"] = false,
            ["screenWidth"] = Math.Max(0, readings.ScreenWidth()),
            ["screenOff"] = false,
            ["lidClosed"] = false,
            ["displayAsleep"] = false,
            // 6.3
            ["apps"] = appLists.Apps,
            ["otherApps"] = appLists.Others,
            ["claude"] = null,
            // 6.6
            ["os"] = "windows",
            // "heat" while either source works: the battery's sensor or the thermal zones.
            ["features"] = FeatureList(temperature.Any),
        };

        // timerRemaining: omitted unless on, with a timer, and in the future.
        if (lidAwake && timer?.Remaining() is { } left) s["timerRemaining"] = left;

        // 6.4: a PC with no battery sends none of these.
        if (readings.Power() is { } p && HasBattery(p))
        {
            // AC line status 255 means Windows can't tell: then onBattery is left out (the page
            // would read false as "Plugged in").
            bool acKnown = p.AcLineStatus != 255;
            bool onBattery = p.AcLineStatus == 0;
            bool charging = (p.BatteryFlag & 8) != 0;
            s["battery"] = (int)p.BatteryLifePercent;
            if (acKnown) s["onBattery"] = onBattery;
            s["charging"] = charging;
            s["charged"] = p.AcLineStatus == 1 && p.BatteryLifePercent == 100;
            // Windows only estimates the time left on battery, and never the time to full.
            s["minutesToEmpty"] = onBattery && p.LifeSeconds > 0 ? (int)(p.LifeSeconds / 60) : -1;
            s["minutesToFull"] = -1;
        }
        // 6.4: on its own, like the Mac, and only from the battery's own sensor (a thermal zone
        // measures the processor or the board, not the battery).
        if (temperature.Battery is { } celsius) s["batteryTemp"] = TemperatureMath.OneDecimal(celsius);

        long now = readings.Milliseconds();
        var memory = readings.Memory();
        var mac = new Dictionary<string, object?>
        {
            ["memTotal"] = memory?.Total ?? 0UL,
            ["uptime"] = Math.Max(0L, readings.UptimeSeconds()),
        };
        if (memory is { } m) mac["memUsed"] = m.Total >= m.Available ? m.Total - m.Available : 0UL;
        if (readings.Disk() is { } d) { mac["diskFree"] = d.Free; mac["diskTotal"] = d.Total; }
        var times = readings.Cpu();
        var netNow = readings.Network();
        // Wi-Fi signal only for an adapter in use that isn't wired; outside the lock (a native call).
        var wifi = netNow is { Wired: false } w ? ReadWifi(w.Id) : null;
        var netInfo = new Dictionary<string, object?>();
        lock (gate)
        {
            if (times is { } t && cpu.Percent(t, now) is { } percent) mac["cpu"] = percent;
            if (netNow is { } n)
            {
                netInfo["iface"] = n.Iface;
                netInfo["wired"] = n.Wired;
                if (wifi is { } r)
                {
                    netInfo["rssi"] = RssiFromQuality(r.Quality);
                    if (!string.IsNullOrEmpty(r.Ssid)) netInfo["wifi"] = r.Ssid;
                }
                if (net.Rates(n, now) is { } rate) { netInfo["down"] = rate.Down; netInfo["up"] = rate.Up; }
            }
            else net.Reset();
        }
        s["mac"] = mac;
        s["net"] = netInfo;
        return s;
    }

    // "apps" is listed only when the apps routes exist (an app list was given).
    string[] FeatureList(bool withHeat)
    {
        var list = withHeat ? FeaturesWithHeat : Features;
        return apps == null ? list : list.Append("apps").ToArray();
    }

    // Never lets a failing process or settings read take the whole status down: an empty list.
    (object Apps, object Others) AppLists()
    {
        if (apps == null) return (Array.Empty<object>(), Array.Empty<object>());
        try
        {
            var lists = apps.Lists();
            return (lists.Apps, lists.Others);
        }
        catch (Exception) { return (Array.Empty<object>(), Array.Empty<object>()); }
    }

    // Windows gives Wi-Fi signal as a quality from 0 to 100, not dBm. The usual approximation:
    // quality = 2 × (dBm + 100), so dBm = quality / 2 − 100, from −100 (0) to −50 (100). A whole
    // number, and never 0, as section 6.5 asks.
    public static int RssiFromQuality(int quality) => Math.Clamp(Math.Clamp(quality, 0, 100) / 2 - 100, -100, -50);

    // A failing Wi-Fi reading only leaves rssi and wifi out; the rest of the status is unaffected.
    WifiReading? ReadWifi(string? adapterId)
    {
        try { return readings.Wifi(adapterId); }
        catch (Exception) { return null; }
    }

    // Flag 128 means no battery; 255 means Windows can't tell; a percent over 100 is unknown.
    internal static bool HasBattery(PowerReading p) =>
        (p.BatteryFlag & 128) == 0 && p.BatteryFlag != 255 && p.BatteryLifePercent <= 100;
}
