using LidAwake;
using Xunit;

public class SettingsTests : IDisposable
{
    readonly List<string> folders = new();

    string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        folders.Add(folder);
        return folder;
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

    [Fact]
    public void SetThenGetRoundTrips()
    {
        var settings = new Settings(NewFolder());
        settings.Set("a", 5);
        Assert.Equal<int?>(5, settings.Get<int?>("a"));
    }

    [Fact]
    public void ValuePersistsToAFreshSettingsOnTheSameFolder()
    {
        var folder = NewFolder();
        new Settings(folder).Set("a", 5);
        Assert.Equal<int?>(5, new Settings(folder).Get<int?>("a"));
    }

    [Fact]
    public void SettingNullClearsTheValue()
    {
        var settings = new Settings(NewFolder());
        settings.Set("a", 5);
        settings.Set<int?>("a", null);
        Assert.Null(settings.Get<int?>("a"));
    }

    [Fact]
    public void MissingKeyIsNull()
    {
        var settings = new Settings(NewFolder());
        Assert.Null(settings.Get<string>("missing"));
    }

    [Fact]
    public void CorruptFileStartsEmpty()
    {
        var folder = NewFolder();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), "{ not json");

        var settings = new Settings(folder);

        Assert.Null(settings.Get<int?>("a"));
        Assert.True(File.Exists(Path.Combine(folder, "settings.json.bad")));
        settings.Set("a", 1);
        Assert.Equal<int?>(1, settings.Get<int?>("a"));
    }

    [Fact]
    public void EmptyFileStartsEmpty()
    {
        var folder = NewFolder();
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "settings.json"), "");

        var settings = new Settings(folder);

        Assert.Null(settings.Get<int?>("a"));
    }
}
