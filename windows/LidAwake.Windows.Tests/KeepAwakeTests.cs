using System.Diagnostics;
using LidAwake;
using Xunit;
using Xunit.Abstractions;

// KeepAwake keeps its state in statics (the powercfg runner, the idle-sleep holder), so every
// test that touches it is in this one class: xUnit runs the tests of a class one at a time.
// Most tests use a fake powercfg; only RealMachineIsLeftAsItWas runs the real one.
[Collection("KeepAwake")]
public class KeepAwakeTests : IDisposable
{
    const string SchemeLine = "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\n";

    const string RealLaptopOutput =
        "Power Scheme GUID: 381b4222-f694-41f0-9685-ff5bb260df2e  (Balanced)\n" +
        "  GUID Alias: SCHEME_BALANCED\n" +
        "  Subgroup GUID: 4f971e89-eebd-4455-a8de-9e59040e7347  (Power buttons and lid)\n" +
        "    GUID Alias: SUB_BUTTONS\n" +
        "    Power Setting GUID: 5ca83367-6e45-459f-a27b-476b1d01c936  (Lid close action)\n" +
        "      GUID Alias: LIDACTION\n" +
        "      Possible Setting Index: 000\n" +
        "      Possible Setting Friendly Name: Do nothing\n" +
        "      Possible Setting Index: 001\n" +
        "      Possible Setting Friendly Name: Sleep\n" +
        "      Possible Setting Index: 002\n" +
        "      Possible Setting Friendly Name: Hibernate\n" +
        "      Possible Setting Index: 003\n" +
        "      Possible Setting Friendly Name: Shut down\n" +
        "    Current AC Power Setting Index: 0x00000001\n" +
        "    Current DC Power Setting Index: 0x00000002\n";

    const string Balanced = "381b4222-f694-41f0-9685-ff5bb260df2e";
    const string PowerSaver = "a1841308-3541-4fab-bc81-f71556f20b4a";

    // A powercfg that remembers a lid action per power plan, answers queries like the real one
    // and records the commands that change something. Commands are matched by their exact first
    // word. Ac and Dc are the values of the active plan. A plan is named by its GUID or by
    // SCHEME_CURRENT; naming one that does not exist fails, like the real powercfg.
    sealed class FakePowercfg
    {
        public string Active = Balanced;
        public readonly Dictionary<string, (int ac, int dc)> Schemes = new() { [Balanced] = (0, 0) };
        public bool HasLid = true;
        public bool FailWrites;    // value-set commands fail, like a powercfg that exits non-zero
        public bool FailQueries;   // /query fails
        public bool IgnoreWrites;  // value-set commands succeed but change nothing
        public bool NoActiveScheme;  // /getactivescheme prints no plan
        public bool FailList;        // /list fails
        public readonly List<string> Writes = new();       // /setacvalueindex and /setdcvalueindex
        public readonly List<string> Activations = new();  // /setactive

        public int Ac { get => Schemes[Active].ac; set => Schemes[Active] = (value, Schemes[Active].dc); }
        public int Dc { get => Schemes[Active].dc; set => Schemes[Active] = (Schemes[Active].ac, value); }

        string Resolve(string name)
        {
            if (name == "SCHEME_CURRENT") return Active;
            if (!Schemes.ContainsKey(name)) throw new InvalidOperationException("powercfg: no such power plan (fake)");
            return name;
        }

