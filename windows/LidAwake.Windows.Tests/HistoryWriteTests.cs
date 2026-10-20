using System.Text.Json;
using LidAwake;
using Xunit;

// How history.json is written: at most every 5 minutes (rows wait in memory, where the routes
// read them), on Flush and when the sampler stops, one write at a time; and what a file may hold.
public sealed class HistoryWriteTests : IDisposable
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

    string FilePath => Path.Combine(folder, "history.json");

    int RowsOnDisk() => File.Exists(FilePath)
        ? JsonDocument.Parse(File.ReadAllText(FilePath)).RootElement.GetProperty("samples").GetArrayLength() : 0;

    static HistorySample Sample(long t) => new(t, 50, false, false, null, 10, 60);

    [Fact]
    public void TheFileIsWrittenAtMostEveryFiveMinutes()
    {
        var store = new HistoryStore(folder, () => now, 4);
        Assert.True(store.Add(Sample(now)));          // the first one is written at once
        Assert.Equal(1, RowsOnDisk());
        for (int i = 1; i < 5; i++)
        {
            now += 60;
            Assert.True(store.Add(Sample(now)));
        }
        // Four minutes later: still one row in the file, all five in memory (the routes).
        Assert.Equal(1, RowsOnDisk());
        Assert.Equal(5, store.Count);
        Assert.Equal(5, JsonDocument.Parse(JsonSerializer.Serialize(store.Battery(24))).RootElement.GetProperty("points").GetArrayLength());
        now += 60;                                    // five minutes after the last write
        Assert.True(store.Add(Sample(now)));
        Assert.Equal(6, RowsOnDisk());
        now += 60;
        store.Add(Sample(now));
        Assert.Equal(6, RowsOnDisk());
        Assert.True(store.Flush());
        Assert.Equal(7, RowsOnDisk());
        Assert.True(store.Flush());                   // nothing new: nothing to do
    }

    [Fact]
    public void StoppingTheSamplerWritesWhatIsLeft()
    {
        var store = new HistoryStore(folder, () => now, 4);
        var sampler = new HistorySampler(store, new FakeReadings { PowerNow = new PowerReading(0, 0, 60, -1) }, () => null);
        sampler.Sample();
        now += 60;
        sampler.Sample();
        Assert.Equal(1, RowsOnDisk());
        sampler.Dispose();
        Assert.Equal(2, RowsOnDisk());
        Assert.Equal(2, new HistoryStore(folder, () => now, 4).Count);
    }

    // Many samples and flushes at once: every write is whole and the last one has every row.
    [Fact]
    public void WritesAtTheSameTimeNeverMixUp()
    {
        var store = new HistoryStore(folder, () => Interlocked.Read(ref now), 4, TimeSpan.Zero);
        Parallel.For(0, 40, i =>
        {
            store.Add(Sample(T0 - 3000 + i * 60 + (i % 2)));
            store.Flush();
        });
        Assert.True(store.Flush());
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        var again = new HistoryStore(folder, () => now, 4);
        Assert.Equal(store.Count, again.Count);
        Assert.Equal(JsonSerializer.Serialize(store.Battery(48)), JsonSerializer.Serialize(again.Battery(48)));
    }

    [Fact]
    public void AFileOver16MegabytesIsNotRead()
    {
        Directory.CreateDirectory(folder);
        var row = $"[{T0 - 60},50,0,0,null,null,0],";
        using (var w = new StreamWriter(FilePath))
        {
            w.Write("{\"v\":1,\"samples\":[");
            for (long written = 0; written <= HistoryStore.MaxFileBytes; written += row.Length) w.Write(row);
            w.Write($"[{T0 - 30},50,0,0,null,null,0]]}}");
        }
        Assert.True(new FileInfo(FilePath).Length > HistoryStore.MaxFileBytes);
        Assert.Equal(0, new HistoryStore(folder, () => now, 4).Count);
    }

    // An exe in the file must be on a local drive: never a share or a device path, which /cpu-icon
    // would then read.
    [Theory]
    [InlineData(@"\\\\server\\share\\evil.exe")]
    [InlineData(@"\\\\.\\C:\\x.exe")]
    [InlineData(@"\\\\?\\C:\\x.exe")]
    [InlineData(@"\\\\?\\UNC\\server\\x.exe")]
    [InlineData(@"relative\\x.exe")]
    [InlineData(@"C:/Tools/x.exe")]
    [InlineData(@"C:\\Tools\\x.dll")]
    public void OnlyLocalExesAreReadFromTheFile(string jsonExe)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, $"{{\"v\":2,\"apps\":{{\"k\":[\"K\",\"app\",\"{jsonExe}\",null]}},"
                                    + $"\"samples\":[[{T0 - 60},50,0,0,null,50,60,{{\"k\":5}}]]}}");
        var store = new HistoryStore(folder, () => now, 4) { Icons = new FakeAppIcons() };
        Assert.Equal(new CpuApp("k", "K", "other"), store.App("k"));
        Assert.Null(store.IconPng("k"));
    }

    [Fact]
    public void ALocalExeIsKept()
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(FilePath, "{\"v\":2,\"apps\":{\"k\":[\"K\",\"app\",\"D:\\\\Tools\\\\x.exe\",null]},"
                                    + $"\"samples\":[[{T0 - 60},50,0,0,null,50,60,{{\"k\":5}}]]}}");
        var store = new HistoryStore(folder, () => now, 4) { Icons = new FakeAppIcons() };
        Assert.Equal(new CpuApp("k", "K", "app", @"D:\Tools\x.exe"), store.App("k"));
        Assert.NotNull(store.IconPng("k"));
    }

    // Two report entries a few seconds apart (a sleep right after a wake) are both kept: only a
    // measured row stops a backfilled one. Running it again still adds nothing.
    [Fact]
    public void ReportEntriesCloseTogetherAreBothKept()
    {
        var store = new HistoryStore(folder, () => now, 4);
        var points = new[]
        {
            new BatteryPoint(T0 - 5 * Hour, 60, false, false),
            new BatteryPoint(T0 - 5 * Hour + 20, 59, false, false),
            new BatteryPoint(T0 - 2 * Hour, 40, true, true),
        };
        store.Add(new HistorySample(T0 - 2 * Hour + 30, 41, true, true, null, 5, 60));
        Assert.Equal(2, store.Backfill(points));
        Assert.Equal(0, store.Backfill(points));
        var times = JsonDocument.Parse(JsonSerializer.Serialize(store.Battery(48))).RootElement.GetProperty("points")
                                .EnumerateArray().Select(p => p[0].GetInt64()).ToArray();
        Assert.Equal(new[] { T0 - 5 * Hour, T0 - 5 * Hour + 20, T0 - 2 * Hour + 30 }, times);
        // The backfill is written at once.
        Assert.Equal(3, RowsOnDisk());
    }
}
