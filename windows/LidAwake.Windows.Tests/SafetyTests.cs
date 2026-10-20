using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using LidAwake;
using Xunit;

// Task 17: the battery and heat limits (main.swift safetyStop on the Mac), the temperature
// sources, and what /status says about them.
public class SafetyTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Settings settings;

    public SafetyTests() => settings = new Settings(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // GetSystemPowerStatus as it comes: AC 0 = on battery, 1 = plugged in; flag 1 = high, 8 = charging.
    static PowerReading OnBattery(byte percent) => new(0, 1, percent, 3600);
    static PowerReading PluggedIn(byte percent) => new(1, 8, percent, -1);

    static TemperatureReading Battery(double? celsius) => new(celsius, null);
    static TemperatureReading Zone(double? celsius) => new(null, celsius);
    static readonly TemperatureReading NoTemperature = TemperatureReading.None;

    // A sensor that has read `reading` once.
    static HeatSensor ReadOnce(TemperatureReading reading)
    {
        var heat = new HeatSensor(() => reading);
        heat.Refresh(TimeSpan.Zero);
        return heat;
    }

    internal static JsonElement Run(Dictionary<string, object?> status)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(status));
        return doc.RootElement.Clone();
    }

    internal static string?[] Features(JsonElement s) => s.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray();

    // ---- Temperature parsing: both sources give tenths of a kelvin ----

    [Theory]
    // IOCTL_BATTERY_QUERY_INFORMATION (BatteryTemperature), a ULONG
    [InlineData(2732u, 0.05)]
    [InlineData(2982u, 25.05)]
    [InlineData(3182u, 45.05)]
    [InlineData(3131u, 39.95)]
    [InlineData(3132u, 40.05)]
    // MSAcpi_ThermalZoneTemperature.CurrentTemperature, a uint32 (the same unit)
    [InlineData(2731u, -0.05)]
    [InlineData(2981u, 24.95)]
    [InlineData(3181u, 44.95)]
    [InlineData(3010u, 27.85)]
    [InlineData(3632u, 90.05)]
    [InlineData(3782u, 105.05)]
    // The edges of what counts as a reading: real overheating up to 150 °C is kept
    [InlineData(2332u, -39.95)]
    [InlineData(4231u, 149.95)]
    public void TenthsOfAKelvinBecomeDegreesCelsius(uint raw, double celsius)
    {
        var parsed = TemperatureMath.FromTenthsOfKelvin(raw);
        Assert.NotNull(parsed);
        Assert.Equal(celsius, parsed!.Value, 6);
    }

    [Theory]
    [InlineData(0L)]            // a sensor that isn't there
    [InlineData(65535L)]        // 0xFFFF
    [InlineData(4294967295L)]   // 0xFFFFFFFF
    [InlineData(1L)]            // -273.05 °C
    [InlineData(2331L)]         // -40.05 °C
    [InlineData(4232L)]         // 150.05 °C
    [InlineData(10000L)]
    [InlineData(-1L)]
    public void OnlyObviousGarbageIsNoReading(long raw)
    {
        Assert.Null(TemperatureMath.FromTenthsOfKelvin(raw));
    }

    [Fact]
    public void SaneMeansMinus40To150()
    {
        Assert.True(TemperatureMath.IsSane(-40.0));
        Assert.True(TemperatureMath.IsSane(150.0));
        Assert.True(TemperatureMath.IsSane(120.0));
        Assert.False(TemperatureMath.IsSane(-40.01));
        Assert.False(TemperatureMath.IsSane(150.01));
        Assert.False(TemperatureMath.IsSane(double.NaN));
        Assert.False(TemperatureMath.IsSane(double.PositiveInfinity));
    }

    [Fact]
    public void OneDecimalRoundsHalvesAwayFromZeroLikeTheMac()
    {
        Assert.Equal(41.2, TemperatureMath.OneDecimal(41.23));
        Assert.Equal(41.3, TemperatureMath.OneDecimal(41.25));
        Assert.Equal(40.0, TemperatureMath.OneDecimal(39.96));
        Assert.Equal(-0.1, TemperatureMath.OneDecimal(-0.05));
    }

    // ---- The decision (safetyStop) ----

    [Fact]
    public void OnBatteryBelowTheLimitIsAReason()
    {
        var why = Safety.Decide(true, true, OnBattery(9), NoTemperature);
        Assert.NotNull(why);
        Assert.Equal("Turned off: battery below 10%", why!.LogOff);
        Assert.Equal("Not turned on: battery below 10%", why.LogRefused);
        Assert.Equal("Battery is at 9%. Turned off so your PC can sleep.", why.OffBody);
        Assert.Equal("Battery is at 9%. Can't keep your PC awake right now.", why.RefusedBody);
        Assert.NotNull(Safety.Decide(true, false, OnBattery(0), NoTemperature));
    }

    [Fact]
    public void AtTheLimitOrAboveIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, OnBattery(10), NoTemperature));
        Assert.Null(Safety.Decide(true, true, OnBattery(11), NoTemperature));
        Assert.Null(Safety.Decide(true, true, OnBattery(100), NoTemperature));
    }

    [Fact]
    public void PluggedInALowBatteryIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, PluggedIn(9), NoTemperature));
        Assert.Null(Safety.Decide(true, true, PluggedIn(0), NoTemperature));
        // Windows can't tell whether it is plugged in: never guess "on battery".
        Assert.Null(Safety.Decide(true, true, new PowerReading(255, 1, 5, -1), NoTemperature));
    }

    [Fact]
    public void NoBatteryOrNoPowerReadingIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, null, NoTemperature));
        Assert.Null(Safety.Decide(true, true, new PowerReading(0, 128, 5, -1), NoTemperature));   // no system battery
        Assert.Null(Safety.Decide(true, true, new PowerReading(0, 255, 5, -1), NoTemperature));   // unknown
        Assert.Null(Safety.Decide(true, true, new PowerReading(0, 1, 255, -1), NoTemperature));   // no percent
    }

    [Fact]
    public void ABatteryAtFortyAndAboveIsAReasonInTheMacsWords()
    {
        var at = Safety.Decide(true, true, PluggedIn(80), Battery(40.0));
        Assert.NotNull(at);
        Assert.Equal("Turned off: battery too hot (40.0°C)", at!.LogOff);
        var above = Safety.Decide(true, true, PluggedIn(80), Battery(41.23));
        Assert.NotNull(above);
        Assert.Equal("Turned off: battery too hot (41.2°C)", above!.LogOff);
        Assert.Equal("Not turned on: battery too hot (41.2°C)", above.LogRefused);
        Assert.Equal("Battery is too hot (41.2°C). Turned off so your PC can sleep.", above.OffBody);
        Assert.Equal("Battery is too hot (41.2°C). Can't keep your PC awake right now.", above.RefusedBody);
        // Heat counts plugged in and with the battery guard off too.
        Assert.NotNull(Safety.Decide(false, true, null, Battery(55.0)));
    }

    // The raw IOCTL value, through the parsing, into the decision.
    [Fact]
    public void TheBatteryLimitFromRawTenthsOfAKelvin()
    {
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Battery(TemperatureMath.FromTenthsOfKelvin(3131))));   // 39.95
        var hot = Safety.Decide(true, true, PluggedIn(80), Battery(TemperatureMath.FromTenthsOfKelvin(3132)));       // 40.05
        Assert.NotNull(hot);
        Assert.Equal("Turned off: battery too hot (40.1°C)", hot!.LogOff);
    }

    [Fact]
    public void AThermalZoneHasItsOwnLimitOfNinety()
    {
        // 40 to 60 °C is normal for the processor or the board: no reason.
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Zone(40.0)));
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Zone(60.0)));
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Zone(89.9)));
        var at = Safety.Decide(true, true, PluggedIn(80), Zone(90.0));
        Assert.NotNull(at);
        Assert.Equal("Turned off: PC too hot (90.0°C)", at!.LogOff);
        Assert.Equal("Not turned on: PC too hot (90.0°C)", at.LogRefused);
        Assert.Equal("Your PC is too hot (90.0°C). Turned off so your PC can sleep.", at.OffBody);
        Assert.Equal("Your PC is too hot (90.0°C). Can't keep your PC awake right now.", at.RefusedBody);
        // Real overheating above 100 °C is never hidden.
        Assert.Equal("Turned off: PC too hot (105.0°C)", Safety.Decide(true, true, PluggedIn(80), Zone(105.0))!.LogOff);
        Assert.Equal("Turned off: PC too hot (150.0°C)", Safety.Decide(true, true, PluggedIn(80), Zone(150.0))!.LogOff);
        Assert.Equal(90, Limits.MaxPcTemp);
    }

    [Fact]
    public void TheBatteryTemperatureIsCheckedBeforeTheZones()
    {
        var both = Safety.Decide(true, true, PluggedIn(80), new TemperatureReading(41.0, 95.0));
        Assert.Equal("Turned off: battery too hot (41.0°C)", both!.LogOff);
        // A cool battery doesn't hide a hot PC.
        var pc = Safety.Decide(true, true, PluggedIn(80), new TemperatureReading(30.0, 95.0));
        Assert.Equal("Turned off: PC too hot (95.0°C)", pc!.LogOff);
        // A warm zone with a cool battery: nothing.
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), new TemperatureReading(30.0, 70.0)));
    }

    [Fact]
    public void BelowTheHeatLimitsOrGarbageIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Battery(39.99)));
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Battery(25.0)));
        // Garbage (outside -40 to 150 °C) is no reading.
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Battery(151.0)));
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Zone(500.0)));
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Battery(double.NaN)));
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), Zone(double.NaN)));
    }

    [Fact]
    public void TheBatteryReasonComesFirst()
    {
        var why = Safety.Decide(true, true, OnBattery(5), new TemperatureReading(45.0, 95.0));
        Assert.Equal("Turned off: battery below 10%", why!.LogOff);
    }

    [Fact]
    public void GuardsOffAreNoReason()
    {
        var hot = new TemperatureReading(45.0, 95.0);
        Assert.Null(Safety.Decide(false, false, OnBattery(5), hot));
        Assert.Null(Safety.Decide(false, true, OnBattery(5), NoTemperature));
        Assert.Null(Safety.Decide(true, false, PluggedIn(50), hot));
        // Each guard only turns off its own limit.
        Assert.Equal("Turned off: battery too hot (45.0°C)", Safety.Decide(false, true, OnBattery(5), hot)!.LogOff);
        Assert.Equal("Turned off: battery below 10%", Safety.Decide(true, false, OnBattery(5), hot)!.LogOff);
    }

    [Fact]
    public void HeatWithNoTemperatureSourceIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, PluggedIn(50), NoTemperature));
    }

    // ---- The check with this PC's readings: a turn-on is refused for the same reasons ----

    [Fact]
    public void ATurnOnIsRefusedOnLowBatteryAndAllowedOnceAGuardIsOff()
    {
        PowerReading? power = OnBattery(8);
        var limits = new Limits(settings);
        var check = new SafetyCheck(limits, ReadOnce(NoTemperature), () => power);
        var refused = check.Now();
        Assert.NotNull(refused);
        Assert.Equal("Not turned on: battery below 10%", refused!.LogRefused);
        Assert.Equal("Battery is at 8%. Can't keep your PC awake right now.", refused.RefusedBody);

        limits.Set(Limits.Battery, false);
        Assert.Null(check.Now());
        limits.Set(Limits.Battery, true);
        power = PluggedIn(8);
        Assert.Null(check.Now());
    }

    [Fact]
    public void ATurnOnIsRefusedWhenTooHotAndAllowedWithoutASource()
    {
        var limits = new Limits(settings);
        var hot = new SafetyCheck(limits, ReadOnce(Battery(42.0)), () => PluggedIn(100));
        Assert.Equal("Not turned on: battery too hot (42.0°C)", hot.Now()!.LogRefused);
        var hotPc = new SafetyCheck(limits, ReadOnce(Zone(91.0)), () => PluggedIn(100));
        Assert.Equal("Not turned on: PC too hot (91.0°C)", hotPc.Now()!.LogRefused);
        limits.Set(Limits.Heat, false);
        Assert.Null(hot.Now());
        Assert.Null(hotPc.Now());

        limits.Set(Limits.Heat, true);
        Assert.Null(new SafetyCheck(limits, ReadOnce(NoTemperature), () => PluggedIn(100)).Now());
        // Not read yet at all: no temperature either.
        Assert.Null(new SafetyCheck(limits, new HeatSensor(() => Battery(42.0)), () => PluggedIn(100)).Now());
    }

    [Fact]
    public void AFailingPowerReadingOnlyLeavesOutTheBatteryLimit()
    {
        var check = new SafetyCheck(new Limits(settings), ReadOnce(Battery(41.0)), () => throw new InvalidOperationException("no power status"));
        Assert.Equal("Turned off: battery too hot (41.0°C)", check.Now()!.LogOff);
        var cool = new SafetyCheck(new Limits(settings), ReadOnce(Battery(30.0)), () => throw new InvalidOperationException("no power status"));
        Assert.Null(cool.Now());
    }

    // ---- Turning off on the UI thread: two queued posts turn off once ----

    [Fact]
    public void TwoQueuedStopsTurnOffExactlyOnce()
    {
        bool on = true;
        var turnedOff = new List<SafetyReason>();
        var check = new SafetyCheck(new Limits(settings), ReadOnce(Battery(45.0)), () => PluggedIn(100));
        var stop = new SafetyStop(() => on, check, why => { turnedOff.Add(why); on = false; });
        // Two checks found the reason before the UI thread ran either post.
        var queue = new List<Action> { () => stop.Run(), () => stop.Run() };
        foreach (var post in queue) post();
        Assert.Single(turnedOff);
        Assert.Equal("Turned off: battery too hot (45.0°C)", turnedOff[0].LogOff);
        Assert.False(on);
    }

    [Fact]
    public void AQueuedStopDoesNothingOnceTheGuardIsOffOrLidAwakeIsOff()
    {
        bool on = true;
        int calls = 0;
        var limits = new Limits(settings);
        var stop = new SafetyStop(() => on, new SafetyCheck(limits, ReadOnce(Battery(45.0)), () => PluggedIn(100)), _ => { calls++; on = false; });
        limits.Set(Limits.Heat, false);   // switched off from the phone before the post ran
        Assert.False(stop.Run());
        limits.Set(Limits.Heat, true);
        on = false;                       // turned off from the tray before the post ran
        Assert.False(stop.Run());
        Assert.Equal(0, calls);
        on = true;
        Assert.True(stop.Run());
        Assert.Equal(1, calls);
    }

    // ---- One check at a time, none dropped ----

    [Fact]
    public void RequestsOneAfterAnotherEachRun()
    {
        var runs = new List<bool>();
        var checks = new Coalescer(read => runs.Add(read));
        checks.Request(true);
        checks.Request(false);
        checks.Request(true);
        Assert.Equal(new[] { true, false, true }, runs);
    }

    [Fact]
    public async Task ARequestDuringARunIsRunAfterItWithItsReadCarried()
    {
        using var inRun = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var runs = new List<bool>();
        int running = 0, overlapped = 0;
        var checks = new Coalescer(read =>
        {
            if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlapped);
            lock (runs) runs.Add(read);
            if (runs.Count == 1) { inRun.Set(); release.Wait(TestThreads.Patience); }
            Interlocked.Decrement(ref running);
        });
        var first = Task.Run(() => checks.Request(false));
        Assert.True(await TestThreads.Until(() => inRun.IsSet));
        // Asked while the first run is going: both return at once, and share one more run that reads.
        checks.Request(true);
        checks.Request(false);
        lock (runs) Assert.Single(runs);
        release.Set();
        await first.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { false, true }, runs);
        Assert.Equal(0, overlapped);
    }

    [Fact]
    public void UnderManyThreadsNoRequestIsDroppedAndRunsNeverOverlap()
    {
        long ticket = 0, lastSeen = 0;
        int running = 0, overlapped = 0;
        var checks = new Coalescer(_ =>
        {
            if (Interlocked.Increment(ref running) > 1) Interlocked.Increment(ref overlapped);
            // Every request made before this run started is covered by it.
            long seen = Interlocked.Read(ref ticket);
            long before;
            do { before = Interlocked.Read(ref lastSeen); }
            while (seen > before && Interlocked.CompareExchange(ref lastSeen, seen, before) != before);
            Thread.SpinWait(200);
            Interlocked.Decrement(ref running);
        });
        var threads = Enumerable.Range(0, 8).Select(i => new Thread(() =>
        {
            for (int n = 0; n < 2000; n++)
            {
                Interlocked.Increment(ref ticket);
                checks.Request(n % 3 == 0);
            }
        })).ToList();
        threads.ForEach(t => t.Start());
        threads.ForEach(t => t.Join());
        Assert.Equal(16_000, Interlocked.Read(ref ticket));
        // The last request was followed by a run that started after it.
        Assert.Equal(16_000, Interlocked.Read(ref lastSeen));
        Assert.Equal(0, overlapped);
    }

    [Fact]
    public void AThrowingRunDoesNotStopLaterOnes()
    {
        int calls = 0;
        var checks = new Coalescer(_ => { calls++; throw new InvalidOperationException("check failed"); });
        checks.Request(true);
        checks.Request(true);
        Assert.Equal(2, calls);
    }

    // ---- The switches ----

    [Fact]
    public void BothSwitchesStartOnAndAreStored()
    {
        var limits = new Limits(settings);
        Assert.True(limits.BatteryGuard);
        Assert.True(limits.HeatGuard);
        Assert.True(limits.Set(Limits.Battery, false));
        Assert.True(limits.Set(Limits.Heat, false));
        Assert.False(limits.BatteryGuard);
        Assert.False(limits.HeatGuard);
        // The same keys as the Mac's UserDefaults, in the roaming settings.
        Assert.False(settings.Get<bool?>("batteryGuard"));
        Assert.False(settings.Get<bool?>("heatGuard"));
        var reloaded = new Limits(new Settings(folder));
        Assert.False(reloaded.BatteryGuard);
        Assert.False(reloaded.HeatGuard);
    }

    [Fact]
    public void AnUnknownNameChangesNothing()
    {
        var limits = new Limits(settings);
        Assert.False(limits.Set("cpu", false));
        Assert.False(limits.Set("Battery", false));
        Assert.True(limits.BatteryGuard);
        Assert.True(limits.HeatGuard);
        Assert.True(Limits.IsName("battery"));
        Assert.True(Limits.IsName("heat"));
        Assert.False(Limits.IsName("Heat"));
        Assert.False(Limits.IsName(null));
    }

    [Fact]
    public void ADamagedSavedSwitchCountsAsOn()
    {
        settings.Set("batteryGuard", "yes");
        settings.Set("heatGuard", 0);
        var limits = new Limits(settings);
        Assert.True(limits.BatteryGuard);
        Assert.True(limits.HeatGuard);
    }

    // ---- The cached temperatures ----

    [Fact]
    public void TheTemperatureIsReadAtMostOncePerMaxAge()
    {
        int calls = 0;
        long clock = 1_000;
        var heat = new HeatSensor(() => { calls++; return Battery(30.0); }, () => clock);
        // Asking for the last reading never reads the hardware.
        Assert.False(heat.Latest.Any);
        Assert.False(heat.HasRead);
        Assert.Equal(0, calls);

        Assert.Equal(30.0, heat.Refresh(HeatSensor.CheckEvery).Battery);
        Assert.Equal(1, calls);
        clock += 14_999;
        Assert.Equal(30.0, heat.Refresh(HeatSensor.CheckEvery).Battery);
        Assert.Equal(30.0, heat.Latest.Battery);
        Assert.Equal(1, calls);
        clock += 1;
        heat.Refresh(HeatSensor.CheckEvery);
        Assert.Equal(2, calls);
        Assert.True(heat.HasRead);
    }

    [Fact]
    public void BothSourcesAreKeptApart()
    {
        var heat = ReadOnce(new TemperatureReading(35.0, 62.0));
        Assert.Equal(35.0, heat.Latest.Battery);
        Assert.Equal(62.0, heat.Latest.Zone);
        Assert.True(heat.Available);
        Assert.True(ReadOnce(Zone(50.0)).Available);
        Assert.True(ReadOnce(Battery(30.0)).Available);
        Assert.False(ReadOnce(NoTemperature).Available);
    }

    [Fact]
    public void AFailingOrGarbageReadIsNoReading()
    {
        var throwing = new HeatSensor(() => throw new InvalidOperationException("sensor gone"));
        Assert.False(throwing.Refresh(TimeSpan.Zero).Any);
        Assert.False(throwing.Available);
        Assert.True(throwing.HasRead);

        Assert.Null(ReadOnce(Battery(151.0)).Latest.Battery);
        Assert.Null(ReadOnce(Zone(-41.0)).Latest.Zone);
        Assert.Null(ReadOnce(Battery(double.NaN)).Latest.Battery);
        // Real heat is kept.
        Assert.Equal(120.0, ReadOnce(Zone(120.0)).Latest.Zone);
        Assert.Equal(36.5, ReadOnce(Battery(36.5)).Latest.Battery);
    }

    [Fact]
    public void TheLastGoodReadingIsKeptThroughTwoMissesAndDroppedByTheThird()
    {
        var next = new TemperatureReading(41.0, 92.0);
        var heat = new HeatSensor(() => next);
        heat.Refresh(TimeSpan.Zero);
        var check = new SafetyCheck(new Limits(settings), heat, () => PluggedIn(100));
        Assert.Equal("Turned off: battery too hot (41.0°C)", check.Now()!.LogOff);

        next = NoTemperature;
        heat.Refresh(TimeSpan.Zero);   // miss 1 (15 s)
        Assert.Equal(new TemperatureReading(41.0, 92.0), heat.Latest);
        Assert.True(heat.Available);
        Assert.NotNull(check.Now());
        heat.Refresh(TimeSpan.Zero);   // miss 2 (30 s)
        Assert.True(heat.Available);
        Assert.NotNull(check.Now());
        heat.Refresh(TimeSpan.Zero);   // miss 3 (45 s): dropped
        Assert.False(heat.Available);
        Assert.Null(check.Now());
        Assert.Equal(3, HeatSensor.MissesKept);
    }

    [Fact]
    public void EachSourceCountsItsOwnMissesAndAGoodReadStartsAgain()
    {
        var next = new TemperatureReading(35.0, 60.0);
        var heat = new HeatSensor(() => next);
        heat.Refresh(TimeSpan.Zero);
        next = new TemperatureReading(null, 61.0);   // the battery misses, the zone answers
        heat.Refresh(TimeSpan.Zero);
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(new TemperatureReading(35.0, 61.0), heat.Latest);
        next = new TemperatureReading(36.0, null);   // a good battery read: its count starts again
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(new TemperatureReading(36.0, 61.0), heat.Latest);
        next = NoTemperature;
        heat.Refresh(TimeSpan.Zero);                 // battery miss 1, zone miss 2
        heat.Refresh(TimeSpan.Zero);                 // battery miss 2, zone miss 3: dropped
        Assert.Equal(new TemperatureReading(36.0, null), heat.Latest);
        // Garbage is a miss too.
        next = new TemperatureReading(500.0, null);
        heat.Refresh(TimeSpan.Zero);                 // battery miss 3: dropped
        Assert.False(heat.Available);
    }

    [Fact]
    public void ChangedIsRaisedOnTheFirstReadAndWhenATemperatureComesOrGoes()
    {
        var value = NoTemperature;
        var heat = new HeatSensor(() => value);
        int changed = 0;
        heat.Changed += () => changed++;
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(1, changed);   // the first read, even with no temperature
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(1, changed);
        value = Battery(30.0);
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(2, changed);
        value = new TemperatureReading(31.0, 50.0);
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(2, changed);   // only new values, not a new availability
        value = NoTemperature;
        for (int miss = 0; miss < HeatSensor.MissesKept; miss++) heat.Refresh(TimeSpan.Zero);
        Assert.Equal(3, changed);
        Assert.False(heat.Available);
    }

    [Fact]
    public void AThrowingChangedHandlerDoesNotBreakTheRead()
    {
        var heat = new HeatSensor(() => Battery(33.0));
        heat.Changed += () => throw new InvalidOperationException("tray gone");
        Assert.Equal(33.0, heat.Refresh(TimeSpan.Zero).Battery);
        Assert.Equal(33.0, heat.Latest.Battery);
    }

    // ---- /status ----

    [Fact]
    public void StatusWithoutATemperatureListsOnlyLimitsAndNoBatteryTemp()
    {
        var limits = new Limits(settings);
        var s = Run(new StatusInfo(new FakeReadings { PowerNow = OnBattery(50) }, limits, ReadOnce(NoTemperature)).Build(true));
        Assert.Equal(new[] { "limits", "history", "memory", "passkeys", "push" }, Features(s));
        Assert.False(s.TryGetProperty("batteryTemp", out _));
        // The stored switch is still sent; the page hides the heat row without "heat".
        Assert.True(s.GetProperty("heatGuard").GetBoolean());
        Assert.True(s.GetProperty("batteryGuard").GetBoolean());
        Assert.Equal(10, s.GetProperty("batteryLimit").GetInt32());
        Assert.Equal(40, s.GetProperty("maxBatteryTemp").GetInt32());
        Assert.Equal("unknown", s.GetProperty("thermalState").GetString());
        StatusInfoTests.AssertEveryKeyWithItsKind(s);
    }

    [Fact]
    public void StatusWithABatteryTemperatureListsHeatAndSendsBatteryTempWithOneDecimal()
    {
        var s = Run(new StatusInfo(new FakeReadings { PowerNow = OnBattery(50) }, new Limits(settings), ReadOnce(new TemperatureReading(36.66, 71.0))).Build(true));
        Assert.Equal(new[] { "limits", "history", "heat", "memory", "passkeys", "push" }, Features(s));
        Assert.Equal(36.7, s.GetProperty("batteryTemp").GetDouble());
        Assert.Equal(JsonValueKind.Number, s.GetProperty("batteryTemp").ValueKind);
        StatusInfoTests.AssertEveryKeyWithItsKind(s);
    }

    [Fact]
    public void AThermalZoneAloneListsHeatButIsNeverSentAsBatteryTemp()
    {
        var s = Run(new StatusInfo(new FakeReadings { PowerNow = OnBattery(50) }, new Limits(settings), ReadOnce(Zone(55.0))).Build(true));
        Assert.Equal(new[] { "limits", "history", "heat", "memory", "passkeys", "push" }, Features(s));
        Assert.False(s.TryGetProperty("batteryTemp", out _));
        StatusInfoTests.AssertEveryKeyWithItsKind(s);
    }

    [Fact]
    public void BatteryTempIsSentOnItsOwnWithoutABatteryReading()
    {
        // GetSystemPowerStatus says no battery, but the battery's sensor answered (6.4: on its own).
        var s = Run(new StatusInfo(new FakeReadings { PowerNow = new PowerReading(1, 128, 255, -1) }, null, ReadOnce(Battery(31.04))).Build(true));
        Assert.False(s.TryGetProperty("battery", out _));
        Assert.Equal(31.0, s.GetProperty("batteryTemp").GetDouble());
        Assert.Contains("heat", Features(s));
    }

    [Fact]
    public void StatusSendsTheStoredSwitches()
    {
        var limits = new Limits(settings);
        var info = new StatusInfo(new FakeReadings(), limits, ReadOnce(Battery(30.0)));
        limits.Set(Limits.Battery, false);
        var s = Run(info.Build(true));
        Assert.False(s.GetProperty("batteryGuard").GetBoolean());
        Assert.True(s.GetProperty("heatGuard").GetBoolean());
        limits.Set(Limits.Heat, false);
        limits.Set(Limits.Battery, true);
        s = Run(info.Build(false));
        Assert.True(s.GetProperty("batteryGuard").GetBoolean());
        Assert.False(s.GetProperty("heatGuard").GetBoolean());
    }

    [Fact]
    public void HeatInFeaturesFollowsTheKeptReadings()
    {
        var value = Battery(35.0);
        var heat = new HeatSensor(() => value);
        var info = new StatusInfo(new FakeReadings(), new Limits(settings), heat);
        // Before the first read: no temperature yet.
        Assert.Equal(new[] { "limits", "history", "memory", "passkeys", "push" }, Features(Run(info.Build(true))));
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(new[] { "limits", "history", "heat", "memory", "passkeys", "push" }, Features(Run(info.Build(true))));
        value = NoTemperature;
        heat.Refresh(TimeSpan.Zero);
        heat.Refresh(TimeSpan.Zero);
        // Two misses: still kept.
        Assert.Equal(new[] { "limits", "history", "heat", "memory", "passkeys", "push" }, Features(Run(info.Build(true))));
        heat.Refresh(TimeSpan.Zero);
        var s = Run(info.Build(true));
        Assert.Equal(new[] { "limits", "history", "memory", "passkeys", "push" }, Features(s));
        Assert.False(s.TryGetProperty("batteryTemp", out _));
    }

    // ---- The real sources on this machine (CI: no battery, probably no readable thermal zone) ----

    [Fact]
    public void TheRealBatteryTemperatureNeverThrowsAndIsNullOrSane()
    {
        var t = BatteryTemperature.Read();
        Assert.True(t == null || TemperatureMath.IsSane(t.Value), $"battery temperature {t}");
    }

    [Fact]
    public void TheRealThermalZonesNeverThrowAreNullOrSaneAndTakeAtMostTheTimeout()
    {
        var clock = Stopwatch.StartNew();
        var t = ThermalZones.Read();
        clock.Stop();
        Assert.True(t == null || TemperatureMath.IsSane(t.Value), $"thermal zone temperature {t}");
        // 3 seconds for the query, plus room for a slow CI machine to start WMI's thread.
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(10), $"took {clock.Elapsed}");
    }

    [Fact]
    public void TheRealSourcesTogetherNeverThrowAndTheSensorStaysConsistent()
    {
        var t = TemperatureSources.Read();
        Assert.True(t.Battery == null || TemperatureMath.IsSane(t.Battery.Value), $"battery {t.Battery}");
        Assert.True(t.Zone == null || TemperatureMath.IsSane(t.Zone.Value), $"zone {t.Zone}");
        var heat = new HeatSensor(TemperatureSources.Read);
        var read = heat.Refresh(TimeSpan.Zero);
        Assert.Equal(read, heat.Latest);
        Assert.Equal(read.Any, heat.Available);
        var s = Run(new StatusInfo(new SystemReadings(), new Limits(settings), heat).Build(true));
        Assert.Equal(read.Any, Features(s).Contains("heat"));
        Assert.Equal(read.Battery != null, s.TryGetProperty("batteryTemp", out _));
        StatusInfoTests.AssertEveryKeyWithItsKind(s);
    }

    // ---- Layouts (see Temperature.cs) ----

    [Fact]
    public void TheMarshalledStructsHaveTheSdkLayout()
    {
        bool x64 = IntPtr.Size == 8;
        Assert.Equal(x64 ? 32 : 28, Marshal.SizeOf<BatteryTemperature.SpDeviceInterfaceData>());
        Assert.Equal(0, (int)Marshal.OffsetOf<BatteryTemperature.SpDeviceInterfaceData>(nameof(BatteryTemperature.SpDeviceInterfaceData.CbSize)));
        Assert.Equal(4, (int)Marshal.OffsetOf<BatteryTemperature.SpDeviceInterfaceData>(nameof(BatteryTemperature.SpDeviceInterfaceData.InterfaceClassGuid)));
        Assert.Equal(20, (int)Marshal.OffsetOf<BatteryTemperature.SpDeviceInterfaceData>(nameof(BatteryTemperature.SpDeviceInterfaceData.Flags)));
        Assert.Equal(24, (int)Marshal.OffsetOf<BatteryTemperature.SpDeviceInterfaceData>(nameof(BatteryTemperature.SpDeviceInterfaceData.Reserved)));
        Assert.Equal(x64 ? 8 : 6, BatteryTemperature.DetailDataCbSize);

        Assert.Equal(12, Marshal.SizeOf<BatteryTemperature.BatteryQueryInformation>());
        Assert.Equal(0, (int)Marshal.OffsetOf<BatteryTemperature.BatteryQueryInformation>(nameof(BatteryTemperature.BatteryQueryInformation.BatteryTag)));
        Assert.Equal(4, (int)Marshal.OffsetOf<BatteryTemperature.BatteryQueryInformation>(nameof(BatteryTemperature.BatteryQueryInformation.InformationLevel)));
        Assert.Equal(8, (int)Marshal.OffsetOf<BatteryTemperature.BatteryQueryInformation>(nameof(BatteryTemperature.BatteryQueryInformation.AtRate)));
        Assert.Equal(2, BatteryTemperature.LevelBatteryTemperature);
    }

    [Fact]
    public void TheIoctlCodesAreTheSdkOnes()
    {
        // CTL_CODE(DeviceType, Function, Method, Access) = (DeviceType << 16) | (Access << 14) | (Function << 2) | Method,
        // with FILE_DEVICE_BATTERY = 0x29, FILE_READ_ACCESS = 1, METHOD_BUFFERED = 0.
        static uint CtlCode(uint function) => (0x29u << 16) | (1u << 14) | (function << 2) | 0u;
        Assert.Equal(BatteryTemperature.IoctlBatteryQueryTag, CtlCode(0x10));
        Assert.Equal(BatteryTemperature.IoctlBatteryQueryInformation, CtlCode(0x11));
        Assert.Equal(0x294040u, BatteryTemperature.IoctlBatteryQueryTag);
        Assert.Equal(0x294044u, BatteryTemperature.IoctlBatteryQueryInformation);
    }
}
