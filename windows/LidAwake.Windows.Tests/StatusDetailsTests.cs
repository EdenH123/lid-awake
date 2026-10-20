using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using LidAwake;
using Xunit;

// Readings with the slow ones too (the thermal zones' counters and the batteries' drivers), set by
// hand. Everything else comes from a FakeReadings.
public sealed class SlowFakeReadings : ISystemReadings, ISlowReadings
{
    public readonly FakeReadings Inner = new();
    public ThermalZoneReading? ZonesNow;
    public BatteryCharge? ChargeNow;

    public PowerReading? Power() => Inner.Power();
    public MemoryReading? Memory() => Inner.Memory();
    public CpuTimes? Cpu() => Inner.Cpu();
    public DiskReading? Disk() => Inner.Disk();
    public NetCounters? Network() => Inner.Network();
    public WifiReading? Wifi(string? adapterId) => Inner.Wifi(adapterId);
    public long UptimeSeconds() => Inner.UptimeSeconds();
    public int ScreenWidth() => Inner.ScreenWidth();
    public string HostName() => Inner.HostName();
    public long Milliseconds() => Inner.Milliseconds();
    public ThermalZoneReading? Zones() => ZonesNow;
    public BatteryCharge? Charge() => ChargeNow;
}

// The /status details that differed from the Mac (link, thermalState, minutesToFull, charged,
// host, screenWidth) and the iPhone launch screens. The CI runner has no battery and no thermal
// zone, so the maths is checked with made-up readings.
public class StatusDetailsTests
{
    static JsonElement Run(Dictionary<string, object?> status)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(status));
        return doc.RootElement.Clone();
    }

    // ---- BackgroundValue: never waits, reads at most every so often, keeps the last good value ----

    [Fact]
    public void ABackgroundValueReadsAtMostEverySoOftenAndKeepsTheLastGoodValue()
    {
        long now = 0;
        int reads = 0;
        string? next = "a";
        bool fail = false;
        var value = new BackgroundValue<string>(() =>
        {
            reads++;
            if (fail) throw new InvalidOperationException("read failed");
            return next;
        }, TimeSpan.FromSeconds(10), () => now, work => work());
        Assert.Equal("a", value.Latest);
        now = 9_999;
        Assert.Equal("a", value.Latest);
        Assert.Equal(1, reads);
        now = 10_000;
        next = "b";
        Assert.Equal("b", value.Latest);
        now = 20_000;
        next = null;                    // nothing this time: the last value stays
        Assert.Equal("b", value.Latest);
        now = 30_000;
        fail = true;                    // a read that throws: the same
        Assert.Equal("b", value.Latest);
        Assert.Equal(4, reads);
    }

    [Fact]
    public void ABackgroundValueAnswersAtOnceAndReadsOneAtATime()
    {
        long now = 0;
        var started = new List<Action>();
        var value = new BackgroundValue<string>(() => "x", TimeSpan.FromSeconds(10), () => now, started.Add);
        // The read hasn't run yet: nothing, at once.
        Assert.Null(value.Latest);
        now = 60_000;
        Assert.Null(value.Latest);
        Assert.Single(started);         // no second read while the first one runs
        started[0]();
        Assert.Equal("x", value.Latest);
        Assert.Single(started);
        // A start that fails lets the next ask try again.
        int tries = 0;
        var broken = new BackgroundValue<string>(() => "y", TimeSpan.FromSeconds(10), () => now, _ => { tries++; throw new InvalidOperationException(); });
        Assert.Null(broken.Latest);
        Assert.Null(broken.Latest);
        Assert.Equal(2, tries);
    }

    // ---- link (6.6) ----

    internal const string PeersJson = """
        {"Self":{"DNSName":"desk.example.ts.net.","UserID":1},"User":{"1":{"LoginName":"owner@example.com"}},
         "Peer":{
          "nodekey:a":{"TailscaleIPs":["100.101.102.103","fd7a:115c:a1e0::1"],"CurAddr":"192.168.1.20:41641","Relay":"fra","Active":true},
          "nodekey:b":{"TailscaleIPs":["100.64.0.2"],"CurAddr":"","Relay":"par","Active":true},
          "nodekey:c":{"TailscaleIPs":["100.64.0.3"],"CurAddr":"","Relay":"xyz","Active":true},
          "nodekey:d":{"TailscaleIPs":["100.64.0.4"],"CurAddr":"","Relay":"fra","Active":false},
          "nodekey:e":{"TailscaleIPs":["100.64.0.5"],"CurAddr":"203.0.113.9:41641","Relay":"fra","Active":false},
          "nodekey:f":{"TailscaleIPs":["100.64.0.6"],"CurAddr":"172.20.10.2:1234","Active":true},
          "nodekey:g":{"TailscaleIPs":["100.64.0.7"],"CurAddr":"[fe80::1]:41641","Active":true},
          "nodekey:h":{"TailscaleIPs":["100.64.0.8"],"CurAddr":"10.0.0.8:41641","Active":true},
          "nodekey:i":{"TailscaleIPs":["100.64.0.9"],"CurAddr":"172.16.4.4:41641","Active":true},
          "nodekey:j":{"TailscaleIPs":["100.64.0.10"],"CurAddr":"172.32.4.4:41641","Active":true},
          "nodekey:k":{"TailscaleIPs":"not a list","CurAddr":"10.0.0.1:1","Active":true}
         }}
        """;

    [Theory]
    [InlineData("100.101.102.103", "Direct · same Wi-Fi")]
    [InlineData("fd7a:115c:a1e0::1", "Direct · same Wi-Fi")]
    [InlineData("FD7A:115C:A1E0:0:0:0:0:1", "Direct · same Wi-Fi")]   // the same address, written another way
    [InlineData("100.64.0.2", "Through a relay in Paris")]
    [InlineData("100.64.0.3", "Through a relay server")]
    [InlineData("100.64.0.4", null)]                                   // idle: no live path yet
    [InlineData("100.64.0.5", "Direct · over the internet")]           // a direct address, even while idle
    [InlineData("100.64.0.6", "Direct · iPhone hotspot")]
    [InlineData("100.64.0.7", "Direct · same Wi-Fi")]
    [InlineData("100.64.0.8", "Direct · same Wi-Fi")]
    [InlineData("100.64.0.9", "Direct · same Wi-Fi")]
    [InlineData("100.64.0.10", "Direct · over the internet")]
    [InlineData("100.64.0.99", null)]                                  // not a peer
    public void ThePeerAtTheAddressGivesTheMacsWords(string ip, string? expected)
    {
        using var doc = JsonDocument.Parse(PeersJson);
        var peers = Tailnet.Peers(doc.RootElement);
        var address = Tailnet.ForwardedAddress(ip);
        Assert.NotNull(address);
        Assert.Equal(expected, peers.TryGetValue(address!, out var peer) ? Tailnet.LinkText(peer) : null);
    }

    [Fact]
    public void ThePeersOfANonsenseStatusAreNone()
    {
        foreach (var json in new[] { "{}", "[]", "{\"Peer\":[]}", "{\"Peer\":{\"a\":5}}", "{\"Peer\":{\"a\":{\"TailscaleIPs\":[5,\"nope\"]}}}" })
        {
            using var doc = JsonDocument.Parse(json);
            Assert.Empty(Tailnet.Peers(doc.RootElement));
        }
    }

    [Fact]
    public void OnlyTheAddressServeAddedCounts()
    {
        Assert.Equal("100.64.0.2", Tailnet.ForwardedAddress("100.64.0.2"));
        // A device can send its own X-Forwarded-For; Serve adds the real address after it.
        Assert.Equal("100.64.0.2", Tailnet.ForwardedAddress("1.2.3.4, 100.64.0.2"));
        Assert.Equal("1.2.3.4", Tailnet.ForwardedAddress("100.64.0.2,1.2.3.4"));
        Assert.Equal("fd7a:115c:a1e0::1", Tailnet.ForwardedAddress(" fd7a:115c:a1e0::1 "));
        foreach (var bad in new[] { null, "", "  ", "nonsense", "100.64.0.2, " })
            Assert.Null(Tailnet.ForwardedAddress(bad));
    }

    [Fact]
    public async Task TheLinkIsReadInTheBackgroundAtMostEveryTenSeconds()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        try
        {
            int runs = 0;
            var tailnet = new Tailnet(new Settings(folder), args =>
            {
                if (args != "status --json") return null;
                Interlocked.Increment(ref runs);
                return PeersJson;
            });
            Assert.Equal(TimeSpan.FromSeconds(10), Tailnet.PeersEvery);
            string? link = null;
            for (int i = 0; i < 200 && link == null; i++)
            {
                link = tailnet.Link("100.64.0.2");
                if (link == null) await Task.Delay(25);
            }
            Assert.Equal("Through a relay in Paris", link);
            for (int i = 0; i < 20; i++) Assert.Equal("Direct · same Wi-Fi", tailnet.Link("100.101.102.103"));
            Assert.Null(tailnet.Link(null));
            Assert.Null(tailnet.Link("100.64.0.99"));
            await Task.Delay(100);
            Assert.Equal(1, Volatile.Read(ref runs));
            // The peer reading never learns the name and owner (no settings written every 10 seconds).
            Assert.Null(tailnet.Host);
        }
        finally
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // ---- thermalState (6.1) ----

    [Theory]
    [InlineData(null, null, null, "unknown")]
    [InlineData(500.0, null, null, "unknown")]          // nonsense is no reading
    [InlineData(45.0, null, null, "nominal")]
    [InlineData(74.9, null, null, "nominal")]
    [InlineData(75.0, null, null, "fair")]
    [InlineData(84.9, null, null, "fair")]
    [InlineData(85.0, null, null, "serious")]
    [InlineData(90.0, null, null, "critical")]          // the limit at which Lid Awake turns off
    [InlineData(40.0, 91.0, null, "critical")]          // the hotter of the two sources counts
    [InlineData(null, 76.0, null, "fair")]
    [InlineData(null, 40.0, 100, "nominal")]
    [InlineData(null, null, 100, "nominal")]
    [InlineData(null, 40.0, 99, "serious")]             // Windows slows the processor down: throttling
    [InlineData(null, null, 51, "serious")]
    [InlineData(null, null, 50, "critical")]
    [InlineData(95.0, 40.0, 100, "critical")]
    public void ThermalStateFromTheZones(double? zone, double? hottest, int? passiveLimit, string expected)
    {
        var zones = hottest == null && passiveLimit == null ? null : new ThermalZoneReading(hottest, passiveLimit);
        Assert.Equal(expected, ThermalState.Name(zone, zones));
        if (zones == null) Assert.Equal(expected, ThermalState.Name(zone, ThermalZoneReading.None));
    }

    [Fact]
    public void TheZoneCountersGiveTheHottestSaneZoneAndTheLowestPassiveLimit()
    {
        var r = ThermalZoneReading.From(new (long?, long?)[]
        {
            (3032, 100),        // 30.05 °C, not slowed
            (3632, 80),         // 90.05 °C, held to 80%
            (0, 0),             // no sensor, no passive cooling
            (null, 150),        // missing, nonsense
            (65535, null),      // a sensor that isn't really there
        });
        Assert.Equal(90.05, r.Hottest!.Value, 2);
        Assert.Equal(80, r.PassiveLimit);
        Assert.Equal(ThermalZoneReading.None, ThermalZoneReading.From(Array.Empty<(long?, long?)>()));
    }

    // ---- minutesToFull and charged (6.4) ----

    const uint Online = BatteryCharge.OnLine, Charging = BatteryCharge.OnLine | BatteryCharge.Charging, Discharging = BatteryCharge.Discharging;

    static BatteryCharge Batteries(params BatteryCell[] cells) => new(cells);

    [Fact]
    public void MinutesToFullIsWhatIsMissingAtTheChargeRate()
    {
        // 25 Wh missing at 12.5 W: two hours.
        Assert.Equal(120, Batteries(new BatteryCell(Charging, 25_000, 12_500, 50_000)).MinutesToFull());
        // Rounded to the minute: 1 Wh at 7 W is 8.57 minutes.
        Assert.Equal(9, Batteries(new BatteryCell(Charging, 49_000, 7_000, 50_000)).MinutesToFull());
        // Two batteries: what both miss, at the rate of the one that charges (40 Wh at 10 W).
        Assert.Equal(240, Batteries(new BatteryCell(Charging, 10_000, 10_000, 40_000), new BatteryCell(Online, 20_000, 0, 30_000)).MinutesToFull());
        // Two that charge: their rates add up (30 Wh at 20 W).
        Assert.Equal(90, Batteries(new BatteryCell(Charging, 10_000, 15_000, 30_000), new BatteryCell(Charging, 20_000, 5_000, 30_000)).MinutesToFull());
        // Over its full charge capacity (a worn battery's estimate): nothing missing.
        Assert.Equal(0, Batteries(new BatteryCell(Charging, 50_500, 1_000, 50_000)).MinutesToFull());
    }

    [Fact]
    public void MinutesToFullIsUnknownWhenItCantBeWorkedOut()
    {
        foreach (var charge in new[]
        {
            Batteries(),                                                                    // no battery
            Batteries(new BatteryCell(Online, 25_000, 0, 50_000)),                          // not charging
            Batteries(new BatteryCell(Discharging, 25_000, -9_000, 50_000)),                // on battery
            Batteries(new BatteryCell(Charging, 25_000, BatteryCharge.UnknownRate, 50_000)),
            Batteries(new BatteryCell(Charging, 25_000, 0, 50_000)),
            Batteries(new BatteryCell(Charging, 25_000, -5, 50_000)),
            Batteries(new BatteryCell(Charging, BatteryCharge.UnknownCapacity, 9_000, 50_000)),
            Batteries(new BatteryCell(Charging, 25_000, 9_000, 0)),
            Batteries(new BatteryCell(Charging, 25_000, 9_000, BatteryCharge.UnknownCapacity)),
            Batteries(new BatteryCell(Charging, 50, 10, 100, Relative: true)),              // units of its own
            Batteries(new BatteryCell(Charging, 20_000, 10, 50_000)),                       // a trickle: 3000 hours
            Batteries(new BatteryCell(Charging, 10_000, 10_000, 40_000), new BatteryCell(Online, BatteryCharge.UnknownCapacity, 0, 30_000)),
        })
            Assert.Equal(-1, charge.MinutesToFull());
        Assert.Equal(-1, Batteries(new BatteryCell(Charging, 0, 1, 1_000_000)).MinutesToFull());  // 16,667 hours
        Assert.Equal(BatteryCharge.MaxMinutes, Batteries(new BatteryCell(Charging, 0, 1_000, 24_000)).MinutesToFull());
    }

    [Fact]
    public void ChargedIsOnThePowerLineIdleAndFull()
    {
        Assert.True(Batteries(new BatteryCell(Online, 49_000, 0, 50_000)).Charged());       // 98%
        Assert.True(Batteries(new BatteryCell(Online, 50_000, 0, 50_000), new BatteryCell(Online, 29_500, 0, 30_000)).Charged());
        Assert.False(Batteries(new BatteryCell(Online, 48_000, 0, 50_000)).Charged());      // 96%: holding a charge
        Assert.False(Batteries(new BatteryCell(Charging, 49_900, 500, 50_000)).Charged());
        Assert.False(Batteries(new BatteryCell(Discharging, 50_000, -5_000, 50_000)).Charged());
        Assert.False(Batteries(new BatteryCell(0, 50_000, 0, 50_000)).Charged());           // off the power line
        Assert.False(Batteries(new BatteryCell(Online, 50_000, 0, 50_000), new BatteryCell(Charging, 10_000, 9_000, 30_000)).Charged());
        // Can't be told: the old rule (plugged in at 100%) applies.
        Assert.Null(Batteries().Charged());
        Assert.Null(Batteries(new BatteryCell(Online, BatteryCharge.UnknownCapacity, 0, 50_000)).Charged());
        Assert.Null(Batteries(new BatteryCell(Online, 100, 0, 100, Relative: true)).Charged());
    }

    [Fact]
    public void StatusSendsTheEstimateAndChargedFromTheBatteries()
    {
        var fake = new SlowFakeReadings { ChargeNow = Batteries(new BatteryCell(Charging, 25_000, 12_500, 50_000)) };
        fake.Inner.PowerNow = new PowerReading(1, 9, 50, -1);       // plugged in, charging
        var s = Run(new StatusInfo(fake).Build(true));
        Assert.Equal(120, s.GetProperty("minutesToFull").GetInt32());
        Assert.False(s.GetProperty("charged").GetBoolean());
        StatusInfoTests.AssertEveryKeyWithItsKind(s);

        // Windows says it isn't charging: no estimate, whatever the drivers said a moment ago.
        fake.Inner.PowerNow = new PowerReading(1, 1, 50, -1);
        Assert.Equal(-1, Run(new StatusInfo(fake).Build(true)).GetProperty("minutesToFull").GetInt32());

        // Full by the drivers at 97%, and not full at 100% while one still charges.
        fake.ChargeNow = Batteries(new BatteryCell(Online, 49_500, 0, 50_000));
        fake.Inner.PowerNow = new PowerReading(1, 1, 97, -1);
        Assert.True(Run(new StatusInfo(fake).Build(true)).GetProperty("charged").GetBoolean());
        fake.ChargeNow = Batteries(new BatteryCell(Charging, 49_900, 300, 50_000));
        fake.Inner.PowerNow = new PowerReading(1, 1, 100, -1);
        Assert.False(Run(new StatusInfo(fake).Build(true)).GetProperty("charged").GetBoolean());

        // Not read yet, or nothing the drivers can tell: plugged in at 100%.
        foreach (var unknown in new BatteryCharge?[] { null, Batteries() })
        {
            fake.ChargeNow = unknown;
            var plain = Run(new StatusInfo(fake).Build(true));
            Assert.True(plain.GetProperty("charged").GetBoolean());
            Assert.Equal(-1, plain.GetProperty("minutesToFull").GetInt32());
        }
    }

    [Fact]
    public void StatusSendsTheThermalStateFromBothZoneSources()
    {
        var fake = new SlowFakeReadings();
        Assert.Equal("unknown", Run(new StatusInfo(fake).Build(true)).GetProperty("thermalState").GetString());
        fake.ZonesNow = ThermalZoneReading.None;
        Assert.Equal("unknown", Run(new StatusInfo(fake).Build(true)).GetProperty("thermalState").GetString());
        fake.ZonesNow = new ThermalZoneReading(52.0, 100);
        Assert.Equal("nominal", Run(new StatusInfo(fake).Build(true)).GetProperty("thermalState").GetString());
        fake.ZonesNow = new ThermalZoneReading(60.0, 70);
        Assert.Equal("serious", Run(new StatusInfo(fake).Build(true)).GetProperty("thermalState").GetString());
        // MSAcpi_ThermalZoneTemperature (the heat limit's reading) counts too.
        fake.ZonesNow = null;
        var heat = new HeatSensor(() => new TemperatureReading(null, 92.0));
        heat.Refresh(TimeSpan.Zero);
        Assert.Equal("critical", Run(new StatusInfo(fake, heat: heat).Build(true)).GetProperty("thermalState").GetString());
    }

    // ---- host and screenWidth (6.1, 6.2) ----

    [Fact]
    public void TheHostIsTheDnsNameElseTheNetBiosName()
    {
        Assert.Equal("Edens-Laptop", SystemReadings.PickHost("Edens-Laptop", "EDENS-LAPTOP"));
        Assert.Equal("Edens-Laptop", SystemReadings.PickHost(" Edens-Laptop ", "EDENS-LAPTOP"));
        foreach (var none in new[] { null, "", "   " })
            Assert.Equal("EDENS-LAPTOP", SystemReadings.PickHost(none, "EDENS-LAPTOP"));
    }

    [Theory]
    [InlineData(1920, 96u, 1920)]       // 100%
    [InlineData(2880, 192u, 1440)]      // 200%
    [InlineData(2560, 144u, 1707)]      // 150%, rounded
    [InlineData(3000, 120u, 2400)]      // 125%
    [InlineData(1920, 0u, 1920)]        // unknown: unchanged
    [InlineData(0, 144u, 0)]
    public void TheScreenWidthIsInPoints(int pixels, uint dpi, int points) =>
        Assert.Equal(points, SystemReadings.Points(pixels, dpi));

    // ---- The real calls on this machine (CI: no battery, probably no thermal zone) ----

    [Fact]
    public void TheRealReadingsNeverThrowAndNeverWait()
    {
        var readings = new SystemReadings();
        var host = readings.HostName();
        Assert.False(string.IsNullOrWhiteSpace(host));
        Assert.False(host.Contains('\0'), "a NUL in the name");  // ordinal: the string overload ignores NULs
        Assert.True(readings.ScreenWidth() >= 0);
        Assert.True(DisplayScale.PrimaryDpi() > 0);
        Assert.True(DisplayScale.PixelsPerPoint() > 0);
        Assert.True(new NativeInput().PixelsPerPoint() > 0);
        // The slow ones answer from memory and only start a read.
        var clock = Stopwatch.StartNew();
        readings.Zones();
        readings.Charge();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(1), $"took {clock.Elapsed}");
    }

    [Fact]
    public void TheRealBatteriesAndZoneCountersNeverThrow()
    {
        var charge = BatteryCharge.Read();
        if (charge != null)
        {
            Assert.True(charge.MinutesToFull() >= -1);
            charge.Charged();
        }
        var clock = Stopwatch.StartNew();
        var zones = ThermalZoneReading.Read();
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(15), $"took {clock.Elapsed}");
        Assert.True(zones?.Hottest == null || TemperatureMath.IsSane(zones.Hottest.Value), $"zone {zones?.Hottest}");
        Assert.True(zones?.PassiveLimit == null || zones.PassiveLimit is >= 1 and <= 100);
        Console.WriteLine($"RUNNER: batteries {(charge == null ? "unreadable" : charge.Cells.Count.ToString())}, " +
                          $"zone counters {(zones == null ? "unreadable" : $"{zones.Hottest?.ToString() ?? "none"} °C, passive {zones.PassiveLimit?.ToString() ?? "none"}")}, " +
                          $"thermalState {ThermalState.Name(null, zones)}, DPI {DisplayScale.PrimaryDpi()}");
    }

    [Fact]
    public void TheBatteryStructsHaveTheSdkLayout()
    {
        Assert.Equal(36, Marshal.SizeOf<BatteryTemperature.BatteryInformation>());
        Assert.Equal(0, (int)Marshal.OffsetOf<BatteryTemperature.BatteryInformation>(nameof(BatteryTemperature.BatteryInformation.Capabilities)));
        Assert.Equal(4, (int)Marshal.OffsetOf<BatteryTemperature.BatteryInformation>(nameof(BatteryTemperature.BatteryInformation.Technology)));
        Assert.Equal(8, (int)Marshal.OffsetOf<BatteryTemperature.BatteryInformation>(nameof(BatteryTemperature.BatteryInformation.Chemistry)));
        Assert.Equal(12, (int)Marshal.OffsetOf<BatteryTemperature.BatteryInformation>(nameof(BatteryTemperature.BatteryInformation.DesignedCapacity)));
        Assert.Equal(16, (int)Marshal.OffsetOf<BatteryTemperature.BatteryInformation>(nameof(BatteryTemperature.BatteryInformation.FullChargedCapacity)));
        Assert.Equal(32, (int)Marshal.OffsetOf<BatteryTemperature.BatteryInformation>(nameof(BatteryTemperature.BatteryInformation.CycleCount)));
        Assert.Equal(20, Marshal.SizeOf<BatteryTemperature.BatteryWaitStatus>());
        Assert.Equal(16, Marshal.SizeOf<BatteryTemperature.BatteryStatus>());
        Assert.Equal(4, (int)Marshal.OffsetOf<BatteryTemperature.BatteryStatus>(nameof(BatteryTemperature.BatteryStatus.Capacity)));
        Assert.Equal(12, (int)Marshal.OffsetOf<BatteryTemperature.BatteryStatus>(nameof(BatteryTemperature.BatteryStatus.Rate)));
        static uint CtlCode(uint function) => (0x29u << 16) | (1u << 14) | (function << 2) | 0u;
        Assert.Equal(BatteryTemperature.IoctlBatteryQueryStatus, CtlCode(0x13));
        Assert.Equal(0x29404Cu, BatteryTemperature.IoctlBatteryQueryStatus);
        Assert.Equal(0, BatteryTemperature.LevelBatteryInformation);
        Assert.Equal(0x40000000u, BatteryTemperature.CapacityRelative);
        Assert.Equal(unchecked((int)0x80000000), BatteryCharge.UnknownRate);
    }

    // ---- The iPhone launch screens (section 3) ----

    [Fact]
    public void TheLinkTagsAreTheMacsOnePerSize()
    {
        Assert.Equal(11, Splash.Sizes.Length);
        var lines = Splash.LinkTags.Split('\n');
        Assert.Equal(Splash.Sizes.Length, lines.Length);
        Assert.Equal("<link rel=\"apple-touch-startup-image\" media=\"(device-width: 440px) and (device-height: 956px) and "
                     + "(-webkit-device-pixel-ratio: 3) and (orientation: portrait)\" href=\"/splash/1320x2868.png\">", lines[0]);
        Assert.Equal("<link rel=\"apple-touch-startup-image\" media=\"(device-width: 375px) and (device-height: 667px) and "
                     + "(-webkit-device-pixel-ratio: 2) and (orientation: portrait)\" href=\"/splash/750x1334.png\">", lines[^1]);
        var hrefs = Regex.Matches(Splash.LinkTags, "href=\"([^\"]+)\"").Select(m => m.Groups[1].Value).ToArray();
        Assert.Equal(Splash.Sizes.Select(s => $"/splash/{s.W * s.Scale}x{s.H * s.Scale}.png"), hrefs);
        Assert.Equal(hrefs.Length, hrefs.Distinct().Count());
    }

    [Fact]
    public void EverySizeIsAClosedLaptopOnBlackAtItsExactPixelSize()
    {
        foreach (var (w, h, scale) in Splash.Sizes)
        {
            int width = w * scale, height = h * scale;
            var png = Splash.Png($"/splash/{width}x{height}.png");
            Assert.NotNull(png);
            Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png!.Take(8).ToArray());
            // Kept after the first request.
            Assert.Same(png, Splash.Png($"/splash/{width}x{height}.png"));
            using var image = new Bitmap(new MemoryStream(png));
            Assert.Equal(width, image.Width);
            Assert.Equal(height, image.Height);
            // Where the page's opening animation starts: min(58% of the width, 300 pt) wide, centred.
            double macW = Math.Min(width * 0.58, 300.0 * scale), macH = macW * 170 / 220, baseH = macH * 0.07;
            double baseTop = height / 2.0 + macH / 2 - baseH, lidH = Math.Max(2, baseH * 0.25);
            AssertGray(image.GetPixel(0, 0), 0, 0);
            AssertGray(image.GetPixel(width / 2, height / 4), 0, 0);
            AssertGray(image.GetPixel(width / 2, (int)(baseTop + baseH / 2)), 130, 215);      // the light base
            AssertGray(image.GetPixel(width / 2, (int)(baseTop - lidH / 2)), 35, 90);         // the dark lid on it
            AssertGray(image.GetPixel(width / 2, (int)(baseTop - lidH - 3)), 0, 0);           // black above it
            AssertGray(image.GetPixel((int)((width - macW) / 2) - 3, (int)(baseTop + baseH / 2)), 0, 0);
        }
    }

    static void AssertGray(Color c, int lowest, int highest)
    {
        Assert.Equal(255, c.A);
        Assert.True(c.R == c.G && c.G == c.B, $"not grey: {c}");
        Assert.InRange(c.R, lowest, highest);
    }

    [Fact]
    public void OnlyTheListedSizesHaveALaunchScreen()
    {
        foreach (var path in new[]
        {
            "/splash/1x1.png", "/splash/1320x2868", "/splash/1320x2868.jpg", "/splash/+1320x2868.png", "/splash/1320x2868x1.png",
            "/splash/.png", "/splash/x.png", "/splash/2868x1320.png", "/splash/01320x2868.png.png", "/sky/1320x2868.png",
        })
            Assert.Null(Splash.Png(path));
    }
}

