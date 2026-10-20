using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LidAwake;
using Xunit;

// Processes that tests set by hand.
public sealed class FakeAppProcesses : IProcessSource
{
    public List<RunningApp> Apps = new();
    public bool Throws;
    public int Scans;
    public IReadOnlyList<RunningApp> Running()
    {
        Scans++;
        return Throws ? throw new InvalidOperationException("no process list") : Apps.ToList();
    }
}

// Window actions that only record what was asked.
public sealed class FakeAppActions : IWindowActions
{
    public readonly List<string> Calls = new();
    public readonly HashSet<string> WithWindow = new(StringComparer.OrdinalIgnoreCase);
    public readonly Dictionary<string, QuitResult> QuitAnswers = new(StringComparer.OrdinalIgnoreCase);
    public bool StartThrows;

    public void Start(string exe)
    {
        if (StartThrows) throw new InvalidOperationException("can't start");
        Calls.Add("start " + exe);
    }
    public bool Focus(string exe) { Calls.Add("focus " + exe); return WithWindow.Contains(exe); }
    public bool Hide(string exe) { Calls.Add("hide " + exe); return WithWindow.Contains(exe); }
    public QuitResult Quit(string exe)
    {
        Calls.Add("quit " + exe);
        return QuitAnswers.TryGetValue(exe, out var r) ? r : QuitResult.NotOpen;
    }
}

// A tiny but real PNG, whatever the program.
public sealed class FakeAppIcons : IAppIcons
{
    public static readonly byte[] Image =
        Convert.FromBase64String("iVBORw0KGgoAAAANSUhEUgAAAAEAAAABCAYAAAAfFcSJAAAADUlEQVR42mNkYPhfDwAChwGA60e6kgAAAABJRU5ErkJggg==");
    public readonly List<(string Exe, int Size)> Asked = new();
    public byte[]? Png(string exe, int size) { Asked.Add((exe, size)); return Image; }
}

public class PhoneAppsTests : IDisposable
{
    public const string Alpha = @"C:\Apps\Alpha\alpha.exe";
    public const string Beta = @"C:\Apps\Beta\beta.exe";
    public const string Gamma = @"C:\Apps\Gamma\gamma.exe";
    public const string Delta = @"C:\Apps\Delta\delta.exe";

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Settings settings;
    readonly AccessLog log;
    readonly FakeAppProcesses processes = new();
    readonly FakeAppActions actions = new();
    readonly FakeAppIcons icons = new();
    readonly HashSet<string> missing = new(StringComparer.OrdinalIgnoreCase);
    readonly PhoneApps apps;
    long now = 1_000;   // the clock the scan cache reads, moved by hand

