using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using LidAwake;
using Xunit;

// Claude plan usage (the Mac's SystemInfo.swift): reading Claude Code's "/usage", the saved file's
// checks, asking at most so often, the 90% alert, finding Claude Code safely, and running it hidden.
public class ClaudeUsageTests : IDisposable
{
    readonly List<string> folders = new();
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    // Tuesday 20 October 2026, 12:00 UTC.
    static readonly DateTimeOffset Noon = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);

    string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lidawake-claude-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        folders.Add(folder);
        return folder;
    }

    public void Dispose()
    {
        foreach (var folder in folders)
        {
            try { Directory.Delete(folder, true); }
            catch (Exception) { }
        }
    }

    const string MacSample =
        "Current session: 74% used · resets Oct 8 at 3:09pm (Asia/Jerusalem)\n" +
        "Current week (all models): 84% used · resets Oct 10 at 10:59pm (Asia/Jerusalem)\n" +
        "Current week (Opus): 12% used\n";

    static long Unix(int month, int day, int hour, int minute, int year = 2026) =>
        new DateTimeOffset(year, month, day, hour, minute, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    // ---- parseUsage ----

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    public void ParsesTheMacSampleWithEitherLineEnding(string newline)
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var limits = ClaudeUsage.ParseUsage(MacSample.Replace("\n", newline), now, Utc)!;
        Assert.Equal(74, (double)limits["five_hour"]!["used_percentage"]!);
        Assert.Equal(Unix(10, 8, 15, 9), (long)limits["five_hour"]!["resets_at"]!);
        Assert.Equal(84, (double)limits["seven_day"]!["used_percentage"]!);
        Assert.Equal(Unix(10, 10, 22, 59), (long)limits["seven_day"]!["resets_at"]!);
        Assert.Equal(2, limits.Count);
    }

    [Fact]
    public void TheMacsOtherFormsAndWhatWindowsMayPrint()
    {
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        // "Oct 10 at 11pm", a decimal percent, no time zone in brackets.
        var limits = ClaudeUsage.ParseUsage("Current session: 12.5% used · resets Oct 10 at 11pm\r\nCurrent week (all models): 3% used · resets Oct 12 at 9:05am  \r\n", now, Utc)!;
        Assert.Equal(12.5, (double)limits["five_hour"]!["used_percentage"]!);
        Assert.Equal(Unix(10, 10, 23, 0), (long)limits["five_hour"]!["resets_at"]!);
        Assert.Equal(Unix(10, 12, 9, 5), (long)limits["seven_day"]!["resets_at"]!);

        // A time alone: today, or tomorrow once it has passed.
        var later = ClaudeUsage.ParseUsage("Current session: 5% used · resets 3pm (Europe/London)", now, Utc)!;
        Assert.Equal(Unix(10, 7, 15, 0), (long)later["five_hour"]!["resets_at"]!);
        var tomorrow = ClaudeUsage.ParseUsage("Current session: 5% used · resets 9:30am (Europe/London)", now, Utc)!;
        Assert.Equal(Unix(10, 8, 9, 30), (long)tomorrow["five_hour"]!["resets_at"]!);

        // A reset that can't be read: the percent stays, without a time.
        var unknown = ClaudeUsage.ParseUsage("Current session: 40% used · resets soon", now, Utc)!;
        Assert.Equal(40, (double)unknown["five_hour"]!["used_percentage"]!);
        Assert.False(unknown["five_hour"]!.AsObject().ContainsKey("resets_at"));
    }

    [Fact]
    public void ADateLongPastIsNextYears()
    {
        var now = new DateTimeOffset(2026, 12, 30, 12, 0, 0, TimeSpan.Zero);
        var limits = ClaudeUsage.ParseUsage("Current week (all models): 50% used · resets Jan 2 at 1am (UTC)", now, Utc)!;
        Assert.Equal(Unix(1, 2, 1, 0, 2027), (long)limits["seven_day"]!["resets_at"]!);
    }

    [Fact]
    public void ResetTimesAreThePcsLocalTime()
    {
        var plusThree = TimeZoneInfo.CreateCustomTimeZone("plus3", TimeSpan.FromHours(3), "plus3", "plus3");
        var now = new DateTimeOffset(2026, 10, 7, 12, 0, 0, TimeSpan.Zero);
        var limits = ClaudeUsage.ParseUsage("Current session: 1% used · resets Oct 8 at 3:09pm (Asia/Jerusalem)", now, plusThree)!;
        Assert.Equal(Unix(10, 8, 12, 9), (long)limits["five_hour"]!["resets_at"]!);
    }

    [Theory]
    [InlineData("")]
    [InlineData("Usage is not available for this account.")]
    [InlineData("  Current session: 74% used")]  // the label must start the line, as on the Mac
    [InlineData("Current session: lots used")]
    public void NothingToReadIsNull(string text) => Assert.Null(ClaudeUsage.ParseUsage(text, Noon, Utc));

    // ---- The saved file (the Mac's claudeUsage()) ----

    string WriteUsage(string folder, string json)
    {
        var path = Path.Combine(folder, ClaudeUsage.FileName);
        File.WriteAllText(path, json);
        return path;
    }

    [Fact]
    public void ReadsTheMacsFile()
    {
        var path = WriteUsage(NewFolder(),
            "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":74,\"resets_at\":1760010000},\"seven_day\":{\"used_percentage\":84.5,\"resets_at\":1760300000.7}},\"updated\":1760000000}");
        var u = ClaudeUsage.ReadFile(path)!;
        Assert.Equal(1760000000, u.Updated);
        Assert.Equal(new ClaudeUsage.Window(74, 1760010000), u.FiveHour);
        Assert.Equal(new ClaudeUsage.Window(84.5, 1760300000), u.SevenDay);
        // As /status sends it (docs/API.md 6.3).
        var json = JsonSerializer.Serialize(u.ToStatus());
        Assert.Equal("{\"updated\":1760000000,\"fiveHour\":{\"pct\":74,\"resetsAt\":1760010000},\"sevenDay\":{\"pct\":84.5,\"resetsAt\":1760300000}}", json);
    }

    [Fact]
    public void OddNumbersAreLeftOutNotTrusted()
    {
        var path = WriteUsage(NewFolder(),
            "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":1001,\"resets_at\":1},\"seven_day\":{\"used_percentage\":-1}},\"updated\":\"yesterday\"}");
        var u = ClaudeUsage.ReadFile(path)!;
        Assert.Null(u.FiveHour);
        Assert.Null(u.SevenDay);
        Assert.Equal(0, u.Updated);

        var times = ClaudeUsage.ReadFile(WriteUsage(NewFolder(),
            "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":0,\"resets_at\":1e12},\"seven_day\":{\"used_percentage\":1000,\"resets_at\":-5}},\"updated\":1e11}"))!;
        Assert.Equal(new ClaudeUsage.Window(0, 0), times.FiveHour);
        Assert.Equal(new ClaudeUsage.Window(1000, 0), times.SevenDay);
        Assert.Equal(0, times.Updated);
    }

    [Fact]
    public void OnlyASmallOrdinaryFileWithRateLimits()
    {
        var folder = NewFolder();
        Assert.Null(ClaudeUsage.ReadFile(Path.Combine(folder, "missing.json")));
        Assert.Null(ClaudeUsage.ReadFile(WriteUsage(folder, "not json")));
        Assert.Null(ClaudeUsage.ReadFile(WriteUsage(folder, "[1,2]")));
        Assert.Null(ClaudeUsage.ReadFile(WriteUsage(folder, "{\"updated\":1}")));
        Assert.Null(ClaudeUsage.ReadFile(WriteUsage(folder, "{\"rate_limits\":[]}")));
        // 64 KB or more: not read at all.
        var big = "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":5}},\"pad\":\"" + new string('x', 64 * 1024) + "\"}";
        Assert.Null(ClaudeUsage.ReadFile(WriteUsage(folder, big)));
        // A folder by that name.
        var dirFolder = NewFolder();
        Directory.CreateDirectory(Path.Combine(dirFolder, ClaudeUsage.FileName));
        Assert.Null(ClaudeUsage.ReadFile(Path.Combine(dirFolder, ClaudeUsage.FileName)));
    }

    // ---- Asking Claude Code at most so often ----

    [Fact]
    public async Task AsksAtMostEveryIntervalAndSavesWhatItRead()
    {
        var folder = NewFolder();
        var now = Noon;
        int asked = 0;
        var usage = new ClaudeUsage(folder, () => { asked++; return MacSample.Replace("Oct 8", "Oct 21").Replace("Oct 10", "Oct 23"); }, () => now, Utc);
        int saved = 0;
        usage.Saved += () => saved++;
        Assert.Null(usage.Read());

        await usage.Refresh(TimeSpan.Zero);
        Assert.Equal(1, asked);
        Assert.Equal(1, saved);
        var u = usage.Read()!;
        Assert.Equal(Noon.ToUnixTimeSeconds(), u.Updated);
        Assert.Equal(74, u.FiveHour!.Value.Pct);
        Assert.Equal(Unix(10, 21, 15, 9), u.FiveHour!.Value.ResetsAt);

        // Within 3 minutes: not asked again.
        now = Noon.AddMinutes(2);
        await usage.Refresh(ClaudeUsage.AskForStatus);
        Assert.Equal(1, asked);
        now = Noon.AddMinutes(4);
        await usage.Refresh(ClaudeUsage.AskForStatus);
        Assert.Equal(2, asked);

        // For /status: the numbers as the page reads them.
        var status = usage.ForStatus()!;
        Assert.Equal(74.0, ((Dictionary<string, object?>)status["fiveHour"]!)["pct"]);
    }

    [Fact]
    public async Task NothingReadNothingSaved()
    {
        var folder = NewFolder();
        var usage = new ClaudeUsage(folder, () => null, () => Noon, Utc);
        await usage.Refresh(TimeSpan.Zero);
        var throwing = new ClaudeUsage(folder, () => throw new InvalidOperationException("claude"), () => Noon, Utc);
        await throwing.Refresh(TimeSpan.Zero);
        Assert.False(File.Exists(usage.FilePath));
        Assert.Null(usage.ForStatus());
    }

    // ---- Words ----

    [Theory]
    [InlineData(0, "<1m")]
    [InlineData(59, "<1m")]
    [InlineData(60, "1m")]
    [InlineData(35 * 60 + 30, "35m")]
    [InlineData(3600, "1h00m")]
    [InlineData(100 * 60, "1h40m")]
    [InlineData(-120, "<1m")]
    public void ShortDurationsAreTheMacs(int seconds, string expected) =>
        Assert.Equal(expected, ClaudeUsage.ShortDuration(TimeSpan.FromSeconds(seconds)));

    [Fact]
    public void TodayIsATimeAnotherDayHasItsWeekday()
    {
        var c = CultureInfo.InvariantCulture;
        Assert.Equal("15:10", ClaudeUsage.When(Noon.AddHours(3).AddMinutes(10), Noon, Utc, c));
        Assert.Equal("Fri 22:59", ClaudeUsage.When(new DateTimeOffset(2026, 10, 23, 22, 59, 0, TimeSpan.Zero), Noon, Utc, c));
    }

    // ---- The 90% alert (main.swift checkAlerts) ----

    sealed class Sent
    {
        public readonly List<(string Kind, string Title, string Body)> All = new();
        public Task Send(string kind, string title, string body)
        {
            All.Add((kind, title, body));
            return Task.CompletedTask;
        }
    }

    void SaveWindows(string folder, double five, long fiveResets, double week, long weekResets) =>
        WriteUsage(folder, $"{{\"rate_limits\":{{\"five_hour\":{{\"used_percentage\":{five.ToString(CultureInfo.InvariantCulture)},\"resets_at\":{fiveResets}}}," +
                           $"\"seven_day\":{{\"used_percentage\":{week.ToString(CultureInfo.InvariantCulture)},\"resets_at\":{weekResets}}}}},\"updated\":1}}");

    [Fact]
    public void UsageAlertOncePerWindowAt90Percent()
    {
        var folder = NewFolder();
        var settings = new Settings(NewFolder());
        var now = Noon;
        var usage = new ClaudeUsage(folder, () => null, () => now, Utc);
        var alert = new ClaudeUsageAlert(usage, settings, () => now, Utc, CultureInfo.InvariantCulture, ask: false);
        var sent = new Sent();
        long fiveResets = Unix(10, 20, 13, 40), weekResets = Unix(10, 23, 22, 59);

        SaveWindows(folder, 89.9, fiveResets, 50, weekResets);
        alert.Check(sent.Send);
        Assert.Empty(sent.All);

        SaveWindows(folder, 92.4, fiveResets, 50, weekResets);
        alert.Check(sent.Send);
        Assert.Equal((AlertKind.ClaudeUsage, "Claude usage at 92%", "5-hour limit, resets at 13:40 (in 1 h 40 min)."), Assert.Single(sent.All));
        Assert.Equal(fiveResets, settings.Get<long?>("claudeAlerted.fiveHour"));

        // Same window: never again, even after a restart (a new alert object, the same settings).
        SaveWindows(folder, 99, fiveResets, 95, weekResets);
        new ClaudeUsageAlert(usage, settings, () => now, Utc, CultureInfo.InvariantCulture, ask: false).Check(sent.Send);
        Assert.Equal(2, sent.All.Count);
        Assert.Equal((AlertKind.ClaudeUsage, "Claude usage at 95%", "Weekly limit, resets Fri at 22:59 (in 82 h 59 min)."), sent.All[1]);
        alert.Check(sent.Send);
        Assert.Equal(2, sent.All.Count);

        // The next 5-hour window alerts again.
        SaveWindows(folder, 90, Unix(10, 20, 18, 40), 95, weekResets);
        alert.Check(sent.Send);
        Assert.Equal(3, sent.All.Count);
        Assert.Equal("Claude usage at 90%", sent.All[2].Title);

        // A window already past its reset (old numbers) or with no reset time: nothing.
        var other = new Settings(NewFolder());
        SaveWindows(folder, 99, Unix(10, 20, 11, 0), 99, 0);
        new ClaudeUsageAlert(usage, other, () => now, Utc, CultureInfo.InvariantCulture, ask: false).Check(sent.Send);
        Assert.Equal(3, sent.All.Count);
    }

    // The status line and "/usage" give the same window's reset a few seconds apart: one alert.
    [Fact]
    public void TheSameWindowToTheMinuteAlertsOnce()
    {
        var folder = NewFolder();
        var settings = new Settings(NewFolder());
        var usage = new ClaudeUsage(folder, () => null, () => Noon, Utc);
        var alert = new ClaudeUsageAlert(usage, settings, () => Noon, Utc, CultureInfo.InvariantCulture, ask: false);
        var sent = new Sent();
        long resets = Unix(10, 20, 13, 40), week = Unix(10, 23, 22, 59);
        SaveWindows(folder, 95, resets + 20, 1, week);
        alert.Check(sent.Send);
        Assert.Single(sent.All);
        Assert.Equal(resets, settings.Get<long?>("claudeAlerted.fiveHour"));
        SaveWindows(folder, 96, resets - 25, 1, week);
        new ClaudeUsageAlert(usage, settings, () => Noon, Utc, CultureInfo.InvariantCulture, ask: false).Check(sent.Send);
        alert.Check(sent.Send);
        Assert.Single(sent.All);
        Assert.Equal(resets, ClaudeUsageAlert.WindowKey(resets + 29));
        Assert.Equal(resets + 60, ClaudeUsageAlert.WindowKey(resets + 30));
    }

    [Fact]
    public void NothingIsSavedIntoAFolderThatIsntThere()
    {
        var missing = Path.Combine(NewFolder(), "gone", ClaudeUsage.FileName);
        Assert.False(ClaudeUsage.Save(missing, new JsonObject { ["five_hour"] = new JsonObject { ["used_percentage"] = 5 } }, Noon));
        Assert.False(Directory.Exists(Path.GetDirectoryName(missing)));
    }

    [Fact]
    public void AnEarlierQuestionsLeftOverFolderIsCleanedUp()
    {
        var temp = NewFolder();
        var old = Directory.CreateDirectory(Path.Combine(temp, ClaudeUsage.TempPrefix + "old"));
        Directory.SetCreationTimeUtc(old.FullName, DateTime.UtcNow.AddMinutes(-11));
        var fresh = Directory.CreateDirectory(Path.Combine(temp, ClaudeUsage.TempPrefix + "fresh"));
        var other = Directory.CreateDirectory(Path.Combine(temp, "someone-else"));
        Directory.SetCreationTimeUtc(other.FullName, DateTime.UtcNow.AddDays(-1));
        ClaudeUsage.CleanUp(temp, DateTime.UtcNow);
        Assert.False(Directory.Exists(old.FullName));
        Assert.True(Directory.Exists(fresh.FullName));
        Assert.True(Directory.Exists(other.FullName));
    }

    [Fact]
    public void UsageAlertGoesWithTheOtherEarlyWarnings()
    {
        var folder = NewFolder();
        var sent = new Sent();
        var usage = new ClaudeUsage(folder, () => null, () => Noon, Utc);
        var alerts = new PhoneAlerts(sent.Send, () => null, () => null, () => true, () => Noon)
        {
            Claude = new ClaudeUsageAlert(usage, new Settings(NewFolder()), () => Noon, Utc, CultureInfo.InvariantCulture, ask: false),
        };
        SaveWindows(folder, 91, Unix(10, 20, 14, 0), 10, Unix(10, 23, 0, 0));
        alerts.Check();
        Assert.Equal(AlertKind.ClaudeUsage, Assert.Single(sent.All).Kind);
        // Nothing saved at all: no alert, no error.
        var empty = new PhoneAlerts(sent.Send, () => null, () => null, () => true, () => Noon)
        {
            Claude = new ClaudeUsageAlert(new ClaudeUsage(NewFolder(), () => null, () => Noon, Utc), new Settings(NewFolder()), ask: false),
        };
        empty.Check();
        Assert.Single(sent.All);
    }

    // ---- Finding Claude Code, and whether it is safe to run (a fake owner and access list) ----

    const string Me = "S-1-5-21-1-2-3-1001";
    const string SomeoneElse = "S-1-5-21-1-2-3-1002";
    const string Users = "S-1-5-32-545";
    const string Everyone = "S-1-1-0";
    const int FullControl = 0x1F01FF, ReadAndExecute = 0x1200A9;

    static FileOwnership Owned(string owner, params AccessEntry[] entries) => new(owner, entries);
    static AccessEntry Allow(string sid, int mask, bool inheritOnly = false) => new(sid, true, mask, inheritOnly);

    [Fact]
    public void TrustedOwnersAndNoOneElseMayWrite()
    {
        var usual = new[] { Allow(Me, FullControl), Allow(ClaudeFinder.SystemSid, FullControl), Allow(ClaudeFinder.AdministratorsSid, FullControl) };
        Assert.True(ClaudeFinder.Trusted(Owned(Me, usual), Me));
        Assert.True(ClaudeFinder.Trusted(Owned(ClaudeFinder.AdministratorsSid, usual), Me));
        Assert.True(ClaudeFinder.Trusted(Owned(ClaudeFinder.SystemSid, usual), Me));
        Assert.True(ClaudeFinder.Trusted(Owned(ClaudeFinder.TrustedInstallerSid, usual), Me));
        // Others may read and run it.
        Assert.True(ClaudeFinder.Trusted(Owned(Me, usual.Append(Allow(Users, ReadAndExecute)).ToArray()), Me));
        // A deny entry, or one that only passes to new children, doesn't open it up.
        Assert.True(ClaudeFinder.Trusted(Owned(Me, usual.Append(new AccessEntry(Everyone, false, FullControl, false))
            .Append(Allow(Users, FullControl, inheritOnly: true)).ToArray()), Me));

        Assert.False(ClaudeFinder.Trusted(Owned(SomeoneElse, usual), Me));
        Assert.False(ClaudeFinder.Trusted(new FileOwnership(null, usual), Me));
        Assert.False(ClaudeFinder.Trusted(Owned(Me, usual.Append(Allow(Users, 0x2)).ToArray()), Me));          // write data
        Assert.False(ClaudeFinder.Trusted(Owned(Me, usual.Append(Allow(Everyone, 0x10000)).ToArray()), Me));   // delete
        Assert.False(ClaudeFinder.Trusted(Owned(Me, usual.Append(Allow(SomeoneElse, 0x40000)).ToArray()), Me)); // change permissions
        Assert.False(ClaudeFinder.Trusted(Owned(Me, usual.Append(Allow(Users, 0x40000000)).ToArray()), Me));   // generic write
    }

    sealed class FakeOwnership : IOwnershipReader
    {
        public readonly Dictionary<string, FileOwnership> Known = new(StringComparer.OrdinalIgnoreCase);
        public FileOwnership? Read(string path, bool directory) => Known.TryGetValue(path, out var o) ? o : null;
    }

    [Fact]
    public void TheFileAndItsFolderMustBothBeSafe()
    {
        var exe = Path.Combine(Path.GetTempPath(), "no-such-folder-" + Guid.NewGuid().ToString("N"), "bin", "claude.exe");
        var folder = Path.GetDirectoryName(exe)!;
        var fake = new FakeOwnership();
        var safe = Owned(Me, Allow(Me, FullControl), Allow(ClaudeFinder.SystemSid, FullControl));
        fake.Known[exe] = safe;
        Assert.False(ClaudeFinder.Safe(exe, fake, Me));  // the folder can't be read
        fake.Known[folder] = Owned(Me, Allow(Me, FullControl), Allow(Users, 0x4));  // others may add files to it
        Assert.False(ClaudeFinder.Safe(exe, fake, Me));
        fake.Known[folder] = safe;
        Assert.True(ClaudeFinder.Safe(exe, fake, Me));
        fake.Known[exe] = Owned(SomeoneElse, Allow(Me, FullControl));
        Assert.False(ClaudeFinder.Safe(exe, fake, Me));
    }

    [Fact]
    public void ReadsARealFilesOwnerAndAccess()
    {
        var file = Path.Combine(NewFolder(), "claude.exe");
        File.WriteAllText(file, "x");
        var o = new WindowsOwnership().Read(file, directory: false);
        Assert.NotNull(o);
        Assert.NotNull(o!.OwnerSid);
        Assert.NotEmpty(o.Entries);
        Assert.NotNull(new WindowsOwnership().Read(Path.GetDirectoryName(file)!, directory: true));
        Console.WriteLine($"RUNNER: a temp file's owner {o.OwnerSid}, safe to run: {ClaudeFinder.Safe(file, new WindowsOwnership(), System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value)}");
    }

    [Fact]
    public void TheCommandLinesForTheTwoKinds()
    {
        Assert.Equal("\"C:\\Users\\me\\.local\\bin\\claude.exe\" -p /usage --setting-sources local --no-session-persistence",
            ClaudeFinder.CommandFor(@"C:\Users\me\.local\bin\claude.exe", ClaudeUsage.UsageArguments)!.Value.CommandLine);
        var cmd = ClaudeFinder.CommandFor(@"C:\Users\me\AppData\Roaming\npm\claude.cmd", ClaudeUsage.UsageArguments)!.Value;
        Assert.EndsWith("cmd.exe", cmd.App, StringComparison.OrdinalIgnoreCase);
        Assert.EndsWith(" /d /v:off /s /c \"\"C:\\Users\\me\\AppData\\Roaming\\npm\\claude.cmd\" -p /usage --setting-sources local --no-session-persistence\"", cmd.CommandLine);
        Assert.Null(ClaudeFinder.CommandFor(@"C:\Users\100%\npm\claude.cmd", ClaudeUsage.UsageArguments));
        Assert.Null(ClaudeFinder.CommandFor("C:\\a\"b\\claude.exe", ClaudeUsage.UsageArguments));
        Assert.Null(ClaudeFinder.CommandFor(@"C:\a\claude.bat", ClaudeUsage.UsageArguments));
        Assert.Null(ClaudeFinder.CommandFor(@"C:\a\claude.com", ClaudeUsage.UsageArguments));
        // The real file behind a link may have no ".exe": run directly, never through cmd.
        Assert.Equal((@"C:\U\.local\share\claude\versions\2.1.0", "\"C:\\U\\.local\\share\\claude\\versions\\2.1.0\" -p"),
            ClaudeFinder.CommandFor(@"C:\U\.local\share\claude\versions\2.1.0", "-p")!.Value);
        Assert.Equal(new[] { @"C:\U\.local\bin\claude.exe", @"C:\A\npm\claude.cmd" }, ClaudeFinder.Candidates(@"C:\U", @"C:\A"));
    }

    // ---- Running it hidden, with a time limit for the whole tree ----

    static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Fact]
    public void RunsHiddenAndReadsTheOutput()
    {
        var folder = NewFolder();
        var r = HiddenRun.Run(Cmd, $"\"{Cmd}\" /d /c echo hello& cd", folder, Path.Combine(folder, "out.txt"), TimeSpan.FromSeconds(20));
        Assert.True(r.Started);
        Assert.False(r.TimedOut);
        var lines = r.Output!.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal("hello", lines[0]);
        // It ran in the folder it was given.
        Assert.Equal(folder.TrimEnd('\\'), lines[1].TrimEnd('\\'), ignoreCase: true);
    }

    // A program that waits, with a child that waits too and then writes a file. The time limit
    // stops both: the file never appears. With time enough, it does (the command really works).
    [Fact]
    public async Task TheTimeLimitStopsTheWholeTree()
    {
        const string sleeper = "/d /c cmd /d /c \"ping -n 4 127.0.0.1 >nul & echo late>marker.txt\"";
        var slow = NewFolder();
        var watch = Stopwatch.StartNew();
        var r = HiddenRun.Run(Cmd, $"\"{Cmd}\" {sleeper}", slow, Path.Combine(slow, "out.txt"), TimeSpan.FromSeconds(1));
        Assert.True(r.Started);
        Assert.True(r.TimedOut);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(10), "took " + watch.Elapsed);
        // The child would have written its file after about 3 seconds; this is time enough to see it doesn't.
        await Task.Delay(6000);
        Assert.False(File.Exists(Path.Combine(slow, "marker.txt")), "the child kept running after the time limit");

        var patient = NewFolder();
        var done = HiddenRun.Run(Cmd, $"\"{Cmd}\" {sleeper}", patient, Path.Combine(patient, "out.txt"), TimeSpan.FromSeconds(30));
        Assert.False(done.TimedOut);
        Assert.True(File.Exists(Path.Combine(patient, "marker.txt")));
    }

    [Fact]
    public void AProgramThatIsntThereIsNotStarted()
    {
        var folder = NewFolder();
        var missing = Path.Combine(folder, "nothing.exe");
        var r = HiddenRun.Run(missing, $"\"{missing}\"", folder, Path.Combine(folder, "out.txt"), TimeSpan.FromSeconds(5));
        Assert.False(r.Started);
        Assert.Null(r.Output);
    }
}
