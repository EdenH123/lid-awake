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

    // A powercfg that remembers a lid action, answers queries like the real one and records
    // the commands that change something. Commands are matched by their exact first word.
    sealed class FakePowercfg
    {
        public int Ac, Dc;
        public bool HasLid = true;
        public bool FailWrites;    // value-set commands fail, like a powercfg that exits non-zero
        public bool FailQueries;   // /query fails
        public bool IgnoreWrites;  // value-set commands succeed but change nothing
        public readonly List<string> Writes = new();       // /setacvalueindex and /setdcvalueindex
        public readonly List<string> Activations = new();  // /setactive

        public string Run(string args)
        {
            var parts = args.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            var command = parts.Length > 0 ? parts[0] : "";
            if (command == "/query")
            {
                if (FailQueries) throw new InvalidOperationException("powercfg failed (fake)");
                if (!HasLid) return SchemeLine;
                return RealLaptopOutput.Substring(0, RealLaptopOutput.IndexOf("    Current AC", StringComparison.Ordinal)) +
                    $"    Current AC Power Setting Index: 0x{Ac:x8}\n" +
                    $"    Current DC Power Setting Index: 0x{Dc:x8}\n";
            }
            if ((command == "/setacvalueindex" || command == "/setdcvalueindex") && int.TryParse(parts[^1], out var value))
            {
                if (FailWrites) throw new InvalidOperationException("powercfg failed (fake)");
                Writes.Add(args);
                if (IgnoreWrites) return "";
                if (command == "/setacvalueindex") Ac = value; else Dc = value;
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
    readonly List<string> folders = new();
    readonly Settings settings;
    readonly FakePowercfg fake = new() { Ac = 1, Dc = 2 };

    public KeepAwakeTests(ITestOutputHelper output)
    {
        this.output = output;
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        folders.Add(folder);
        settings = new Settings(folder);
        // Last, so a constructor that throws cannot leave the fake installed.
        KeepAwake.Powercfg = fake.Run;
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

        KeepAwake.Off(settings);

        Assert.False(KeepAwake.IsHolding);
        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));
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
        settings.Set("savedLidAc", 9);

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
