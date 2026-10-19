using LidAwake;
using Xunit;

public class SettingsTests
{
    static string NewFolder() => Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());

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
}
