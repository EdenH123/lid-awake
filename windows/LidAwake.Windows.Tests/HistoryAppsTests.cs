using System.Globalization;
using System.Text.Json;
using LidAwake;
using Xunit;

public sealed class FakeBatteryReport : IBatteryReport
{
    public string? Xml;
    public int Calls;
    public string? ReadXml() { Interlocked.Increment(ref Calls); return Xml; }
}

// Per-program processor history (docs/API.md 5.7) and the battery backfill: the new row format,
// old files, the icons route's rule, and the battery report's parser and gap filling.
public sealed class HistoryAppsTests : IDisposable
{
    const long T0 = 1_760_000_000;
    const long Hour = 3600;
    const long Second = 10_000_000;
    const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    const string Code = @"C:\Program Files\Microsoft VS Code\Code.exe";

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly FakeAppIcons icons = new();
    long now = T0;

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    HistoryStore NewStore(int cores = 4) => new(folder, () => now, cores, TimeSpan.Zero) { Icons = icons };

    static JsonElement Run(object answer)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(answer));
        return doc.RootElement.Clone();
    }

    static readonly CpuApp ChromeApp = new(PhoneApps.Id(Chrome), "Google Chrome", "app", Chrome, "#4285f4");
    static readonly CpuApp CodeApp = new(PhoneApps.Id(Code), "Visual Studio Code", "app", Code);
    static readonly CpuApp WindowsApp = new("other.windows", "Windows (other)", "other");

    static HistorySample Row(long t, double cpu, int span, Dictionary<string, double>? apps = null)
        => new(t, 50, false, false, null, cpu, span, apps);

    // ---- the file ----

    // A version 1 file (7 numbers a row, no "apps") still loads: the processor graph has its
    // stretches with no program listed.
    [Fact]
    public void OldRowsStillLoad()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "history.json"),
            $"{{\"v\":1,\"samples\":[[{T0 - 120},50,0,0,null,25,60],[{T0 - 60},51,1,0,30.5,50,60]]}}");
        var store = NewStore();
        Assert.Equal(2, store.Count);
        Assert.Equal($"{{\"now\":{T0},\"cores\":4,\"apps\":[],\"intervals\":[[{T0 - 180},{T0 - 120},{{}},60],[{T0 - 120},{T0 - 60},{{}},120]]}}",
                     JsonSerializer.Serialize(store.Cpu(24)));
        Assert.Equal(2, Run(store.Battery(24)).GetProperty("points").GetArrayLength());
        // The next write turns it into version 2, the old rows as they were.
        store.Add(Row(T0, 10, 60));
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "history.json"))).RootElement;
        Assert.Equal(2, doc.GetProperty("v").GetInt32());
        Assert.Equal($"[{T0 - 120},50,0,0,null,25,60]", doc.GetProperty("samples")[0].GetRawText());
    }

    [Fact]
    public void RowsWithProgramsAreSavedAndReadBack()
    {
        var store = NewStore();
        store.Add(Row(T0 - 60, 50, 60, new() { [ChromeApp.Key] = 30, [CodeApp.Key] = 6, ["other.windows"] = 1 }),
                  new[] { ChromeApp, CodeApp, WindowsApp });
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "history.json"))).RootElement;
        Assert.Equal($"[{T0 - 60},50,0,0,null,50,60,{{\"{ChromeApp.Key}\":30,\"{CodeApp.Key}\":6,\"other.windows\":1}}]",
                     doc.GetProperty("samples")[0].GetRawText());
        Assert.Equal($"[\"Google Chrome\",\"app\",{JsonSerializer.Serialize(Chrome)},\"#4285f4\"]",
                     doc.GetProperty("apps").GetProperty(ChromeApp.Key).GetRawText());
        Assert.Equal("[\"Windows (other)\",\"other\",null,null]", doc.GetProperty("apps").GetProperty("other.windows").GetRawText());

        var again = NewStore();
        Assert.Equal(JsonSerializer.Serialize(store.Cpu(24)), JsonSerializer.Serialize(again.Cpu(24)));
        Assert.Equal(ChromeApp, again.App(ChromeApp.Key));
    }

    // {"apps": [{key, name, kind, color?}], "intervals": [[start, end, {"index": seconds}, other]]}.
    [Fact]
    public void TheProcessorAnswerListsThePrograms()
    {
        var store = NewStore(cores: 4);
        store.Add(Row(T0 - 120, 50, 60, new() { [ChromeApp.Key] = 30, ["other.windows"] = 1 }), new[] { ChromeApp, WindowsApp });
        store.Add(Row(T0 - 60, 25, 60, new() { [CodeApp.Key] = 6, [ChromeApp.Key] = 2.5 }), new[] { CodeApp, ChromeApp });
        var cpu = Run(store.Cpu(24));
        Assert.Equal($"[{{\"key\":\"{ChromeApp.Key}\",\"name\":\"Google Chrome\",\"kind\":\"app\",\"color\":\"#4285f4\"}},"
                     + "{\"key\":\"other.windows\",\"name\":\"Windows (other)\",\"kind\":\"other\"},"
                     + $"{{\"key\":\"{CodeApp.Key}\",\"name\":\"Visual Studio Code\",\"kind\":\"app\"}}]",
                     cpu.GetProperty("apps").GetRawText());
        // 50% of 4 cores for 60 s = 120 s, of which Chrome 30 and Windows 1: other 89.
        // 25% = 60 s: Code 6, Chrome 2.5, other 51.5.
        Assert.Equal($"[[{T0 - 180},{T0 - 120},{{\"0\":30,\"1\":1}},89],[{T0 - 120},{T0 - 60},{{\"2\":6,\"0\":2.5}},51.5]]",
                     cpu.GetProperty("intervals").GetRawText());
        Assert.DoesNotContain("macos", cpu.GetProperty("apps").GetRawText());
    }

    // The programs never add up to more than the stretch, and a key with no name is "other".
    [Fact]
    public void ProgramsAreScaledToTheStretchAndUnnamedKeysAreOther()
    {
        var store = NewStore(cores: 1);
        // 10% of 1 core for 60 s = 6 s, but the programs say 12 s: halved.
        store.Add(Row(T0 - 60, 10, 60, new() { [ChromeApp.Key] = 8, [CodeApp.Key] = 4, ["nobody"] = 3 }), new[] { ChromeApp, CodeApp });
        var row = Run(store.Cpu(24)).GetProperty("intervals")[0];
        Assert.Equal("{\"0\":4,\"1\":2}", row[2].GetRawText());
        Assert.Equal(0, row[3].GetDouble());
        Assert.Equal(2, Run(store.Cpu(24)).GetProperty("apps").GetArrayLength());
    }

    // A bad program entry only leaves that program out; a bad row shape is skipped; a damaged
    // programs table only loses its names.
    [Fact]
    public void BadProgramEntriesAreSkipped()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "history.json"),
            "{\"v\":2,\"apps\":{\"a1\":[\"A\",\"app\",\"C:\\\\a.exe\",\"#123456\"],\"bad\":[1,2],\"m\":[\"M\",\"macos\",null,null],"
            + "\"p\":[\"P\",\"app\",\"C:\\\\p.txt\",\"red\"]},"
            + $"\"samples\":[[{T0 - 180},50,0,0,null,50,60,{{\"a1\":5,\"x\":-1,\"y\":\"z\"}}],"
            + $"[{T0 - 120},50,0,0,null,50,60,5],"
            + $"[{T0 - 90},50,0,0,null,50,60,null,7,8],"
            + $"[{T0 - 60},50,0,0,null,50,60,{{\"p\":1}},0]]}}");
        var store = NewStore();
        Assert.Equal(2, store.Count);
        Assert.Equal(new CpuApp("a1", "A", "app", @"C:\a.exe", "#123456"), store.App("a1"));
        Assert.Null(store.App("bad"));
        Assert.Null(store.App("m"));                                   // "macos" is never a Windows kind
        Assert.Equal(new CpuApp("p", "P", "other"), store.App("p"));   // not an exe: no icon, so "other"
        var cpu = Run(store.Cpu(24));
        Assert.Equal("{\"a1\":5}".Replace("a1", "0"), cpu.GetProperty("intervals")[0][2].GetRawText());
    }

    // ---- the sampler ----

    [Fact]
    public void TheSamplerRecordsWhoUsedTheProcessor()
    {
        var store = NewStore(cores: 4);
        var fake = new FakeReadings { PowerNow = new PowerReading(1, 0, 80, -1), CpuNow = new CpuTimes(0, 0, 0) };
        var table = new FakeProcessTable();
        var sampler = new HistorySampler(store, fake, () => null, processes: table)
        {
            WindowsFolder = @"C:\Windows",
            NameOf = exe => Path.GetFileNameWithoutExtension(exe),
        };
        long ft = 133_000_000_000_000_000;
        table.Next = new ProcessSnapshot(new[]
        {
            new ProcessEntry(0, 0, "Idle", null, 0, 0, 500 * Second),
            new ProcessEntry(10, 1, "chrome.exe", Chrome, 1, 1, 10 * Second),
            new ProcessEntry(20, 1, "Code.exe", Code, 1, 1, 1 * Second),
            new ProcessEntry(30, 1, "svchost.exe", @"C:\Windows\System32\svchost.exe", 1, 1, 0),
            new ProcessEntry(40, 1, "tiny.exe", @"C:\Tools\tiny.exe", 1, 1, 0),
        }, ft);
        sampler.Sample();  // starts both measurements

        now += 60;
        fake.Ms += 60_000;
        fake.CpuNow = new CpuTimes(500, 1000, 0);  // 50% busy: 4 cores x 60 s x 0.5 = 120 s
        table.Next = new ProcessSnapshot(new[]
        {
            new ProcessEntry(0, 0, "Idle", null, 0, 0, 600 * Second),
            new ProcessEntry(10, 1, "chrome.exe", Chrome, 1, 1, 40 * Second),
            new ProcessEntry(20, 1, "Code.exe", Code, 1, 1, 7 * Second),
            new ProcessEntry(30, 1, "svchost.exe", @"C:\Windows\System32\svchost.exe", 1, 1, 1 * Second),
            new ProcessEntry(40, 1, "tiny.exe", @"C:\Tools\tiny.exe", 1, 1, Second / 10),
        }, ft + 60 * Second);
        sampler.Sample();

        var cpu = Run(store.Cpu(24));
        var apps = cpu.GetProperty("apps").EnumerateArray().Select(a => (a.GetProperty("key").GetString(), a.GetProperty("name").GetString(), a.GetProperty("kind").GetString())).ToList();
        Assert.Equal(new (string?, string?, string?)[]
        {
            (PhoneApps.Id(Chrome), "chrome", "app"), (PhoneApps.Id(Code), "Code", "app"), ("other.windows", "Windows (other)", "other"),
        }, apps);
        var row = cpu.GetProperty("intervals")[0];
        // tiny.exe's 0.1 s is under 0.2 s and 0.5% of 37.1 s: folded into other (120 - 37 = 83).
        Assert.Equal("{\"0\":30,\"1\":6,\"2\":1}", row[2].GetRawText());
        Assert.Equal(83, row[3].GetDouble(), 2);
        Assert.Equal(1, table.Calls - 1);
    }

    // After a sleep (a gap over 150 s) the processor measurement starts again, programs too.
    [Fact]
    public void AfterAGapProgramsStartAgain()
    {
        var store = NewStore();
        var fake = new FakeReadings { CpuNow = new CpuTimes(0, 0, 0) };
        var table = new FakeProcessTable { Next = new ProcessSnapshot(new[] { new ProcessEntry(10, 1, "chrome.exe", Chrome, 1, 1, 0) }, 1) };
        var sampler = new HistorySampler(store, fake, () => null, processes: table) { NameOf = exe => "Chrome" };
        sampler.Sample();
        now += 600;
        fake.Ms += 600_000;
        fake.CpuNow = new CpuTimes(500, 1000, 0);
        table.Next = new ProcessSnapshot(new[] { new ProcessEntry(10, 1, "chrome.exe", Chrome, 1, 1, 300 * Second) }, 2);
        sampler.Sample();
        Assert.Equal(0, Run(store.Cpu(24)).GetProperty("intervals").GetArrayLength());
    }

    // ---- /cpu-icon ----

    [Fact]
    public void TheIconIsOnlyForProgramsTheHistoryNames()
    {
        var store = NewStore();
        store.Add(Row(T0 - 60, 50, 60, new() { [ChromeApp.Key] = 30, ["other.windows"] = 1 }), new[] { ChromeApp, WindowsApp });
        Assert.Equal(FakeAppIcons.Image, store.IconPng(ChromeApp.Key));
        Assert.Equal((Chrome, HistoryStore.IconSize), Assert.Single(icons.Asked));
        // Kept: asked once.
        Assert.Equal(FakeAppIcons.Image, store.IconPng(ChromeApp.Key));
        Assert.Single(icons.Asked);
        // Never for a key it doesn't name, a system entry, a path, or nothing.
        Assert.Null(store.IconPng(PhoneApps.Id(Code)));
        Assert.Null(store.IconPng("other.windows"));
        Assert.Null(store.IconPng(Chrome));
        Assert.Null(store.IconPng(@"C:\Windows\System32\notepad.exe"));
        Assert.Null(store.IconPng(""));
        Assert.Null(store.IconPng(new string('a', 500)));
        Assert.Single(icons.Asked);
    }

    // A program whose rows are older than 48 hours is forgotten, with its icon.
    [Fact]
    public void AProgramNoRowNamesIsForgotten()
    {
        var store = NewStore();
        store.Add(Row(T0, 50, 60, new() { [ChromeApp.Key] = 30 }), new[] { ChromeApp });
        now = T0 + 49 * Hour;
        store.Add(Row(now, 50, 60));
        Assert.Null(store.App(ChromeApp.Key));
        Assert.Null(store.IconPng(ChromeApp.Key));
    }

    // ---- the battery backfill ----

    static string Iso(long t) => DateTimeOffset.FromUnixTimeSeconds(t).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
    static string Local(long t) => DateTimeOffset.FromUnixTimeSeconds(t).ToLocalTime().DateTime.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);

    // The shape of powercfg /batteryreport /xml (namespace, other sections with capacities too).
    static string Report() => $"""
        <?xml version="1.0" encoding="utf-8"?>
        <BatteryReport xmlns="http://schemas.microsoft.com/battery/2012">
          <ReportInformation><ReportVersion>1</ReportVersion><LocalScanTime>{Local(T0)}</LocalScanTime></ReportInformation>
          <Batteries><Battery><Id>DELL 1234</Id><DesignCapacity>50000</DesignCapacity><FullChargeCapacity>45000</FullChargeCapacity></Battery></Batteries>
          <RecentUsage>
            <UsageEntry Timestamp="{Iso(T0 - 60 * Hour)}" LocalTimestamp="{Local(T0 - 60 * Hour)}" EntryType="Active" Ac="0" ChargeCapacity="22500" Discharge="0" FullChargeCapacity="45000" IsNextOnBattery="0" />
            <UsageEntry Timestamp="{Iso(T0 - 30 * Hour)}" LocalTimestamp="{Local(T0 - 30 * Hour)}" EntryType="Suspend" Ac="0" ChargeCapacity="40500" Discharge="0" FullChargeCapacity="45000" IsNextOnBattery="1" />
            <UsageEntry Timestamp="{Iso(T0 - 20 * Hour)}" LocalTimestamp="{Local(T0 - 20 * Hour)}" EntryType="Active" Ac="1" ChargeCapacity="9000" Discharge="0" FullChargeCapacity="45000" IsNextOnBattery="0" />
            <UsageEntry LocalTimestamp="{Local(T0 - 10 * Hour)}" EntryType="Active" Ac="1" ChargeCapacity="45000" Discharge="0" FullChargeCapacity="45000" IsNextOnBattery="0" />
            <UsageEntry Timestamp="{Iso(T0 - 5 * Hour)}" EntryType="Active" Ac="1" ChargeCapacity="44000" Discharge="0" FullChargeCapacity="0" IsNextOnBattery="0" />
            <UsageEntry Timestamp="not a time" EntryType="Active" Ac="1" ChargeCapacity="1" FullChargeCapacity="2" />
            <UsageEntry Timestamp="{Iso(T0 + Hour)}" EntryType="Active" Ac="1" ChargeCapacity="36000" Discharge="0" FullChargeCapacity="45000" IsNextOnBattery="0" />
          </RecentUsage>
          <History><HistoryEntry StartDate="2025-01-01" FullChargeCapacity="46000" DesignCapacity="50000" /></History>
        </BatteryReport>
        """;

    [Fact]
    public void TheBatteryReportIsParsed()
    {
        var points = BatteryReport.Parse(Report());
        Assert.Equal(new[]
        {
            new BatteryPoint(T0 - 60 * Hour, 50, false, false),
            new BatteryPoint(T0 - 30 * Hour, 90, false, false),
            new BatteryPoint(T0 - 20 * Hour, 20, true, true),    // on AC, and the next one has more charge
            new BatteryPoint(T0 - 10 * Hour, 100, true, false),  // only a local time
            new BatteryPoint(T0 + Hour, 80, true, false),
        }, points);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not xml")]
    [InlineData("<BatteryReport/>")]
    [InlineData("<BatteryReport><RecentUsage/></BatteryReport>")]
    [InlineData("<!DOCTYPE x [<!ENTITY e \"1\">]><BatteryReport><RecentUsage><UsageEntry Timestamp=\"2025-01-01T00:00:00Z\" Ac=\"&e;\" ChargeCapacity=\"1\" FullChargeCapacity=\"2\"/></RecentUsage></BatteryReport>")]
    public void AReportThatCantBeReadGivesNoLevels(string xml)
    {
        Assert.Empty(BatteryReport.Parse(xml));
    }

    // Only the gaps of the last 48 hours: never next to a row History has, never in the future.
    [Fact]
    public void TheBackfillOnlyFillsGaps()
    {
        var store = NewStore();
        store.Add(new HistorySample(T0 - 20 * Hour + 60, 21, true, true, null, 5, 60));
        store.Add(new HistorySample(T0 - 60, 99, true, false, null, 5, 60));
        int added = store.Backfill(BatteryReport.Parse(Report()));
        Assert.Equal(2, added);  // 30 h and 10 h ago; not 60 h (too old), 20 h (a row is there) or the future
        var points = Run(store.Battery(48)).GetProperty("points");
        Assert.Equal($"[[{T0 - 30 * Hour},90,0,0],[{T0 - 20 * Hour + 60},21,1,1],[{T0 - 10 * Hour},100,1,0],[{T0 - 60},99,1,0]]", points.GetRawText());
        // They have no processor or temperature numbers, and are marked in the file.
        Assert.Equal(2, Run(store.Cpu(48)).GetProperty("intervals").GetArrayLength());
        var rows = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "history.json"))).RootElement.GetProperty("samples");
        Assert.Equal($"[{T0 - 30 * Hour},90,0,0,null,null,0,null,1]", rows[0].GetRawText());
        Assert.Equal($"[{T0 - 20 * Hour + 60},21,1,1,null,5,60]", rows[1].GetRawText());
        // Read back with the mark; running it again adds nothing.
        var again = NewStore();
        Assert.Equal(4, again.Count);
        Assert.Equal(0, again.Backfill(BatteryReport.Parse(Report())));
        // A measured sample after them goes on as usual.
        now += 60;
        Assert.True(again.Add(new HistorySample(now, 98, true, false, null, 5, 60)));
        Assert.Equal(5, NewStore().Count);
    }

    // A PC with no battery (the CI runner) skips the report altogether.
    [Fact]
    public void APcWithoutABatterySkipsTheBackfill()
    {
        var store = NewStore();
        var report = new FakeBatteryReport { Xml = Report() };
        var noBattery = new FakeReadings { PowerNow = new PowerReading(1, 128, 255, -1) };
        Assert.Equal(0, new HistorySampler(store, noBattery, () => null, report: report).Backfill());
        Assert.Equal(0, new HistorySampler(store, new FakeReadings(), () => null, report: report).Backfill());
        Assert.Equal(0, report.Calls);

        var battery = new FakeReadings { PowerNow = new PowerReading(0, 0, 70, -1) };
        Assert.Equal(3, new HistorySampler(store, battery, () => null, report: report).Backfill());
        Assert.Equal(1, report.Calls);
        // A report that couldn't be made: nothing, no error.
        Assert.Equal(0, new HistorySampler(store, battery, () => null, report: new FakeBatteryReport()).Backfill());
        Assert.Equal(0, new HistorySampler(store, battery, () => null).Backfill());
    }

    // The sampler asks for the report at its first sample and then not again within a day.
    [Fact]
    public async Task TheSamplerBackfillsAtStartThenOnceADay()
    {
        var store = NewStore();
        var report = new FakeBatteryReport();
        var sampler = new HistorySampler(store, new FakeReadings { PowerNow = new PowerReading(0, 0, 70, -1) }, () => null, report: report);
        sampler.Sample();
        Assert.True(await TestThreads.Until(() => Volatile.Read(ref report.Calls) == 1));
        now += 3600;
        sampler.Sample();  // within a day: not asked (whether or not the first has finished)
        now += 23 * 3600;
        // A day later it is asked again, once the first backfill has finished in the background
        // (a sample while it still runs skips it, and the next sample tries again).
        Assert.True(await TestThreads.Until(() =>
        {
            sampler.Sample();
            return Volatile.Read(ref report.Calls) == 2;
        }));
        sampler.Sample();  // the same day again: not asked
        await Task.Delay(200);
        Assert.Equal(2, Volatile.Read(ref report.Calls));
    }
}
