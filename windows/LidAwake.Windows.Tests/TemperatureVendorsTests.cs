using System.Diagnostics;
using System.Management;
using System.Text.Json;
using LidAwake;
using Xunit;

// Task 33: the laptop makers' own temperature sensors (TemperatureVendors.cs), tried after
// Windows' own sources: parsing and units, the order, "not on this PC" and its 10-minute recheck,
// and where each reading ends up (the battery's 40 °C limit or the PC's 90 °C one).
public class TemperatureVendorsTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Settings settings;
    long clock = 1_000;
    readonly List<string> logged = new();

    public TemperatureVendorsTests() => settings = new Settings(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    // One WMI object, with the property types System.Management gives (uint32 as uint, sint32 as
    // int, uint16 as ushort).
    static IReadOnlyDictionary<string, object?> Row(params (string Name, object? Value)[] properties)
    {
        var row = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (name, value) in properties) row[name] = value;
        return row;
    }

    // HP_BIOSNumericSensor, as the BIOS's MOF declares it (uint32 / sint32).
    static IReadOnlyDictionary<string, object?> HpSensor(string name, uint reading, uint type = 2, uint units = 2, int modifier = 0,
        uint status = 2, string state = "Normal") =>
        Row(("Name", name), ("Description", "Reports " + name), ("SensorType", type), ("OtherSensorType", null),
            ("OperationalStatus", status), ("CurrentState", state), ("PossibleStates", new[] { "Normal", "Caution", "Critical", "Not Present" }),
            ("BaseUnits", units), ("UnitModifier", modifier), ("CurrentReading", reading));

    // DCIM_NumericSensor (CIM_NumericSensor: uint16 SensorType and BaseUnits, sint32 readings).
    static IReadOnlyDictionary<string, object?> DellSensor(string element, int? reading, ushort type = 2, ushort units = 2, int modifier = -1) =>
        Row(("ElementName", element), ("DeviceID", "Root/MainSystemChassis/TemperatureObj"), ("SensorType", type),
            ("BaseUnits", units), ("UnitModifier", modifier), ("CurrentReading", reading));

    static WmiResult Rows(params IReadOnlyDictionary<string, object?>[] rows) => WmiResult.Of(rows);

    // Answers each source's query with what the test set, and counts the queries.
    sealed class FakeWmi : IWmi
    {
        public readonly Dictionary<string, WmiResult> Answers = new();
        public readonly List<string> Asked = new();
        public Exception? Throw;

        public WmiResult Query(string scope, string query)
        {
            Asked.Add(scope);
            if (Throw != null) throw Throw;
            return Answers.TryGetValue(scope, out var r) ? r : WmiResult.Missing;
        }

        public int Count(VendorSource source) => Asked.Count(a => a == source.Scope);
    }

    static readonly VendorSource Hp = VendorSensors.HpInstrumentedBios;
    static readonly VendorSource HpWmi = VendorSensors.HpAcpiWmi;
    static readonly VendorSource Dell = VendorSensors.DellCommandMonitor;
    static readonly VendorSource Probes = VendorSensors.WindowsProbes;

    double? battery, acpi, counters;
    int countersAsked;

    TemperatureChain Chain(FakeWmi wmi) => new(() => battery, () => acpi, () => { countersAsked++; return counters; },
        VendorSensors.All, wmi, () => clock, logged.Add);

    void Later(TimeSpan by) => clock += (long)by.TotalMilliseconds;

    // ---- Units ----

    [Theory]
    // HP Z440: CPU0 Temperature, BaseUnits 2 (Degrees C), UnitModifier 0, CurrentReading 39.
    [InlineData(39L, 2L, 0L, 39.0)]
    // Dell: Degrees C in tenths (UnitModifier -1).
    [InlineData(452L, 2L, -1L, 45.2)]
    [InlineData(4520L, 2L, -2L, 45.2)]
    [InlineData(4L, 2L, 1L, 40.0)]
    // Degrees F and K.
    [InlineData(104L, 3L, 0L, 40.0)]
    [InlineData(1130L, 3L, -1L, 45.0)]
    [InlineData(3132L, 4L, -1L, 40.05)]
    [InlineData(313L, 4L, 0L, 39.85)]
    public void DegreesConvertsTheUnitAndThePowerOfTen(long reading, long units, long modifier, double expected)
    {
        Assert.Equal(expected, VendorSensors.Degrees(reading, units, modifier)!.Value, 6);
    }

    [Theory]
    // A raw 0 is no reading.
    [InlineData(0L, 2L, 0L)]
    // Not degrees: Other ("Thermal Index"), Volts, RPM, Unknown.
    [InlineData(39L, 1L, 0L)]
    [InlineData(39L, 5L, 0L)]
    [InlineData(2500L, 19L, 0L)]
    [InlineData(39L, 0L, 0L)]
    // Outside -40 to 150 °C.
    [InlineData(151L, 2L, 0L)]
    [InlineData(1600L, 2L, -1L)]
    [InlineData(-41L, 2L, 0L)]
    [InlineData(0xFFFFFFFFL, 2L, 0L)]
    [InlineData(0xFFFFL, 2L, -1L)]
    [InlineData(400L, 4L, -1L)]
    // A modifier that is garbage.
    [InlineData(39L, 2L, 7L)]
    [InlineData(39L, 2L, -7L)]
    public void DegreesRejectsWhatIsNotASaneTemperature(long reading, long units, long modifier)
    {
        Assert.Null(VendorSensors.Degrees(reading, units, modifier));
    }

    [Fact]
    public void TheSaneRangeEdgesAreKept()
    {
        Assert.Equal(150.0, VendorSensors.Degrees(150, 2, 0));
        Assert.Equal(-40.0, VendorSensors.Degrees(-40, 2, 0));
        Assert.Equal(-40.0, VendorSensors.Degrees(-40, 3, 0)!.Value, 6);
    }

    // ---- HP BIOS sensors ----

    [Fact]
    public void HpTemperatureSensorsAreReadAndLabelledBatteryOrNot()
    {
        Assert.Equal(new VendorSensor("CPU0 Temperature", 39.0, false), VendorSensors.Hp(HpSensor("CPU0 Temperature", 39)));
        Assert.Equal(new VendorSensor("Battery Temperature", 41.0, true), VendorSensors.Hp(HpSensor("Battery Temperature", 41)));
        Assert.Equal(new VendorSensor("BATT Temp", 35.5, true), VendorSensors.Hp(HpSensor("BATT Temp", 355, modifier: -1)));
        // Fahrenheit and kelvin sensors.
        Assert.Equal(40.0, VendorSensors.Hp(HpSensor("Chassis Temperature", 104, units: 3))!.Value.Celsius!.Value, 6);
        Assert.Equal(39.85, VendorSensors.Hp(HpSensor("Board Temperature", 313, units: 4))!.Value.Celsius!.Value, 6);
    }

    [Fact]
    public void HpSensorsThatAreNotTemperaturesOrAreNotThereAreSkipped()
    {
        // A fan (Tachometer, RPM) and a voltage.
        Assert.Null(VendorSensors.Hp(HpSensor("CPU0 Fan", 1008, type: 5, units: 19)));
        Assert.Null(VendorSensors.Hp(HpSensor("CPU Voltage", 1100, type: 3, units: 5, modifier: -3)));
        // HP's "Thermal Index" is a temperature sensor in "Other" units: not degrees.
        Assert.Null(VendorSensors.Hp(HpSensor("CPU Thermal Index", 30, units: 1)));
        // "Not Present" and "No Contact" (OperationalStatus 12).
        Assert.Null(VendorSensors.Hp(HpSensor("Ambient Temperature", 0, state: "Not Present")));
        Assert.Null(VendorSensors.Hp(HpSensor("Ambient Temperature", 25, status: 12)));
        // No reading at all.
        Assert.Null(VendorSensors.Hp(Row(("Name", "CPU0 Temperature"), ("SensorType", 2u), ("BaseUnits", 2u))));
    }

    [Fact]
    public void AnHpSensorWithoutASensorTypeCountsWhenItsNameSaysTemperature()
    {
        var named = Row(("Name", "Memory Temperature"), ("BaseUnits", 2u), ("UnitModifier", 0), ("CurrentReading", 44u));
        Assert.Equal(new VendorSensor("Memory Temperature", 44.0, false), VendorSensors.Hp(named));
        var other = Row(("Name", "Sensor 3"), ("SensorType", 1u), ("OtherSensorType", "Temperature"), ("BaseUnits", 2u), ("CurrentReading", 50u));
        Assert.Equal(50.0, VendorSensors.Hp(other)!.Value.Celsius);
        var unnamed = Row(("Name", "Sensor 4"), ("BaseUnits", 2u), ("CurrentReading", 50u));
        Assert.Null(VendorSensors.Hp(unnamed));
    }

    [Fact]
    public void AnHpSensorWithAGarbageValueIsASensorWithNoTemperature()
    {
        var sensor = VendorSensors.Hp(HpSensor("CPU0 Temperature", 0xFFFFFFFF));
        Assert.NotNull(sensor);
        Assert.Null(sensor!.Value.Celsius);
    }

    // ---- Dell Command | Monitor ----

    [Fact]
    public void DellTemperatureSensorsAreReadInTenths()
    {
        Assert.Equal(new VendorSensor("Temperature Sensor:CPU Internal Temperature", 45.2, false),
            VendorSensors.Dell(DellSensor("Temperature Sensor:CPU Internal Temperature", 452)));
        Assert.Equal(new VendorSensor("Temperature Sensor:Battery", 38.0, true), VendorSensors.Dell(DellSensor("Temperature Sensor:Battery", 380)));
        // No ElementName: the DeviceID.
        var bare = Row(("DeviceID", "Root/MainSystemChassis/TemperatureObj"), ("SensorType", (ushort)2), ("BaseUnits", (ushort)2),
            ("UnitModifier", -1), ("CurrentReading", 400));
        Assert.Equal(new VendorSensor("Root/MainSystemChassis/TemperatureObj", 40.0, false), VendorSensors.Dell(bare));
    }

    [Fact]
    public void DellSensorsThatAreNotTemperaturesOrHaveNoReadingAreSkipped()
    {
        // OptiPlexes and many laptops leave CurrentReading empty.
        Assert.Null(VendorSensors.Dell(DellSensor("Temperature Sensor:CPU Internal Temperature", null)));
        Assert.Null(VendorSensors.Dell(DellSensor("Fan:CPU Fan", 2400, type: 5, units: 19, modifier: 0)));
        Assert.Null(VendorSensors.Dell(DellSensor("Voltage", 1200, type: 3, units: 5, modifier: -3)));
    }

    // ---- Win32_TemperatureProbe ----

    [Fact]
    public void WindowsProbesAreInTenthsOfADegree()
    {
        Assert.Equal(new VendorSensor("Numeric Sensor", 40.5, false), VendorSensors.Probe(Row(("Name", "Numeric Sensor"), ("CurrentReading", 405))));
        Assert.Equal(new VendorSensor("Battery probe", 33.0, true), VendorSensors.Probe(Row(("Name", "Battery probe"), ("CurrentReading", 330))));
        // Not filled (as Microsoft says it isn't today): no sensor.
        Assert.Null(VendorSensors.Probe(Row(("Name", "Numeric Sensor"), ("CurrentReading", null))));
        Assert.Null(VendorSensors.Probe(Row(("Name", "Numeric Sensor"))));
        // Filled with garbage: a sensor, no temperature.
        Assert.Null(VendorSensors.Probe(Row(("Name", "Numeric Sensor"), ("CurrentReading", 9999)))!.Value.Celsius);
    }

    [Fact]
    public void TheHottestBatterySensorAndTheHottestOtherAreKeptApart()
    {
        var reading = VendorSensors.Hottest(new[]
        {
            new VendorSensor("CPU0 Temperature", 55.0, false),
            new VendorSensor("Battery Temperature", 36.0, true),
            new VendorSensor("Chassis Temperature", 61.0, false),
            new VendorSensor("Battery 2", 37.5, true),
            new VendorSensor("Garbage", null, false),
            new VendorSensor("Hot garbage", 400.0, false),
        });
        Assert.Equal(new TemperatureReading(37.5, 61.0), reading);
        Assert.Equal(TemperatureReading.None, VendorSensors.Hottest(Array.Empty<VendorSensor>()));
    }

    [Fact]
    public void ARowThatThrowsIsSkipped()
    {
        var rows = new[] { Row(("Name", "a")), Row(("Name", "b")) };
        var sensors = VendorSensors.Each(rows, r => (string)r["Name"]! == "a" ? throw new InvalidCastException() : new VendorSensor("b", 30.0, false));
        Assert.Equal(new[] { new VendorSensor("b", 30.0, false) }, sensors);
    }

    [Fact]
    public void TheSourcesAreInTheirOrderWithTheirClasses()
    {
        Assert.Equal(new[] { Hp, HpWmi, Dell, Probes }, VendorSensors.All);
        Assert.Equal(@"root\HP\InstrumentedBIOS", Hp.Scope);
        Assert.Contains("HP_BIOSNumericSensor", Hp.Query);
        Assert.Equal(@"root\WMI", HpWmi.Scope);
        Assert.Contains("HPBIOS_BIOSNumericSensor", HpWmi.Query);
        Assert.Equal(@"root\dcim\sysman", Dell.Scope);
        Assert.Contains("DCIM_NumericSensor", Dell.Query);
        Assert.Equal(@"root\cimv2", Probes.Scope);
        Assert.Contains("Win32_TemperatureProbe", Probes.Query);
    }

    // ---- The order: Windows' own sources first ----

    [Fact]
    public void WindowsOwnSourcesComeFirstAndTheMakersAreNotAsked()
    {
        var wmi = new FakeWmi();
        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Temperature", 60));
        var chain = Chain(wmi);
        battery = 31.0;
        Assert.Equal(new TemperatureReading(31.0, null), chain.Read());
        Assert.Equal(TemperatureChain.FromBattery, chain.Source);
        // No ACPI zone: the counters are asked.
        Assert.Equal(1, countersAsked);
        Assert.Empty(wmi.Asked);

        acpi = 48.0;
        Assert.Equal(new TemperatureReading(31.0, 48.0), chain.Read());
        Assert.Equal(1, countersAsked);
        Assert.Equal("the battery's own sensor and the ACPI thermal zones", chain.Source);

        battery = acpi = null;
        counters = 52.0;
        Assert.Equal(new TemperatureReading(null, 52.0), chain.Read());
        Assert.Equal(TemperatureChain.FromZoneCounters, chain.Source);
        Assert.Empty(wmi.Asked);
        Assert.Equal(new[]
        {
            "Temperature from the battery's own sensor",
            "Temperature from the battery's own sensor and the ACPI thermal zones",
            "Temperature from the thermal zone counters",
        }, logged);
    }

    [Fact]
    public void WindowsOwnValuesOutsideTheSaneRangeCountAsNothing()
    {
        var wmi = new FakeWmi();
        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Temperature", 60));
        battery = 400.0;
        acpi = double.NaN;
        counters = -50.0;
        Assert.Equal(new TemperatureReading(null, 60.0), Chain(wmi).Read());
    }

    [Fact]
    public void TheMakersAreAskedInOrderAndTheFirstWithAReadingIsUsed()
    {
        var wmi = new FakeWmi();
        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Fan", 1008, type: 5, units: 19), HpSensor("CPU0 Temperature", 58),
            HpSensor("Battery Temperature", 34));
        wmi.Answers[Dell.Scope] = Rows(DellSensor("Temperature Sensor:CPU Internal Temperature", 700));
        var chain = Chain(wmi);
        Assert.Equal(new TemperatureReading(34.0, 58.0), chain.Read());
        Assert.Equal("HP BIOS sensors", chain.Source);
        Assert.Equal(new[] { Hp.Scope }, wmi.Asked);
        chain.Read();
        // Logged once only.
        Assert.Equal(new[] { "Temperature from HP BIOS sensors" }, logged);
    }

    [Fact]
    public void NoHpMeansDellAndHpIsNotAskedAgainForTenMinutes()
    {
        var wmi = new FakeWmi();
        wmi.Answers[Dell.Scope] = Rows(DellSensor("Temperature Sensor:CPU Internal Temperature", 452));
        var chain = Chain(wmi);
        for (int round = 0; round < 5; round++)
        {
            Assert.Equal(new TemperatureReading(null, 45.2), chain.Read());
            Later(TimeSpan.FromSeconds(15));
        }
        Assert.Equal("Dell Command | Monitor sensors", chain.Source);
        Assert.Equal(1, wmi.Count(Hp));
        Assert.Equal(1, wmi.Count(HpWmi));
        Assert.Equal(5, wmi.Count(Dell));
        Assert.Equal(0, wmi.Count(Probes));
        Assert.Equal(new[] { "Temperature from Dell Command | Monitor sensors" }, logged);

        // About ten minutes after the first look: HP is checked again.
        Later(TemperatureChain.Recheck);
        chain.Read();
        Assert.Equal(2, wmi.Count(Hp));
        Assert.Equal(2, wmi.Count(HpWmi));
    }

    [Fact]
    public void ARecheckFindsASourceThatAppearedLater()
    {
        var wmi = new FakeWmi();
        var chain = Chain(wmi);
        Assert.Equal(TemperatureReading.None, chain.Read());
        Assert.Null(chain.Source);
        Assert.Equal(new[] { Hp.Scope, HpWmi.Scope, Dell.Scope, Probes.Scope }, wmi.Asked);
        // Dell Command | Monitor installed meanwhile: not seen until the recheck.
        wmi.Answers[Dell.Scope] = Rows(DellSensor("Temperature Sensor:CPU Internal Temperature", 452));
        Later(TemperatureChain.Recheck - TimeSpan.FromSeconds(1));
        Assert.Equal(TemperatureReading.None, chain.Read());
        Assert.Equal(4, wmi.Asked.Count);
        Later(TimeSpan.FromSeconds(1));
        Assert.Equal(new TemperatureReading(null, 45.2), chain.Read());
        Assert.Equal(new[] { "Temperature from Dell Command | Monitor sensors" }, logged);
    }

    [Fact]
    public void AClassWithNoTemperatureSensorCountsAsNotOnThisPc()
    {
        var wmi = new FakeWmi();
        // HP with fans only, Dell with empty readings, probes with nothing filled.
        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Fan", 1008, type: 5, units: 19));
        wmi.Answers[Dell.Scope] = Rows(DellSensor("Temperature Sensor:CPU Internal Temperature", null));
        wmi.Answers[Probes.Scope] = Rows(Row(("Name", "Numeric Sensor"), ("CurrentReading", null)));
        var chain = Chain(wmi);
        chain.Read();
        Later(TimeSpan.FromSeconds(15));
        chain.Read();
        Assert.Equal(new[] { Hp.Scope, HpWmi.Scope, Dell.Scope, Probes.Scope }, wmi.Asked);
        // An empty class too.
        var empty = new FakeWmi();
        empty.Answers[Probes.Scope] = Rows();
        var other = Chain(empty);
        other.Read();
        other.Read();
        Assert.Equal(1, empty.Count(Probes));
    }

    [Fact]
    public void SensorsWithGarbageValuesAreAskedAgainNextRound()
    {
        var wmi = new FakeWmi();
        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Temperature", 0xFFFFFFFF), HpSensor("Battery Temperature", 900));
        wmi.Answers[Dell.Scope] = Rows(DellSensor("Temperature Sensor:CPU Internal Temperature", 452));
        var chain = Chain(wmi);
        // HP's sensors say nothing sane: the next source in line is used this round.
        Assert.Equal(new TemperatureReading(null, 45.2), chain.Read());
        Later(TimeSpan.FromSeconds(15));
        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Temperature", 57));
        Assert.Equal(new TemperatureReading(null, 57.0), chain.Read());
        Assert.Equal(2, wmi.Count(Hp));
        Assert.Equal(new[] { "Temperature from Dell Command | Monitor sensors", "Temperature from HP BIOS sensors" }, logged);
    }

    [Fact]
    public void ASourceThatKeepsFailingRestsAfterThreeFailures()
    {
        var wmi = new FakeWmi();
        wmi.Answers[Hp.Scope] = WmiResult.Failed;
        var chain = Chain(wmi);
        for (int round = 0; round < TemperatureChain.FailuresBeforeRest + 2; round++)
        {
            chain.Read();
            Later(TimeSpan.FromSeconds(15));
        }
        Assert.Equal(TemperatureChain.FailuresBeforeRest, wmi.Count(Hp));
        // A success in between starts the count again.
        var flaky = new FakeWmi();
        var chain2 = Chain(flaky);
        flaky.Answers[Hp.Scope] = WmiResult.Failed;
        chain2.Read();
        chain2.Read();
        flaky.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Temperature", 50));
        chain2.Read();
        flaky.Answers[Hp.Scope] = WmiResult.Failed;
        chain2.Read();
        chain2.Read();
        chain2.Read();
        Assert.Equal(6, flaky.Count(Hp));
        chain2.Read();
        Assert.Equal(6, flaky.Count(Hp));
    }

    [Fact]
    public void ThrowingSourcesGiveNothingAndNeverThrow()
    {
        var wmi = new FakeWmi { Throw = new ManagementException("broken") };
        var chain = new TemperatureChain(() => throw new InvalidOperationException(), () => throw new TimeoutException(),
            () => throw new ManagementException(), VendorSensors.All, wmi, () => clock, _ => throw new IOException("log"));
        Assert.Equal(TemperatureReading.None, chain.Read());
        Assert.Null(chain.Source);
        // A throwing log is no problem either.
        wmi.Throw = null;
        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Temperature", 50));
        Later(TemperatureChain.Recheck);
        Assert.Equal(new TemperatureReading(null, 50.0), chain.Read());
    }

    [Fact]
    public void MissingNamespacesAndClassesAreNotOnThisPcOtherErrorsAreFailures()
    {
        Assert.True(Wmi.IsMissing(ManagementStatus.InvalidNamespace));
        Assert.True(Wmi.IsMissing(ManagementStatus.InvalidClass));
        Assert.True(Wmi.IsMissing(ManagementStatus.NotFound));
        Assert.True(Wmi.IsMissing(ManagementStatus.AccessDenied));
        Assert.True(Wmi.IsMissing(ManagementStatus.ProviderLoadFailure));
        Assert.False(Wmi.IsMissing(ManagementStatus.Failed));
        Assert.False(Wmi.IsMissing(ManagementStatus.Timedout));
        Assert.False(Wmi.IsMissing(ManagementStatus.OutOfMemory));
    }

    [Fact]
    public void WmiValuesAreWholeNumbersOfEveryWidth()
    {
        Assert.Equal(7L, WmiValues.Whole((byte)7));
        Assert.Equal(-7L, WmiValues.Whole((sbyte)-7));
        Assert.Equal(7L, WmiValues.Whole((ushort)7));
        Assert.Equal(-7L, WmiValues.Whole((short)-7));
        Assert.Equal(7L, WmiValues.Whole(7u));
        Assert.Equal(-7L, WmiValues.Whole(-7));
        Assert.Equal(-7L, WmiValues.Whole(-7L));
        Assert.Equal(7L, WmiValues.Whole(7UL));
        Assert.Null(WmiValues.Whole(ulong.MaxValue));
        Assert.Null(WmiValues.Whole("7"));
        Assert.Null(WmiValues.Whole(7.0));
        Assert.Null(WmiValues.Whole(new uint[] { 2 }));
        Assert.Null(WmiValues.Whole(null));
    }

    [Fact]
    public void OneAtATimeGivesTheFallbackOnATimeoutAndStartsNoSecondCall()
    {
        var once = new OneAtATime<int>();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        int Slow() { Interlocked.Increment(ref calls); release.Wait(TimeSpan.FromSeconds(30)); return 1; }
        Assert.Equal(-1, once.Run(Slow, TimeSpan.FromMilliseconds(50), -1));
        // Still stuck: no second call.
        Assert.Equal(-1, once.Run(Slow, TimeSpan.FromMilliseconds(50), -1));
        Assert.Equal(1, Volatile.Read(ref calls));
        release.Set();
        var wait = Stopwatch.StartNew();
        int answer = -1;
        while (wait.Elapsed < TimeSpan.FromSeconds(10) && (answer = once.Run(() => 2, TimeSpan.FromSeconds(5), -1)) == -1) Thread.Sleep(10);
        Assert.Equal(2, answer);
        Assert.Equal(-1, once.Run(() => throw new InvalidOperationException(), TimeSpan.FromSeconds(5), -1));
    }

    // ---- Into the safety limits and /status ----

    static PowerReading PluggedIn => new(1, 8, 100, -1);

    HeatSensor Sensor(FakeWmi wmi)
    {
        var chain = Chain(wmi);
        var heat = new HeatSensor(chain.Read);
        heat.Refresh(TimeSpan.Zero);
        return heat;
    }

    [Fact]
    public void ABatterySensorGetsTheBatteryLimitAndAProcessorSensorThePcLimit()
    {
        var wmi = new FakeWmi();
        wmi.Answers[Hp.Scope] = Rows(HpSensor("Battery Temperature", 41), HpSensor("CPU0 Temperature", 85));
        var hot = Sensor(wmi);
        Assert.True(hot.Available);
        Assert.Equal("Turned off: battery too hot (41.0°C)", Safety.Decide(true, true, PluggedIn, hot.Latest)!.LogOff);
        // 85 °C on the processor is under the PC's 90 °C limit, and well over the battery's 40.
        wmi.Answers[Hp.Scope] = Rows(HpSensor("Battery Temperature", 39), HpSensor("CPU0 Temperature", 85));
        var warmCpu = Sensor(wmi);
        Assert.Null(Safety.Decide(true, true, PluggedIn, warmCpu.Latest));
        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Temperature", 91));
        var hotCpu = Sensor(wmi);
        Assert.Equal("Turned off: PC too hot (91.0°C)", Safety.Decide(true, true, PluggedIn, hotCpu.Latest)!.LogOff);
        Assert.Null(Safety.Decide(true, false, PluggedIn, hotCpu.Latest));
        // Dell's processor sensor in tenths.
        var dell = new FakeWmi();
        dell.Answers[Dell.Scope] = Rows(DellSensor("Temperature Sensor:CPU Internal Temperature", 905));
        Assert.Equal("Turned off: PC too hot (90.5°C)", Safety.Decide(true, true, PluggedIn, Sensor(dell).Latest)!.LogOff);
    }

    [Fact]
    public void AMakersSensorTurnsOnHeatInStatusAndOnlyABatterySensorIsBatteryTemp()
    {
        var wmi = new FakeWmi();
        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Temperature", 52));
        var cpuOnly = Run(new StatusInfo(new FakeReadings(), new Limits(settings), Sensor(wmi)).Build(true));
        Assert.Contains("heat", Features(cpuOnly));
        Assert.False(cpuOnly.TryGetProperty("batteryTemp", out _));

        wmi.Answers[Hp.Scope] = Rows(HpSensor("CPU0 Temperature", 52), HpSensor("Battery Temperature", 333, modifier: -1));
        var withBattery = Run(new StatusInfo(new FakeReadings(), new Limits(settings), Sensor(wmi)).Build(true));
        Assert.Contains("heat", Features(withBattery));
        Assert.Equal(33.3, withBattery.GetProperty("batteryTemp").GetDouble());

        var nothing = Run(new StatusInfo(new FakeReadings(), new Limits(settings), Sensor(new FakeWmi())).Build(true));
        Assert.DoesNotContain("heat", Features(nothing));
    }

    static JsonElement Run(Dictionary<string, object?> status) => SafetyTests.Run(status);
    static string?[] Features(JsonElement s) => SafetyTests.Features(s);

    // ---- The real makers' sources on this machine (CI: none of them) ----

    // Counts the real queries.
    sealed class CountingWmi : IWmi
    {
        public readonly List<(string Scope, WmiOutcome Outcome)> Asked = new();
        public WmiResult Query(string scope, string query)
        {
            var r = Wmi.Shared.Query(scope, query);
            lock (Asked) Asked.Add((scope, r.Outcome));
            return r;
        }
    }

    [Fact]
    public void TheRealMakersSourcesNeverThrowAndGiveNothingOrSane()
    {
        foreach (var source in VendorSensors.All)
        {
            var took = Stopwatch.StartNew();
            var result = Wmi.Shared.Query(source.Scope, source.Query);
            took.Stop();
            Assert.True(took.Elapsed < TimeSpan.FromSeconds(10), $"{source.Scope} took {took.Elapsed}");
            var reading = VendorSensors.Hottest(VendorSensors.Each(result.Rows, source.Parse));
            Console.WriteLine($"RUNNER: {source.Scope} {source.Query}: {result.Outcome}, {result.Rows.Count} objects, {reading}");
            Assert.True(reading.Battery == null || TemperatureMath.IsSane(reading.Battery.Value));
            Assert.True(reading.Zone == null || TemperatureMath.IsSane(reading.Zone.Value));
        }

        // With nothing from Windows' own sources: the makers' (none on the CI runner) give null,
        // and what isn't on this PC is not asked again on the next round.
        var wmi = new CountingWmi();
        var chain = new TemperatureChain(() => null, () => null, () => null, VendorSensors.All, wmi, () => clock, logged.Add);
        var first = chain.Read();
        Console.WriteLine($"RUNNER: makers' sources: {first} from {chain.Source ?? "nothing"}");
        if (chain.Source == null)
        {
            Assert.Equal(TemperatureReading.None, first);
            Assert.Empty(logged);
        }
        else Assert.True(first.Any);
        int asked = wmi.Asked.Count;
        int resting = wmi.Asked.Count(a => a.Outcome != WmiOutcome.Failed);
        chain.Read();
        if (chain.Source == null) Assert.True(wmi.Asked.Count - asked <= asked - resting, $"asked again: {string.Join(", ", wmi.Asked)}");
    }

    [Fact]
    public void TheRealSourcesWithTheMakersNeverThrow()
    {
        var took = Stopwatch.StartNew();
        var t = TemperatureSources.Read();
        took.Stop();
        Assert.True(t.Battery == null || TemperatureMath.IsSane(t.Battery.Value), $"battery {t.Battery}");
        Assert.True(t.Zone == null || TemperatureMath.IsSane(t.Zone.Value), $"zone {t.Zone}");
        Console.WriteLine($"RUNNER: temperature {t} in {took.Elapsed}");
    }
}