// /status over HTTP: the link of the phone that asks, and never a wait for Tailscale.
[Collection("WebServer")]
public sealed class StatusLinkTests : IDisposable
{
    const string TailHost = "pc.example.ts.net";
    const string Owner = "owner@example.com";
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Settings settings;
    readonly Auth auth;
    readonly ManualResetEventSlim tailscale = new(false);  // set: `status --json` answers
    int runs;
    readonly WebServer server;
    readonly HttpClient http = new(new HttpClientHandler { UseCookies = false }) { Timeout = TimeSpan.FromSeconds(30) };
    readonly int port;
    readonly string cookie;

    public StatusLinkTests()
    {
        Directory.CreateDirectory(Path.Combine(folder, "web"));
        File.WriteAllText(Path.Combine(folder, "web", "index.html"), "<script>a()</script>");
        settings = new Settings(Path.Combine(folder, "settings"));
        settings.Set("tailnetHost", TailHost);
        settings.Set("tailnetOwner", Owner);
        auth = new Auth(settings, iterations: 1000);
        auth.SetPassword("correct horse battery");
        cookie = $"{Auth.CookieName}={auth.Login("correct horse battery", "iPhone").Token}";
        var tailnet = new Tailnet(settings, args =>
        {
            if (args != "status --json") return null;
            Interlocked.Increment(ref runs);
            // Tailscale that takes its time (it is started, or the PC is busy).
            tailscale.Wait(TimeSpan.FromSeconds(30));
            return StatusDetailsTests.PeersJson;
        });
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        server = new WebServer(auth, tailnet, () => true, () => new StatusInfo(new FakeReadings()).Build(true), () => { }, (_, _) => { }, port, folder);
        server.Start();
    }

