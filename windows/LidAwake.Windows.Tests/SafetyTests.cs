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

    // A sensor that has read `celsius` once (null: no temperature on this PC).
    static HeatSensor ReadOnce(double? celsius)
    {
        var heat = new HeatSensor(() => celsius);
        heat.Refresh(TimeSpan.Zero);
        return heat;
    }

    static JsonElement Run(Dictionary<string, object?> status)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(status));
        return doc.RootElement.Clone();
    }

    static string?[] Features(JsonElement s) => s.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray();

    // ---- Temperature parsing: both sources give tenths of a kelvin ----

    [Theory]
    // IOCTL_BATTERY_QUERY_INFORMATION (BatteryTemperature), a ULONG
    [InlineData(2732u, 0.05)]
    [InlineData(2982u, 25.05)]
    [InlineData(3182u, 45.05)]
    [InlineData(2532u, -19.95)]
    [InlineData(3731u, 99.95)]
    // MSAcpi_ThermalZoneTemperature.CurrentTemperature, a uint32 (the same unit)
    [InlineData(2731u, -0.05)]
    [InlineData(2981u, 24.95)]
    [InlineData(3181u, 44.95)]
    [InlineData(3010u, 27.85)]
    public void TenthsOfAKelvinBecomeDegreesCelsius(uint raw, double celsius)
    {
        var parsed = TemperatureMath.FromTenthsOfKelvin(raw);
        Assert.NotNull(parsed);
        Assert.Equal(celsius, parsed!.Value, 6);
    }

    [Theory]
    [InlineData(0L)]            // "not supported" placeholders read as -273.15 °C
    [InlineData(1L)]
    [InlineData(2531L)]         // -20.05 °C
    [InlineData(3732L)]         // 100.05 °C
    [InlineData(4000L)]
    [InlineData(65535L)]
    [InlineData(4294967295L)]   // uint.MaxValue
    [InlineData(-1L)]
    public void ATemperatureOutsideMinus20To100IsNoReading(long raw)
    {
        Assert.Null(TemperatureMath.FromTenthsOfKelvin(raw));
    }

    [Fact]
    public void SaneMeansMinus20To100()
    {
        Assert.True(TemperatureMath.IsSane(-20.0));
        Assert.True(TemperatureMath.IsSane(100.0));
        Assert.False(TemperatureMath.IsSane(-20.01));
        Assert.False(TemperatureMath.IsSane(100.01));
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
        var why = Safety.Decide(true, true, OnBattery(9), null);
        Assert.NotNull(why);
        Assert.Equal("Turned off: battery below 10%", why!.LogOff);
        Assert.Equal("Not turned on: battery below 10%", why.LogRefused);
        Assert.Equal("Battery is at 9%. Turned off so your PC can sleep.", why.OffBody);
        Assert.Equal("Battery is at 9%. Can't keep your PC awake right now.", why.RefusedBody);
        Assert.NotNull(Safety.Decide(true, false, OnBattery(0), null));
    }

    [Fact]
    public void AtTheLimitOrAboveIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, OnBattery(10), null));
        Assert.Null(Safety.Decide(true, true, OnBattery(11), null));
        Assert.Null(Safety.Decide(true, true, OnBattery(100), null));
    }

    [Fact]
    public void PluggedInALowBatteryIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, PluggedIn(9), null));
        Assert.Null(Safety.Decide(true, true, PluggedIn(0), null));
        // Windows can't tell whether it is plugged in: never guess "on battery".
        Assert.Null(Safety.Decide(true, true, new PowerReading(255, 1, 5, -1), null));
    }

    [Fact]
    public void NoBatteryOrNoPowerReadingIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, null, null));
        Assert.Null(Safety.Decide(true, true, new PowerReading(0, 128, 5, -1), null));   // no system battery
        Assert.Null(Safety.Decide(true, true, new PowerReading(0, 255, 5, -1), null));   // unknown
        Assert.Null(Safety.Decide(true, true, new PowerReading(0, 1, 255, -1), null));   // no percent
    }

    [Fact]
    public void HeatAtTheLimitAndAboveIsAReason()
    {
        var at = Safety.Decide(true, true, PluggedIn(80), 40.0);
        Assert.NotNull(at);
        Assert.Equal("Turned off: too hot (40.0°C)", at!.LogOff);
        var above = Safety.Decide(true, true, PluggedIn(80), 41.23);
        Assert.NotNull(above);
        Assert.Equal("Turned off: too hot (41.2°C)", above!.LogOff);
        Assert.Equal("Not turned on: too hot (41.2°C)", above.LogRefused);
        Assert.Equal("Your PC is too hot (41.2°C). Turned off so your PC can sleep.", above.OffBody);
        Assert.Equal("Your PC is too hot (41.2°C). Can't keep your PC awake right now.", above.RefusedBody);
        // Heat counts plugged in and with no battery too.
        Assert.NotNull(Safety.Decide(false, true, null, 55.0));
    }

    [Fact]
    public void BelowTheHeatLimitIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), 39.99));
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), 25.0));
        // A value no sensor gives is no reading.
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), 150.0));
        Assert.Null(Safety.Decide(true, true, PluggedIn(80), double.NaN));
    }

    [Fact]
    public void TheBatteryReasonComesFirst()
    {
        var why = Safety.Decide(true, true, OnBattery(5), 45.0);
        Assert.Equal("Turned off: battery below 10%", why!.LogOff);
    }

    [Fact]
    public void GuardsOffAreNoReason()
    {
        Assert.Null(Safety.Decide(false, false, OnBattery(5), 45.0));
        Assert.Null(Safety.Decide(false, true, OnBattery(5), null));
        Assert.Null(Safety.Decide(true, false, PluggedIn(50), 45.0));
        // Each guard only turns off its own limit.
        Assert.Equal("Turned off: too hot (45.0°C)", Safety.Decide(false, true, OnBattery(5), 45.0)!.LogOff);
        Assert.Equal("Turned off: battery below 10%", Safety.Decide(true, false, OnBattery(5), 45.0)!.LogOff);
    }

    [Fact]
    public void HeatWithNoTemperatureSourceIsNoReason()
    {
        Assert.Null(Safety.Decide(true, true, PluggedIn(50), null));
    }

    // ---- The check with this PC's readings: a turn-on is refused for the same reasons ----

    [Fact]
    public void ATurnOnIsRefusedOnLowBatteryAndAllowedOnceAGuardIsOff()
    {
        PowerReading? power = OnBattery(8);
        var limits = new Limits(settings);
        var check = new SafetyCheck(limits, ReadOnce(null), () => power);
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
        var hot = new SafetyCheck(limits, ReadOnce(42.0), () => PluggedIn(100));
        Assert.Equal("Not turned on: too hot (42.0°C)", hot.Now()!.LogRefused);
        limits.Set(Limits.Heat, false);
        Assert.Null(hot.Now());

        limits.Set(Limits.Heat, true);
        Assert.Null(new SafetyCheck(limits, ReadOnce(null), () => PluggedIn(100)).Now());
        // Not read yet at all: no temperature either.
        Assert.Null(new SafetyCheck(limits, new HeatSensor(() => 42.0), () => PluggedIn(100)).Now());
    }

    [Fact]
    public void AFailingPowerReadingOnlyLeavesOutTheBatteryLimit()
    {
        var check = new SafetyCheck(new Limits(settings), ReadOnce(41.0), () => throw new InvalidOperationException("no power status"));
        Assert.Equal("Turned off: too hot (41.0°C)", check.Now()!.LogOff);
        var cool = new SafetyCheck(new Limits(settings), ReadOnce(30.0), () => throw new InvalidOperationException("no power status"));
        Assert.Null(cool.Now());
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

    // ---- The cached temperature ----

    [Fact]
    public void TheTemperatureIsReadAtMostOncePerMaxAge()
    {
        int calls = 0;
        long clock = 1_000;
        var heat = new HeatSensor(() => { calls++; return 30.0; }, () => clock);
        // Asking for the last reading never reads the hardware.
        Assert.Null(heat.Latest);
        Assert.False(heat.HasRead);
        Assert.Equal(0, calls);

        Assert.Equal(30.0, heat.Refresh(HeatSensor.CheckEvery));
        Assert.Equal(1, calls);
        clock += 14_999;
        Assert.Equal(30.0, heat.Refresh(HeatSensor.CheckEvery));
        Assert.Equal(30.0, heat.Latest);
        Assert.Equal(1, calls);
        clock += 1;
        heat.Refresh(HeatSensor.CheckEvery);
        Assert.Equal(2, calls);
        Assert.True(heat.HasRead);
    }

    [Fact]
    public void AFailingOrImpossibleReadIsNoReading()
    {
        var throwing = new HeatSensor(() => throw new InvalidOperationException("sensor gone"));
        Assert.Null(throwing.Refresh(TimeSpan.Zero));
        Assert.False(throwing.Available);
        Assert.True(throwing.HasRead);

        Assert.Null(ReadOnce(150.0).Latest);
        Assert.Null(ReadOnce(-40.0).Latest);
        Assert.Null(ReadOnce(double.NaN).Latest);
        Assert.Equal(36.5, ReadOnce(36.5).Latest);
        Assert.True(ReadOnce(36.5).Available);
    }

    [Fact]
    public void ChangedIsRaisedOnTheFirstReadAndWhenATemperatureComesOrGoes()
    {
        double? value = null;
        var heat = new HeatSensor(() => value);
        int changed = 0;
        heat.Changed += () => changed++;
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(1, changed);   // the first read, even with no temperature
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(1, changed);
        value = 30.0;
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(2, changed);
        value = 31.0;
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(2, changed);   // only a new value, not a new availability
        value = null;
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(3, changed);
        Assert.False(heat.Available);
    }

    [Fact]
    public void AThrowingChangedHandlerDoesNotBreakTheRead()
    {
        var heat = new HeatSensor(() => 33.0);
        heat.Changed += () => throw new InvalidOperationException("tray gone");
        Assert.Equal(33.0, heat.Refresh(TimeSpan.Zero));
        Assert.Equal(33.0, heat.Latest);
    }

    // ---- /status ----

    [Fact]
    public void StatusWithoutATemperatureListsOnlyLimitsAndNoBatteryTemp()
    {
        var limits = new Limits(settings);
        var s = Run(new StatusInfo(new FakeReadings { PowerNow = OnBattery(50) }, limits, ReadOnce(null)).Build(true));
        Assert.Equal(new[] { "limits" }, Features(s));
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
    public void StatusWithATemperatureListsHeatAndSendsBatteryTempWithOneDecimal()
    {
        var s = Run(new StatusInfo(new FakeReadings { PowerNow = OnBattery(50) }, new Limits(settings), ReadOnce(36.66)).Build(true));
        Assert.Equal(new[] { "limits", "heat" }, Features(s));
        Assert.Equal(36.7, s.GetProperty("batteryTemp").GetDouble());
        Assert.Equal(JsonValueKind.Number, s.GetProperty("batteryTemp").ValueKind);
        StatusInfoTests.AssertEveryKeyWithItsKind(s);
    }

    [Fact]
    public void BatteryTempIsSentOnItsOwnWithoutABattery()
    {
        // A desktop whose thermal zone reads: no battery keys, but the temperature (6.4).
        var s = Run(new StatusInfo(new FakeReadings { PowerNow = new PowerReading(1, 128, 255, -1) }, null, ReadOnce(31.04)).Build(true));
        Assert.False(s.TryGetProperty("battery", out _));
        Assert.Equal(31.0, s.GetProperty("batteryTemp").GetDouble());
        Assert.Contains("heat", Features(s));
    }

    [Fact]
    public void StatusSendsTheStoredSwitches()
    {
        var limits = new Limits(settings);
        var info = new StatusInfo(new FakeReadings(), limits, ReadOnce(30.0));
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
    public void HeatInFeaturesFollowsTheLastRead()
    {
        double? value = 35.0;
        var heat = new HeatSensor(() => value);
        var info = new StatusInfo(new FakeReadings(), new Limits(settings), heat);
        // Before the first read: no temperature yet.
        Assert.Equal(new[] { "limits" }, Features(Run(info.Build(true))));
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal(new[] { "limits", "heat" }, Features(Run(info.Build(true))));
        value = null;
        heat.Refresh(TimeSpan.Zero);
        var s = Run(info.Build(true));
        Assert.Equal(new[] { "limits" }, Features(s));
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
        Assert.True(t == null || TemperatureMath.IsSane(t.Value), $"temperature {t}");
        var heat = new HeatSensor(TemperatureSources.Read);
        var read = heat.Refresh(TimeSpan.Zero);
        Assert.Equal(read, heat.Latest);
        Assert.Equal(read != null, heat.Available);
        var s = Run(new StatusInfo(new SystemReadings(), new Limits(settings), heat).Build(true));
        Assert.Equal(read != null, Features(s).Contains("heat"));
        Assert.Equal(read != null, s.TryGetProperty("batteryTemp", out _));
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
        Assert.Equal(CtlCode(0x10), BatteryTemperature.IoctlBatteryQueryTag);
        Assert.Equal(CtlCode(0x11), BatteryTemperature.IoctlBatteryQueryInformation);
        Assert.Equal(0x294040u, BatteryTemperature.IoctlBatteryQueryTag);
        Assert.Equal(0x294044u, BatteryTemperature.IoctlBatteryQueryInformation);
    }
}
