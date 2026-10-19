using LidAwake;
using Xunit;

// Every test that touches powercfg is in this one class: xUnit runs the tests of a class one at a
// time, and these change the machine's power plan.
public class KeepAwakeTests : IDisposable
{
    readonly List<string> folders = new();

    Settings NewSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        folders.Add(folder);
        return new Settings(folder);
    }

    public void Dispose()
    {
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

    static (int ac, int dc) ReadBefore()
    {
        var before = KeepAwake.ReadLidAction();
        Assert.NotNull(before);
        return before!.Value;
    }

    [Fact]
    public void ReadLidActionFindsValues()
    {
        var (ac, dc) = ReadBefore();
        Assert.InRange(ac, 0, 4);
        Assert.InRange(dc, 0, 4);
    }

    [Fact]
    public void OnSetsLidToDoNothingThenOffRestores()
    {
        var before = ReadBefore();
        var settings = NewSettings();
        try
        {
            KeepAwake.On(settings);

            Assert.Equal((0, 0), KeepAwake.ReadLidAction()!.Value);
            Assert.Equal<int?>(before.ac, settings.Get<int?>("savedLidAc"));
            Assert.Equal<int?>(before.dc, settings.Get<int?>("savedLidDc"));
        }
        finally
        {
            KeepAwake.Off(settings);
        }

        Assert.Equal(before, KeepAwake.ReadLidAction()!.Value);
        Assert.Null(settings.Get<int?>("savedLidAc"));
        Assert.Null(settings.Get<int?>("savedLidDc"));
    }

    [Fact]
    public void OnTwiceKeepsTheFirstSavedValues()
    {
        var before = ReadBefore();
        var settings = NewSettings();
        try
        {
            KeepAwake.On(settings);
            KeepAwake.On(settings);

            Assert.Equal<int?>(before.ac, settings.Get<int?>("savedLidAc"));
            Assert.Equal<int?>(before.dc, settings.Get<int?>("savedLidDc"));
        }
        finally
        {
            KeepAwake.Off(settings);
        }

        Assert.Equal(before, KeepAwake.ReadLidAction()!.Value);
    }

    [Fact]
    public void OffWithoutOnChangesNothing()
    {
        var before = ReadBefore();
        var settings = NewSettings();

        KeepAwake.Off(settings);

        Assert.Equal(before, KeepAwake.ReadLidAction()!.Value);
    }
}
