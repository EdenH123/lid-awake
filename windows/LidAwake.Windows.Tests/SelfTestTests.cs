using System.Diagnostics;
using System.Text;
using LidAwake;
using Xunit;
using Xunit.Abstractions;

// `LidAwake.exe --selftest` and `--battery`: the checks with stand-ins for every reading, the exit
// code, that nothing is changed, and the battery report's lines.
public class SelfTestTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), "lidawake-selftest-" + Guid.NewGuid().ToString("N"));

    public SelfTestTests() => Directory.CreateDirectory(folder);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    const string Status = """
        {"BackendState":"Running","CertDomains":["pc.tail1234.ts.net"],
         "Self":{"DNSName":"PC.tail1234.ts.net.","UserID":7},"User":{"7":{"LoginName":"owner@example.com"}}}
        """;

    // A laptop where everything works.
    SelfTestProbes Good() => new()
    {
        TailscaleStatus = () => new Tailnet.CliResult(true, Status),
        Port = _ => new PortUse(true),
        SettingsFile = Path.Combine(folder, "settings.json"),
        MachineFile = Path.Combine(folder, "machine.json"),
        LidAction = () => (1, 1),
        HasLid = () => true,
        Power = () => new PowerReading(1, 8, 80, -1),
        Charge = () => new BatteryCharge(new[] { new BatteryCell(BatteryCharge.OnLine | BatteryCharge.Charging, 40_000, 20_000, 50_000) }),
        Temperatures = () => new TemperatureReading(31.24, null),
        Zones = () => new ThermalZoneReading(45, null),
        Memory = () => new MemoryReading(8UL << 30, 3UL << 30),
        Screen = () => ("Desktop Duplication", 1280, 800, null),
        Encoder = hardware => ("NVIDIA H.264 Encoder MFT", hardware, null),
        Cameras = () => new[] { "Integrated Camera" },
        Microphones = () => new[] { "Microphone Array" },
        ClaudeCode = () => ClaudeHook.SetUp,
    };

    static CheckResult Line(List<CheckResult> results, string name) => Assert.Single(results, r => r.Name == name);

    [Fact]
    public void WhenEverythingWorksEveryLineIsTickedAndTheExitCodeIsZero()
    {
        var results = SelfTest.Run(Good());
        Assert.All(results, r => Assert.Equal(CheckState.Pass, r.State));
        Assert.Equal(0, SelfTest.ExitCode(results));
        Assert.Equal("✓ Tailscale HTTPS name: pc.tail1234.ts.net", Line(results, "Tailscale HTTPS name").Line);
        Assert.Equal("✓ Phone page port 8787: free for Lid Awake (it isn't running)", Line(results, "Phone page port 8787").Line);
        Assert.Equal("✓ Live screen port 8788: free for Lid Awake (it isn't running)", Line(results, "Live screen port 8788").Line);
        Assert.Equal("✓ Lid setting (powercfg): closing the lid: sleep when plugged in, sleep on battery", Line(results, "Lid setting (powercfg)").Line);
        Assert.Equal("✓ Battery: Battery 80% · full in 0h 30m", Line(results, "Battery").Line);
        Assert.Equal("✓ Temperature: battery 31.2 °C, thermal zones 45.0 °C, state nominal", Line(results, "Temperature").Line);
        Assert.Equal("✓ Memory: 5.0 of 8.0 GB used", Line(results, "Memory").Line);
        Assert.Equal("✓ Screen capture: Desktop Duplication, 1280x800", Line(results, "Screen capture").Line);
        Assert.Equal("✓ H.264 encoder: NVIDIA H.264 Encoder MFT, the graphics card's", Line(results, "H.264 encoder").Line);
        Assert.Equal("✓ Camera: Integrated Camera", Line(results, "Camera").Line);
        Assert.Equal("✓ Settings: not made yet (Lid Awake makes it when it needs it)", Line(results, "Settings").Line);
        Assert.Equal("✓ Phone alerts (Web Push): no key yet (made when a phone first turns alerts on)", Line(results, "Phone alerts (Web Push)").Line);
        Assert.Equal("✓ Claude Code: found, alerts and usage set up", Line(results, "Claude Code").Line);
        var report = SelfTest.Report(results, new DateTimeOffset(2026, 10, 20, 15, 4, 5, TimeSpan.FromHours(2)));
        Assert.StartsWith("Lid Awake self-test · 2026-10-20 15:04:05 +02:00\n", report);
        Assert.Contains("\nAll required checks passed.\n", report);
    }

    [Fact]
    public void ARequiredFailureMakesTheExitCodeOneAndIsNamedAtTheEnd()
    {
        var p = Good();
        var results = SelfTest.Run(new SelfTestProbes
        {
            TailscaleStatus = () => new Tailnet.CliResult(true, """{"BackendState":"NeedsLogin","CertDomains":null}"""),
            Port = port => port == 8787 ? new PortUse(false, "python", 4242) : new PortUse(false, "LidAwake", 99),
            SettingsFile = p.SettingsFile, MachineFile = p.MachineFile, LidAction = p.LidAction, HasLid = p.HasLid, Power = p.Power,
            Charge = p.Charge, Temperatures = p.Temperatures, Zones = p.Zones, Memory = p.Memory, Screen = p.Screen, Encoder = p.Encoder,
            Cameras = p.Cameras, Microphones = p.Microphones, ClaudeCode = p.ClaudeCode,
        });
        Assert.Equal(1, SelfTest.ExitCode(results));
        Assert.Equal("✗ Tailscale signed in: no (Tailscale says NeedsLogin): open Tailscale and sign in", Line(results, "Tailscale signed in").Line);
        Assert.Equal("✗ Tailscale HTTPS name: unknown until Tailscale is signed in", Line(results, "Tailscale HTTPS name").Line);
        Assert.Equal("✗ Phone page port 8787: in use by another program (python); Lid Awake needs it", Line(results, "Phone page port 8787").Line);
        // Our own running app holding its port is fine.
        Assert.Equal("✓ Live screen port 8788: in use by Lid Awake (it's running)", Line(results, "Live screen port 8788").Line);
        var report = SelfTest.Report(results, DateTimeOffset.Now);
        Assert.Contains("\n3 required checks failed: Tailscale signed in, Tailscale HTTPS name, Phone page port 8787.", report);
    }

    [Fact]
    public void WhatThisPcDoesntHaveIsNeverAFailure()
    {
        var g = Good();
        var results = SelfTest.Run(new SelfTestProbes
        {
            TailscaleStatus = () => new Tailnet.CliResult(false, "", Missing: true),
            Port = g.Port, SettingsFile = g.SettingsFile, MachineFile = g.MachineFile,
            LidAction = () => null, HasLid = () => false,
            Power = () => new PowerReading(1, 128, 255, -1), Charge = () => new BatteryCharge(Array.Empty<BatteryCell>()),
            Temperatures = () => TemperatureReading.None, Zones = () => ThermalZoneReading.None,
            Memory = g.Memory, Screen = g.Screen, Encoder = g.Encoder,
            Cameras = () => Array.Empty<string>(), Microphones = () => Array.Empty<string>(),
            ClaudeCode = () => ClaudeHook.NoClaudeCode,
        });
        Assert.Equal(0, SelfTest.ExitCode(results));
        foreach (var name in new[] { "Tailscale installed", "Tailscale signed in", "Tailscale HTTPS name", "Lid setting (powercfg)", "Battery",
                                     "Temperature", "Camera", "Microphone", "Claude Code" })
        {
            var r = Line(results, name);
            Assert.Equal(CheckState.NotHere, r.State);
            Assert.StartsWith("– " + name + ": not on this PC", r.Line);
        }
        Assert.Equal("– Battery: not on this PC (no battery)", Line(results, "Battery").Line);
        Assert.Contains("\nAll required checks passed.\n", SelfTest.Report(results, DateTimeOffset.Now));
    }

    [Fact]
    public void AnOptionalFailureIsShownButKeepsTheExitCodeAtZero()
    {
        var g = Good();
        var results = SelfTest.Run(new SelfTestProbes
        {
            TailscaleStatus = g.TailscaleStatus, Port = g.Port, SettingsFile = g.SettingsFile, MachineFile = g.MachineFile,
            LidAction = g.LidAction, HasLid = g.HasLid, Power = g.Power, Charge = g.Charge, Temperatures = g.Temperatures, Zones = g.Zones,
            Memory = g.Memory, Screen = g.Screen,
            Encoder = _ => (null, false, "No H.264 encoder on this PC."),
            Cameras = () => throw new InvalidOperationException("boom"),
            Microphones = () => null, ClaudeCode = g.ClaudeCode,
        });
        Assert.Equal(0, SelfTest.ExitCode(results));
        Assert.Equal("✗ H.264 encoder: none works, so the phone gets still pictures instead of video: No H.264 encoder on this PC. (optional)",
            Line(results, "H.264 encoder").Line);
        Assert.Equal(CheckState.Fail, Line(results, "Camera").State);
        Assert.Equal(CheckState.Fail, Line(results, "Microphone").State);
        Assert.Contains("All required checks passed. 3 optional checks didn't pass.", SelfTest.Report(results, DateTimeOffset.Now));
    }

    [Fact]
    public void ARequiredCheckThatThrowsOrFindsNothingFails()
    {
        var g = Good();
        var results = SelfTest.Run(new SelfTestProbes
        {
            TailscaleStatus = () => new Tailnet.CliResult(false, "", TimedOut: true), Port = _ => new PortUse(false, Problem: "An attempt was made to access a socket in a way forbidden by its access permissions."),
            SettingsFile = g.SettingsFile, MachineFile = g.MachineFile,
            LidAction = () => null, HasLid = () => true, Power = g.Power, Charge = g.Charge, Temperatures = g.Temperatures, Zones = g.Zones,
            Memory = () => throw new InvalidOperationException("no"), Screen = () => (null, 0, 0, "Desktop Duplication unavailable: E_ACCESSDENIED"),
            Encoder = g.Encoder, Cameras = g.Cameras, Microphones = g.Microphones, ClaudeCode = g.ClaudeCode,
        });
        Assert.Equal(1, SelfTest.ExitCode(results));
        Assert.Equal("✓ Tailscale installed: yes", Line(results, "Tailscale installed").Line);
        Assert.Equal("✗ Tailscale signed in: it didn't answer in time", Line(results, "Tailscale signed in").Line);
        Assert.StartsWith("✗ Phone page port 8787: can't be used: An attempt was made", Line(results, "Phone page port 8787").Line);
        Assert.Equal("✗ Lid setting (powercfg): powercfg couldn't read what closing the lid does", Line(results, "Lid setting (powercfg)").Line);
        Assert.Equal(CheckState.Fail, Line(results, "Memory").State);
        Assert.Equal("✗ Screen capture: none works: Desktop Duplication unavailable: E_ACCESSDENIED", Line(results, "Screen capture").Line);
    }

    [Fact]
    public void ADamagedSettingsFileFailsAndIsLeftWhereItIs()
    {
        var g = Good();
        var damaged = Path.Combine(folder, "settings.json");
        File.WriteAllText(damaged, "{\"remoteScreen\": tru");
        var results = SelfTest.Run(g);
        Assert.Equal(1, SelfTest.ExitCode(results));
        Assert.Equal("✗ Settings: damaged: not valid JSON; Lid Awake sets it aside and starts afresh", Line(results, "Settings").Line);
        // Settings would have moved it aside as .bad; the self-test only reads.
        Assert.Equal("{\"remoteScreen\": tru", File.ReadAllText(damaged));
        Assert.False(File.Exists(damaged + ".bad"));
    }

    [Fact]
    public void TheSelfTestChangesNoSetting()
    {
        var g = Good();
        using (var key = System.Security.Cryptography.ECDsa.Create(System.Security.Cryptography.ECCurve.NamedCurves.nistP256))
        {
            var pushKey = Convert.ToBase64String(key.ExportPkcs8PrivateKey());
            File.WriteAllText(g.MachineFile, $$"""{"pushKey":"{{pushKey}}","pushSubscriptions":[{"Endpoint":"x"}],"savedLidAc":1,"savedLidDc":1,"tailnetHost":"old.ts.net"}""");
        }
        File.WriteAllText(g.SettingsFile, """{"remoteScreen":true,"screenEncoder":"software"}""");
        var stamp = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(g.MachineFile, stamp);
        File.SetLastWriteTimeUtc(g.SettingsFile, stamp);
        var before = Snapshot();
        bool? askedHardware = null;
        var results = SelfTest.Run(new SelfTestProbes
        {
            TailscaleStatus = g.TailscaleStatus, Port = g.Port, SettingsFile = g.SettingsFile, MachineFile = g.MachineFile,
            LidAction = g.LidAction, HasLid = g.HasLid, Power = g.Power, Charge = g.Charge, Temperatures = g.Temperatures, Zones = g.Zones,
            Memory = g.Memory, Screen = g.Screen, Encoder = hw => { askedHardware = hw; return ("H264 Encoder MFT", false, null); },
            Cameras = g.Cameras, Microphones = g.Microphones, ClaudeCode = g.ClaudeCode,
        });
        Assert.Equal(0, SelfTest.ExitCode(results));
        // What the files say is read: the encoder setting, the push key and phones, the saved lid values.
        Assert.False(askedHardware);
        Assert.Equal("✓ H.264 encoder: H264 Encoder MFT, Windows' own (software, as settings.json asks)", Line(results, "H.264 encoder").Line);
        Assert.Equal("✓ Phone alerts (Web Push): key present, 1 phone gets alerts", Line(results, "Phone alerts (Web Push)").Line);
        Assert.Contains("Lid Awake has changed it", Line(results, "Lid setting (powercfg)").Detail);
        Assert.Equal("✓ Settings: readable, 2 values", Line(results, "Settings").Line);
        // A Tailscale name other than the stored one is not saved (Tailnet.CheckSetup would).
        Assert.Equal(before, Snapshot());
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(g.MachineFile));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(g.SettingsFile));
    }

    // Every file in the folder with its bytes.
    string Snapshot() => string.Join("\n", Directory.GetFiles(folder).OrderBy(f => f, StringComparer.Ordinal)
        .Select(f => Path.GetFileName(f) + "=" + Convert.ToBase64String(File.ReadAllBytes(f))));

    [Fact]
    public void ADamagedPushKeyIsAnOptionalFailure()
    {
        var g = Good();
        File.WriteAllText(g.MachineFile, """{"pushKey":"bm90IGEga2V5"}""");
        var results = SelfTest.Run(g);
        Assert.Equal(0, SelfTest.ExitCode(results));
        Assert.Equal(CheckState.Fail, Line(results, "Phone alerts (Web Push)").State);
        Assert.False(Line(results, "Phone alerts (Web Push)").Required);
    }

    [Fact]
    public void TailscalesStatusIsReadWithoutSavingAnything()
    {
        var (state, name, backend) = Tailnet.ReadSetup(new Tailnet.CliResult(true, Status));
        Assert.Equal(new Tailnet.SetupState(true, true, true), state);
        Assert.Equal("pc.tail1234.ts.net", name);
        Assert.Equal("Running", backend);
        Assert.Equal(new Tailnet.SetupState(false, false, false), Tailnet.ReadSetup(new Tailnet.CliResult(false, "", Missing: true)).State);
        Assert.Equal(new Tailnet.SetupState(true, false, false), Tailnet.ReadSetup(new Tailnet.CliResult(true, "not json")).State);
        Assert.Null(Tailnet.ReadSetup(new Tailnet.CliResult(true, """{"Self":{"DNSName":"pc.example.com."}}""")).Name);
    }

    // ---- The battery check ----

    [Fact]
    public void TheBatteryReportGivesTheRawReadings()
    {
        var r = new BatteryCheck.Readings(
            new PowerReading(0, 0, 47, 5400),
            new BatteryCharge(new[] { new BatteryCell(BatteryCharge.Discharging, 24_000, -12_000, 51_000) }),
            BatterySensor: 33.25, AcpiZone: null, Zones: new ThermalZoneReading(52.0, 100));
        var text = BatteryCheck.Report(r, new DateTimeOffset(2026, 10, 20, 15, 4, 5, TimeSpan.Zero));
        Assert.Equal("""
            Lid Awake battery check · 2026-10-20 15:04:05 +00:00

            on AC: no (on battery) (ACLineStatus 0)
            charge: 47% (BatteryLifePercent 47)
            battery flag: none set (BatteryFlag 0x00)
            Windows' time left: 1h 30m (90 min)
            batteries: 1
            battery 1: discharging, capacity 24000 of 51000 mWh (47%), rate -12000 mW
            minutes to full: unknown
            minutes to empty: 2h 00m (120 min)
            charged: no
            temperature, battery sensor (IOCTL_BATTERY_QUERY_INFORMATION): 33.3 °C
            temperature, thermal zones (MSAcpi_ThermalZoneTemperature): none (most PCs need administrator rights for it)
            temperature, zone counters (ThermalZoneInformation): 52.0 °C, passive limit 100%
            thermalState: nominal
            safety stop with both limits on: none

            """.Replace("\r\n", "\n"), text);
    }

    [Fact]
    public void TheBatteryReportOnAPcWithoutABattery()
    {
        var r = new BatteryCheck.Readings(new PowerReading(1, 128, 255, -1), new BatteryCharge(Array.Empty<BatteryCell>()), null, null, null);
        var text = BatteryCheck.Report(r, DateTimeOffset.Now);
        Assert.Contains("\non AC: yes (ACLineStatus 1)\n", text);
        Assert.Contains("\ncharge: unknown (BatteryLifePercent 255)\n", text);
        Assert.Contains("\nbattery flag: no battery (BatteryFlag 0x80)\n", text);
        Assert.Contains("\nbatteries: 0\n", text);
        Assert.Contains("\nminutes to empty: unknown\n", text);
        Assert.Contains("\ncharged: can't tell\n", text);
        Assert.Contains("\nthermalState: unknown\n", text);
    }

    [Fact]
    public void ALowHotBatteryShowsTheSafetyStop()
    {
        var r = new BatteryCheck.Readings(new PowerReading(0, 2, 6, -1), null, 44.0, null, null);
        var text = BatteryCheck.Report(r, DateTimeOffset.Now);
        Assert.Contains("\nbattery flag: low (BatteryFlag 0x02)\n", text);
        Assert.Contains("\nbatteries: the list couldn't be read\n", text);
        Assert.Contains("\nsafety stop with both limits on: battery below 10%\n", text);
    }

    [Fact]
    public void MinutesToEmptyNeedsAKnownDischargeRate()
    {
        Assert.Equal(60, BatteryCheck.MinutesToEmpty(new BatteryCharge(new[]
        {
            new BatteryCell(BatteryCharge.Discharging, 10_000, -15_000, 50_000),
            new BatteryCell(0, 5_000, 0, 50_000),
        })));
        Assert.Equal(-1, BatteryCheck.MinutesToEmpty(new BatteryCharge(new[] { new BatteryCell(BatteryCharge.Charging | BatteryCharge.OnLine, 10_000, 5_000, 50_000) })));
        Assert.Equal(-1, BatteryCheck.MinutesToEmpty(new BatteryCharge(new[] { new BatteryCell(BatteryCharge.Discharging, 10_000, BatteryCharge.UnknownRate, 50_000) })));
        Assert.Equal(-1, BatteryCheck.MinutesToEmpty(new BatteryCharge(new[] { new BatteryCell(BatteryCharge.Discharging, 10_000, -100, 50_000, Relative: true) })));
    }

    // ---- The saved report ----

    [Fact]
    public void TheReportIsSavedWithWindowsLineEndsReplacingTheLastOne()
    {
        Assert.Equal(Path.Combine(folder, "LidAwake-selftest.txt"), Troubleshooting.Save(Troubleshooting.SelfTestFlag, "old\n", folder));
        var path = Troubleshooting.Save(Troubleshooting.SelfTestFlag, "✓ one\n✗ two\n", folder);
        Assert.Equal("✓ one\r\n✗ two\r\n", File.ReadAllText(path!, Encoding.UTF8));
        Assert.Equal(Path.Combine(folder, "LidAwake-battery.txt"), Troubleshooting.Save(Troubleshooting.BatteryFlag, "x", folder));
    }

    [Fact]
    public void OtherArgumentsAreNotTroubleshooting()
    {
        Assert.Null(Troubleshooting.Run(Array.Empty<string>()));
        Assert.Null(Troubleshooting.Run(new[] { "--claude-event" }));
        Assert.Null(Troubleshooting.Run(new[] { "selftest" }));
    }
}