    public void Dispose()
    {
        tailscale.Set();
        http.Dispose();
        server.Dispose();
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    async Task<JsonElement> Status(string? forwardedFor)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/status");
        req.Headers.Host = TailHost;
        req.Headers.TryAddWithoutValidation("Tailscale-User-Login", Owner);
        req.Headers.TryAddWithoutValidation("Cookie", cookie);
        if (forwardedFor != null) req.Headers.TryAddWithoutValidation("X-Forwarded-For", forwardedFor);
        using var response = await http.SendAsync(req);
        var body = Encoding.UTF8.GetString(await response.Content.ReadAsByteArrayAsync());
        Assert.Equal(200, (int)response.StatusCode);
        using var doc = JsonDocument.Parse(body);
        return doc.RootElement.Clone();
    }

    [Fact]
    public async Task StatusNeverWaitsForTailscaleAndThenSaysHowThePhoneConnects()
    {
        // Tailscale doesn't answer: /status answers at once, without a link.
        var clock = Stopwatch.StartNew();
        var first = await Status("100.64.0.2");
        Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        Assert.False(first.TryGetProperty("link", out _));
        StatusInfoTests.AssertEveryKeyWithItsKind(first);
        for (int i = 0; i < 3; i++)
        {
            clock.Restart();
            Assert.False((await Status("100.64.0.2")).TryGetProperty("link", out _));
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(5), $"took {clock.Elapsed}");
        }
        for (int i = 0; i < 100 && Volatile.Read(ref runs) == 0; i++) await Task.Delay(20);
        Assert.Equal(1, Volatile.Read(ref runs));  // one read at a time, still waiting for Tailscale

        // Tailscale answers: the next status says how this phone connects.
        tailscale.Set();
        string? link = null;
        for (int i = 0; i < 200 && link == null; i++)
        {
            var s = await Status("100.64.0.2");
            link = s.TryGetProperty("link", out var l) ? l.GetString() : null;
            if (link == null) await Task.Delay(25);
        }
        Assert.Equal("Through a relay in Paris", link);
        var direct = await Status("100.101.102.103");
        Assert.Equal("Direct · same Wi-Fi", direct.GetProperty("link").GetString());
        StatusInfoTests.AssertEveryKeyWithItsKind(direct);
        // No address, or one Tailscale doesn't list: no link.
        Assert.False((await Status(null)).TryGetProperty("link", out _));
        Assert.False((await Status("100.64.0.99")).TryGetProperty("link", out _));
        Assert.False((await Status("100.64.0.4")).TryGetProperty("link", out _));   // idle
    }
}
