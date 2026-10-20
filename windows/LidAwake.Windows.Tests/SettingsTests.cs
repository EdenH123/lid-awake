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

    // Tailscale's name for this PC and the Serve state move from the roaming settings to
    // machine.json once; a value machine.json already has wins; the rest of settings.json stays.
    [Fact]
    public void TheServeStateMovesToThisPcsFileOnce()
    {
        var roamingFolder = NewFolder();
        var machineFolder = NewFolder();
        var roaming = new Settings(roamingFolder);
        roaming.Set(Tailnet.ServedKey, true);
        roaming.Set(Tailnet.LiveServedKey, true);
        roaming.Set(Tailnet.HostKey, "old.example.ts.net");
        roaming.Set(Tailnet.OwnerKey, "owner@example.com");
        roaming.Set("remoteScreen", true);
        var machine = Settings.ForMachine(machineFolder);
        machine.Set(Tailnet.HostKey, "pc.example.ts.net");

        Tailnet.MoveToMachine(roaming, machine);
        var moved = Settings.ForMachine(machineFolder);
        Assert.True(moved.Get<bool?>(Tailnet.ServedKey));
        Assert.True(moved.Get<bool?>(Tailnet.LiveServedKey));
        Assert.Equal("pc.example.ts.net", moved.Get<string>(Tailnet.HostKey));
        Assert.Equal("owner@example.com", moved.Get<string>(Tailnet.OwnerKey));
        var left = File.ReadAllText(Path.Combine(roamingFolder, "settings.json"));
        foreach (var key in new[] { Tailnet.ServedKey, Tailnet.LiveServedKey, Tailnet.HostKey, Tailnet.OwnerKey })
            Assert.DoesNotContain("\"" + key + "\"", left);
        Assert.True(new Settings(roamingFolder).Get<bool?>("remoteScreen"));
        Assert.Equal("pc.example.ts.net", new Tailnet(moved, _ => null).Host);

        // Again: nothing more to move, nothing changes.
        var before = File.ReadAllText(Path.Combine(machineFolder, Settings.MachineFileName));
        Tailnet.MoveToMachine(new Settings(roamingFolder), moved);
        Assert.Equal(before, File.ReadAllText(Path.Combine(machineFolder, Settings.MachineFileName)));
    }

    [Fact]
    public void AValueThatCantBeMovedStaysWhereItWas()
    {
        var roamingFolder = NewFolder();
        var machineFolder = NewFolder();
        var roaming = new Settings(roamingFolder);
        roaming.Set(Tailnet.ServedKey, true);
        var machine = Settings.ForMachine(machineFolder);
        // machine.json can't be written: its temp file's name is taken by a folder.
        Directory.CreateDirectory(Path.Combine(machineFolder, Settings.MachineFileName + ".tmp"));
        Tailnet.MoveToMachine(roaming, machine);
        Assert.True(new Settings(roamingFolder).Get<bool?>(Tailnet.ServedKey));
    }

    [Fact]
    public void RemoveTakesTheKeyOut()
    {
        var folder = NewFolder();
        var settings = new Settings(folder);
        settings.Set("a", 1);
        settings.Set("b", 2);
        settings.Remove("a");
        settings.Remove("missing");
        Assert.DoesNotContain("\"a\"", File.ReadAllText(Path.Combine(folder, "settings.json")));
        Assert.Equal<int?>(2, new Settings(folder).Get<int?>("b"));
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