// The real LidAwake.exe on this PC (the CI runner): --selftest and --battery end on their own, write
// their reports, print them, and change no setting. A required check the runner can't pass (no
// Tailscale, no battery, no lid) must say "not on this PC", so the self-test still exits 0.
[Collection("Real machine")]
public sealed class RealSelfTestTests
{
    readonly ITestOutputHelper output;

    public RealSelfTestTests(ITestOutputHelper output) => this.output = output;

    static string Exe => Path.Combine(AppContext.BaseDirectory, "LidAwake.exe");

    static bool ExeIsHere => File.Exists(Exe) && File.Exists(Path.Combine(AppContext.BaseDirectory, "LidAwake.runtimeconfig.json"));

    static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LidAwake");
    static string Machine => Path.Combine(Troubleshooting.Folder, Settings.MachineFileName);

    // The settings as they are on disk: whether the folder is there, and every file's bytes and time.
    static string SettingsState()
    {
        var sb = new StringBuilder();
        sb.Append(Directory.Exists(Roaming) ? "roaming folder\n" : "no roaming folder\n");
        if (Directory.Exists(Roaming))
            foreach (var f in Directory.GetFiles(Roaming).OrderBy(f => f, StringComparer.Ordinal))
                sb.Append(Path.GetFileName(f)).Append(' ').Append(File.GetLastWriteTimeUtc(f).Ticks).Append(' ')
                  .Append(Convert.ToBase64String(File.ReadAllBytes(f))).Append('\n');
        sb.Append(File.Exists(Machine) ? "machine " + File.GetLastWriteTimeUtc(Machine).Ticks + " " + Convert.ToBase64String(File.ReadAllBytes(Machine)) : "no machine.json");
        return sb.ToString();
    }