    public PhoneAppsTests()
    {
        settings = new Settings(Path.Combine(folder, "settings"));
        log = new AccessLog(Path.Combine(folder, "logs"));
        apps = new PhoneApps(settings, processes, actions, icons, log, exe => !missing.Contains(exe), () => now);
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static StartApp Pick(string name, string exe) => new(name, exe);

    void PickAll() => apps.SetPicks(new[] { Pick("Alpha", Alpha), Pick("Beta", Beta), Pick("Gamma", Gamma) });

    static string[] Ids(List<Dictionary<string, object>> list) => list.Select(e => (string)e["id"]).ToArray();

    // ---- ids ----

    [Fact]
    public void AnIdIsTheFirstSixBytesOfSha256OfThePathAsTwelveHexCharacters()
    {
        var expected = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Alpha.ToLowerInvariant())).AsSpan(0, 6)).ToLowerInvariant();
        Assert.Equal(expected, PhoneApps.Id(Alpha));
        Assert.Matches("^[0-9a-f]{12}$", PhoneApps.Id(Alpha));
    }

    [Fact]
    public void TheIdIgnoresCaseAndDiffersPerPath()
    {
        Assert.Equal(PhoneApps.Id(Alpha), PhoneApps.Id(Alpha.ToUpperInvariant()));
        Assert.NotEqual(PhoneApps.Id(Alpha), PhoneApps.Id(Beta));
    }

    // ---- the picked list ----

    [Fact]
    public void TheListHasIdNameAndRunningInThePickedOrder()
    {
        PickAll();
        processes.Apps.Add(new RunningApp(Beta, "Beta", true));
        // A different spelling of the same path still counts as running.
        processes.Apps.Add(new RunningApp(Gamma.ToUpperInvariant(), "Gamma", false));
        var list = apps.List();
        Assert.Equal(new[] { PhoneApps.Id(Alpha), PhoneApps.Id(Beta), PhoneApps.Id(Gamma) }, Ids(list));
        Assert.Equal(new object[] { "Alpha", "Beta", "Gamma" }, list.Select(e => e["name"]).ToArray());
        Assert.Equal(new object[] { false, true, true }, list.Select(e => e["running"]).ToArray());
        Assert.All(list, e => Assert.Equal(new[] { "id", "name", "running" }, e.Keys.ToArray()));
    }

    [Fact]
    public void AnAppThatNoLongerExistsIsLeftOutOfTheList()
    {
        PickAll();
        missing.Add(Beta);
        Assert.Equal(new[] { PhoneApps.Id(Alpha), PhoneApps.Id(Gamma) }, Ids(apps.List()));
        // Still picked: it is only hidden while the file is gone.
        Assert.Equal(3, apps.Picks().Count);
    }

    [Fact]
    public void AFailingProcessListStillAnswersTheListAsNotRunning()
    {
        PickAll();
        processes.Throws = true;
        var list = apps.List();
        Assert.Equal(3, list.Count);
        Assert.All(list, e => Assert.Equal(false, e["running"]));
        Assert.Empty(apps.Others());
    }

    [Fact]
    public void PicksAreKeptOnceAndSurviveARestart()
    {
        apps.SetPicks(new[] { Pick("Alpha", Alpha), Pick("Alpha again", Alpha.ToUpperInvariant()), Pick("Beta", Beta) });
        Assert.Equal(new[] { "Alpha", "Beta" }, apps.Picks().Select(p => p.Name).ToArray());
        var again = new PhoneApps(new Settings(Path.Combine(folder, "settings")), processes, actions, icons);
        Assert.Equal(new[] { "Alpha", "Beta" }, again.Picks().Select(p => p.Name).ToArray());
    }

    [Fact]
    public void ADamagedStoredListIsEmptyNotAnError()
    {
        settings.Set(PhoneApps.SettingsKey, "not a list");
        Assert.Empty(apps.Picks());
        Assert.Empty(apps.List());
    }

    // ---- the other apps ----

    [Fact]
    public void OtherAppsAreWindowedAppsNotOnTheListAndNotLidAwakeItself()
    {
        apps.SetPicks(new[] { Pick("Alpha", Alpha) });
        processes.Apps.Add(new RunningApp(Alpha, "Alpha", true));           // picked: not "other"
        processes.Apps.Add(new RunningApp(Beta, "Beta", true));             // other
        processes.Apps.Add(new RunningApp(Beta, "Beta again", true));       // same exe: once
        processes.Apps.Add(new RunningApp(Gamma, "Gamma", false));          // no window
        processes.Apps.Add(new RunningApp(Delta, "Lid Awake", true, IsSelf: true));
        var others = apps.Others();
        var only = Assert.Single(others);
        Assert.Equal(new[] { "id", "name" }, only.Keys.ToArray());
        Assert.Equal(PhoneApps.Id(Beta), only["id"]);
        Assert.Equal("Beta", only["name"]);
    }

    // ---- one cached scan ----

    [Fact]
    public void TwoStatusBuildsWithinTheCacheTimeMakeOneScan()
    {
        PickAll();
        processes.Apps.Add(new RunningApp(Beta, "Beta", true));
        var info = new StatusInfo(new FakeReadings(), apps: apps);
        info.Build(true);
        now += PhoneApps.ScanTtlMilliseconds - 1;
        info.Build(true);
        apps.IconPng(PhoneApps.Id(Delta), 256);   // the icon route shares the same look
        Assert.Equal(1, processes.Scans);
    }

    [Fact]
    public void OneStatusBuildIsOneScanEvenForBothLists()
    {
        PickAll();
        new StatusInfo(new FakeReadings(), apps: apps).Build(true);
        Assert.Equal(1, processes.Scans);
        now += PhoneApps.ScanTtlMilliseconds;   // the cache has run out
        new StatusInfo(new FakeReadings(), apps: apps).Build(true);
        Assert.Equal(2, processes.Scans);
    }

    [Fact]
    public void OpenFocusAndQuitMakeTheNextStatusLookAgain()
    {
        PickAll();
        Assert.Equal(false, apps.List()[0]["running"]);
        Assert.Equal(1, processes.Scans);
        processes.Apps.Add(new RunningApp(Alpha, "Alpha", true));
        Assert.Equal(false, apps.List()[0]["running"]);   // still the old look
        Assert.Equal(1, processes.Scans);
        actions.WithWindow.Add(Alpha);
        Assert.True(apps.Open(PhoneApps.Id(Alpha), "iPhone"));   // decided on the old look, then forgets it
        Assert.Equal(true, apps.List()[0]["running"]);
        Assert.Equal(2, processes.Scans);

        apps.List();
        Assert.Equal(2, processes.Scans);
        apps.Focus(PhoneApps.Id(Alpha), "hide", "iPhone");
        apps.List();
        Assert.Equal(3, processes.Scans);
        apps.Quit(PhoneApps.Id(Alpha), "iPhone");
        apps.List();
        Assert.Equal(4, processes.Scans);
    }

    // ---- order ----

    [Fact]
    public void ReorderFollowsTheIdsIgnoresUnknownOnesAndKeepsTheRestAtTheEnd()
    {
        PickAll();
        apps.Reorder(new[] { PhoneApps.Id(Gamma), "unknown", PhoneApps.Id(Alpha), PhoneApps.Id(Gamma) });
        Assert.Equal(new[] { "Gamma", "Alpha", "Beta" }, apps.Picks().Select(p => p.Name).ToArray());
        apps.Reorder(Array.Empty<string>());
        Assert.Equal(new[] { "Gamma", "Alpha", "Beta" }, apps.Picks().Select(p => p.Name).ToArray());
    }

    // ---- open ----

    [Fact]
    public void OpeningAPickedAppStartsItAndLogsIt()
    {
        PickAll();
        Assert.True(apps.Open(PhoneApps.Id(Alpha), "iPhone"));
        Assert.Equal(new[] { "start " + Alpha }, actions.Calls.ToArray());
        Assert.Contains("Opened Alpha from the phone · iPhone", log.Recent(1)[0]);
    }

    [Fact]
    public void OpeningAnAppThatIsAlreadyOpenFrontsItInsteadOfStartingItAgain()
    {
        PickAll();
        processes.Apps.Add(new RunningApp(Alpha, "Alpha", true));
        actions.WithWindow.Add(Alpha);
        Assert.True(apps.Open(PhoneApps.Id(Alpha), "iPhone"));
        Assert.Equal(new[] { "focus " + Alpha }, actions.Calls.ToArray());
    }

    [Fact]
    public void OpeningStartsItWhenTheOpenAppHasNoWindowToFront()
    {
        PickAll();
        processes.Apps.Add(new RunningApp(Alpha, "Alpha", true));  // listed with a window, but fronting fails
        Assert.True(apps.Open(PhoneApps.Id(Alpha), "iPhone"));
        Assert.Equal(new[] { "focus " + Alpha, "start " + Alpha }, actions.Calls.ToArray());
    }

    [Fact]
    public void OpeningWithAnIdNotOnTheListDoesNothing()
    {
        PickAll();
        processes.Apps.Add(new RunningApp(Delta, "Delta", true));
        Assert.False(apps.Open(PhoneApps.Id(Delta), "iPhone"));   // running but not picked
        Assert.False(apps.Open("", "iPhone"));
        Assert.False(apps.Open("nonsense", "iPhone"));
        Assert.Empty(actions.Calls);
    }

    [Fact]
    public void APathIsNeverAnId()
    {
        PickAll();
        processes.Apps.Add(new RunningApp(Delta, "Delta", true));
        foreach (var path in new[] { Alpha, Delta, @"C:\Windows\System32\cmd.exe", "..\\..\\evil.exe", "alpha.exe" })
        {
            Assert.False(apps.Open(path, "iPhone"));
            Assert.False(apps.Focus(path, "focus", "iPhone"));
            Assert.False(apps.Focus(path, "quit", "iPhone"));
            Assert.Null(apps.Quit(path, "iPhone"));
            Assert.Null(apps.IconPng(path, 256));
        }
        Assert.Empty(actions.Calls);
        Assert.Empty(icons.Asked);
    }

    [Fact]
    public void AStartThatFailsIsLoggedAndStillAnswersDone()
    {
        PickAll();
        actions.StartThrows = true;
        Assert.True(apps.Open(PhoneApps.Id(Alpha), "iPhone"));
        Assert.Contains("Couldn't open Alpha", log.Recent(1)[0]);
    }

    // ---- focus ----

    [Fact]
    public void FocusHideAndQuitWorkOnAnyRunningAppAndAreLogged()
    {
        processes.Apps.Add(new RunningApp(Delta, "Delta", true));
        actions.WithWindow.Add(Delta);
        actions.QuitAnswers[Delta] = QuitResult.Asked;
        var id = PhoneApps.Id(Delta);
        Assert.True(apps.Focus(id, "focus", "iPhone"));
        Assert.True(apps.Focus(id, "hide", "iPhone"));
        Assert.True(apps.Focus(id, "quit", "iPhone"));
        Assert.Equal(new[] { "focus " + Delta, "hide " + Delta, "quit " + Delta }, actions.Calls.ToArray());
        var lines = log.Recent(3);
        Assert.Contains("Quit Delta from the phone · iPhone", lines[0]);
        Assert.Contains("Hid Delta from the phone · iPhone", lines[1]);
        Assert.Contains("Brought Delta to the front from the phone · iPhone", lines[2]);
    }

    [Fact]
    public void FocusIsRefusedForAnAppThatIsNotRunningOrCannotBeShown()
    {
        PickAll();
        Assert.False(apps.Focus(PhoneApps.Id(Alpha), "focus", "iPhone"));   // picked but not running
        Assert.Empty(actions.Calls);
        processes.Apps.Add(new RunningApp(Alpha, "Alpha", false));
        Assert.False(apps.Focus(PhoneApps.Id(Alpha), "focus", "iPhone"));   // running, no window
        Assert.False(apps.Focus(PhoneApps.Id(Alpha), "quit", "iPhone"));    // no window to ask
    }

    [Fact]
    public void AProcessWithNoWindowCannotBeReachedByItsId()
    {
        processes.Apps.Add(new RunningApp(@"C:\Windows\explorer.exe", "explorer", false));
        actions.WithWindow.Add(@"C:\Windows\explorer.exe");
        actions.QuitAnswers[@"C:\Windows\explorer.exe"] = QuitResult.Asked;
        var id = PhoneApps.Id(@"C:\Windows\explorer.exe");
        Assert.False(apps.Focus(id, "focus", "iPhone"));
        Assert.False(apps.Focus(id, "hide", "iPhone"));
        Assert.False(apps.Focus(id, "quit", "iPhone"));
        Assert.Null(apps.IconPng(id, 256));
        Assert.Empty(actions.Calls);
        Assert.Empty(icons.Asked);
        Assert.Empty(apps.Others());
    }

    [Fact]
    public void LidAwakeItselfCannotBeFrontedHiddenOrQuitFromThePhone()
    {
        processes.Apps.Add(new RunningApp(Delta, "Lid Awake", true, IsSelf: true));
        actions.WithWindow.Add(Delta);
        var id = PhoneApps.Id(Delta);
        Assert.False(apps.Focus(id, "focus", "iPhone"));
        Assert.False(apps.Focus(id, "quit", "iPhone"));
        Assert.Empty(actions.Calls);
        Assert.Null(apps.IconPng(id, 256));
    }

    // ---- quit ----

    [Fact]
    public void QuitAsksAPickedAppAndLogsIt()
    {
        PickAll();
        actions.QuitAnswers[Alpha] = QuitResult.Asked;
        Assert.True(apps.Quit(PhoneApps.Id(Alpha), "iPhone"));
        Assert.Equal(new[] { "quit " + Alpha }, actions.Calls.ToArray());
        Assert.Contains("Quit Alpha from the phone · iPhone", log.Recent(1)[0]);
    }

    [Fact]
    public void QuitIsNullForAnAppNotOnTheListAndFalseWhenItCannotBeAsked()
    {
        PickAll();
        processes.Apps.Add(new RunningApp(Delta, "Delta", true));
        Assert.Null(apps.Quit(PhoneApps.Id(Delta), "iPhone"));              // not on the list
        Assert.False(apps.Quit(PhoneApps.Id(Beta), "iPhone"));              // not open
        actions.QuitAnswers[Gamma] = QuitResult.NoWindow;
        Assert.False(apps.Quit(PhoneApps.Id(Gamma), "iPhone"));             // running, nothing to close
        Assert.DoesNotContain(actions.Calls, c => c.Contains(Delta));
        Assert.Empty(log.Recent(5));
    }

    // ---- icons ----

    [Fact]
    public void IconsAreServedForPickedAndRunningAppsOnlyAndAreCached()
    {
        PickAll();
        processes.Apps.Add(new RunningApp(Delta, "Delta", true));
        Assert.Equal(FakeAppIcons.Image, apps.IconPng(PhoneApps.Id(Alpha), 256));
        Assert.Equal(FakeAppIcons.Image, apps.IconPng(PhoneApps.Id(Delta), 256));
        Assert.Null(apps.IconPng("0123456789ab", 256));
        Assert.Null(apps.IconPng("", 256));
        apps.IconPng(PhoneApps.Id(Alpha), 256);
        Assert.Equal(2, icons.Asked.Count);   // the second Alpha came from the cache
    }

    [Fact]
    public void IconSizeIs512ForFiveHundredTwelveOrMoreAnd256Otherwise()
    {
        PickAll();
        var id = PhoneApps.Id(Alpha);
        apps.IconPng(id, 512);
        apps.IconPng(id, 2000);
        apps.IconPng(id, 100);
        apps.IconPng(id, -5);
        apps.IconPng(id, 0);
        Assert.Equal(new[] { 512, 256 }, icons.Asked.Select(a => a.Size).ToArray());   // each size once
    }

    // ---- StatusInfo ----

    [Fact]
    public void StatusCarriesTheListsAndTheAppsFeatureOnlyWithAnAppList()
    {
        PickAll();
        processes.Apps.Add(new RunningApp(Beta, "Beta", true));
        processes.Apps.Add(new RunningApp(Delta, "Delta", true));
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new StatusInfo(new FakeReadings(), apps: apps).Build(true)));
        var s = doc.RootElement;
        var list = s.GetProperty("apps").EnumerateArray().ToArray();
        Assert.Equal(3, list.Length);
        Assert.Equal(PhoneApps.Id(Beta), list[1].GetProperty("id").GetString());
        Assert.True(list[1].GetProperty("running").GetBoolean());
        Assert.False(list[0].GetProperty("running").GetBoolean());
        var other = Assert.Single(s.GetProperty("otherApps").EnumerateArray().ToArray());
        Assert.Equal("Delta", other.GetProperty("name").GetString());
        Assert.Contains("apps", s.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        StatusInfoTests.AssertEveryKeyWithItsKind(s);

        using var plain = JsonDocument.Parse(JsonSerializer.Serialize(new StatusInfo(new FakeReadings()).Build(true)));
        Assert.DoesNotContain("apps", plain.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.Empty(plain.RootElement.GetProperty("apps").EnumerateArray());
    }

    [Fact]
    public void ControlIsListedAfterApps()
    {
        var control = new RemoteControl(settings, () => true, new InputInjector(new FakeInput()), new FakePower());
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new StatusInfo(new FakeReadings(), apps: apps) { Control = control }.Build(true)));
        Assert.Equal(new[] { "limits", "history", "memory", "passkeys", "push", "apps", "control" },
            doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetString()).ToArray());
    }

    [Fact]
    public void AFailingAppListNeverBreaksTheStatus()
    {
        settings.Set(PhoneApps.SettingsKey, "not a list");
        var bad = new PhoneApps(settings, processes, actions, icons);
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new StatusInfo(new FakeReadings(), apps: bad).Build(true)));
        Assert.Empty(doc.RootElement.GetProperty("apps").EnumerateArray());
    }

    // ---- the Start menu scan, with a fake shortcut resolver ----

    sealed class FakeResolver : IShortcutResolver
    {
        public readonly Dictionary<string, string?> Targets = new(StringComparer.OrdinalIgnoreCase);
        public string? Target(string shortcutPath) =>
            Targets.TryGetValue(Path.GetFileName(shortcutPath), out var t) ? t : throw new InvalidOperationException("unreadable shortcut");
    }

    [Fact]
    public void TheScanListsExeShortcutsFromBothFoldersOnceEachAndSkipsTheRest()
    {
        var user = Path.Combine(folder, "user");
        var common = Path.Combine(folder, "common");
        Directory.CreateDirectory(Path.Combine(user, "Tools"));
        Directory.CreateDirectory(common);
        var resolver = new FakeResolver();
        void Link(string dir, string name, string? target)
        {
            File.WriteAllText(Path.Combine(dir, name), "");
            resolver.Targets[name] = target;
        }
        Link(user, "Zeta.lnk", Alpha);
        Link(Path.Combine(user, "Tools"), "Beta Tool.lnk", Beta);
        Link(common, "Alpha From All Users.lnk", Alpha);        // same exe: the user's one wins
        Link(common, "Gamma.lnk", Gamma);
        Link(common, "Uninstall Gamma.lnk", Delta);             // an uninstaller by name
        Link(common, "Cleanup.lnk", @"C:\Apps\Cleanup\unins000.exe");   // an uninstaller by exe
        Link(common, "Manual.lnk", @"C:\Apps\Manual\readme.pdf");       // not an exe
        Link(common, "Gone.lnk", @"C:\Apps\Gone\gone.exe");     // the exe is missing
        Link(common, "Empty.lnk", null);                        // no target
        File.WriteAllText(Path.Combine(common, "Broken.lnk"), "");      // the resolver throws
        File.WriteAllText(Path.Combine(common, "notes.txt"), "");
        var gone = @"C:\Apps\Gone\gone.exe";
        var scanner = new StartMenuScanner(resolver, new[] { user, common, Path.Combine(folder, "missing") },
            exists: exe => !string.Equals(exe, gone, StringComparison.OrdinalIgnoreCase));
        var found = scanner.Scan();
        Assert.Equal(new[] { "Beta Tool", "Gamma", "Zeta" }, found.Select(a => a.Name).ToArray());
        Assert.Equal(new[] { Beta, Gamma, Alpha }, found.Select(a => a.Exe).ToArray());
    }

    // ---- the real Windows pieces (the CI runs on Windows) ----

    static string Cmd => Path.Combine(Environment.SystemDirectory, "cmd.exe");

    [Theory]
    [InlineData(256)]
    [InlineData(512)]
    public void TheRealIconIsAPngOfTheAskedSize(int size)
    {
        var png = new WindowsIcons().Png(Cmd, size);
        Assert.NotNull(png);
        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png!.Take(8).ToArray());
        using var image = System.Drawing.Image.FromStream(new MemoryStream(png));
        Assert.Equal(size, image.Width);
        Assert.Equal(size, image.Height);
    }

    [Fact]
    public void TheRealIconOfAMissingFileIsNull()
    {
        Assert.Null(new WindowsIcons().Png(@"C:\definitely\not\here.exe", 256));
    }

    [Fact]
    public void TheRealResolverReadsWhereAShortcutPoints()
    {
        Directory.CreateDirectory(folder);
        var link = Path.Combine(folder, "Command Prompt.lnk");
        var type = Type.GetTypeFromProgID("WScript.Shell")!;
        dynamic shell = Activator.CreateInstance(type)!;
        dynamic shortcut = shell.CreateShortcut(link);
        shortcut.TargetPath = Cmd;
        shortcut.Save();
        using (var resolver = new ShortcutResolver())
        {
            Assert.Equal(Cmd, resolver.Target(link), ignoreCase: true);
            Assert.Equal(Cmd, resolver.Target(link), ignoreCase: true);   // the same shell, used again
            Assert.Null(resolver.Target(Path.Combine(folder, "nothing.lnk")));
        }
        // And the scanner built on it.
        var found = new StartMenuScanner(new ShortcutResolver(), new[] { folder }).Scan();
        var app = Assert.Single(found);
        Assert.Equal("Command Prompt", app.Name);
        Assert.Equal(Cmd, app.Exe, ignoreCase: true);
    }

    [Fact]
    public void TheRealProcessListHasTheTestProgramMarkedAsSelf()
    {
        var running = new ProcessSource().Running();
        var self = running.Where(r => r.IsSelf).ToArray();
        Assert.NotEmpty(self);
        Assert.Equal(Environment.ProcessPath, self[0].Exe, ignoreCase: true);
    }

    [Fact]
    public async Task TheRealStartRefusesAnythingButAnExistingExe()
    {
        Directory.CreateDirectory(folder);
        var script = Path.Combine(folder, "run.bat");
        File.WriteAllText(script, "@echo off\r\necho x > " + Path.Combine(folder, "ran.txt") + "\r\n");
        var document = Path.Combine(folder, "notes.txt");
        File.WriteAllText(document, "hi");
        var actionsReal = new WindowActions();
        Assert.Throws<InvalidOperationException>(() => actionsReal.Start(script));
        Assert.Throws<InvalidOperationException>(() => actionsReal.Start(document));
        Assert.Throws<InvalidOperationException>(() => actionsReal.Start(@"C:\definitely\not\here.exe"));
        await Task.Delay(500);
        Assert.False(File.Exists(Path.Combine(folder, "ran.txt")));   // the script never ran
    }

    [Fact]
    public void TheRealQuitOnAProgramThatIsNotRunningIsNotOpen()
    {
        Assert.Equal(QuitResult.NotOpen, new WindowActions().Quit(@"C:\definitely\not\here.exe"));
        Assert.False(new WindowActions().Focus(@"C:\definitely\not\here.exe"));
        Assert.False(new WindowActions().Hide(@"C:\definitely\not\here.exe"));
    }

    [Fact]
    public async Task TheRealQuitOnAProgramWithNoWindowAsksAndNeverKillsIt()
    {
        var ping = Path.Combine(Environment.SystemDirectory, "PING.EXE");
        using var process = Process.Start(new ProcessStartInfo(ping, "-n 30 127.0.0.1")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
        })!;
        try
        {
            await Task.Delay(300);
            Assert.Equal(QuitResult.NoWindow, new WindowActions().Quit(ping));
            await Task.Delay(300);
            Assert.False(process.HasExited);   // asked politely only: nothing killed it
        }
        finally
        {
            try { process.Kill(); }
            catch (Exception) { }
        }
    }
}
