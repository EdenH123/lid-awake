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

// Bytes through the network interface in use since it came up.
public readonly record struct NetCounters(string Iface, bool Wired, ulong Received, ulong Sent);

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
    // No /memory route yet, so "memory" is not listed (section 4). Grows as features land.
    public static readonly string[] Features = Array.Empty<string>();

    readonly ISystemReadings readings;
    readonly object gate = new();
    readonly CpuMeter cpu = new();
    readonly NetMeter net = new();

    public StatusInfo(ISystemReadings readings) => this.readings = readings;

    // `timer` is the "Turn off after" setting: timerMinutes is its length and timerRemaining the
    // time left (6.1). Without one (tests, before the tray exists) the length is 0, never.
    public Dictionary<string, object?> Build(bool lidAwake, AutoOff? timer = null)
    {
        var s = new Dictionary<string, object?>
        {
            // 6.1: the switches and limits Windows does not have get the documented safe defaults.
            ["host"] = readings.HostName(),
            ["lidAwake"] = lidAwake,
            ["timerMinutes"] = timer?.Minutes ?? 0,
            // false, not the documented default true: there are no battery or heat guards on
            // Windows yet (no /limits route), and the page shows a true switch as protection.
            ["batteryGuard"] = false,
            ["heatGuard"] = false,
            ["batteryLimit"] = 10,
            ["maxBatteryTemp"] = 40,
            // "unknown", not the documented "nominal": Windows measures no heat yet, and the page
            // shows a dash for a value it doesn't know, instead of "Normal".
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
            ["apps"] = Array.Empty<object>(),
            ["otherApps"] = Array.Empty<object>(),
            ["claude"] = null,
            // 6.6
            ["os"] = "windows",
            ["features"] = Features,
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
        var netInfo = new Dictionary<string, object?>();
        lock (gate)
        {
            if (times is { } t && cpu.Percent(t, now) is { } percent) mac["cpu"] = percent;
            if (netNow is { } n)
            {
                netInfo["iface"] = n.Iface;
                netInfo["wired"] = n.Wired;
                if (net.Rates(n, now) is { } rate) { netInfo["down"] = rate.Down; netInfo["up"] = rate.Up; }
            }
            else net.Reset();
        }
        s["mac"] = mac;
        s["net"] = netInfo;
        return s;
    }

    // Flag 128 means no battery; 255 means Windows can't tell; a percent over 100 is unknown.
    static bool HasBattery(PowerReading p) =>
        (p.BatteryFlag & 128) == 0 && p.BatteryFlag != 255 && p.BatteryLifePercent <= 100;
}
