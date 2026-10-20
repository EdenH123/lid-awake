using System.Diagnostics;
using System.Globalization;
using System.Management;
using System.Text.Json;
using LidAwake;
using Xunit;

// Task 33: every temperature source put together (TemperatureChain), and the laptop makers' own
// sensors (TemperatureVendors.cs): parsing and units, the kinds (battery, board, die), the order,
// "not on this PC" and its 10-minute recheck, the stuck-sensor check, the held and confirmed zone
// limit, throttling, and where each reading ends up in the safety limits and /status.
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

    // MSAcpi_ThermalZoneTemperature, tenths of a kelvin.
    static IReadOnlyDictionary<string, object?> AcpiZone(string name, uint tenths) =>
        Row(("InstanceName", name), ("CurrentTemperature", tenths));

    // The Thermal Zone Information counters: one zone in tenths of a kelvin, and a passive limit.
    static ThermalZoneReading Counters(long? tenths, long? passive = 100, string name = @"\_TZ.TZ00") =>
        ThermalZoneReading.From(new[] { (name, tenths, passive) });

    static WmiResult Rows(params IReadOnlyDictionary<string, object?>[] rows) => WmiResult.Of(rows);

    // Answers each query with what the test set (none: not on this PC), and counts the queries.
    sealed class FakeWmi : IWmi
    {
        public readonly Dictionary<string, WmiResult> Answers = new();
        public readonly List<string> Asked = new();
        public Exception? Throw;
        public Action<string>? Asking;

        public WmiResult Query(string scope, string query)
        {
            Asked.Add(query);
            Asking?.Invoke(query);
            if (Throw != null) throw Throw;
            return Answers.TryGetValue(query, out var r) ? r : WmiResult.Missing;
        }

        public WmiResult this[VendorSource source] { set => Answers[source.Query] = value; }
        public int Count(VendorSource source) => Asked.Count(a => a == source.Query);
        public int AcpiAsked => Asked.Count(a => a == ThermalZones.Query);
    }

    static readonly VendorSource Hp = VendorSensors.HpInstrumentedBios;
    static readonly VendorSource HpWmi = VendorSensors.HpAcpiWmi;
    static readonly VendorSource Dell = VendorSensors.DellCommandMonitor;

    double? battery;
    ThermalZoneReading? counters;
    double? load = 10;
    int countersAsked;

    TemperatureChain Chain(FakeWmi wmi) => new(() => battery, () => { countersAsked++; return counters; },
        VendorSensors.All, wmi, () => load, () => clock, logged.Add);

    void Later(TimeSpan by) => clock += (long)by.TotalMilliseconds;

    static PowerReading PluggedIn => new(1, 8, 100, -1);

    static string? Why(TemperatureReading reading) => Safety.Decide(true, true, PluggedIn, reading)?.LogOff;

    static string Shown(long tenths) =>
        TemperatureMath.OneDecimal(TemperatureMath.FromTenthsOfKelvin(tenths)!.Value).ToString("0.0", CultureInfo.InvariantCulture);

    // The turn-off for a zone read in tenths of a kelvin.
    static string Hot(long tenths) => $"Turned off: PC too hot ({Shown(tenths)}°C)";

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
    // Not degrees: Other ("Thermal Index"), Volts, RPM, Unknown.
    [InlineData(39L, 1L, 0L)]
    [InlineData(39L, 5L, 0L)]
    [InlineData(2500L, 19L, 0L)]
    [InlineData(39L, 0L, 0L)]
    // Outside -40 to 150 °C.
    [InlineData(151L, 2L, 0L)]
    [InlineData(1600L, 2L, -1L)]
    [InlineData(-41L, 2L, 0L)]
    [InlineData(400L, 4L, -1L)]
    // A modifier that is garbage.
    [InlineData(39L, 2L, 7L)]
    [InlineData(39L, 2L, -7L)]
    public void DegreesRejectsWhatIsNotASaneTemperature(long reading, long units, long modifier)
    {
        Assert.Null(VendorSensors.Degrees(reading, units, modifier));
    }

    [Theory]
    // Fillers for "no reading", rejected before scaling: 0x7FFF and 0xFFFF in thousandths would
    // otherwise be a plausible 32.8 and 65.5 °C.
    [InlineData(0L, -1L)]
    [InlineData(-1L, 0L)]
    [InlineData(0x7FFFL, -3L)]
    [InlineData(0xFFFFL, -3L)]
    [InlineData(0xFFFFFFFFL, -6L)]
    [InlineData(-2147483648L, -6L)]
    [InlineData(2147483647L, -6L)]
    public void RawSentinelsAreNoReading(long reading, long modifier)
    {
        Assert.True(VendorSensors.IsSentinel(reading));
        Assert.Null(VendorSensors.Degrees(reading, 2, modifier));
        Assert.False(VendorSensors.IsSentinel(0x7FFE));
        Assert.Equal(32.766, VendorSensors.Degrees(0x7FFE, 2, -3)!.Value, 6);
    }

    [Fact]
    public void TheSaneRangeEdgesAreKept()
    {
        Assert.Equal(150.0, VendorSensors.Degrees(150, 2, 0));
        Assert.Equal(-40.0, VendorSensors.Degrees(-40, 2, 0));
        Assert.Equal(-40.0, VendorSensors.Degrees(-40, 3, 0)!.Value, 6);
    }

    // ---- Kinds ----

    [Theory]
    [InlineData("Battery Temperature", SensorKind.Battery)]
    [InlineData("BATT Temp", SensorKind.Battery)]
    [InlineData("Temperature Sensor:Battery", SensorKind.Battery)]
    [InlineData("Battery Charger IC", SensorKind.Board)]
    [InlineData("Battery FET Temperature", SensorKind.Board)]
    [InlineData("Battery charging circuit", SensorKind.Board)]
    [InlineData("Battery VR", SensorKind.Board)]
    [InlineData("Battleground", SensorKind.Board)]
    [InlineData("CPU0 Temperature", SensorKind.Die)]
    [InlineData("Temperature Sensor:CPU Internal Temperature", SensorKind.Die)]
    [InlineData("Processor Package", SensorKind.Die)]
    [InlineData("GPU Temperature", SensorKind.Die)]
    [InlineData("Core 2", SensorKind.Die)]
    [InlineData("Chassis Temperature", SensorKind.Board)]
    [InlineData("Ambient Temperature", SensorKind.Board)]
    [InlineData("Memory Temperature", SensorKind.Board)]
    [InlineData("Skin Temperature", SensorKind.Board)]
    [InlineData("Chipset Temperature", SensorKind.Board)]
    [InlineData("Board Temperature", SensorKind.Board)]
    [InlineData("", SensorKind.Board)]
    [InlineData(null, SensorKind.Board)]
    public void SensorsAreSortedByTheirLabels(string? label, SensorKind kind)
    {
        Assert.Equal(kind, VendorSensors.Kind(label));
    }

    // ---- HP BIOS sensors ----

    [Fact]
    public void HpTemperatureSensorsAreReadWithTheirKind()
    {
        Assert.Equal(new VendorSensor("CPU0 Temperature", 39.0, SensorKind.Die), VendorSensors.Hp(HpSensor("CPU0 Temperature", 39)));
        Assert.Equal(new VendorSensor("Battery Temperature", 41.0, SensorKind.Battery), VendorSensors.Hp(HpSensor("Battery Temperature", 41)));
        Assert.Equal(new VendorSensor("BATT Temp", 35.5, SensorKind.Battery), VendorSensors.Hp(HpSensor("BATT Temp", 355, modifier: -1)));
        var fahrenheit = VendorSensors.Hp(HpSensor("Chassis Temperature", 104, units: 3))!.Value;
        Assert.Equal(40.0, fahrenheit.Celsius!.Value, 6);
        Assert.Equal(SensorKind.Board, fahrenheit.Kind);
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
        Assert.Equal(new VendorSensor("Memory Temperature", 44.0, SensorKind.Board), VendorSensors.Hp(named));
        var other = Row(("Name", "Sensor 3"), ("SensorType", 1u), ("OtherSensorType", "Temperature"), ("BaseUnits", 2u), ("CurrentReading", 50u));
        Assert.Equal(50.0, VendorSensors.Hp(other)!.Value.Celsius);
        var unnamed = Row(("Name", "Sensor 4"), ("BaseUnits", 2u), ("CurrentReading", 50u));
        Assert.Null(VendorSensors.Hp(unnamed));
    }

    [Fact]
    public void AnHpSensorWithAGarbageValueIsASensorWithNoTemperature()
    {
        var sensor = VendorSensors.Hp(HpSensor("Chassis Temperature", 0xFFFFFFFF));
        Assert.NotNull(sensor);
        Assert.Null(sensor!.Value.Celsius);
        Assert.Null(VendorSensors.Hp(HpSensor("Chassis Temperature", 0x7FFF, modifier: -3))!.Value.Celsius);
    }

    // ---- Dell Command | Monitor ----

    [Fact]
    public void DellTemperatureSensorsAreReadInTenths()
    {
        Assert.Equal(new VendorSensor("Temperature Sensor:CPU Internal Temperature", 45.2, SensorKind.Die),
            VendorSensors.Dell(DellSensor("Temperature Sensor:CPU Internal Temperature", 452)));
        Assert.Equal(new VendorSensor("Temperature Sensor:Battery", 38.0, SensorKind.Battery), VendorSensors.Dell(DellSensor("Temperature Sensor:Battery", 380)));
        // No ElementName: the DeviceID.
        var bare = Row(("DeviceID", "Root/MainSystemChassis/TemperatureObj"), ("SensorType", (ushort)2), ("BaseUnits", (ushort)2),
            ("UnitModifier", -1), ("CurrentReading", 400));
        Assert.Equal(new VendorSensor("Root/MainSystemChassis/TemperatureObj", 40.0, SensorKind.Board), VendorSensors.Dell(bare));
    }

    [Fact]
    public void DellSensorsThatAreNotTemperaturesOrHaveNoReadingAreSkipped()
    {
        // OptiPlexes and many laptops leave CurrentReading empty.
        Assert.Null(VendorSensors.Dell(DellSensor("Temperature Sensor:CPU Internal Temperature", null)));
        Assert.Null(VendorSensors.Dell(DellSensor("Fan:CPU Fan", 2400, type: 5, units: 19, modifier: 0)));
        Assert.Null(VendorSensors.Dell(DellSensor("Voltage", 1200, type: 3, units: 5, modifier: -3)));
        Assert.Null(VendorSensors.Dell(DellSensor("Temperature Sensor:Ambient", -1))!.Value.Celsius);
    }

    [Fact]
    public void ARowThatThrowsIsSkipped()
    {
        var rows = new[] { Row(("Name", "a")), Row(("Name", "b")) };
        var sensors = VendorSensors.Each(rows, r => (string)r["Name"]! == "a" ? throw new InvalidCastException() : new VendorSensor("b", 30.0, SensorKind.Board));
        Assert.Equal(new[] { new VendorSensor("b", 30.0, SensorKind.Board) }, sensors);
    }

    [Fact]
    public void TheSourcesAreInTheirOrderWithTheirClasses()
    {
        Assert.Equal(new[] { Hp, HpWmi, Dell }, VendorSensors.All);
        Assert.Equal(@"root\HP\InstrumentedBIOS", Hp.Scope);
        Assert.Contains("HP_BIOSNumericSensor", Hp.Query);
        Assert.Equal(@"root\WMI", HpWmi.Scope);
        Assert.Contains("HPBIOS_BIOSNumericSensor", HpWmi.Query);
        Assert.Equal(@"root\dcim\sysman", Dell.Scope);
        Assert.Contains("DCIM_NumericSensor", Dell.Query);
        Assert.DoesNotContain(VendorSensors.All, s => s.Query.Contains("Win32_TemperatureProbe"));
        Assert.Equal(@"root\WMI", ThermalZones.Scope);
        Assert.Contains("InstanceName", ThermalZones.Query);
        Assert.Contains("SELECT Name,", ThermalZoneReading.Query);
    }

    // ---- The counters' names, and the ACPI zones ----

    [Fact]
    public void TheCountersKeepEachZoneByName()
    {
        var r = ThermalZoneReading.FromRows(new[]
        {
            Row(("Name", @"\_TZ.TZ00"), ("HighPrecisionTemperature", 3032u), ("Temperature", 303u), ("PercentPassiveLimit", 100u)),
            Row(("Name", @"\_TZ.TZ01"), ("HighPrecisionTemperature", 0u), ("Temperature", 330u), ("PercentPassiveLimit", 70u)),
            Row(("Name", @"\_TZ.TZ02"), ("HighPrecisionTemperature", 0u), ("Temperature", 0u)),
        });
        Assert.Equal(new[] { @"\_TZ.TZ00", @"\_TZ.TZ01" }, r.Zones.Select(z => z.Name));
        Assert.Equal(56.85, r.Hottest!.Value, 2);
        Assert.Equal(70, r.PassiveLimit);
        Assert.Equal(ThermalZoneReading.None, ThermalZoneReading.FromRows(Array.Empty<IReadOnlyDictionary<string, object?>>()));
        Assert.Equal(r, ThermalZoneReading.FromRows(new[]
        {
            Row(("Name", @"\_TZ.TZ00"), ("HighPrecisionTemperature", 3032u), ("PercentPassiveLimit", 100u)),
            Row(("Name", @"\_TZ.TZ01"), ("Temperature", 330u), ("PercentPassiveLimit", 70u)),
        }));
    }

    [Fact]
    public void TheAcpiZonesAreEachRowWithATemperature()
    {
        var zones = ThermalZones.Each(new[] { AcpiZone(@"ACPI\ThermalZone\TZ00_0", 3212), AcpiZone("TZ01", 0), Row(("InstanceName", "TZ02")) });
        Assert.Equal(2, zones.Count);
        Assert.Equal(48.05, zones[0].Celsius!.Value, 6);
        Assert.Null(zones[1].Celsius);
        Assert.Equal(48.05, NamedZone.Hottest(zones)!.Value, 6);
    }

    // ---- Which source fills which slot ----

    [Fact]
    public void WindowsOwnSourcesComeFirstAndTheMakersAreNotAskedWhenTheyHaveBoth()
    {
        var wmi = new FakeWmi();
        wmi[Hp] = Rows(HpSensor("Chassis Temperature", 60));
        wmi.Answers[ThermalZones.Query] = Rows(AcpiZone("TZ00", 3212));
        battery = 31.0;
        var chain = Chain(wmi);
        var reading = chain.Read();
        Assert.Equal(31.0, reading.Battery);
        Assert.Equal(48.05, reading.Zone!.Value, 6);
        Assert.Equal(Limits.MaxPcTemp, reading.ZoneLimit);
        Assert.False(reading.ZoneConfirmed);
        Assert.Equal("the battery's own sensor and the ACPI thermal zones", chain.Source);
        Assert.Equal(0, wmi.Count(Hp));
        // The counters are read every round anyway (their passive limit), from the shared reading.
        Assert.Equal(1, countersAsked);
        Assert.Equal(new[] { "Temperature from the battery's own sensor and the ACPI thermal zones" }, logged);
    }

    [Fact]
    public void TheMakersFillWhatWindowsLeavesOut()
    {
        // The counters at 52 °C and HP's battery sensor at 41 °C: both, and the battery turns it off.
        var wmi = new FakeWmi();
        wmi[Hp] = Rows(HpSensor("Battery Temperature", 41));
        counters = Counters(3252);
        var chain = Chain(wmi);
        var reading = chain.Read();
        Assert.Equal(41.0, reading.Battery);
        Assert.Equal(52.05, reading.Zone!.Value, 6);
        Assert.Equal("HP BIOS sensors and the thermal zone counters", chain.Source);
        Assert.Equal("Turned off: battery too hot (41.0°C)", Why(reading));

        // Windows' own battery sensor wins over the maker's.
        battery = 33.0;
        Assert.Equal(33.0, chain.Read().Battery);
        Assert.Equal("the battery's own sensor and the thermal zone counters", chain.Source);
    }

    [Fact]
    public void AMakersBoardSensorIsPreferredOverTheCountersAndADieSensorOnlyWithoutEither()
    {
        var wmi = new FakeWmi();
        counters = Counters(3252);  // 52.05 °C
        wmi[Hp] = Rows(HpSensor("CPU0 Temperature", 70), HpSensor("Chassis Temperature", 45));
        var chain = Chain(wmi);
        Assert.Equal(new TemperatureReading(null, 45.0, Limits.MaxPcTemp, ZoneConfirmed: false), chain.Read());
        Assert.Equal("HP BIOS sensors", chain.Source);

        // Only a die sensor: the counters (board type) come first.
        wmi[Hp] = Rows(HpSensor("CPU0 Temperature", 70));
        Assert.Equal(52.05, chain.Read().Zone!.Value, 6);

        // No counters either: the die sensor, with its own 100 °C limit.
        counters = null;
        Assert.Equal(new TemperatureReading(null, 70.0, Limits.MaxDieTemp, ZoneConfirmed: false), chain.Read());
        Assert.Equal(100, Limits.MaxDieTemp);
    }

    [Fact]
    public void TheMakersAreAskedInOrderAndTheFirstWithAReadingIsUsed()
    {
        var wmi = new FakeWmi();
        wmi[Hp] = Rows(HpSensor("CPU0 Fan", 1008, type: 5, units: 19), HpSensor("Chassis Temperature", 58), HpSensor("Battery Temperature", 34));
        wmi[Dell] = Rows(DellSensor("Temperature Sensor:Ambient", 700));
        var chain = Chain(wmi);
        Assert.Equal(new TemperatureReading(34.0, 58.0, ZoneConfirmed: false), chain.Read());
        Assert.Equal("HP BIOS sensors", chain.Source);
        Assert.Equal(0, wmi.Count(HpWmi));
        Assert.Equal(0, wmi.Count(Dell));
        chain.Read();
        // Logged once only.
        Assert.Equal(new[] { "Temperature from HP BIOS sensors" }, logged);
    }

    [Fact]
    public void NoHpMeansDellAndHpIsNotAskedAgainForTenMinutes()
    {
        var wmi = new FakeWmi();
        wmi[Dell] = Rows(DellSensor("Temperature Sensor:Ambient", 452));
        var chain = Chain(wmi);
        for (int round = 0; round < 5; round++)
        {
            Assert.Equal(45.2, chain.Read().Zone);
            Later(TimeSpan.FromSeconds(15));
        }
        Assert.Equal("Dell Command | Monitor sensors", chain.Source);
        Assert.Equal(1, wmi.Count(Hp));
        Assert.Equal(1, wmi.Count(HpWmi));
        Assert.Equal(5, wmi.Count(Dell));
        // The ACPI zones (refused here) rest the same way.
        Assert.Equal(1, wmi.AcpiAsked);
        Assert.Equal(new[] { "Temperature from Dell Command | Monitor sensors" }, logged);

        // About ten minutes after the first look: HP and the ACPI zones are checked again.
        Later(TemperatureChain.Recheck);
        chain.Read();
        Assert.Equal(2, wmi.Count(Hp));
        Assert.Equal(2, wmi.Count(HpWmi));
        Assert.Equal(2, wmi.AcpiAsked);
    }

    [Fact]
    public void ARecheckFindsASourceThatAppearedLater()
    {
        var wmi = new FakeWmi();
        var chain = Chain(wmi);
        Assert.Equal(TemperatureReading.None, chain.Read());
        Assert.Null(chain.Source);
        Assert.Equal(new[] { ThermalZones.Query, Hp.Query, HpWmi.Query, Dell.Query }, wmi.Asked);
        // Dell Command | Monitor installed meanwhile: not seen until the recheck.
        wmi[Dell] = Rows(DellSensor("Temperature Sensor:Ambient", 452));
        Later(TemperatureChain.Recheck - TimeSpan.FromSeconds(1));
        Assert.Equal(TemperatureReading.None, chain.Read());
        Assert.Equal(4, wmi.Asked.Count);
        Later(TimeSpan.FromSeconds(1));
        Assert.Equal(45.2, chain.Read().Zone);
        Assert.Equal(new[] { "Temperature from Dell Command | Monitor sensors" }, logged);
    }

    [Fact]
    public void AClassWithNoTemperatureSensorCountsAsNotOnThisPc()
    {
        var wmi = new FakeWmi();
        // HP with fans only, Dell with empty readings, no ACPI zone at all.
        wmi[Hp] = Rows(HpSensor("CPU0 Fan", 1008, type: 5, units: 19));
        wmi[Dell] = Rows(DellSensor("Temperature Sensor:Ambient", null));
        wmi.Answers[ThermalZones.Query] = Rows();
        var chain = Chain(wmi);
        chain.Read();
        Later(TimeSpan.FromSeconds(15));
        chain.Read();
        Assert.Equal(new[] { ThermalZones.Query, Hp.Query, HpWmi.Query, Dell.Query }, wmi.Asked);
    }

    [Fact]
    public void SensorsWithGarbageValuesAreAskedAgainNextRound()
    {
        var wmi = new FakeWmi();
        wmi[Hp] = Rows(HpSensor("Chassis Temperature", 0xFFFFFFFF), HpSensor("Battery Temperature", 900));
        wmi[Dell] = Rows(DellSensor("Temperature Sensor:Ambient", 452));
        var chain = Chain(wmi);
        // HP's sensors say nothing sane: the next source in line is used this round.
        Assert.Equal(45.2, chain.Read().Zone);
        Later(TimeSpan.FromSeconds(15));
        wmi[Hp] = Rows(HpSensor("Chassis Temperature", 57));
        Assert.Equal(57.0, chain.Read().Zone);
        Assert.Equal(2, wmi.Count(Hp));
        Assert.Equal(new[] { "Temperature from Dell Command | Monitor sensors", "Temperature from HP BIOS sensors" }, logged);
    }

    [Fact]
    public void ASourceThatKeepsFailingRestsAfterThreeFailures()
    {
        var wmi = new FakeWmi();
        wmi[Hp] = WmiResult.Failed;
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
        flaky[Hp] = WmiResult.Failed;
        chain2.Read();
        chain2.Read();
        flaky[Hp] = Rows(HpSensor("Chassis Temperature", 50));
        chain2.Read();
        flaky[Hp] = WmiResult.Failed;
        chain2.Read();
        chain2.Read();
        chain2.Read();
        Assert.Equal(6, flaky.Count(Hp));
        chain2.Read();
        Assert.Equal(6, flaky.Count(Hp));
    }

    [Fact]
    public void TheMakersGetAboutSixSecondsPerRound()
    {
        var wmi = new FakeWmi();
        // Each maker's query takes 4 seconds here; HP isn't on this PC (both of its places).
        wmi.Asking = query => { if (query != ThermalZones.Query) Later(TimeSpan.FromSeconds(4)); };
        wmi[Dell] = Rows(DellSensor("Temperature Sensor:Ambient", 452));
        var chain = Chain(wmi);
        // HP twice (8 s): Dell waits for the next round.
        Assert.Null(chain.Read().Zone);
        Assert.Equal(0, wmi.Count(Dell));
        // HP rests now: Dell's turn.
        Assert.Equal(45.2, chain.Read().Zone);
        Assert.Equal(1, wmi.Count(Dell));
        Assert.Equal(TimeSpan.FromSeconds(6), TemperatureChain.MakersBudget);
    }

    [Fact]
    public void ThrowingSourcesGiveNothingAndNeverThrow()
    {
        var wmi = new FakeWmi { Throw = new ManagementException("broken") };
        var chain = new TemperatureChain(() => throw new InvalidOperationException(), () => throw new ManagementException(),
            VendorSensors.All, wmi, () => throw new TimeoutException(), () => clock, _ => throw new IOException("log"));
        Assert.Equal(TemperatureReading.None, chain.Read());
        Assert.Null(chain.Source);
        // A throwing log is no problem either.
        wmi.Throw = null;
        wmi[Hp] = Rows(HpSensor("Chassis Temperature", 50));
        Assert.Equal(50.0, chain.Read().Zone);
    }

    // ---- Is the zone real? (stuck values, movement, held for two reads) ----

    // Reads every 15 seconds while the processor load swings between 5% and 60%.
    void Rounds(HeatSensor heat, int count)
    {
        for (int i = 0; i < count; i++)
        {
            load = i % 2 == 0 ? 5 : 60;
            heat.Refresh(TimeSpan.Zero);
            Later(TimeSpan.FromSeconds(15));
        }
    }

    static string StuckLine(long tenths) =>
        $"The thermal zone counters have said {Shown(tenths)}°C for 30 minutes while the processor load changed: not used";

    HeatSensor Heat(FakeWmi? wmi = null) => new(Chain(wmi ?? new FakeWmi()).Read, () => clock);

    [Fact]
    public void AConstantFakeZoneBecomesStuckAndGreysTheItemOut()
    {
        counters = Counters(3010);  // a firmware's fixed 27.85 °C
        var heat = Heat();
        Rounds(heat, 4);
        Assert.True(heat.Available);
        // 30 minutes of 15-second rounds while the load moved: no longer used (after the kept misses).
        Rounds(heat, 120 + HeatSensor.MissesKept);
        Assert.False(heat.Available);
        Assert.Null(heat.Latest.Zone);
        Rounds(heat, 20);
        Assert.Single(logged, l => l == StuckLine(3010));

        // The battery limit still works.
        battery = 41.0;
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal("Turned off: battery too hot (41.0°C)", Why(heat.Latest));
        Assert.Null(heat.Latest.Zone);
    }

    [Fact]
    public void AZoneThatNeverChangesWhileTheLoadStaysIsNotStuck()
    {
        var heat = Heat();
        counters = Counters(3010);
        for (int i = 0; i < 200; i++)
        {
            heat.Refresh(TimeSpan.Zero);
            Later(TimeSpan.FromSeconds(15));
        }
        Assert.True(heat.Available);
        Assert.DoesNotContain(logged, l => l.Contains("not used"));
    }

    [Fact]
    public void AStuckZoneCountsAgainAsSoonAsItChanges()
    {
        var heat = Heat();
        counters = Counters(3010);
        Rounds(heat, 130);
        Assert.False(heat.Available);
        counters = Counters(3020);
        Rounds(heat, 1);
        Assert.True(heat.Available);
        Assert.Equal(28.85, heat.Latest.Zone!.Value, 6);
        // Logged once only, even when it sticks again.
        Rounds(heat, 130);
        Assert.False(heat.Available);
        Assert.Single(logged, l => l.Contains("not used"));
    }

    [Fact]
    public void AConstantFakeHotZoneNeverTurnsItOff()
    {
        var heat = Heat();
        counters = Counters(4002);  // 127.05 °C, never moving
        for (int i = 0; i < 10; i++)
        {
            heat.Refresh(TimeSpan.Zero);
            Later(TimeSpan.FromSeconds(15));
            Assert.Null(Why(heat.Latest));
            Assert.Null(new SafetyCheck(new Limits(settings), heat, () => PluggedIn).Now());
        }
        Assert.True(heat.Available);
    }

    // The turn-off after each reading of the counters' zone.
    string?[] Trips(HeatSensor heat, params long[] tenths)
    {
        var why = new List<string?>();
        foreach (var t in tenths)
        {
            counters = Counters(t);
            heat.Refresh(TimeSpan.Zero);
            why.Add(Why(heat.Latest));
        }
        return why.ToArray();
    }

    [Fact]
    public void ARealRisingZoneTurnsItOffAfterTwoReadsAtTheLimit()
    {
        // 85, 88, 91, 93 °C.
        Assert.Equal(new[] { null, null, null, Hot(3662) }, Trips(Heat(), 3582, 3612, 3642, 3662));
    }

    [Fact]
    public void AZoneMustStayAtTheLimitForTwoReadsInARow()
    {
        // 85, 91, 88, 92, 88, 93, 94 °C.
        Assert.Equal(new[] { null, null, null, null, null, null, Hot(3672) }, Trips(Heat(), 3582, 3642, 3612, 3652, 3612, 3662, 3672));
        // Hot from the start (95, 95, 96, 97 °C): it must still be seen to move first.
        Assert.Equal(new[] { null, null, null, Hot(3702) }, Trips(Heat(), 3682, 3682, 3692, 3702));
    }

    // The turn-off after each reading of one maker's sensor.
    string?[] MakerTrips(Func<int, IReadOnlyDictionary<string, object?>> sensor, VendorSource source, int count)
    {
        var wmi = new FakeWmi();
        var heat = Heat(wmi);
        var why = new List<string?>();
        for (int i = 0; i < count; i++)
        {
            wmi[source] = Rows(sensor(i));
            heat.Refresh(TimeSpan.Zero);
            why.Add(Why(heat.Latest));
        }
        return why.ToArray();
    }

    [Fact]
    public void ABoardSensorHasTheNinetyLimitADieSensorTheHundred()
    {
        uint[] board = { 89, 91, 92 };
        Assert.Equal(new[] { null, null, "Turned off: PC too hot (92.0°C)" },
            MakerTrips(i => HpSensor("Chassis Temperature", board[i]), Hp, 3));
        // 91 and 95 °C on the processor die are under its 100 °C limit.
        uint[] warm = { 89, 91, 95 };
        Assert.Equal(new string?[] { null, null, null }, MakerTrips(i => HpSensor("CPU0 Temperature", warm[i]), Hp, 3));
        uint[] hot = { 95, 101, 102 };
        Assert.Equal(new[] { null, null, "Turned off: PC too hot (102.0°C)" }, MakerTrips(i => HpSensor("CPU0 Temperature", hot[i]), Hp, 3));
        // Dell's processor sensor in tenths.
        int[] dell = { 990, 1005, 1008 };
        Assert.Equal(new[] { null, null, "Turned off: PC too hot (100.8°C)" },
            MakerTrips(i => DellSensor("Temperature Sensor:CPU Internal Temperature", dell[i]), Dell, 3));
    }

    [Fact]
    public void TheBatteryLimitNeedsNoMovement()
    {
        var wmi = new FakeWmi();
        wmi[Hp] = Rows(HpSensor("Battery Temperature", 41));
        var heat = Heat(wmi);
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal("Turned off: battery too hot (41.0°C)", Why(heat.Latest));
    }

    // ---- Windows slowing the processor down ----

    [Fact]
    public void WindowsHoldingTheProcessorToHalfForTwoReadsTurnsItOff()
    {
        var heat = Heat();
        var why = new List<string?>();
        foreach (var passive in new long?[] { 100, 40, 70, 50, 45 })
        {
            counters = Counters(3252, passive);
            heat.Refresh(TimeSpan.Zero);
            why.Add(Why(heat.Latest));
        }
        Assert.Equal(new[] { null, null, null, null, "Turned off: PC too hot (slowed down by Windows)" }, why);
        var reason = Safety.Decide(true, true, PluggedIn, heat.Latest)!;
        Assert.Equal("Your PC got too hot (slowed down by Windows)", reason.AlertReason);
        Assert.Equal("Not turned on: PC too hot (slowed down by Windows)", reason.LogRefused);
        Assert.Null(Safety.Decide(true, false, PluggedIn, heat.Latest));
        // Back to full speed: no reason.
        counters = Counters(3252, 100);
        heat.Refresh(TimeSpan.Zero);
        Assert.Null(Why(heat.Latest));
    }

    // ---- WMI pieces ----

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

    // (When claude/windows-phase-3's TestThreads is merged in, the SpinUntil waits below become
    // `await TestThreads.Until(...)` with TestThreads.Patience.)
    [Fact]
    public async Task OneAtATimeSharesARunningCallAndGivesTheFallbackOnceItIsStuck()
    {
        var once = new OneAtATime<int>();
        using var release = new ManualResetEventSlim();
        int calls = 0;
        int Slow() { Interlocked.Increment(ref calls); release.Wait(TimeSpan.FromSeconds(30)); return 1; }
        Assert.Equal(-1, once.Run(Slow, TimeSpan.FromMilliseconds(50), -1));
        // Past its timeout: the fallback at once, and no second call.
        var quick = Stopwatch.StartNew();
        Assert.Equal(-1, once.Run(Slow, TimeSpan.FromMilliseconds(50), -1));
        Assert.True(quick.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(1, Volatile.Read(ref calls));
        release.Set();
        Assert.True(SpinWait.SpinUntil(() => once.Run(() => 2, TimeSpan.FromSeconds(5), -1) == 2, TimeSpan.FromSeconds(10)));
        Assert.Equal(-1, once.Run(() => throw new InvalidOperationException(), TimeSpan.FromSeconds(5), -1));

        // Two callers of a call still within its time: both get its answer, from one call.
        var shared = new OneAtATime<int>();
        using var go = new ManualResetEventSlim();
        int runs = 0;
        int Gated() { Interlocked.Increment(ref runs); go.Wait(TimeSpan.FromSeconds(30)); return 7; }
        var first = Task.Run(() => shared.Run(Gated, TimeSpan.FromSeconds(20), -1));
        Assert.True(SpinWait.SpinUntil(() => Volatile.Read(ref runs) == 1, TimeSpan.FromSeconds(10)));
        var second = Task.Run(() => shared.Run(Gated, TimeSpan.FromSeconds(20), -1));
        go.Set();
        Assert.Equal(7, await first.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(7, await second.WaitAsync(TimeSpan.FromSeconds(20)));
        Assert.Equal(1, Volatile.Read(ref runs));
    }

    // ---- Into /status ----

    HeatSensor Sensor(FakeWmi wmi)
    {
        var heat = Heat(wmi);
        heat.Refresh(TimeSpan.Zero);
        return heat;
    }

    [Fact]
    public void AMakersSensorTurnsOnHeatInStatusAndOnlyABatterySensorIsBatteryTemp()
    {
        var wmi = new FakeWmi();
        wmi[Hp] = Rows(HpSensor("CPU0 Temperature", 52));
        var cpuOnly = Run(new StatusInfo(new FakeReadings(), new Limits(settings), Sensor(wmi)).Build(true));
        Assert.Contains("heat", Features(cpuOnly));
        Assert.False(cpuOnly.TryGetProperty("batteryTemp", out _));

        wmi[Hp] = Rows(HpSensor("CPU0 Temperature", 52), HpSensor("Battery Temperature", 333, modifier: -1));
        var withBattery = Run(new StatusInfo(new FakeReadings(), new Limits(settings), Sensor(wmi)).Build(true));
        Assert.Contains("heat", Features(withBattery));
        Assert.Equal(33.3, withBattery.GetProperty("batteryTemp").GetDouble());

        var nothing = Run(new StatusInfo(new FakeReadings(), new Limits(settings), Sensor(new FakeWmi())).Build(true));
        Assert.DoesNotContain("heat", Features(nothing));
    }

    static JsonElement Run(Dictionary<string, object?> status) => SafetyTests.Run(status);
    static string?[] Features(JsonElement s) => SafetyTests.Features(s);

    // ---- The real sources on this machine (CI: none of the makers') ----

    sealed class CountingWmi : IWmi
    {
        public readonly List<(string Query, WmiOutcome Outcome)> Asked = new();
        public WmiResult Query(string scope, string query)
        {
            var r = Wmi.Shared.Query(scope, query);
            lock (Asked) Asked.Add((query, r.Outcome));
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
            var sensors = VendorSensors.Each(result.Rows, source.Parse);
            Console.WriteLine($"RUNNER: {source.Scope} {source.Query}: {result.Outcome}, {result.Rows.Count} objects, " +
                              string.Join("; ", sensors.Select(s => $"{s.Label} {s.Celsius} {s.Kind}")));
            Assert.All(sensors, s => Assert.True(s.Celsius == null || TemperatureMath.IsSane(s.Celsius.Value)));
        }

        // With nothing from the battery and the counters: the ACPI zones and the makers (none on
        // the CI runner) give null, and what isn't on this PC is not asked again on the next round.
        var wmi = new CountingWmi();
        var chain = new TemperatureChain(() => null, () => null, VendorSensors.All, wmi, () => null, () => clock, logged.Add);
        var first = chain.Read();
        Console.WriteLine($"RUNNER: ACPI zones and makers' sources: {first} from {chain.Source ?? "nothing"}");
        if (chain.Source == null)
        {
            Assert.Equal(TemperatureReading.None, first);
            Assert.Empty(logged);
            int asked = wmi.Asked.Count;
            int rested = wmi.Asked.Count(a => a.Outcome != WmiOutcome.Failed);
            chain.Read();
            Assert.True(wmi.Asked.Count - asked <= asked - rested, $"asked again: {string.Join(", ", wmi.Asked)}");
        }
        else Assert.True(first.Any);
    }

    [Fact]
    public void TheRealSourcesWithTheMakersNeverThrow()
    {
        var took = Stopwatch.StartNew();
        var t = TemperatureSources.Read();
        took.Stop();
        Assert.True(t.Battery == null || TemperatureMath.IsSane(t.Battery.Value), $"battery {t.Battery}");
        Assert.True(t.Zone == null || TemperatureMath.IsSane(t.Zone.Value), $"zone {t.Zone}");
        var counted = ZoneCounters.Read();
        Assert.True(counted == null || counted.Zones.All(z => z.Celsius == null || TemperatureMath.IsSane(z.Celsius.Value)));
        // Read again within 10 seconds: the same shared reading, no second query.
        if (counted != null) Assert.Same(counted, ZoneCounters.Read());
        Console.WriteLine($"RUNNER: temperature {t} in {took.Elapsed}; counters {counted?.Zones.Count.ToString() ?? "unreadable"} zones");
    }
}
