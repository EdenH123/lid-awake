using LidAwake;
using Xunit;
using Xunit.Abstractions;

// KeepAwake keeps its state in statics (the powercfg runner, the idle-sleep holder), so every
// test that touches it is in this one class: xUnit runs the tests of a class one at a time.
// Most tests use a fake powercfg; only RealMachineIsLeftAsItWas runs the real one.
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
    // every call that is not a query.
    sealed class FakePowercfg
    {
        public int Ac, Dc;
        public bool HasLid = true;
        public readonly List<string> Writes = new();

        public string Run(string args)
        {
            if (args.StartsWith("/query"))
            {
                if (!HasLid) return SchemeLine;
                return RealLaptopOutput.Substring(0, RealLaptopOutput.IndexOf("    Current AC", StringComparison.Ordinal)) +
                    $"    Current AC Power Setting Index: 0x{Ac:x8}\n" +
                    $"    Current DC Power Setting Index: 0x{Dc:x8}\n";
            }
            Writes.Add(args);
            var last = int.Parse(args.Split(' ')[^1]);
            if (args.StartsWith("/setacvalueindex")) Ac = last;
            return "";
        }
    }

    readonly ITestOutputHelper output;
    readonly Func<string, string> originalPowercfg = KeepAwake.Powercfg;
    readonly List<string> folders = new();
    readonly Settings settings;
    readonly FakePowercfg fake = new() { Ac = 1, Dc = 2 };

    public KeepAwakeTests(ITestOutputHelper output)
    {
        this.output = output;
        KeepAwake.Powercfg = fake.Run;
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        folders.Add(folder);
        settings = new Settings(folder);
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
        KeepAwake.On(settings);

        Assert.Equal((0, 0), (fake.Ac, fake.Dc));
        Assert.Equal<int?>(1, settings.Get<int?>("savedLidAc"));
        Assert.Equal<int?>(2, settings.Get<int?>("savedLidDc"));

        KeepAwake.Off(settings);

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
        Assert.Equal((1, 2), (fake.Ac, fake.Dc));
    }

    [Fact]
    public void NoLidSettingChangesAndSavesNothing()
    {
        fake.HasLid = false;

        KeepAwake.On(settings);

        Assert.Empty(fake.Writes);
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));

        KeepAwake.Off(settings);

        Assert.Empty(fake.Writes);
    }

    [Fact]
    public void RealMachineIsLeftAsItWas()
    {
        KeepAwake.Powercfg = KeepAwake.RunPowercfg;
        output.WriteLine(KeepAwake.RunPowercfg("/query SCHEME_CURRENT 4f971e89-eebd-4455-a8de-9e59040e7347 5ca83367-6e45-459f-a27b-476b1d01c936"));
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
