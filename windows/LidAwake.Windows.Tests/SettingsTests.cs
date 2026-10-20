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
        // machine.json can't be written: its name is taken by a folder.
        Directory.CreateDirectory(Path.Combine(machineFolder, Settings.MachineFileName));
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

    // Another program reading settings.json (an antivirus scan) keeps it from being replaced for a
    // moment: the save is tried again and goes through once the file is let go.
    [Fact]
    public void ASaveWaitsForAFileAnotherProgramHoldsForAMoment()
    {
        var folder = NewFolder();
        var settings = new Settings(folder);
        settings.Set("a", 1);
        var file = Path.Combine(folder, "settings.json");

        // Let go once a save has written its temp file and is trying to move it into place.
        var held = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
        var letGo = new Thread(() =>
        {
            var deadline = DateTime.UtcNow.AddSeconds(30);
            while (Directory.GetFiles(folder, "*.tmp").Length == 0 && DateTime.UtcNow < deadline) Thread.Sleep(1);
            held.Dispose();
        });
        letGo.Start();
        settings.Set("a", 2);
        letGo.Join();
        Assert.Equal<int?>(2, new Settings(folder).Get<int?>("a"));
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
    }

    // Held for good, the save fails (after the retries): the caller is told, the old value stays
    // in memory as on disk, and no temp file is left.
    [Fact]
    public void ASaveThatFailsKeepsTheOldValue()
    {
        var folder = NewFolder();
        var settings = new Settings(folder);
        settings.Set("a", 1);
        settings.Set("b", "x");
        using (new FileStream(Path.Combine(folder, "settings.json"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var set = Record.Exception(() => settings.Set("a", 2));
            Assert.True(set is IOException or UnauthorizedAccessException, $"got {set?.GetType().Name ?? "no error"}");
            Assert.Equal<int?>(1, settings.Get<int?>("a"));
            var added = Record.Exception(() => settings.Set("c", true));
            Assert.True(added is IOException or UnauthorizedAccessException, $"got {added?.GetType().Name ?? "no error"}");
            Assert.Null(settings.Get<bool?>("c"));
            var removed = Record.Exception(() => settings.Remove("b"));
            Assert.True(removed is IOException or UnauthorizedAccessException, $"got {removed?.GetType().Name ?? "no error"}");
            Assert.Equal("x", settings.Get<string>("b"));
        }
        Assert.Empty(Directory.GetFiles(folder, "*.tmp"));
        var reread = new Settings(folder);
        Assert.Equal<int?>(1, reread.Get<int?>("a"));
        Assert.Equal("x", reread.Get<string>("b"));
        // Once the file can be written again, the next change is saved with everything else.
        settings.Set("a", 3);
        Assert.Equal<int?>(3, new Settings(folder).Get<int?>("a"));
        Assert.Equal("x", new Settings(folder).Get<string>("b"));
    }

    // A value the key already has writes nothing: here the file can't be replaced, and setting the
    // same values again still works (Tailscale's name is learned again every 30 seconds).
    [Fact]
    public void SettingTheSameValueWritesNothing()
    {
        var folder = NewFolder();
        var settings = new Settings(folder);
        settings.Set("host", "desk-pc.tail1234.ts.net");
        settings.Set("n", 5);
        settings.Set<string>("gone", null);
        settings.Set("list", new List<string> { "a", "b" });
        var file = Path.Combine(folder, "settings.json");
        var before = File.ReadAllText(file);
        using (new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            settings.Set("host", "desk-pc.tail1234.ts.net");
            settings.Set("n", 5);
            settings.Set<string>("gone", null);
            settings.Set("list", new List<string> { "a", "b" });
            new Settings(folder).Set("n", 5);  // as read back from the file
            settings.Remove("never there");
        }
        Assert.Equal(before, File.ReadAllText(file));
        // A different value is written as before.
        settings.Set("n", 6);
        Assert.Equal<int?>(6, new Settings(folder).Get<int?>("n"));
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
