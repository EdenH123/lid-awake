using System.Diagnostics;
using System.Text.Json;
using LidAwake;
using Xunit;

// Run alone, after the parallel tests: the first performance-counter query (PDH) loads its
// providers, and powercfg starts a process; neither should slow the other tests' threads.
[CollectionDefinition("Real machine", DisableParallelization = true)]
public sealed class RealMachineCollection { }

// The memory window, the process table and the battery report on this very PC (the CI runner):
// the real Windows calls, with numbers that must make sense rather than exact ones.
[Collection("Real machine")]
public sealed class RealMachineTests
{
    const long Second = 10_000_000;  // 100-nanosecond units

    [Fact]
    public void TheSnapshotHasThisVeryProcess()
    {
        var snap = new ProcessTable().Snapshot();
        Assert.NotNull(snap);
        var self = Assert.Single(snap!.Processes, p => p.Pid == Environment.ProcessId);
        Assert.Equal(Environment.ProcessPath, self.Path, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.GetFileName(Environment.ProcessPath), self.Name, StringComparer.OrdinalIgnoreCase);
        Assert.True(self.PrivateBytes > 0);
        Assert.True(self.WorkingSet >= self.PrivateBytes);
        Assert.True(self.CpuTime > 0);
        using var me = Process.GetCurrentProcess();
        Assert.Equal(me.StartTime.ToUniversalTime().ToFileTimeUtc() / Second, self.CreateTime / Second);
        // The idle and system processes are there too, with no exe; and no one twice.
        Assert.Contains(snap.Processes, p => p.Pid == 0 && p.Path == null);
        Assert.Contains(snap.Processes, p => p.Pid == 4 && p.Path == null && p.Name == "System");
        Assert.Equal(snap.Processes.Count, snap.Processes.Select(p => (p.Pid, p.CreateTime)).Distinct().Count());
        Assert.True(snap.Processes.Count > 20);
        Console.WriteLine($"RUNNER: {snap.Processes.Count} processes, {snap.Processes.Count(p => p.Path != null)} with an exe path");
    }

    // The processor time keeps growing while this process works.
    [Fact]
    public void TheProcessorTimeGrows()
    {
        long first = new ProcessTable().Snapshot()!.Processes.Single(p => p.Pid == Environment.ProcessId).CpuTime;
        var sw = Stopwatch.StartNew();
        double x = 0;
        while (sw.ElapsedMilliseconds < 300) x += Math.Sqrt(sw.ElapsedTicks);
        Assert.True(x > 0);
        long later = new ProcessTable().Snapshot()!.Processes.Single(p => p.Pid == Environment.ProcessId).CpuTime;
        Assert.True(later > first);
    }

    // Each process's exe is looked up once in its life; a snapshot is reused for a second.
    [Fact]
    public void PathsAreLookedUpOncePerProcessAndSnapshotsReused()
    {
        long now = 0;
        var asked = new List<int>();
        var table = new ProcessTable(pid => { lock (asked) asked.Add(pid); return null; }, () => now);
        var a = table.Snapshot();
        Assert.Same(a, table.Snapshot());
        int firstRound = asked.Count;
        Assert.True(firstRound > 0);
        Assert.DoesNotContain(0, asked);
        Assert.DoesNotContain(4, asked);
        now = ProcessTable.ReuseMilliseconds;
        var b = table.Snapshot();
        Assert.NotSame(a, b);
        // Only processes that started since were looked up again.
        Assert.True(asked.Count - firstRound < firstRound);
    }

    [Fact]
    public void ThePageFilesUseIsReadOrLeftOut()
    {
        var used = ProcessTable.PageFileInUse();
        Console.WriteLine($"RUNNER: page files in use {(used is { } u ? (u >> 20) + " MB" : "unknown")}");
        if (used is { } bytes) Assert.True(bytes < 1UL << 50);
    }

    [Fact]
    public void TheRealMemoryAnswerMakesSense()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new MemoryInfo(new SystemReadings(), new ProcessTable()).Snapshot()));
        var m = doc.RootElement;
        ulong total = m.GetProperty("total").GetUInt64(), used = m.GetProperty("used").GetUInt64();
        Assert.True(total > 0);
        Assert.InRange(used, 1UL, total);
        Assert.InRange(m.GetProperty("free").GetUInt64() + m.GetProperty("cached").GetUInt64(), 1UL, total);
        Assert.Contains(m.GetProperty("level").GetString(), new[] { "normal", "warn", "critical" });
        Assert.InRange(m.GetProperty("pressure").GetInt32(), 0, 100);
        var ids = m.GetProperty("top").EnumerateArray().Select(t => t.GetProperty("id").GetString()).ToList();
        Assert.InRange(ids.Count, 1, 5);
        Assert.Equal(ids.Count, ids.Distinct().Count());
        Console.WriteLine($"RUNNER: memory {used >> 20} of {total >> 20} MB used, free {m.GetProperty("free").GetUInt64() >> 20} MB, "
                          + $"cached {m.GetProperty("cached").GetUInt64() >> 20} MB, swap {m.GetProperty("swapUsed").GetUInt64() >> 20} MB, "
                          + $"level {m.GetProperty("level").GetString()}, top {string.Join(", ", m.GetProperty("top").EnumerateArray().Select(t => t.GetProperty("name").GetString()))}");
    }

    // The CI runner has no battery, so the sampler never runs powercfg there; here it is called
    // directly and must not throw or hang.
    [Fact]
    public void TheRealBatteryReportDoesNotThrow()
    {
        var xml = new PowercfgReport().ReadXml();
        var points = xml == null ? new List<BatteryPoint>() : BatteryReport.Parse(xml);
        Console.WriteLine($"RUNNER: battery report {(xml == null ? "not made" : xml.Length + " characters")}, {points.Count} recent levels");
    }
}
