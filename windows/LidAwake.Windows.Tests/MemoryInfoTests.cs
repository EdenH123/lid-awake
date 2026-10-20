using System.Text.Json;
using LidAwake;
using Xunit;

// A process table the tests fill in.
public sealed class FakeProcessTable : IProcessTable
{
    public ProcessSnapshot? Next;
    public int Calls;
    public ProcessSnapshot? Snapshot() { Calls++; return Next; }
}

public sealed class FakeMemorySource : IMemorySource
{
    public MemoryCounts? Now;
    public MemoryCounts? Read() => Now;
}

// The Memory window's answer (docs/API.md 5.8): the Mac's shape, the level, and the programs
// grouped by exe, from fake numbers and a fake process list.
public sealed class MemoryInfoTests
{
    const ulong GiB = 1UL << 30;
    const string Windows = @"C:\Windows";
    const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    const string Code = @"C:\Users\me\AppData\Local\Programs\Microsoft VS Code\Code.exe";
    const string Node = @"C:\Program Files\nodejs\node.exe";

    static ProcessEntry P(int pid, string name, string? path, ulong bytes, long cpu = 0, long created = 1)
        => new(pid, created, name, path, bytes, bytes, cpu);

    static JsonElement Run(object answer)
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(answer));
        return doc.RootElement.Clone();
    }

    static string NameOf(string exe) => Path.GetFileNameWithoutExtension(exe) switch
    {
        "chrome" => "Google Chrome",
        "Code" => "Visual Studio Code",
        var other => other,
    };

    static readonly MemoryCounts Typical = new(Total: 16 * GiB, Available: 6 * GiB, Free: 1 * GiB, Standby: 5 * GiB,
                                               CommitTotal: 12 * GiB, CommitLimit: 24 * GiB, SwapUsed: GiB / 2, Wired: GiB / 4, LowMemory: false);

    static List<ProcessEntry> Typical_Processes() => new()
    {
        P(0, "Idle", null, 0),
        P(4, "System", null, 2 * GiB),
        P(100, "Memory Compression", null, GiB / 2),
        P(200, "svchost.exe", @"C:\Windows\System32\svchost.exe", GiB),
        P(300, "chrome.exe", Chrome, 2 * GiB),
        P(301, "chrome.exe", Chrome, GiB),
        P(302, "chrome.exe", @"c:\program files\google\chrome\application\CHROME.EXE", GiB / 2),
        P(400, "Code.exe", Code, GiB),
        P(500, "node.exe", Node, GiB / 4),
        P(600, "a.exe", @"C:\Tools\a.exe", 100UL << 20),
        P(601, "b.exe", @"C:\Tools\b.exe", 90UL << 20),
        P(602, "c.exe", @"C:\Tools\c.exe", 80UL << 20),
        P(603, "d.exe", @"C:\Tools\d.exe", 70UL << 20),
    };

    MemoryInfo Info(MemoryCounts? counts, List<ProcessEntry>? processes = null) =>
        new(new FakeMemorySource { Now = counts },
            new FakeProcessTable { Next = new ProcessSnapshot(processes ?? Typical_Processes(), 1) }, Windows, NameOf);

    [Fact]
    public void TheAnswerHasTheMacsShape()
    {
        var m = Run(Info(Typical).Snapshot());
        Assert.Equal(16 * GiB, m.GetProperty("total").GetUInt64());
        Assert.Equal(10 * GiB, m.GetProperty("used").GetUInt64());           // total - available
        Assert.Equal(1 * GiB, m.GetProperty("free").GetUInt64());            // free & zero pages
        Assert.Equal(5 * GiB, m.GetProperty("cached").GetUInt64());          // the standby list
        Assert.Equal(GiB / 2, m.GetProperty("swapUsed").GetUInt64());
        Assert.Equal(GiB / 4, m.GetProperty("wired").GetUInt64());
        Assert.Equal(GiB / 2, m.GetProperty("compressed").GetUInt64());      // the Memory Compression process
        Assert.Equal(50, m.GetProperty("pressure").GetInt32());              // half the commit limit
        Assert.Equal("normal", m.GetProperty("level").GetString());
        // "app": every process's private working set (the idle process has none), at most "used".
        ulong all = Typical_Processes().Aggregate(0UL, (sum, p) => sum + p.PrivateBytes);
        Assert.Equal(Math.Min(all, 10 * GiB), m.GetProperty("app").GetUInt64());
        Assert.Equal(JsonValueKind.Array, m.GetProperty("top").ValueKind);
        Assert.True(m.GetProperty("otherPrograms").GetUInt64() > 0);
        foreach (var key in new[] { "total", "used", "app", "cached", "free", "swapUsed", "pressure", "level", "top", "otherPrograms" })
            Assert.True(m.TryGetProperty(key, out _), key);
    }

    [Fact]
    public void ProgramsAreGroupedByExeWithTheAppsPageId()
    {
        var (top, others) = MemoryInfo.Top(Typical_Processes(), 5, Windows, NameOf);
        var j = Run(top);
        // Chrome's three processes (one path spelled in other case) are one entry.
        Assert.Equal("Google Chrome", j[0].GetProperty("name").GetString());
        Assert.Equal(3 * GiB + GiB / 2, j[0].GetProperty("bytes").GetUInt64());
        Assert.Equal(PhoneApps.Id(Chrome), j[0].GetProperty("id").GetString());
        Assert.Equal("Visual Studio Code", j[1].GetProperty("name").GetString());
        Assert.Equal(PhoneApps.Id(Code), j[1].GetProperty("id").GetString());
        Assert.Equal("node", j[2].GetProperty("name").GetString());
        Assert.Equal(new[] { "a", "b" }, new[] { j[3].GetProperty("name").GetString(), j[4].GetProperty("name").GetString() });
        Assert.Equal(5, j.GetArrayLength());
        // Windows' own (System, Memory Compression, C:\Windows\...) are in neither: the page counts
        // them as "Windows & system". The rest of the programs are "otherPrograms".
        Assert.Equal((80UL << 20) + (70UL << 20), others);
        Assert.DoesNotContain(top, t => (string)t["name"] is "svchost" or "System" or "Memory Compression");
    }

    [Fact]
    public void EveryEntryHasNameBytesAndId()
    {
        var top = Run(Info(Typical).Snapshot()).GetProperty("top");
        foreach (var t in top.EnumerateArray())
        {
            Assert.Equal(JsonValueKind.String, t.GetProperty("name").ValueKind);
            Assert.Equal(JsonValueKind.Number, t.GetProperty("bytes").ValueKind);
            Assert.Matches("^[0-9a-f]{12}$", t.GetProperty("id").GetString());
        }
    }

    [Theory]
    [InlineData(0.50, 0.40, false, "normal")]
    [InlineData(0.74, 0.11, false, "normal")]
    [InlineData(0.75, 0.40, false, "warn")]
    [InlineData(0.50, 0.09, false, "warn")]
    [InlineData(0.89, 0.40, false, "warn")]
    [InlineData(0.90, 0.40, false, "critical")]
    [InlineData(0.50, 0.02, false, "critical")]
    [InlineData(0.10, 0.80, true, "critical")]   // Windows' own low-memory signal
    public void TheLevelFollowsTheCommitAndWindowsSignal(double commit, double available, bool low, string level)
    {
        Assert.Equal(level, MemoryInfo.Level(commit, available, low));
    }

    [Fact]
    public void TheLevelInTheAnswer()
    {
        Assert.Equal("warn", Run(Info(Typical with { CommitTotal = 20 * GiB }).Snapshot()).GetProperty("level").GetString());
        Assert.Equal("critical", Run(Info(Typical with { LowMemory = true }).Snapshot()).GetProperty("level").GetString());
        Assert.Equal("critical", Run(Info(Typical with { Available = GiB / 4 }).Snapshot()).GetProperty("level").GetString());
        Assert.Equal(83, Run(Info(Typical with { CommitTotal = 20 * GiB }).Snapshot()).GetProperty("pressure").GetInt32());
    }

    // Without the performance counters, available memory is split by the one that worked, or
    // all counted as free.
    [Fact]
    public void FreeAndCachedFallBackCleanly()
    {
        Assert.Equal((1 * GiB, 5 * GiB), MemoryInfo.FreeAndCached(Typical));
        Assert.Equal((2 * GiB, 4 * GiB), MemoryInfo.FreeAndCached(Typical with { Free = 2 * GiB, Standby = null }));
        Assert.Equal((3 * GiB, 3 * GiB), MemoryInfo.FreeAndCached(Typical with { Free = null, Standby = 3 * GiB }));
        Assert.Equal((6 * GiB, 0UL), MemoryInfo.FreeAndCached(Typical with { Free = null, Standby = null }));
        // A counter larger than what is available (read at another moment) never goes below zero.
        Assert.Equal((6 * GiB, 0UL), MemoryInfo.FreeAndCached(Typical with { Free = 9 * GiB, Standby = null }));
        var m = Run(Info(Typical with { Free = null, Standby = null }).Snapshot());
        Assert.Equal(6 * GiB, m.GetProperty("free").GetUInt64());
        Assert.Equal(0UL, m.GetProperty("cached").GetUInt64());
    }

    // No numbers at all, no process list: still an answer the page reads (it prints dashes).
    [Fact]
    public void NothingReadableStillAnswers()
    {
        var info = new MemoryInfo(new FakeMemorySource(), new FakeProcessTable(), Windows, NameOf);
        var m = Run(info.Snapshot());
        Assert.Equal("normal", m.GetProperty("level").GetString());
        Assert.Equal(0, m.GetProperty("top").GetArrayLength());
        Assert.Equal(0UL, m.GetProperty("otherPrograms").GetUInt64());
        Assert.False(m.TryGetProperty("free", out _));
    }

    [Fact]
    public void SystemMeansNoPathOrInsideTheWindowsFolder()
    {
        Assert.True(ProcessTable.IsSystem(null, Windows));
        Assert.True(ProcessTable.IsSystem(@"C:\Windows\explorer.exe", Windows));
        Assert.True(ProcessTable.IsSystem(@"c:\windows\system32\DWM.EXE", @"C:\Windows\"));
        Assert.False(ProcessTable.IsSystem(@"C:\WindowsApps\x.exe", Windows));
        Assert.False(ProcessTable.IsSystem(Chrome, Windows));
    }

    [Fact]
    public void FeaturesIncludeMemory()
    {
        Assert.Contains("memory", StatusInfo.Features);
        Assert.Contains("memory", StatusInfo.FeaturesWithHeat);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new StatusInfo(new FakeReadings()).Build(true)));
        Assert.Contains("memory", doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
    }
}