        public string Run(string args)
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = parts.Length > 0 ? parts[0] : "";
            if (command == "/getactivescheme") return NoActiveScheme ? "" : $"Power Scheme GUID: {Active}  (Balanced)\n";
            if (command == "/list")
            {
                if (FailList) throw new InvalidOperationException("powercfg failed (fake)");
                return "Existing Power Schemes (* Active)\n-----------------------------------\n" +
                    string.Concat(Schemes.Keys.Select(g => $"Power Scheme GUID: {g}  (Plan){(g == Active ? " *" : "")}\n"));
            }
            if (command == "/query")
            {
                if (FailQueries) throw new InvalidOperationException("powercfg failed (fake)");
                var scheme = Resolve(parts[1]);
                if (!HasLid) return SchemeLine;
                var (ac, dc) = Schemes[scheme];
                return RealLaptopOutput.Substring(0, RealLaptopOutput.IndexOf("    Current AC", StringComparison.Ordinal)) +
                    $"    Current AC Power Setting Index: 0x{ac:x8}\n" +
                    $"    Current DC Power Setting Index: 0x{dc:x8}\n";
            }
            if ((command == "/setacvalueindex" || command == "/setdcvalueindex") && int.TryParse(parts[^1], out var value))
            {
                if (FailWrites) throw new InvalidOperationException("powercfg failed (fake)");
                var scheme = Resolve(parts[1]);
                Writes.Add(args);
                if (IgnoreWrites) return "";
                var (ac, dc) = Schemes[scheme];
                Schemes[scheme] = command == "/setacvalueindex" ? (value, dc) : (ac, value);
            }
            else if (command == "/setactive")
            {
                Activations.Add(args);
            }
            return "";
        }
    }

    readonly ITestOutputHelper output;
    readonly Func<string, string> originalPowercfg = KeepAwake.Powercfg;
    readonly Func<bool?> originalHasLid = KeepAwake.HasLid;
    readonly Action<string>? originalLog = KeepAwake.Log;
    readonly List<string> notes = new();  // what KeepAwake told the activity log
    readonly List<string> folders = new();
    readonly string machineFolder;
    readonly Settings settings;  // the machine-state settings (machine.json), which KeepAwake uses
    readonly FakePowercfg fake = new() { Ac = 1, Dc = 2 };

    public KeepAwakeTests(ITestOutputHelper output)
    {
        this.output = output;
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        folders.Add(folder);
        machineFolder = folder;
        settings = new Settings(folder, Settings.MachineFileName);
        // Last, so a constructor that throws cannot leave the fake installed.
        KeepAwake.Powercfg = fake.Run;
        KeepAwake.Log = notes.Add;
    }

    public void Dispose()
    {
        // Put the lid action back and release the idle-sleep holder, whatever the test did.
        try
        {
            KeepAwake.Off(settings);
        }
        finally
        {
            KeepAwake.Powercfg = originalPowercfg;
            KeepAwake.HasLid = originalHasLid;
            KeepAwake.Log = originalLog;
        }
        foreach (var folder in folders)
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    // A roaming settings.json of its own, as the app's other settings use.
    Settings NewRoaming()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        folders.Add(folder);
        return new Settings(folder);
    }

    [Fact]
    public void ParsesRealPowercfgOutput()
    {
        Assert.Equal((1, 2), KeepAwake.ParseLidAction(RealLaptopOutput)!.Value);
    }

    [Fact]
    public void ParseReturnsNullWithoutValues()
    {
        Assert.Null(KeepAwake.ParseLidAction(SchemeLine));
        Assert.Null(KeepAwake.ParseLidAction(""));
    }

    [Fact]
    public void OnSetsLidToDoNothingThenOffRestores()
    {
        Assert.Equal(LidResult.Changed, KeepAwake.On(settings));

        Assert.True(KeepAwake.IsHolding);
        Assert.Equal((0, 0), (fake.Ac, fake.Dc));
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));
        Assert.Equal(Balanced, settings.Get<string>("savedLidScheme"));

        KeepAwake.Off(settings);

        Assert.False(KeepAwake.IsHolding);
        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));
        Assert.Null(settings.Get<string>("savedLidScheme"));
    }

    [Fact]
    public void OnTwiceKeepsTheFirstSavedValues()
    {
        KeepAwake.On(settings);
        KeepAwake.On(settings);

        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));

        KeepAwake.Off(settings);

        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
    }

    [Fact]
    public void OffWithoutOnChangesNothing()
    {
        KeepAwake.Off(settings);

        Assert.Empty(fake.Writes);
        Assert.Empty(fake.Activations);
        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
    }

    [Fact]
    public void NoLidSettingChangesAndSavesNothing()
    {
        fake.HasLid = false;

        Assert.Equal(LidResult.NoLidSetting, KeepAwake.On(settings));

        Assert.True(KeepAwake.IsHolding);
        Assert.Empty(fake.Writes);
        Assert.Empty(fake.Activations);
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));

        KeepAwake.Off(settings);

        Assert.False(KeepAwake.IsHolding);
        Assert.Empty(fake.Writes);
        Assert.Empty(fake.Activations);
    }

    [Fact]
    public void ParsesOutputWithHebrewLabels()
    {
        var hebrew = RealLaptopOutput
            .Replace("Current AC Power Setting Index", "אינדקס הגדרת צריכת חשמל נוכחית של AC")
            .Replace("Current DC Power Setting Index", "אינדקס הגדרת צריכת חשמל נוכחית של DC");

        Assert.Equal((1, 2), KeepAwake.ParseLidAction(hebrew)!.Value);
    }

    [Fact]
    public void OnReadsFreshWhenOnlyOneSavedValueExists()
    {
        settings.Set("savedLidAc", 3);

        KeepAwake.On(settings);

        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));
        Assert.Equal((0, 0), (fake.Ac, fake.Dc));
    }

    [Fact]
    public void OnWhenWritesFailDoesNotThrowAndStillHolds()
    {
        fake.FailWrites = true;
        KeepAwake.HasLid = () => true;

        Assert.Equal(LidResult.Failed, KeepAwake.On(settings));

        Assert.True(KeepAwake.IsHolding);
        // The saved values stay, so a later Off can still restore.
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));
    }

    [Fact]
    public void OffWhenRestoreFailsKeepsSavedValuesAndReleasesTheHold()
    {
        KeepAwake.On(settings);
        fake.FailWrites = true;

        KeepAwake.Off(settings);

        Assert.False(KeepAwake.IsHolding);
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));
        Assert.Equal((0, 0), (fake.Ac, fake.Dc));

        // Once powercfg works again, the next Off finishes the job.
        fake.FailWrites = false;
        KeepAwake.Off(settings);

        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));
    }

    [Fact]
    public void OffKeepsSavedValuesWhenTheReadBackDoesNotMatch()
    {
        KeepAwake.On(settings);
        fake.IgnoreWrites = true;

        KeepAwake.Off(settings);

        Assert.False(KeepAwake.IsHolding);
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));

        fake.IgnoreWrites = false;
        KeepAwake.Off(settings);

        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
        Assert.Null(settings.Get<int?>("savedLidAc"));
    }

    [Fact]
    public void OnFailsWhenPowercfgCannotReadTheLid()
    {
        fake.FailQueries = true;
        KeepAwake.HasLid = () => true;

        Assert.Equal(LidResult.Failed, KeepAwake.On(settings));
        Assert.True(KeepAwake.IsHolding);
        Assert.Null(settings.Get<int?>("savedLidAc"));
    }

    [Fact]
    public void OnFailsWhenTheLidActionDoesNotChange()
    {
        // powercfg says yes, but something (a maker's power app) keeps the old lid action.
        fake.IgnoreWrites = true;
        KeepAwake.HasLid = () => true;

        Assert.Equal(LidResult.Failed, KeepAwake.On(settings));
        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
    }

    // A desktop PC: nothing to change, nothing to warn about.
    [Fact]
    public void AFailureOnAPcWithoutALidIsNoLidSetting()
    {
        fake.FailQueries = true;
        KeepAwake.HasLid = () => false;

        Assert.Equal(LidResult.NoLidSetting, KeepAwake.On(settings));
    }

    [Fact]
    public void OnNeverThrowsEvenWhenTheLidCheckThrows()
    {
        fake.FailWrites = true;
        KeepAwake.HasLid = () => throw new InvalidOperationException("boom");

        Assert.Equal(LidResult.Failed, KeepAwake.On(settings));
        Assert.True(KeepAwake.IsHolding);
    }

    // Signing out or shutting down while on (SessionEnd, called from the tray's SessionEnding).
    [Fact]
    public void SessionEndPutsTheLidBackRemovesServeAndLogs()
    {
        KeepAwake.On(settings);
        settings.Set(Tailnet.ServedKey, true);
        var commands = new List<string>();
        var tailnet = new Tailnet(settings, args => { lock (commands) commands.Add(args); return ""; });
        var logFolder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        folders.Add(logFolder);
        var log = new AccessLog(logFolder);

        SessionEnd.TurnOff(settings, () => tailnet.SetServed(false, 8787), log);

        Assert.False(KeepAwake.IsHolding);
        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
        Assert.Null(settings.Get<int?>("savedLidAc"));
        lock (commands) Assert.Equal(new[] { "serve --https=443 off" }, commands);
        Assert.False(settings.Get<bool?>(Tailnet.ServedKey));
        Assert.EndsWith("  " + SessionEnd.LogLine, Assert.Single(log.Recent(10)));
    }

    [Fact]
    public void SessionEndWaitsForTailscaleOnlyAFewSeconds()
    {
        Assert.True(SessionEnd.MaxWait <= TimeSpan.FromSeconds(3));
        KeepAwake.On(settings);
        settings.Set(Tailnet.ServedKey, true);
        var stuck = new ManualResetEventSlim(false);
        var tailnet = new Tailnet(settings, _ => { stuck.Wait(TimeSpan.FromSeconds(30)); return ""; });
        var watch = Stopwatch.StartNew();

        SessionEnd.TurnOff(settings, () => tailnet.SetServed(false, 8787), null, TimeSpan.FromMilliseconds(300));

        watch.Stop();
        stuck.Set();
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"took {watch.Elapsed}");
        // The lid part does not wait for Tailscale.
        Assert.False(KeepAwake.IsHolding);
        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
    }

    // The plan the values came from is where they go back, even if the owner (or a maker's power
    // app) switched plans while Lid Awake was on. The restore is also read back in that plan: if
    // it were read in the active one, the saved values would never be confirmed and cleared.
    [Fact]
    public void ValuesAreRestoredIntoTheSavedPlanWhileAnotherIsActive()
    {
        fake.Schemes[PowerSaver] = (3, 3);
        KeepAwake.On(settings);
        Assert.Equal(Balanced, settings.Get<string>("savedLidScheme"));
        fake.Active = PowerSaver;
        fake.Writes.Clear();
        fake.Activations.Clear();

        KeepAwake.Off(settings);

        Assert.Equal((1, 2), fake.Schemes[Balanced]);
        Assert.Equal((3, 3), fake.Schemes[PowerSaver]);
        Assert.Equal(PowerSaver, fake.Active);
        Assert.Equal(2, fake.Writes.Count);
        Assert.All(fake.Writes, w => Assert.Contains(Balanced, w));
        // Activating the current plan re-applies it; it never switches to another one.
        Assert.Equal(new[] { "/setactive SCHEME_CURRENT" }, fake.Activations);
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));
        Assert.Null(settings.Get<string>("savedLidScheme"));
    }

    [Fact]
    public void WithoutASavedPlanTheValuesGoBackIntoTheCurrentPlan()
    {
        // State from an older version, or from a PC that would not say which plan was active.
        fake.Ac = 0;
        fake.Dc = 0;
        settings.Set("savedLidAc", 1);
        settings.Set("savedLidDc", 2);

        KeepAwake.Off(settings);

        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
        Assert.Equal(2, fake.Writes.Count);
        Assert.All(fake.Writes, w => Assert.Contains("SCHEME_CURRENT", w));
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));
    }

    [Fact]
    public void WhenWindowsWontNameThePlanOnStillWorksAndOffUsesTheCurrentPlan()
    {
        fake.NoActiveScheme = true;

        Assert.Equal(LidResult.Changed, KeepAwake.On(settings));
        Assert.Null(settings.Get<string>("savedLidScheme"));
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));

        fake.Writes.Clear();
        KeepAwake.Off(settings);

        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
        Assert.All(fake.Writes, w => Assert.Contains("SCHEME_CURRENT", w));
    }

    // The plan Lid Awake changed was deleted: nothing may be written (not even into the plan that
    // is active now), the saved values are forgotten, and the log says so.
    [Fact]
    public void APlanThatIsGoneSinceIsNotWrittenToAndTheValuesAreForgotten()
    {
        fake.Schemes[PowerSaver] = (3, 3);
        KeepAwake.On(settings);
        fake.Active = PowerSaver;
        fake.Schemes.Remove(Balanced);
        fake.Writes.Clear();
        fake.Activations.Clear();

        KeepAwake.Off(settings);

        Assert.Empty(fake.Writes);
        Assert.Empty(fake.Activations);
        Assert.Equal((3, 3), fake.Schemes[PowerSaver]);
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));
        Assert.Null(settings.Get<string>("savedLidScheme"));
        Assert.Equal(new[] { KeepAwake.PlanGoneNote }, notes);
        Assert.False(KeepAwake.IsHolding);
    }

    [Fact]
    public void WhenThePlansCannotBeListedTheSavedPlanIsTried()
    {
        fake.Schemes[PowerSaver] = (3, 3);
        KeepAwake.On(settings);
        fake.Active = PowerSaver;
        fake.FailList = true;
        fake.Writes.Clear();

        KeepAwake.Off(settings);

        Assert.Equal((1, 2), fake.Schemes[Balanced]);
        Assert.Equal((3, 3), fake.Schemes[PowerSaver]);
        Assert.All(fake.Writes, w => Assert.Contains(Balanced, w));
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Empty(notes);
    }

    [Fact]
    public void WhenThePlansCannotBeListedAndTheSavedPlanIsGoneTheValuesStay()
    {
        fake.Schemes[PowerSaver] = (3, 3);
        KeepAwake.On(settings);
        fake.Active = PowerSaver;
        fake.Schemes.Remove(Balanced);
        fake.FailList = true;

        KeepAwake.Off(settings);

        // The write into the missing plan failed: keep everything, change nothing else.
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));
        Assert.Equal(Balanced, settings.Get<string>("savedLidScheme"));
        Assert.Equal((3, 3), fake.Schemes[PowerSaver]);
    }

    // The plan that gets changed is always the plan that gets restored.
    [Fact]
    public void OnWithValuesAlreadySavedWritesIntoTheSavedPlan()
    {
        fake.Schemes[PowerSaver] = (3, 3);
        KeepAwake.HasLid = () => true;
        KeepAwake.On(settings);  // Balanced, saved as (1, 2)
        fake.Active = PowerSaver;
        fake.Writes.Clear();

        var result = KeepAwake.On(settings);

        Assert.Equal(2, fake.Writes.Count);
        Assert.All(fake.Writes, w => Assert.Contains(Balanced, w));
        Assert.Equal((3, 3), fake.Schemes[PowerSaver]);
        // The plan in use still closes the lid to sleep, which is told.
        Assert.Equal(LidResult.Failed, result);
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal(Balanced, settings.Get<string>("savedLidScheme"));

        KeepAwake.Off(settings);

        Assert.Equal((1, 2), fake.Schemes[Balanced]);
        Assert.Equal((3, 3), fake.Schemes[PowerSaver]);
    }

    [Fact]
    public void OnWithValuesSavedForAGonePlanStartsAgainFromTheCurrentPlan()
    {
        fake.Schemes[PowerSaver] = (1, 2);
        fake.Active = PowerSaver;
        fake.Schemes.Remove(Balanced);
        settings.Set("savedLidAc", 2);
        settings.Set("savedLidDc", 3);
        settings.Set("savedLidScheme", Balanced);  // not in the list of plans

        Assert.Equal(LidResult.Changed, KeepAwake.On(settings));

        Assert.Equal(PowerSaver, settings.Get<string>("savedLidScheme"));
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));
        Assert.Equal((0, 0), fake.Schemes[PowerSaver]);
        Assert.Contains(KeepAwake.PlanGoneNote, notes);

        KeepAwake.Off(settings);

        Assert.Equal((1, 2), fake.Schemes[PowerSaver]);
    }

    // Saved values that are not lid actions (0 to 3) are treated like a missing plan: nothing is
    // written, they are forgotten, and the log says so. The uninstaller does the same.
    [Theory]
    [InlineData(7, 2)]
    [InlineData(1, 4)]
    [InlineData(-1, 0)]
    [InlineData(1, -5)]
    public void OffWithSavedValuesOutsideZeroToThreeWritesNothingAndForgetsThem(int ac, int dc)
    {
        settings.Set("savedLidAc", ac);
        settings.Set("savedLidDc", dc);
        settings.Set("savedLidScheme", Balanced);
        fake.Ac = 0;
        fake.Dc = 0;

        KeepAwake.Off(settings);

        Assert.Empty(fake.Writes);
        Assert.Empty(fake.Activations);
        Assert.Equal((0, 0), fake.Schemes[Balanced]);
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));
        Assert.Null(settings.Get<string>("savedLidScheme"));
        Assert.Equal(new[] { KeepAwake.BadSavedNote }, notes);
    }

    [Fact]
    public void OnWithSavedValuesOutsideZeroToThreeForgetsThemAndSavesFreshOnes()
    {
        settings.Set("savedLidAc", 1);
        settings.Set("savedLidDc", 9);
        settings.Set("savedLidScheme", Balanced);

        Assert.Equal(LidResult.Changed, KeepAwake.On(settings));

        Assert.Contains(KeepAwake.BadSavedNote, notes);
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));  // read from the plan: Ac = 1
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));  // read from the plan: Dc = 2
        Assert.Equal(Balanced, settings.Get<string>("savedLidScheme"));
        Assert.Equal((0, 0), fake.Schemes[Balanced]);

        KeepAwake.Off(settings);

        Assert.Equal((1, 2), fake.Schemes[Balanced]);
        Assert.Null(settings.Get<int?>("savedLidAc"));
    }

    [Fact]
    public void ASavedPlanThatIsNotAGuidIsIgnored()
    {
        fake.Ac = 0;
        fake.Dc = 0;
        settings.Set("savedLidAc", 1);
        settings.Set("savedLidDc", 2);
        settings.Set("savedLidScheme", "SCHEME_MAX");

        KeepAwake.Off(settings);

        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
        Assert.All(fake.Writes, w => Assert.Contains("SCHEME_CURRENT", w));
    }

    // The saved values used to live in the roaming settings.json.
    [Fact]
    public void SavedValuesMoveFromTheRoamingSettingsToMachineJson()
    {
        var roaming = NewRoaming();
        roaming.Set("savedLidAc", 1);
        roaming.Set("savedLidDc", 2);
        roaming.Set("tailnetHost", "pc.ts.net");
        // A plan left in machine.json is not the plan of values that came with none.
        settings.Set("savedLidScheme", PowerSaver);
        fake.Ac = 0;
        fake.Dc = 0;

        KeepAwake.MigrateSavedLid(roaming, settings);

        Assert.Null(roaming.Get<int?>("savedLidAc"));
        Assert.Null(roaming.Get<int?>("savedLidDc"));
        Assert.Equal("pc.ts.net", roaming.Get<string>("tailnetHost"));
        var reread = new Settings(machineFolder, Settings.MachineFileName);
        Assert.Equal<int?>(1, reread.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, reread.Get<int?>("savedLidDc"));
        Assert.Null(reread.Get<string>("savedLidScheme"));

        // And the moved values are what the next Off puts back.
        KeepAwake.Off(settings);
        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
        Assert.Null(settings.Get<int?>("savedLidAc"));
    }

    [Fact]
    public void MigrationKeepsValuesAlreadyInMachineJson()
    {
        var roaming = NewRoaming();
        roaming.Set("savedLidAc", 3);
        roaming.Set("savedLidDc", 3);
        settings.Set("savedLidAc", 1);
        settings.Set("savedLidDc", 2);
        settings.Set("savedLidScheme", Balanced);

        KeepAwake.MigrateSavedLid(roaming, settings);

        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));
        Assert.Equal(Balanced, settings.Get<string>("savedLidScheme"));
        Assert.Null(roaming.Get<int?>("savedLidAc"));
        Assert.Null(roaming.Get<int?>("savedLidDc"));
    }

    [Fact]
    public void MigrationLeavesAHalfSavedPairAlone()
    {
        var roaming = NewRoaming();
        roaming.Set("savedLidAc", 1);

        KeepAwake.MigrateSavedLid(roaming, settings);

        Assert.Equal<int?>(1, roaming.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));
        // Nothing to move at all is fine too.
        KeepAwake.MigrateSavedLid(NewRoaming(), settings);
    }

    [Fact]
    public void RealMachineIsLeftAsItWas()
    {
        // This changes the real power plan of whoever runs it, so only the CI machine does.
        if (Environment.GetEnvironmentVariable("GITHUB_ACTIONS") != "true")
        {
            output.WriteLine("Skipped: runs only on GitHub Actions (GITHUB_ACTIONS is not \"true\").");
            return;
        }

        KeepAwake.Powercfg = KeepAwake.RunPowercfg;
        try
        {
            output.WriteLine(KeepAwake.RunPowercfg("/query SCHEME_CURRENT 4f971e89-eebd-4455-a8de-9e59040e7347 5ca83367-6e45-459f-a27b-476b1d01c936"));
        }
        catch (Exception e)
        {
            output.WriteLine($"powercfg /query failed: {e.Message}");
        }
        var before = KeepAwake.ReadLidAction();
        output.WriteLine($"Lid action before: {(before is null ? "not available" : before.Value.ToString())}");

        try
        {
            KeepAwake.On(settings);
        }
        finally
        {
            KeepAwake.Off(settings);
        }

        Assert.Equal<(int ac, int dc)?>(before, KeepAwake.ReadLidAction());
    }
}
