using System.Text.Json;
using LidAwake;
using Xunit;

// Readings that tests set by hand.
public sealed class FakeReadings : ISystemReadings
{
    public PowerReading? PowerNow;
    public MemoryReading? MemoryNow = new(16UL << 30, 4UL << 30);
    public CpuTimes? CpuNow;
    public DiskReading? DiskNow = new(200_000_000_000, 500_000_000_000);
    public NetCounters? NetNow;
    public long Uptime = 90_000;
    public int Width = 1920;
    public string Host = "DESKTOP-TEST";
    public long Ms = 1_000_000;

    public PowerReading? Power() => PowerNow;
    public MemoryReading? Memory() => MemoryNow;
    public CpuTimes? Cpu() => CpuNow;
    public DiskReading? Disk() => DiskNow;
    public NetCounters? Network() => NetNow;
    public long UptimeSeconds() => Uptime;
    public int ScreenWidth() => Width;
    public string HostName() => Host;
    public long Milliseconds() => Ms;
}

public class StatusInfoTests
{
    static JsonElement Run(Dictionary<string, object?> status)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(status));
        return doc.RootElement.Clone();
    }

    static JsonElement Run(FakeReadings fake, bool on = true) => Run(new StatusInfo(fake).Build(on));

    static bool IsBool(JsonElement e) => e.ValueKind is JsonValueKind.True or JsonValueKind.False;

    static readonly string[] PowerKeys = { "battery", "onBattery", "charging", "charged", "minutesToEmpty", "minutesToFull", "batteryTemp" };

    // Every key the Mac always sends (docs/API.md section 6), with the kind of JSON value the page
    // expects. Shared with the web server test, which checks the same answer over HTTP.
    // Every top-level key section 6 allows (always sent, or sent only sometimes).
    static readonly HashSet<string> AllowedTopLevel = new()
    {
        "host", "lidAwake", "timerMinutes", "timerRemaining", "batteryGuard", "heatGuard", "batteryLimit", "maxBatteryTemp", "thermalState",
        "remoteScreen", "remoteControl", "cameraAllowed", "screenPermission", "controlPermission", "screenWidth", "screenOff", "lidClosed", "displayAsleep",
        "apps", "otherApps", "claude",
        "battery", "onBattery", "charging", "charged", "minutesToEmpty", "minutesToFull", "batteryTemp",
        "mac", "net", "link", "os", "features",
    };

    public static void AssertEveryKeyWithItsKind(JsonElement s)
    {
        Assert.Equal(JsonValueKind.Object, s.ValueKind);
        // Nothing outside section 6 at the top level (memTotal and uptime belong inside "mac").
        foreach (var property in s.EnumerateObject())
            Assert.True(AllowedTopLevel.Contains(property.Name), "unexpected top-level key: " + property.Name);
        // 6.1
        Assert.Equal(JsonValueKind.String, s.GetProperty("host").ValueKind);
        Assert.True(IsBool(s.GetProperty("lidAwake")));
        Assert.Equal(JsonValueKind.Number, s.GetProperty("timerMinutes").ValueKind);
        Assert.True(IsBool(s.GetProperty("batteryGuard")));
        Assert.True(IsBool(s.GetProperty("heatGuard")));
        Assert.Equal(JsonValueKind.Number, s.GetProperty("batteryLimit").ValueKind);
        Assert.Equal(JsonValueKind.Number, s.GetProperty("maxBatteryTemp").ValueKind);
        Assert.Equal(JsonValueKind.String, s.GetProperty("thermalState").ValueKind);
        // 6.2
        foreach (var key in new[] { "remoteScreen", "remoteControl", "cameraAllowed", "screenPermission", "controlPermission", "screenOff", "lidClosed", "displayAsleep" })
            Assert.True(IsBool(s.GetProperty(key)), key);
        Assert.Equal(JsonValueKind.Number, s.GetProperty("screenWidth").ValueKind);
        // 6.3
        Assert.Equal(JsonValueKind.Array, s.GetProperty("apps").ValueKind);
        Assert.Equal(JsonValueKind.Array, s.GetProperty("otherApps").ValueKind);
        Assert.Equal(JsonValueKind.Null, s.GetProperty("claude").ValueKind);
        // 6.5: both objects are always there; memTotal and uptime are always in "mac".
        var mac = s.GetProperty("mac");
        Assert.Equal(JsonValueKind.Object, mac.ValueKind);
        Assert.Equal(JsonValueKind.Number, mac.GetProperty("memTotal").ValueKind);
        Assert.Equal(JsonValueKind.Number, mac.GetProperty("uptime").ValueKind);
        Assert.Equal(JsonValueKind.Object, s.GetProperty("net").ValueKind);
        // 6.6
        Assert.Equal(JsonValueKind.String, s.GetProperty("os").ValueKind);
        Assert.Equal(JsonValueKind.Array, s.GetProperty("features").ValueKind);
    }

    [Fact]
    public void EveryKeyTheMacAlwaysSendsIsThereWithTheRightKind()
    {
        var s = Run(new FakeReadings
        {
            CpuNow = new(0, 0, 0),
            NetNow = new("Ethernet", true, 0, 0),
        });
        AssertEveryKeyWithItsKind(s);
        Assert.Equal("DESKTOP-TEST", s.GetProperty("host").GetString());
    }

    [Fact]
    public void SafeDefaultsAreTheDocumentedOnes()
    {
        var s = Run(new FakeReadings(), on: false);
        Assert.False(s.GetProperty("lidAwake").GetBoolean());
        Assert.Equal(0, s.GetProperty("timerMinutes").GetInt32());
        Assert.False(s.TryGetProperty("timerRemaining", out _));
        // false: there are no battery or heat guards on Windows yet, so the page must not show them on.
        Assert.False(s.GetProperty("batteryGuard").GetBoolean());
        Assert.False(s.GetProperty("heatGuard").GetBoolean());
        Assert.Equal(10, s.GetProperty("batteryLimit").GetInt32());
        Assert.Equal(40, s.GetProperty("maxBatteryTemp").GetInt32());
        // Not measured on Windows: "unknown", which the page shows as a dash (not "Normal").
        Assert.Equal("unknown", s.GetProperty("thermalState").GetString());
        foreach (var key in new[] { "remoteScreen", "remoteControl", "cameraAllowed", "screenPermission", "controlPermission", "screenOff", "lidClosed", "displayAsleep" })
            Assert.False(s.GetProperty(key).GetBoolean(), key);
        Assert.Equal(0, s.GetProperty("apps").GetArrayLength());
        Assert.Equal(0, s.GetProperty("otherApps").GetArrayLength());
        Assert.False(s.TryGetProperty("link", out _));
    }

    // The "Turn off after" setting, on a settings folder of its own and a clock the test moves.
    sealed class TimerFixture : IDisposable
    {
        readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        public DateTimeOffset Clock = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
        public AutoOff Timer;

        public TimerFixture() => Timer = new AutoOff(new Settings(folder), () => Clock);

        public void Dispose()
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    [Fact]
    public void TimerMinutesIsTheChosenLengthAndTimerRemainingIsOmittedWithoutACountdown()
    {
        using var f = new TimerFixture();
        f.Timer.Set(60);
        var info = new StatusInfo(new FakeReadings());
        // Chosen but not started: the length is there, the time left is not.
        var s = Run(info.Build(true, f.Timer));
        Assert.Equal(60, s.GetProperty("timerMinutes").GetInt32());
        Assert.False(s.TryGetProperty("timerRemaining", out _));
        // No timer at all: 0, and no time left.
        f.Timer.Set(0);
        f.Timer.Start();
        s = Run(info.Build(true, f.Timer));
        Assert.Equal(0, s.GetProperty("timerMinutes").GetInt32());
        Assert.False(s.TryGetProperty("timerRemaining", out _));
        AssertEveryKeyWithItsKind(s);
    }

    [Fact]
    public void TimerRemainingIsTheSecondsLeftWhileOnWithATimer()
    {
        using var f = new TimerFixture();
        f.Timer.Set(15);
        f.Timer.Start();
        var info = new StatusInfo(new FakeReadings());
        var s = Run(info.Build(true, f.Timer));
        Assert.Equal(15, s.GetProperty("timerMinutes").GetInt32());
        Assert.Equal(JsonValueKind.Number, s.GetProperty("timerRemaining").ValueKind);
        Assert.Equal(900.0, s.GetProperty("timerRemaining").GetDouble(), 3);
        AssertEveryKeyWithItsKind(s);

        f.Clock += TimeSpan.FromSeconds(90.5);
        Assert.Equal(809.5, Run(info.Build(true, f.Timer)).GetProperty("timerRemaining").GetDouble(), 3);
    }

    [Fact]
    public void TimerRemainingIsOmittedWhenOffOrWhenTheTimeHasPassed()
    {
        using var f = new TimerFixture();
        f.Timer.Set(15);
        f.Timer.Start();
        var info = new StatusInfo(new FakeReadings());
        // The caller says off (the tray's switch is the authority on lidAwake): no countdown shown.
        var off = Run(info.Build(false, f.Timer));
        Assert.False(off.GetProperty("lidAwake").GetBoolean());
        Assert.False(off.TryGetProperty("timerRemaining", out _));
        Assert.Equal(15, off.GetProperty("timerMinutes").GetInt32());

        f.Clock += TimeSpan.FromMinutes(16);
        Assert.False(Run(info.Build(true, f.Timer)).TryGetProperty("timerRemaining", out _));

        f.Timer.Stop();
        f.Clock -= TimeSpan.FromMinutes(16);
        Assert.False(Run(info.Build(true, f.Timer)).TryGetProperty("timerRemaining", out _));
    }

    [Fact]
    public void OsIsWindowsAndFeaturesAreOnlyWhatIsImplemented()
    {
        var s = Run(new FakeReadings());
        Assert.Equal("windows", s.GetProperty("os").GetString());
        // There is no /memory route (or any other optional one) yet, so none is listed.
        Assert.Equal(0, s.GetProperty("features").GetArrayLength());
    }

    [Fact]
    public void LidAwakeFollowsTheSwitch()
    {
        Assert.True(Run(new FakeReadings(), on: true).GetProperty("lidAwake").GetBoolean());
        Assert.False(Run(new FakeReadings(), on: false).GetProperty("lidAwake").GetBoolean());
    }

    [Fact]
    public void HostAndScreenWidthComeFromTheReadings()
    {
        var s = Run(new FakeReadings { Host = "MY-PC", Width = 2560 });
        Assert.Equal("MY-PC", s.GetProperty("host").GetString());
        Assert.Equal(2560, s.GetProperty("screenWidth").GetInt32());
        Assert.Equal(0, Run(new FakeReadings { Width = -5 }).GetProperty("screenWidth").GetInt32());
    }

    // ---- 6.4 power ----

    [Fact]
    public void NoBatteryOmitsEveryPowerKey()
    {
        // BatteryFlag 128 = no system battery (a desktop, or a CI machine).
        foreach (var power in new PowerReading?[]
        {
            new PowerReading(1, 128, 255, -1),
            new PowerReading(1, 128, 100, -1),
            null,                                   // the call failed
            new PowerReading(255, 255, 255, -1),    // Windows can't tell
            new PowerReading(0, 1, 255, -1),        // a battery, but no percent
        })
        {
            var s = Run(new FakeReadings { PowerNow = power });
            foreach (var key in PowerKeys)
                Assert.False(s.TryGetProperty(key, out _), key);
            // The rest is still there.
            AssertEveryKeyWithItsKind(s);
        }
    }

    [Fact]
    public void OnBatteryGivesPercentAndTimeLeft()
    {
        var s = Run(new FakeReadings { PowerNow = new PowerReading(0, 1, 80, 7200) });
        Assert.Equal(80, s.GetProperty("battery").GetInt32());
        Assert.True(s.GetProperty("onBattery").GetBoolean());
        Assert.False(s.GetProperty("charging").GetBoolean());
        Assert.False(s.GetProperty("charged").GetBoolean());
        Assert.Equal(120, s.GetProperty("minutesToEmpty").GetInt32());
        Assert.Equal(-1, s.GetProperty("minutesToFull").GetInt32());
        Assert.False(s.TryGetProperty("batteryTemp", out _));
    }

    [Fact]
    public void UnknownAcLineStatusLeavesOutOnBatteryButKeepsTheRest()
    {
        // AC 255 = Windows can't tell whether it is plugged in.
        var s = Run(new FakeReadings { PowerNow = new PowerReading(255, 1, 60, -1) });
        Assert.False(s.TryGetProperty("onBattery", out _));
        Assert.Equal(60, s.GetProperty("battery").GetInt32());
        Assert.False(s.GetProperty("charging").GetBoolean());
        Assert.False(s.GetProperty("charged").GetBoolean());
        Assert.Equal(-1, s.GetProperty("minutesToEmpty").GetInt32());
        Assert.Equal(-1, s.GetProperty("minutesToFull").GetInt32());
    }

    [Fact]
    public void OnBatteryWithUnknownTimeLeftSendsMinusOne()
    {
        var s = Run(new FakeReadings { PowerNow = new PowerReading(0, 2, 15, -1) });
        Assert.Equal(15, s.GetProperty("battery").GetInt32());
        Assert.Equal(-1, s.GetProperty("minutesToEmpty").GetInt32());
    }

    [Fact]
    public void ChargingIsPluggedInAndCharging()
    {
        // Flag 9 = high (1) + charging (8).
        var s = Run(new FakeReadings { PowerNow = new PowerReading(1, 9, 50, -1) });
        Assert.Equal(50, s.GetProperty("battery").GetInt32());
        Assert.False(s.GetProperty("onBattery").GetBoolean());
        Assert.True(s.GetProperty("charging").GetBoolean());
        Assert.False(s.GetProperty("charged").GetBoolean());
        Assert.Equal(-1, s.GetProperty("minutesToEmpty").GetInt32());
        Assert.Equal(-1, s.GetProperty("minutesToFull").GetInt32());
    }

    [Fact]
    public void PluggedInAtOneHundredIsCharged()
    {
        var s = Run(new FakeReadings { PowerNow = new PowerReading(1, 1, 100, -1) });
        Assert.Equal(100, s.GetProperty("battery").GetInt32());
        Assert.False(s.GetProperty("onBattery").GetBoolean());
        Assert.False(s.GetProperty("charging").GetBoolean());
        Assert.True(s.GetProperty("charged").GetBoolean());
    }

    [Fact]
    public void TimeLeftIsNotSentWhilePluggedIn()
    {
        // Windows can still report a stale estimate while on AC.
        var s = Run(new FakeReadings { PowerNow = new PowerReading(1, 1, 90, 3600) });
        Assert.Equal(-1, s.GetProperty("minutesToEmpty").GetInt32());
    }

    // ---- 6.5 system info ----

    [Fact]
    public void MemoryDiskAndUptimeComeFromTheReadings()
    {
        var s = Run(new FakeReadings());
        var mac = s.GetProperty("mac");
        Assert.Equal(16UL << 30, mac.GetProperty("memTotal").GetUInt64());
        Assert.Equal(12UL << 30, mac.GetProperty("memUsed").GetUInt64());
        Assert.Equal(200_000_000_000, mac.GetProperty("diskFree").GetInt64());
        Assert.Equal(500_000_000_000, mac.GetProperty("diskTotal").GetInt64());
        Assert.Equal(90_000, mac.GetProperty("uptime").GetInt64());
    }

    [Fact]
    public void UnknownMemoryAndDiskAreLeftOutButMemTotalAndUptimeStay()
    {
        var s = Run(new FakeReadings { MemoryNow = null, DiskNow = null });
        var mac = s.GetProperty("mac");
        Assert.Equal(0, mac.GetProperty("memTotal").GetInt32());
        Assert.False(mac.TryGetProperty("memUsed", out _));
        Assert.False(mac.TryGetProperty("diskFree", out _));
        Assert.False(mac.TryGetProperty("diskTotal", out _));
        Assert.True(mac.TryGetProperty("uptime", out _));
    }

    [Fact]
    public void MoreAvailableThanTotalGivesZeroUsed()
    {
        var s = Run(new FakeReadings { MemoryNow = new MemoryReading(100, 200) });
        Assert.Equal(0, s.GetProperty("mac").GetProperty("memUsed").GetInt32());
    }

    // ---- processor load, between two calls ----

    [Fact]
    public void CpuNeedsTwoCallsThenIsBusyOverTotal()
    {
        var meter = new CpuMeter();
        Assert.Null(meter.Percent(new CpuTimes(1000, 2000, 500), 0));
        // Over the next 1000 ms: kernel +1000 (of which idle +750), user +500: busy 750 of 1500.
        Assert.Equal(50.0, meter.Percent(new CpuTimes(1750, 3000, 1000), 1000));
        // Fully idle.
        Assert.Equal(0.0, meter.Percent(new CpuTimes(2750, 4000, 1000), 2000));
        // Fully busy.
        Assert.Equal(100.0, meter.Percent(new CpuTimes(2750, 5000, 2000), 3000));
    }

    [Fact]
    public void CpuIsRoundedToOneDecimal()
    {
        var meter = new CpuMeter();
        meter.Percent(new CpuTimes(0, 0, 0), 0);
        // idle 2 of 3 units: busy 1/3 = 33.3%.
        Assert.Equal(33.3, meter.Percent(new CpuTimes(2, 3, 0), 1000));
    }

    [Fact]
    public void CpuAskedTooSoonKeepsTheOldStartingPoint()
    {
        var meter = new CpuMeter();
        meter.Percent(new CpuTimes(0, 0, 0), 0);
        Assert.Null(meter.Percent(new CpuTimes(10, 20, 0), 50));        // only 50 ms later
        // Measured from the first call, not from the one that was too soon: idle 500 of 1000.
        Assert.Equal(50.0, meter.Percent(new CpuTimes(500, 1000, 0), 1000));
    }

    [Fact]
    public void CpuCountersGoingBackwardsStartOver()
    {
        var meter = new CpuMeter();
        meter.Percent(new CpuTimes(1000, 2000, 0), 0);
        Assert.Null(meter.Percent(new CpuTimes(10, 20, 0), 1000));
        Assert.Equal(50.0, meter.Percent(new CpuTimes(510, 1020, 0), 2000));
    }

    [Fact]
    public void CpuWithNoTimeElapsedInTheCountersHasNoAnswer()
    {
        var meter = new CpuMeter();
        meter.Percent(new CpuTimes(100, 200, 50), 0);
        Assert.Null(meter.Percent(new CpuTimes(100, 200, 50), 1000));
    }

    [Fact]
    public void StatusHasNoCpuTheFirstTimeThenTheLoad()
    {
        var fake = new FakeReadings { CpuNow = new CpuTimes(1000, 2000, 500), Ms = 0 };
        var info = new StatusInfo(fake);
        Assert.False(Run(info.Build(true)).GetProperty("mac").TryGetProperty("cpu", out _));
        fake.CpuNow = new CpuTimes(1750, 3000, 1000);
        fake.Ms = 1000;
        var cpu = Run(info.Build(true)).GetProperty("mac").GetProperty("cpu");
        Assert.Equal(JsonValueKind.Number, cpu.ValueKind);
        Assert.Equal(50.0, cpu.GetDouble());
    }

    // ---- network speed, between two calls ----

    [Fact]
    public void NetworkRatesAreBytesPerSecondBetweenTwoCalls()
    {
        var meter = new NetMeter();
        Assert.Null(meter.Rates(new NetCounters("Ethernet", true, 1000, 500), 0));
        var rate = meter.Rates(new NetCounters("Ethernet", true, 3000, 1500), 2000);
        Assert.NotNull(rate);
        Assert.Equal(1000.0, rate!.Value.Down);   // 2000 bytes in 2 seconds
        Assert.Equal(500.0, rate.Value.Up);       // 1000 bytes in 2 seconds
    }

    [Fact]
    public void NetworkOnAnotherInterfaceOrAfterACounterResetStartsOver()
    {
        var meter = new NetMeter();
        meter.Rates(new NetCounters("Wi-Fi", false, 1000, 1000), 0);
        Assert.Null(meter.Rates(new NetCounters("Ethernet", true, 5000, 5000), 1000));      // another interface
        Assert.Null(meter.Rates(new NetCounters("Ethernet", true, 100, 100), 2000));        // counters went down
        var rate = meter.Rates(new NetCounters("Ethernet", true, 1100, 600), 3000);
        Assert.Equal(1000.0, rate!.Value.Down);
        Assert.Equal(500.0, rate.Value.Up);
    }

    [Fact]
    public void NetworkAskedTooSoonKeepsTheOldStartingPoint()
    {
        var meter = new NetMeter();
        meter.Rates(new NetCounters("Ethernet", true, 0, 0), 0);
        Assert.Null(meter.Rates(new NetCounters("Ethernet", true, 50, 50), 100));
        var rate = meter.Rates(new NetCounters("Ethernet", true, 1000, 2000), 1000);
        Assert.Equal(1000.0, rate!.Value.Down);
        Assert.Equal(2000.0, rate.Value.Up);
    }

    [Fact]
    public void NetworkRatesAreForgottenWhenThereIsNoInterface()
    {
        var meter = new NetMeter();
        meter.Rates(new NetCounters("Ethernet", true, 0, 0), 0);
        meter.Reset();
        Assert.Null(meter.Rates(new NetCounters("Ethernet", true, 9999, 9999), 1000));
    }

    [Fact]
    public void StatusHasTheInterfaceAtOnceAndTheRatesTheSecondTime()
    {
        var fake = new FakeReadings { NetNow = new NetCounters("Wi-Fi", false, 1000, 500), Ms = 0 };
        var info = new StatusInfo(fake);
        var first = Run(info.Build(true)).GetProperty("net");
        Assert.Equal("Wi-Fi", first.GetProperty("iface").GetString());
        Assert.False(first.GetProperty("wired").GetBoolean());
        Assert.False(first.TryGetProperty("down", out _));
        Assert.False(first.TryGetProperty("up", out _));
        // No Wi-Fi signal is measured on Windows (yet), so none is sent: `rssi` is omitted.
        Assert.False(first.TryGetProperty("rssi", out _));
        Assert.False(first.TryGetProperty("wifi", out _));

        fake.NetNow = new NetCounters("Wi-Fi", false, 3000, 1500);
        fake.Ms = 2000;
        var second = Run(info.Build(true)).GetProperty("net");
        Assert.Equal(JsonValueKind.Number, second.GetProperty("down").ValueKind);
        Assert.Equal(1000.0, second.GetProperty("down").GetDouble());
        Assert.Equal(500.0, second.GetProperty("up").GetDouble());
    }

    [Fact]
    public void NoNetworkInterfaceGivesAnEmptyNetObject()
    {
        var s = Run(new FakeReadings { NetNow = null });
        var net = s.GetProperty("net");
        Assert.Equal(JsonValueKind.Object, net.ValueKind);
        Assert.Empty(net.EnumerateObject());
    }

    // ---- the real Windows readings ----

    [Fact]
    public void RealReadingsGiveAWholeStatusOnThisMachine()
    {
        var real = new SystemReadings();
        var info = new StatusInfo(real);
        var s = Run(info.Build(true));
        AssertEveryKeyWithItsKind(s);
        var mac = s.GetProperty("mac");
        Assert.True(mac.GetProperty("memTotal").GetUInt64() > 0);
        Assert.True(mac.GetProperty("memUsed").GetUInt64() > 0);
        Assert.True(mac.GetProperty("memUsed").GetUInt64() <= mac.GetProperty("memTotal").GetUInt64());
        Assert.True(mac.GetProperty("uptime").GetInt64() >= 0);
        Assert.True(mac.GetProperty("diskTotal").GetInt64() > 0);
        Assert.True(mac.GetProperty("diskFree").GetInt64() <= mac.GetProperty("diskTotal").GetInt64());
        Assert.False(mac.TryGetProperty("cpu", out _));   // the first call has no load yet
        Assert.True(s.GetProperty("screenWidth").GetInt32() >= 0);
    }

    [Fact]
    public void RealBatteryKeysFollowWhetherThisMachineHasABattery()
    {
        // A CI machine has none (the keys must be missing); a laptop does (they must all be there).
        var power = new SystemReadings().Power();
        Assert.NotNull(power);
        bool hasBattery = (power!.Value.BatteryFlag & 128) == 0 && power.Value.BatteryFlag != 255 && power.Value.BatteryLifePercent <= 100;
        var s = Run(new StatusInfo(new SystemReadings()).Build(true));
        foreach (var key in new[] { "battery", "charging", "charged", "minutesToEmpty", "minutesToFull" })
            Assert.Equal(hasBattery, s.TryGetProperty(key, out _));
        // onBattery is also left out when the AC line status is unknown (255).
        Assert.Equal(hasBattery && power.Value.AcLineStatus != 255, s.TryGetProperty("onBattery", out _));
        Assert.False(s.TryGetProperty("batteryTemp", out _));
        if (hasBattery)
        {
            var pct = s.GetProperty("battery").GetInt32();
            Assert.InRange(pct, 0, 100);
        }
    }

    [Fact]
    public void RealCpuAppearsOnTheSecondCallAndIsAPercentage()
    {
        var info = new StatusInfo(new SystemReadings());
        Run(info.Build(true));
        Thread.Sleep(400);
        var mac = Run(info.Build(true)).GetProperty("mac");
        // GetSystemTimes always works and 400 ms is long enough for the counters to advance.
        Assert.True(mac.TryGetProperty("cpu", out var load));
        Assert.InRange(load.GetDouble(), 0.0, 100.0);
    }
}
