using System.Text.Json;
using LidAwake;
using Xunit;

// The graphs' history (docs/API.md 5.7): the sampler, the 48-hour file, and the answers of the
// three routes. A real folder per test, fake readings and a clock the tests move by hand.
public sealed class HistoryTests : IDisposable
{
    const long T0 = 1_760_000_000;
    const long Hour = 3600;

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    long now = T0;

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    HistoryStore NewStore(int cores = 4) => new(folder, () => now, cores);

    static JsonElement Run(object answer)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(answer));
        return doc.RootElement.Clone();
    }

    static HistorySample Sample(long t, int? percent = 50, bool plugged = false, bool charging = false, double? temp = null, double? cpu = null, int span = 0)
        => new(t, percent, plugged, charging, temp, cpu, span);

    // ---- the sampler ----

    // Charging at 87% on AC, a battery temperature, and 25% processor use over the last minute.
    [Fact]
    public void SamplerRecordsBatteryTemperatureAndProcessorUse()
    {
        var store = NewStore();
        var fake = new FakeReadings { PowerNow = new PowerReading(1, 8, 87, -1), CpuNow = new CpuTimes(0, 0, 0), Ms = 1_000_000 };
        double? temp = 31.25;
        var sampler = new HistorySampler(store, fake, () => temp);

        sampler.Sample();  // the first one only starts the processor measurement
        now = T0 + 60;
        fake.Ms += 60_000;
        fake.CpuNow = new CpuTimes(750, 1000, 0);  // 1000 ticks, 750 idle: 25% busy
        sampler.Sample();

        var battery = Run(store.Battery(24)).GetProperty("points");
        Assert.Equal(2, battery.GetArrayLength());
        Assert.Equal("[" + T0 + ",87,1,1]", battery[0].GetRawText());
        var temps = Run(store.Temperature(24)).GetProperty("points");
        Assert.Equal("[" + (T0 + 60) + ",31.25]", temps[1].GetRawText());
        var cpu = Run(store.Cpu(24));
        // Only the second sample has a processor stretch: [start, end, {}, 25% x 4 cores x 60 s].
        var intervals = cpu.GetProperty("intervals");
        Assert.Equal(1, intervals.GetArrayLength());
        Assert.Equal(T0, intervals[0][0].GetInt64());
        Assert.Equal(T0 + 60, intervals[0][1].GetInt64());
        Assert.Equal(JsonValueKind.Object, intervals[0][2].ValueKind);
        Assert.Empty(intervals[0][2].EnumerateObject());
        Assert.Equal(60.0, intervals[0][3].GetDouble(), 2);
    }

    // The page turns the stretch back into the percent the sampler measured.
    [Fact]
    public void ProcessorSecondsGiveBackThePercentTheWayThePageReadsThem()
    {
        var store = NewStore(cores: 8);
        var fake = new FakeReadings { PowerNow = new PowerReading(0, 0, 60, -1), CpuNow = new CpuTimes(0, 0, 0) };
        var sampler = new HistorySampler(store, fake, () => null);
        sampler.Sample();
        now += 60;
        fake.Ms += 60_000;
        fake.CpuNow = new CpuTimes(2000, 2500, 500);  // 3000 ticks, 2000 idle: 33.3% busy
        sampler.Sample();

        var cpu = Run(store.Cpu(24));
        var row = cpu.GetProperty("intervals")[0];
        double covered = row[1].GetInt64() - row[0].GetInt64();
        double pct = row[3].GetDouble() / (covered * cpu.GetProperty("cores").GetInt32()) * 100;  // index.html: pct()
        Assert.Equal(33.3, pct, 0);
    }

    [Fact]
    public void PcWithoutBatteryHasNoBatteryPointsButStillHasProcessorUse()
    {
        var store = NewStore();
        var fake = new FakeReadings { PowerNow = new PowerReading(1, 128, 255, -1), CpuNow = new CpuTimes(0, 0, 0) };
        var sampler = new HistorySampler(store, fake, () => null);
        sampler.Sample();
        now += 60;
        fake.Ms += 60_000;
        fake.CpuNow = new CpuTimes(500, 1000, 0);
        sampler.Sample();

        Assert.Equal(0, Run(store.Battery(24)).GetProperty("points").GetArrayLength());
        Assert.Equal(0, Run(store.Temperature(24)).GetProperty("points").GetArrayLength());
        Assert.Equal(1, Run(store.Cpu(24)).GetProperty("intervals").GetArrayLength());
    }

    [Fact]
    public void FailingReadingsLeaveOnlyTheirPartOut()
    {
        var store = NewStore();
        var fake = new FakeReadings { PowerNow = null, CpuNow = null };
        var sampler = new HistorySampler(store, fake, () => throw new InvalidOperationException("no sensor"));
        sampler.Sample();
        Assert.Equal(1, store.Count);
        Assert.Equal(0, Run(store.Battery(24)).GetProperty("points").GetArrayLength());
        Assert.Equal(0, Run(store.Temperature(24)).GetProperty("points").GetArrayLength());
        Assert.Equal(0, Run(store.Cpu(24)).GetProperty("intervals").GetArrayLength());
    }

    [Fact]
    public void OnlyTheBatteryTemperatureIsInTheGraphAndOnlyBetween0And100()
    {
        var store = NewStore();
        foreach (var (t, c) in new (long, double?)[] { (T0, 0.0), (T0 + 60, 100.0), (T0 + 120, -3.0), (T0 + 180, 36.123), (T0 + 240, null) })
            store.Add(Sample(t, temp: c));
        var points = Run(store.Temperature(24)).GetProperty("points");
        Assert.Equal(1, points.GetArrayLength());
        Assert.Equal(36.12, points[0][1].GetDouble(), 2);
    }

    // The PC slept (or the app was stuck): the processor use across the gap was never measured,
    // so that stretch is left out, and measuring starts again.
    [Fact]
    public void ALongGapDoesNotInventProcessorUse()
    {
        var store = NewStore();
        var fake = new FakeReadings { PowerNow = new PowerReading(0, 0, 70, -1), CpuNow = new CpuTimes(0, 0, 0) };
        var sampler = new HistorySampler(store, fake, () => null);
        sampler.Sample();
        now += 60; fake.Ms += 60_000; fake.CpuNow = new CpuTimes(500, 1000, 0);
        sampler.Sample();
        now += 8 * Hour; fake.Ms += 60_000; fake.CpuNow = new CpuTimes(1000, 2000, 0);  // slept: little CPU time passed
        sampler.Sample();
        Assert.Equal(1, Run(store.Cpu(48)).GetProperty("intervals").GetArrayLength());
        now += 60; fake.Ms += 60_000; fake.CpuNow = new CpuTimes(1500, 3000, 0);
        sampler.Sample();
        var intervals = Run(store.Cpu(48)).GetProperty("intervals");
        Assert.Equal(2, intervals.GetArrayLength());
        Assert.Equal(60, intervals[1][1].GetInt64() - intervals[1][0].GetInt64());
        // The battery graph still has the points either side of the gap (the page draws it as a sleep).
        Assert.Equal(4, Run(store.Battery(48)).GetProperty("points").GetArrayLength());
    }

    [Fact]
    public async Task ASampleWhileAnotherRunsIsDropped()
    {
        var store = NewStore();
        using var inside = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var sampler = new HistorySampler(store, new FakeReadings(), () => { inside.Set(); release.Wait(5000); return null; });
        var first = Task.Run(sampler.Sample);
        Assert.True(inside.Wait(5000));
        sampler.Sample();  // returns at once: one is already running
        release.Set();
        await first;
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void StartTakesAFirstSampleAtOnceAndDisposeStopsIt()
    {
        var store = NewStore();
        using (var sampler = new HistorySampler(store, new FakeReadings { PowerNow = new PowerReading(0, 0, 40, -1) }, () => null))
        {
            sampler.Start();
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (store.Count == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);
            Assert.Equal(1, store.Count);
        }
    }

    // ---- the file ----

    [Fact]
    public void SamplesComeBackFromTheFile()
    {
        var store = NewStore();
        store.Add(Sample(T0 - 120, 80, plugged: true, charging: true, temp: 30.5, cpu: 12.5, span: 60));
        store.Add(Sample(T0 - 60, null));
        store.Add(Sample(T0, 79, temp: 31));

        var again = NewStore();
        Assert.Equal(3, again.Count);
        Assert.Equal(Run(store.Battery(24)).GetRawText(), Run(again.Battery(24)).GetRawText());
        Assert.Equal(Run(store.Temperature(24)).GetRawText(), Run(again.Temperature(24)).GetRawText());
        Assert.Equal(Run(store.Cpu(24)).GetRawText(), Run(again.Cpu(24)).GetRawText());
    }

    [Fact]
    public void TheFileUsesCompactRows()
    {
        var store = NewStore();
        store.Add(Sample(T0, 87, plugged: true, charging: false, temp: 31.25, cpu: 4.2, span: 60));
        var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "history.json"))).RootElement;
        Assert.Equal(2, doc.GetProperty("v").GetInt32());
        // A row with no programs listed keeps the 7 numbers of version 1.
        Assert.Equal("[" + T0 + ",87,1,0,31.25,4.2,60]", doc.GetProperty("samples")[0].GetRawText());
        Assert.Equal("{}", doc.GetProperty("apps").GetRawText());
        Assert.DoesNotContain("\n", File.ReadAllText(Path.Combine(folder, "history.json")));
    }

    [Fact]
    public void TrimsEverythingOlderThan48HoursOnWrite()
    {
        var store = NewStore();
        store.Add(Sample(T0));
        store.Add(Sample(T0 + 2 * Hour));
        // 47 h 59 min after the first: all three are kept.
        store.Add(Sample(T0 + 48 * Hour - 60));
        Assert.Equal(3, store.Count);
        Assert.Equal(3, NewStoreAt(T0 + 48 * Hour - 60).Count);
        // 48 h 01 min after the first: it goes, the next one stays.
        now = T0 + 48 * Hour + 60;
        store.Add(Sample(now));
        Assert.Equal(3, store.Count);
        var onDisk = JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "history.json"))).RootElement.GetProperty("samples");
        Assert.Equal(3, onDisk.GetArrayLength());
        Assert.Equal(T0 + 2 * Hour, onDisk[0][0].GetInt64());
    }

    HistoryStore NewStoreAt(long at) => new(folder, () => at, 4);

    [Fact]
    public void RowsOlderThan48HoursInTheFileAreDroppedWhenItIsRead()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "history.json"),
            $"{{\"v\":1,\"samples\":[[{T0 - 49 * Hour},50,0,0,null,null,0],[{T0 - Hour},51,0,0,null,null,0]]}}");
        var store = NewStore();
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void AClockSetBackDropsTheRowsFromTheFuture()
    {
        var store = NewStore();
        store.Add(Sample(T0 + 100));
        store.Add(Sample(T0 + 200));
        store.Add(Sample(T0 + 150));
        var points = Run(store.Battery(48)).GetProperty("points");
        Assert.Equal(2, points.GetArrayLength());
        Assert.Equal(T0 + 100, points[0][0].GetInt64());
        Assert.Equal(T0 + 150, points[1][0].GetInt64());
    }

    [Fact]
    public void WritesAreAtomicAndLeaveNoTempFile()
    {
        var store = NewStore();
        for (int i = 0; i < 5; i++) store.Add(Sample(T0 + i * 60));
        Assert.True(File.Exists(Path.Combine(folder, "history.json")));
        Assert.False(File.Exists(Path.Combine(folder, "history.json.tmp")));
        Assert.Equal(5, JsonDocument.Parse(File.ReadAllText(Path.Combine(folder, "history.json"))).RootElement.GetProperty("samples").GetArrayLength());
    }

    // A write that can't finish (here: the temp name is taken by a folder) must leave the file as
    // it was, and the sample is still served from memory.
    [Fact]
    public void AFailedWriteKeepsTheOldFileAndTheSampleInMemory()
    {
        var store = NewStore();
        Assert.True(store.Add(Sample(T0)));
        var path = Path.Combine(folder, "history.json");
        var before = File.ReadAllText(path);
        Directory.CreateDirectory(path + ".tmp");

        Assert.False(store.Add(Sample(T0 + 60)));
        Assert.Equal(before, File.ReadAllText(path));
        Assert.Equal(2, Run(store.Battery(24)).GetProperty("points").GetArrayLength());

        // Once the obstacle is gone, the next sample saves everything.
        Directory.Delete(path + ".tmp");
        Assert.True(store.Add(Sample(T0 + 120)));
        Assert.Equal(3, NewStore().Count);
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("")]
    [InlineData("[1,2,3]")]
    [InlineData("{\"v\":1}")]
    [InlineData("{\"v\":1,\"samples\":5}")]
    [InlineData("{\"v\":1,\"samples\":[[1,2")]
    [InlineData("\0\0\0\0")]
    public void ACorruptFileStartsEmptyAndIsReplacedByTheNextWrite(string content)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "history.json");
        File.WriteAllText(path, content);

        var store = NewStore();
        Assert.Equal(0, store.Count);
        Assert.Equal(0, Run(store.Battery(24)).GetProperty("points").GetArrayLength());
        Assert.Equal(0, Run(store.Cpu(24)).GetProperty("intervals").GetArrayLength());

        Assert.True(store.Add(Sample(T0)));
        Assert.Equal(1, NewStore().Count);
    }

    [Fact]
    public void ABadRowIsSkippedAndTheGoodOnesStay()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "history.json"),
            $"{{\"v\":1,\"samples\":[[{T0 - 120},50,0,0,null,null,0],\"x\",[1,2],[{T0 - 90},150,0,0,null,null,0],[{T0 - 80},50,2,0,null,null,0],[{T0 - 60},51,1,0,30,null,0]]}}");
        var store = NewStore();
        Assert.Equal(2, store.Count);
    }

    [Fact]
    public void ANewStoreWithNoFolderYetCreatesIt()
    {
        var inner = Path.Combine(folder, "deep", "LidAwake");
        var store = new HistoryStore(inner, () => now, 4);
        Assert.True(store.Add(Sample(T0)));
        Assert.True(File.Exists(Path.Combine(inner, "history.json")));
    }

    // ---- the hours clamp ----

    [Theory]
    [InlineData(null, 24.0)]
    [InlineData("", 24.0)]
    [InlineData("abc", 24.0)]
    [InlineData("NaN", 24.0)]
    [InlineData("1,5", 24.0)]
    [InlineData("6", 6.0)]
    [InlineData("24", 24.0)]
    [InlineData("1", 1.0)]
    [InlineData("48", 48.0)]
    [InlineData("0", 1.0)]
    [InlineData("0.2", 1.0)]
    [InlineData("-5", 1.0)]
    [InlineData("49", 48.0)]
    [InlineData("100", 48.0)]
    [InlineData("1e9", 48.0)]
    [InlineData("2.5", 2.5)]
    public void HoursAreClampedTo1To48AndDefaultTo24(string? query, double expected)
    {
        Assert.Equal(expected, HistoryStore.ClampHours(query));
    }

    [Fact]
    public void TheClampDecidesHowFarBackTheAnswerGoes()
    {
        var store = NewStore();
        foreach (var ago in new[] { 47 * Hour, 30 * Hour, 10 * Hour, 90 * 60L, 1800L })
            store.Add(Sample(T0 - ago));
        int Count(double hours) => Run(store.Battery(hours)).GetProperty("points").GetArrayLength();
        Assert.Equal(1, Count(1));
        Assert.Equal(1, Count(0));
        Assert.Equal(1, Count(-4));
        Assert.Equal(2, Count(2));
        Assert.Equal(3, Count(24));
        Assert.Equal(4, Count(40));
        Assert.Equal(5, Count(48));
        Assert.Equal(5, Count(5000));
        Assert.Equal(T0, Run(store.Temperature(1)).GetProperty("now").GetInt64());
    }

    // ---- the shapes (docs/API.md 5.7) ----

    // The three examples of the API document, filled from samples with the same numbers.
    [Fact]
    public void AnswersHaveTheShapeOfTheApiExamples()
    {
        now = 1_760_003_600;
        var store = NewStore(cores: 8);
        store.Add(Sample(1_760_000_000, 87, plugged: true, charging: false, temp: 31.25, cpu: 12.5, span: 60));

        // {"points": [[1760000000, 31.25], ...], "now": 1760003600}
        Assert.Equal("{\"points\":[[1760000000,31.25]],\"now\":1760003600}", JsonSerializer.Serialize(store.Temperature(24)));
        // {"points": [[1760000000, 87, 1, 0], ...], "now": 1760003600}
        Assert.Equal("{\"points\":[[1760000000,87,1,0]],\"now\":1760003600}", JsonSerializer.Serialize(store.Battery(24)));
        // {"now": 1760003600, "cores": 8, "apps": [...], "intervals": [[start, end, {"0": 12.5}, 4.2]]}:
        // here no app is listed, so the stretch is [start, end, {}, other].
        // 12.5% of 8 cores for 60 s = 60 processor-seconds.
        Assert.Equal("{\"now\":1760003600,\"cores\":8,\"apps\":[],\"intervals\":[[1759999940,1760000000,{},60]]}",
            JsonSerializer.Serialize(store.Cpu(24)));
    }

    [Fact]
    public void EmptyHistoryAnswersTheSameShapesWithEmptyLists()
    {
        var store = NewStore(cores: 2);
        Assert.Equal("{\"points\":[],\"now\":" + T0 + "}", JsonSerializer.Serialize(store.Temperature(24)));
        Assert.Equal("{\"points\":[],\"now\":" + T0 + "}", JsonSerializer.Serialize(store.Battery(24)));
        Assert.Equal("{\"now\":" + T0 + ",\"cores\":2,\"apps\":[],\"intervals\":[]}", JsonSerializer.Serialize(store.Cpu(24)));
    }

    [Fact]
    public void ProcessorAnswerHasWhatThePageNeeds()
    {
        var store = NewStore(cores: 4);
        store.Add(Sample(T0 - 120, cpu: 10, span: 60));
        store.Add(Sample(T0 - 60, cpu: 0, span: 60));
        var cpu = Run(store.Cpu(24));
        // The page needs both arrays (index.html: parse), and cores of at least 1.
        Assert.Equal(JsonValueKind.Array, cpu.GetProperty("apps").ValueKind);
        Assert.Equal(JsonValueKind.Array, cpu.GetProperty("intervals").ValueKind);
        Assert.True(cpu.GetProperty("cores").GetInt32() >= 1);
        foreach (var row in cpu.GetProperty("intervals").EnumerateArray())
        {
            Assert.Equal(4, row.GetArrayLength());
            Assert.True(row[1].GetInt64() > row[0].GetInt64());
            Assert.Equal(JsonValueKind.Object, row[2].ValueKind);
            Assert.True(row[3].GetDouble() >= 0);
        }
        Assert.Equal(2, cpu.GetProperty("intervals").GetArrayLength());
    }
}
