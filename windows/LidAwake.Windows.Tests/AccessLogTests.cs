using System.Text;
using LidAwake;
using Xunit;

public class AccessLogTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    DateTimeOffset clock = new(2026, 10, 19, 9, 30, 0, TimeSpan.FromHours(3));

    AccessLog NewLog(long maxBytes = AccessLog.DefaultMaxBytes) => new(folder, maxBytes, () => clock);

    string LogPath => Path.Combine(folder, "LidAwake-access.log");

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void NoLogMeansAnEmptyList()
    {
        Assert.Empty(NewLog().Recent(8));
        Assert.False(File.Exists(LogPath));
    }

    [Fact]
    public void ALineIsTheTimeWithItsOffsetTwoSpacesAndTheText()
    {
        var log = NewLog();
        log.Write("New sign-in to Lid Awake · With the password, on an iPhone.");
        Assert.Equal("2026-10-19T09:30:00+03:00  New sign-in to Lid Awake · With the password, on an iPhone.", Assert.Single(log.Recent(8)));
        // The file is plain UTF-8, one line per event, with no byte order mark.
        var bytes = File.ReadAllBytes(LogPath);
        Assert.NotEqual(0xEF, bytes[0]);
        Assert.Equal("2026-10-19T09:30:00+03:00  New sign-in to Lid Awake · With the password, on an iPhone.\n", Encoding.UTF8.GetString(bytes));
    }

    [Fact]
    public void TheTimeParsesAsIso8601WithTheOffset()
    {
        clock = new DateTimeOffset(2026, 1, 5, 23, 59, 59, TimeSpan.FromMinutes(-330));
        var log = NewLog();
        log.Write("x");
        var line = Assert.Single(log.Recent(1));
        var time = line[..line.IndexOf("  ", StringComparison.Ordinal)];
        Assert.Equal("2026-01-05T23:59:59-05:30", time);
        Assert.Equal(clock, DateTimeOffset.Parse(time, System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public void RecentGivesTheNewestFirstAndAtMostTheCountAsked()
    {
        var log = NewLog();
        for (int i = 1; i <= 12; i++)
        {
            clock += TimeSpan.FromSeconds(1);
            log.Write("event " + i);
        }
        var lines = log.Recent(8);
        Assert.Equal(8, lines.Count);
        Assert.EndsWith("  event 12", lines[0]);
        Assert.EndsWith("  event 5", lines[7]);
        Assert.Equal(3, log.Recent(3).Count);
        Assert.Equal(12, log.Recent(100).Count);
    }

    [Fact]
    public void ControlCharactersAreRemovedAndTheTextIsCutAt300()
    {
        var log = NewLog();
        log.Write("a\nb\r\tc\u0000d\u007fe\u0085f");
        log.Write(new string('x', 500));
        var lines = log.Recent(8);
        Assert.Equal(2, lines.Count);
        var cut = lines[0][(lines[0].IndexOf("  ", StringComparison.Ordinal) + 2)..];
        Assert.Equal(300, cut.Length);
        Assert.EndsWith("  abcdef", lines[1]);
    }

    [Fact]
    public void CuttingCountsCharactersNotUtf16Units()
    {
        // 300 emoji are 600 UTF-16 units but 300 characters: none is split in half.
        var cut = AccessLog.Clean(string.Concat(Enumerable.Repeat("\U0001F600", 301)));
        Assert.Equal(300, cut.EnumerateRunes().Count());
        Assert.Equal(600, cut.Length);
    }

    [Fact]
    public void ALogOverTheLimitMovesToOldAndStartsAgain()
    {
        var log = NewLog(maxBytes: 300);
        // A write rolls the log over only when it is already over the limit, so this stops just past it.
        int n = 0;
        while (!File.Exists(LogPath) || new FileInfo(LogPath).Length <= 300) log.Write("filling the log " + n++);
        Assert.False(File.Exists(LogPath + ".old"));

        log.Write("first after rolling over");
        Assert.True(File.Exists(LogPath + ".old"));
        Assert.EndsWith("  first after rolling over", Assert.Single(log.Recent(8)));
        Assert.Contains("filling the log " + (n - 1), File.ReadAllText(LogPath + ".old"));

        // Rolling over again replaces the old file: only the previous log is kept.
        int m = 0;
        while (new FileInfo(LogPath).Length <= 300) log.Write("second round " + m++);
        log.Write("second roll");
        var old = File.ReadAllText(LogPath + ".old");
        Assert.Contains("second round " + (m - 1), old);
        Assert.DoesNotContain("filling the log", old);
        Assert.EndsWith("  second roll", Assert.Single(log.Recent(8)));
    }

    [Fact]
    public void TheDefaultLimitIsOneMillionBytes()
    {
        Assert.Equal(1_000_000, AccessLog.DefaultMaxBytes);
        var log = NewLog();
        log.Write("one");
        File.AppendAllText(LogPath, new string('x', 1_000_000));
        log.Write("two");
        Assert.True(File.Exists(LogPath + ".old"));
    }

    [Fact]
    public void ALogThatCannotBeWrittenNeverThrows()
    {
        // The "folder" is a file, so nothing can be created in it.
        Directory.CreateDirectory(folder);
        var blocker = Path.Combine(folder, "notafolder");
        File.WriteAllText(blocker, "x");
        var log = new AccessLog(blocker, AccessLog.DefaultMaxBytes, () => clock);
        log.Write("lost");
        Assert.Empty(log.Recent(8));
    }

    // On Windows an open file cannot be replaced: here .old is held open (an editor showing it, a
    // backup program), so rolling the log aside fails. The line must still be written, and
    // nothing may throw (the tray calls Write on the UI thread).
    [Fact]
    public void ALogThatCannotBeRolledAsideStillGetsTheLine()
    {
        var log = NewLog(maxBytes: 100);
        log.Write(new string('a', 200));
        Assert.True(new FileInfo(LogPath).Length > 100);
        File.WriteAllText(LogPath + ".old", "the previous log");
        using var held = new FileStream(LogPath + ".old", FileMode.Open, FileAccess.ReadWrite, FileShare.None);

        var thrown = Record.Exception(() => log.Write("still written"));

        Assert.Null(thrown);
        Assert.EndsWith("  still written", log.Recent(1)[0]);
    }

    [Fact]
    public async Task WritesFromManyThreadsAllArriveWhole()
    {
        var log = NewLog();
        await Task.WhenAll(Enumerable.Range(0, 8).Select(t => Task.Run(() =>
        {
            for (int i = 0; i < 50; i++) log.Write($"thread {t} line {i}");
        })));
        var lines = log.Recent(1000);
        Assert.Equal(400, lines.Count);
        Assert.All(lines, l => Assert.Matches(@"^2026-10-19T09:30:00\+03:00  thread \d line \d+$", l));
    }
}