    (int Code, string Output) RunExe(string flag)
    {
        var info = new ProcessStartInfo(Exe, flag)
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
        };
        using var p = Process.Start(info)!;
        var text = p.StandardOutput.ReadToEndAsync();
        bool ended = p.WaitForExit(120_000);
        if (!ended)
        {
            try { p.Kill(true); }
            catch (Exception) { }
        }
        Assert.True(ended, $"LidAwake.exe {flag} did not end within 2 minutes");
        Assert.True(text.Wait(10_000));
        return (p.ExitCode, text.Result);
    }

    [Fact]
    public void TheRealSelfTestEndsWritesItsReportAndChangesNothing()
    {
        if (!ExeIsHere)
        {
            output.WriteLine("RUNNER: LidAwake.exe is not next to the tests; the real self-test is not run");
            return;
        }
        var report = Troubleshooting.ReportPath(Troubleshooting.SelfTestFlag);
        bool hadReport = File.Exists(report);
        var before = SettingsState();
        var started = DateTime.UtcNow.AddSeconds(-2);
        try
        {
            var (code, printed) = RunExe(Troubleshooting.SelfTestFlag);
            foreach (var line in printed.Split('\n')) output.WriteLine("SELFTEST: " + line.TrimEnd('\r'));
            output.WriteLine($"SELFTEST: exit code {code}");
            Assert.StartsWith(SelfTest.Title + " · ", printed);
            Assert.Contains("Screen capture: ", printed);
            Assert.Contains("Phone page port 8787: ", printed);
            Assert.Equal(0, code);
            Assert.Contains("\nAll required checks passed.", printed);
            // Saved too, as printed (with Windows line ends).
            Assert.True(File.Exists(report));
            Assert.True(File.GetLastWriteTimeUtc(report) >= started);
            Assert.Equal(printed.Replace("\r\n", "\n"), File.ReadAllText(report, Encoding.UTF8).Replace("\r\n", "\n"));
            Assert.Equal(before, SettingsState());
        }
        finally
        {
            if (!hadReport)
            {
                try { File.Delete(report); }
                catch (Exception) { }
            }
        }
    }

    [Fact]
    public void TheRealBatteryCheckEndsAndWritesItsReport()
    {
        if (!ExeIsHere)
        {
            output.WriteLine("RUNNER: LidAwake.exe is not next to the tests; the real battery check is not run");
            return;
        }
        var report = Troubleshooting.ReportPath(Troubleshooting.BatteryFlag);
        bool hadReport = File.Exists(report);
        var before = SettingsState();
        try
        {
            var (code, printed) = RunExe(Troubleshooting.BatteryFlag);
            foreach (var line in printed.Split('\n')) output.WriteLine("BATTERY: " + line.TrimEnd('\r'));
            Assert.Equal(0, code);
            Assert.StartsWith(BatteryCheck.Title + " · ", printed);
            Assert.Contains("\non AC: ", printed);
            Assert.Contains("\nsafety stop with both limits on: ", printed);
            Assert.True(File.Exists(report));
            Assert.Equal(before, SettingsState());
        }
        finally
        {
            if (!hadReport)
            {
                try { File.Delete(report); }
                catch (Exception) { }
            }
        }
    }
}
