using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using LidAwake;
using Xunit;

// What Claude Code runs (LidAwake.exe --claude-event, --claude-statusline, --claude-setup) and the
// merge into Claude Code's settings file.
public class ClaudeHookTests : IDisposable
{
    readonly List<string> folders = new();

    string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lidawake-hook-test-" + Guid.NewGuid().ToString("N"));
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

    const string Exe = @"C:\Users\me\AppData\Local\LidAwake\LidAwake.exe";
    const string Moved = @"D:\Apps\LidAwake\LidAwake.exe";
    static readonly string OurEvent = "\"C:/Users/me/AppData/Local/LidAwake/LidAwake.exe\" --claude-event";
    static readonly string OurLine = "\"C:/Users/me/AppData/Local/LidAwake/LidAwake.exe\" --claude-statusline";

    static MemoryStream Input(string text) => new(Encoding.UTF8.GetBytes(text));

    // ---- --claude-event ----

    [Fact]
    public void TheHookWritesOneFileAndExitsZero()
    {
        var events = Path.Combine(NewFolder(), "claude-events");
        var json = "{\"hook_event_name\":\"Stop\",\"session_id\":\"abc\"}";
        // No folder yet (the tray makes it at start): nothing is written, and still 0.
        Assert.Equal(0, ClaudeHook.Run(new[] { ClaudeHook.EventFlag }, () => Input(json), () => new MemoryStream(), events, "unused", new ClaudeSettingsFile(), Exe));
        Assert.False(Directory.Exists(events));
        ClaudeHook.EnsurePrivateFolder(events);
        var code = ClaudeHook.Run(new[] { ClaudeHook.EventFlag }, () => Input(json), () => new MemoryStream(), events, "unused", new ClaudeSettingsFile(Path.Combine(NewFolder(), "s.json")), Exe);
        Assert.Equal(0, code);
        var file = Assert.Single(Directory.GetFiles(events));
        Assert.EndsWith(".json", file);
        Assert.Equal(json, File.ReadAllText(file));
        Assert.Empty(Directory.GetFiles(events, "*.tmp"));

        // Too much, or nothing: no file, still 0.
        var big = new string('x', ClaudeHook.MaxInputBytes + 1);
        Assert.Equal(0, ClaudeHook.Run(new[] { ClaudeHook.EventFlag }, () => Input(big), () => new MemoryStream(), events, "unused", new ClaudeSettingsFile(), Exe));
        Assert.Equal(0, ClaudeHook.Run(new[] { ClaudeHook.EventFlag }, () => new MemoryStream(), () => new MemoryStream(), events, "unused", new ClaudeSettingsFile(), Exe));
        Assert.Equal(0, ClaudeHook.Run(new[] { ClaudeHook.EventFlag }, () => throw new IOException("no stdin"), () => new MemoryStream(), events, "unused", new ClaudeSettingsFile(), Exe));
        Assert.Single(Directory.GetFiles(events));
    }

    [Fact]
    public void OtherArgumentsStartLidAwakeAsUsual()
    {
        Assert.Null(ClaudeHook.Run(Array.Empty<string>()));
        Assert.Null(ClaudeHook.Run(new[] { "--something-else" }));
    }

    [Fact]
    public void TheEventsFolderIsThisUsersOnly()
    {
        var events = Path.Combine(NewFolder(), "claude-events");
        ClaudeHook.EnsurePrivateFolder(events);
        var o = new WindowsOwnership().Read(events, directory: true)!;
        var me = System.Security.Principal.WindowsIdentity.GetCurrent().User!.Value;
        Assert.All(o.Entries, e => Assert.Equal(me, e.Sid));
        Assert.NotEmpty(o.Entries);
    }

    // A stdin that never ends doesn't hold the hook up. It ends when the test is done with it, so
    // the thread-pool thread that reads it isn't held for the rest of the run.
    sealed class NeverEnds : Stream
    {
        readonly ManualResetEventSlim done = new();
        public override int Read(byte[] buffer, int offset, int count) { done.Wait(); return 0; }
        protected override void Dispose(bool disposing)
        {
            done.Set();
            base.Dispose(disposing);
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => 0; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    [Fact]
    public void AStdinThatNeverEndsIsGivenUpOn()
    {
        var watch = Stopwatch.StartNew();
        using var stdin = new NeverEnds();
        Assert.Null(ClaudeHook.ReadInput(stdin, wait: TimeSpan.FromMilliseconds(300)));
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5));
    }

    // The real LidAwake.exe, when the build put it next to the tests: a hook run ends at once with 0.
    [Fact]
    public void TheRealExeHandlesTheHookBeforeAnythingElse()
    {
        var exe = Path.Combine(AppContext.BaseDirectory, "LidAwake.exe");
        if (!File.Exists(exe) || !File.Exists(Path.Combine(AppContext.BaseDirectory, "LidAwake.runtimeconfig.json")))
        {
            Console.WriteLine("RUNNER: LidAwake.exe is not next to the tests; the process-level hook run is not checked");
            return;
        }
        var marker = "test-" + Guid.NewGuid().ToString("N");
        // The folder the tray makes at start; taken away again if this test made it.
        var parent = Path.GetDirectoryName(ClaudeAlerts.DefaultFolder)!;
        bool hadParent = Directory.Exists(parent), hadFolder = Directory.Exists(ClaudeAlerts.DefaultFolder);
        ClaudeHook.EnsurePrivateFolder(ClaudeAlerts.DefaultFolder);
        try
        {
        var info = new ProcessStartInfo(exe, ClaudeHook.EventFlag) { RedirectStandardInput = true, UseShellExecute = false, CreateNoWindow = true };
        using var p = Process.Start(info)!;
        p.StandardInput.Write("{\"hook_event_name\":\"Test\",\"session_id\":\"" + marker + "\"}");
        p.StandardInput.Close();
        Assert.True(p.WaitForExit(15000), "the hook did not end");
        Assert.Equal(0, p.ExitCode);
        var written = Directory.GetFiles(ClaudeAlerts.DefaultFolder, "*.json").Where(f => File.ReadAllText(f).Contains(marker)).ToList();
        Assert.Single(written);
        foreach (var f in written) File.Delete(f);
        }
        finally
        {
            try
            {
                if (!hadFolder) Directory.Delete(ClaudeAlerts.DefaultFolder, true);
                if (!hadParent) Directory.Delete(parent, false);
            }
            catch (Exception) { }
        }
    }

    // ---- --claude-statusline ----

    [Fact]
    public void TheStatusLineSavesTheNumbersAndPrintsALine()
    {
        var usageFile = Path.Combine(NewFolder(), ClaudeUsage.FileName);
        var json = "{\"model\":{\"id\":\"claude-opus\",\"display_name\":\"Opus\"},\"rate_limits\":{" +
                   "\"five_hour\":{\"used_percentage\":59.4,\"resets_at\":1760010000}," +
                   "\"seven_day\":{\"used_percentage\":84,\"resets_at\":\"2026-10-23T22:59:00Z\"}}}";
        var output = new MemoryStream();
        Assert.Equal(0, ClaudeHook.Run(new[] { ClaudeHook.StatusLineFlag }, () => Input(json), () => output, "unused", usageFile, new ClaudeSettingsFile(), Exe));
        Assert.Equal("Opus · session 59%\n", Encoding.UTF8.GetString(output.ToArray()));
        var u = ClaudeUsage.ReadFile(usageFile)!;
        Assert.Equal(new ClaudeUsage.Window(59.4, 1760010000), u.FiveHour);
        Assert.Equal(new ClaudeUsage.Window(84, new DateTimeOffset(2026, 10, 23, 22, 59, 0, TimeSpan.Zero).ToUnixTimeSeconds()), u.SevenDay);
        Assert.True(u.Updated > 0);
    }

    // Claude Code runs the status line several times a second: the same numbers are written again
    // only once the file is a minute old; new numbers are written at once.
    [Fact]
    public void TheStatusLineWritesTheSameNumbersAtMostOnceAMinute()
    {
        var usageFile = Path.Combine(NewFolder(), ClaudeUsage.FileName);
        var json = Encoding.UTF8.GetBytes("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":59,\"resets_at\":1760010000}}}");
        var at = DateTimeOffset.FromUnixTimeSeconds(1760000000);
        ClaudeHook.StatusLine(json, usageFile, at);
        Assert.Equal(1760000000, ClaudeUsage.ReadFile(usageFile)!.Updated);
        // A mark only a rewrite would remove.
        var marked = File.ReadAllText(usageFile).Replace("\"updated\"", "\"mark\":1,\"updated\"");
        File.WriteAllText(usageFile, marked);

        ClaudeHook.StatusLine(json, usageFile, at.AddSeconds(30));
        Assert.Equal(marked, File.ReadAllText(usageFile));

        var other = Encoding.UTF8.GetBytes("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":60,\"resets_at\":1760010000}}}");
        ClaudeHook.StatusLine(other, usageFile, at.AddSeconds(31));
        Assert.Equal(60, ClaudeUsage.ReadFile(usageFile)!.FiveHour!.Value.Pct);
        Assert.DoesNotContain("\"mark\"", File.ReadAllText(usageFile));

        File.WriteAllText(usageFile, File.ReadAllText(usageFile).Replace("\"updated\"", "\"mark\":1,\"updated\""));
        ClaudeHook.StatusLine(other, usageFile, at.AddSeconds(31 + 60));
        Assert.DoesNotContain("\"mark\"", File.ReadAllText(usageFile));
        Assert.Equal(1760000000 + 91, ClaudeUsage.ReadFile(usageFile)!.Updated);
    }

    [Fact]
    public void AStatusLineWithoutNumbersSavesNothing()
    {
        var usageFile = Path.Combine(NewFolder(), ClaudeUsage.FileName);
        Assert.Equal("Sonnet", ClaudeHook.StatusLine(Encoding.UTF8.GetBytes("{\"model\":{\"display_name\":\"Sonnet\"}}"), usageFile, DateTimeOffset.UtcNow));
        Assert.Equal("Claude", ClaudeHook.StatusLine(Encoding.UTF8.GetBytes("not json"), usageFile, DateTimeOffset.UtcNow));
        Assert.Equal("Claude", ClaudeHook.StatusLine(null, usageFile, DateTimeOffset.UtcNow));
        Assert.Equal("Claude", ClaudeHook.StatusLine(Encoding.UTF8.GetBytes("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":5000}}}"), usageFile, DateTimeOffset.UtcNow));
        Assert.False(File.Exists(usageFile));
    }

    // ---- Claude Code's settings file ----

    const string Theirs = """
        {
          "model": "opus",
          "env": { "A": "1" },
          "hooks": {
            "Stop": [ { "hooks": [ { "type": "command", "command": "say done" } ] } ],
            "PreToolUse": [ { "matcher": "Bash", "hooks": [ { "type": "command", "command": "check.sh" } ] } ]
          },
          "statusLine": { "type": "command", "command": "my-line.sh" },
          "permissions": { "allow": [ "Bash(ls:*)" ] }
        }
        """;

    static string Same(string json) => JsonNode.Parse(json)!.ToJsonString();

    static JsonObject Load(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    static List<string> Commands(JsonObject root, string e) =>
        root["hooks"]![e]!.AsArray().SelectMany(g => g!["hooks"]!.AsArray()).Select(h => (string)h!["command"]!).ToList();

    [Fact]
    public void TurningOnKeepsTheirHooksAndStatusLineAndTurningOffPutsItBack()
    {
        var path = Path.Combine(NewFolder(), "settings.json");
        File.WriteAllText(path, Theirs);
        var file = new ClaudeSettingsFile(path);
        Assert.Equal(ClaudeSettingsFile.Setup.Off, file.State());

        var on = file.Install(Exe);
        Assert.True(on.Ok);
        Assert.True(on.Changed);
        Assert.True(on.TheirStatusLine);
        // The backup is the file as it was.
        Assert.Equal(Theirs, File.ReadAllText(path + ClaudeSettingsFile.BackupSuffix));
        var root = Load(path);
        Assert.Equal(new[] { "say done", OurEvent }, Commands(root, "Stop"));
        Assert.Equal(new[] { OurEvent }, Commands(root, "Notification"));
        Assert.Equal(new[] { "check.sh" }, Commands(root, "PreToolUse"));
        Assert.Equal("my-line.sh", (string)root["statusLine"]!["command"]!);
        Assert.Equal("opus", (string)root["model"]!);
        Assert.Equal(Same("{\"allow\":[\"Bash(ls:*)\"]}"), root["permissions"]!.ToJsonString());
        Assert.Equal(ClaudeSettingsFile.Setup.On, file.State());

        // Again: nothing more is added.
        var again = file.Install(Exe);
        Assert.True(again.Ok);
        Assert.False(again.Changed);
        Assert.Single(Commands(Load(path), "Notification"));

        var off = file.Remove();
        Assert.True(off.Ok);
        Assert.True(off.Changed);
        Assert.Equal(Same(Theirs), Load(path).ToJsonString());
        Assert.Equal(ClaudeSettingsFile.Setup.Off, file.State());
        Assert.False(file.Remove().Changed);
    }

    [Fact]
    public void WithNoFileOursIsAddedWithTheStatusLineAndRemovedCompletely()
    {
        var path = Path.Combine(NewFolder(), ".claude", "settings.json");
        var file = new ClaudeSettingsFile(path);
        Assert.True(file.Remove().Ok);
        var on = file.Install(Exe);
        Assert.True(on.Ok && on.Changed && !on.TheirStatusLine);
        Assert.False(File.Exists(path + ClaudeSettingsFile.BackupSuffix));
        var root = Load(path);
        Assert.Equal(OurLine, (string)root["statusLine"]!["command"]!);
        Assert.Equal("command", (string)root["hooks"]!["Stop"]![0]!["hooks"]![0]!["type"]!);
        Assert.Equal(ClaudeSettingsFile.HookTimeout, (int)root["hooks"]!["Stop"]![0]!["hooks"]![0]!["timeout"]!);
        Assert.True(file.Remove().Changed);
        Assert.Equal("{}", Load(path).ToJsonString());
    }

    [Fact]
    public void ANewPlaceOfLidAwakeReplacesOursAndNothingElse()
    {
        var path = Path.Combine(NewFolder(), "settings.json");
        File.WriteAllText(path, Theirs);
        var file = new ClaudeSettingsFile(path);
        file.Install(Exe);
        var moved = file.Update(Moved, installed: Moved);
        Assert.True(moved.Changed);
        var root = Load(path);
        Assert.Equal(new[] { "say done", "\"D:/Apps/LidAwake/LidAwake.exe\" --claude-event" }, Commands(root, "Stop"));
        Assert.Equal("my-line.sh", (string)root["statusLine"]!["command"]!);
        Assert.False(file.Update(Moved, installed: Moved).Changed);

        // Turned on again from the new place: still one of ours per event.
        file.Install(Exe);
        Assert.Equal(new[] { "say done", OurEvent }, Commands(Load(path), "Stop"));

        // Update never adds what the owner took out.
        var bare = Path.Combine(NewFolder(), "settings.json");
        File.WriteAllText(bare, Theirs);
        Assert.False(new ClaudeSettingsFile(bare).Update(Moved, installed: Moved).Changed);
        Assert.Equal(Theirs, File.ReadAllText(bare));
    }

    [Theory]
    [InlineData("{ \"model\": \"opus\", }")]
    [InlineData("{ // a comment\n \"model\": \"opus\" }")]
    [InlineData("[1, 2]")]
    [InlineData("{\"hooks\": []}")]
    [InlineData("{\"hooks\": {\"Stop\": {}}}")]
    [InlineData("{\"model\": \"opus\", \"model\": \"sonnet\"}")]
    [InlineData("{\"hooks\": {\"Stop\": [{\"hooks\": [{\"type\": \"command\", \"type\": \"x\"}]}]}}")]
    public void AFileThatIsntPlainJsonIsLeftAlone(string text)
    {
        var path = Path.Combine(NewFolder(), "settings.json");
        File.WriteAllText(path, text);
        var file = new ClaudeSettingsFile(path);
        var on = file.Install(Exe);
        Assert.False(on.Ok);
        Assert.Contains("left", on.Message);
        Assert.False(file.Remove().Ok);
        Assert.False(file.Update(Moved, installed: Moved).Ok);
        Assert.Equal(ClaudeSettingsFile.Setup.Unreadable, file.State());
        Assert.Equal(text, File.ReadAllText(path));
        Assert.False(File.Exists(path + ClaudeSettingsFile.BackupSuffix));
    }

    [Fact]
    public void OurCommandIsQuotedWithForwardSlashesAndAPathTheShellsWouldChangeIsRefused()
    {
        Assert.Equal(OurEvent, ClaudeSettingsFile.Command(Exe, ClaudeHook.EventFlag));
        var path = Path.Combine(NewFolder(), "settings.json");
        var r = new ClaudeSettingsFile(path).Install(@"C:\Users\100%\LidAwake.exe");
        Assert.False(r.Ok);
        Assert.False(File.Exists(path));
        Assert.False(ClaudeSettingsFile.UsablePath("LidAwake.exe"));
        Assert.False(ClaudeSettingsFile.UsablePath(@"C:\a$b\LidAwake.exe"));
        Assert.True(ClaudeSettingsFile.UsablePath(Exe));
    }

    [Fact]
    public void SetupForTheInstallers()
    {
        var path = Path.Combine(NewFolder(), "settings.json");
        var file = new ClaudeSettingsFile(path);
        var said = new List<string>();
        Assert.Equal(ClaudeHook.NoClaudeCode, ClaudeHook.Setup("status", file, Exe, said.Add, () => false));
        Assert.Equal(ClaudeHook.NotSetUp, ClaudeHook.Setup("status", file, Exe, said.Add, () => true));
        Assert.Equal(ClaudeHook.SetUp, ClaudeHook.Setup("on", file, Exe, said.Add, () => true));
        Assert.StartsWith("Added", said[^1]);
        Assert.Equal(ClaudeHook.SetUp, ClaudeHook.Setup("status", file, Exe, said.Add, () => false));
        Assert.Equal(ClaudeHook.SetUp, ClaudeHook.Setup("off", file, Exe, said.Add));
        Assert.StartsWith("Removed", said[^1]);
        Assert.Equal(ClaudeHook.NotSetUp, ClaudeHook.Setup("status", file, Exe, said.Add, () => true));

        File.WriteAllText(path, "{oops");
        Assert.Equal(ClaudeHook.Refused, ClaudeHook.Setup("status", file, Exe, said.Add, () => true));
        Assert.Equal(ClaudeHook.Refused, ClaudeHook.Setup("on", file, Exe, said.Add, () => true));
        Assert.Contains("isn't valid JSON", said[^1]);

        // Through Run, as the installers call it: the reason is printed.
        var output = new MemoryStream();
        Assert.Equal(ClaudeHook.Refused, ClaudeHook.Run(new[] { ClaudeHook.SetupFlag, "off" }, () => new MemoryStream(), () => output, "unused", "unused", file, Exe));
        Assert.Contains("isn't valid JSON", Encoding.UTF8.GetString(output.ToArray()));
    }
    [Fact]
    public void AFileThatIsntUtf8IsLeftAlone()
    {
        var path = Path.Combine(NewFolder(), "settings.json");
        var bytes = new byte[] { (byte)'{', (byte)'"', (byte)'m', (byte)'"', (byte)':', (byte)'"', 0xC3, 0x28, (byte)'"', (byte)'}' };
        File.WriteAllBytes(path, bytes);
        var file = new ClaudeSettingsFile(path);
        var r = file.Install(Exe);
        Assert.False(r.Ok);
        Assert.Contains("UTF-8", r.Message);
        Assert.Equal(ClaudeSettingsFile.Setup.Unreadable, file.State());
        Assert.Equal(bytes, File.ReadAllBytes(path));
        // A byte-order mark is fine.
        File.WriteAllBytes(path, new byte[] { 0xEF, 0xBB, 0xBF }.Concat(Encoding.UTF8.GetBytes("{\"model\":\"opus\"}")).ToArray());
        Assert.True(file.Install(Exe).Ok);
    }

    // Only the exact form "<...\LidAwake.exe>" --flag is ours: an owner's wrapper or another
    // tool's hook that mentions the flag stays, whatever Lid Awake does.
    const string Wrapper = """
        {
          "hooks": {
            "Stop": [ { "hooks": [
              { "type": "command", "command": "C:/tools/notify.cmd --claude-event" },
              { "type": "command", "command": "\"C:/Users/me/AppData/Local/LidAwake/LidAwake.exe\" --claude-event --verbose" },
              { "type": "command", "command": "\"C:/Users/me/OtherAwake.exe\" --claude-event" }
            ] } ]
          },
          "statusLine": { "type": "command", "command": "my-line.cmd --claude-statusline" }
        }
        """;

    [Fact]
    public void AWrapperThatMentionsTheFlagIsNeverOurs()
    {
        var path = Path.Combine(NewFolder(), "settings.json");
        File.WriteAllText(path, Wrapper);
        var file = new ClaudeSettingsFile(path);
        Assert.Equal(ClaudeSettingsFile.Setup.Off, file.State());
        Assert.False(file.Update(Moved, installed: Moved).Changed);
        Assert.False(file.Remove().Changed);
        Assert.Equal(Wrapper, File.ReadAllText(path));

        var on = file.Install(Exe);
        Assert.True(on.Changed);
        Assert.True(on.TheirStatusLine);
        var root = Load(path);
        Assert.Equal(new[] { "C:/tools/notify.cmd --claude-event", "\"C:/Users/me/AppData/Local/LidAwake/LidAwake.exe\" --claude-event --verbose",
                             "\"C:/Users/me/OtherAwake.exe\" --claude-event", OurEvent }, Commands(root, "Stop"));
        Assert.Equal("my-line.cmd --claude-statusline", (string)root["statusLine"]!["command"]!);
        file.Update(Moved, installed: Moved);
        Assert.Equal(3, Commands(Load(path), "Stop").Count(c => !c.StartsWith("\"D:/", StringComparison.Ordinal)));
        file.Remove();
        Assert.Equal(Same(Wrapper), Load(path).ToJsonString());
    }

    [Fact]
    public void ABackupIsNeverOverwritten()
    {
        var path = Path.Combine(NewFolder(), "settings.json");
        File.WriteAllText(path, Theirs);
        var clock = new DateTime(2026, 10, 20, 14, 5, 9);
        var file = new ClaudeSettingsFile(path, () => clock);
        var first = file.Install(Exe);
        Assert.Contains(path + ClaudeSettingsFile.BackupSuffix + ")", first.Message);
        file.Remove();
        var again = file.Install(Exe);
        // The very first copy is still the owner's original file.
        Assert.Equal(Theirs, File.ReadAllText(path + ClaudeSettingsFile.BackupSuffix));
        var stamped = path + ClaudeSettingsFile.BackupSuffix + "-20261020-140509";
        Assert.True(File.Exists(stamped));          // before "off"
        Assert.True(File.Exists(stamped + "-2"));   // before "on" again, the same second
        Assert.Contains(stamped + "-2", again.Message);
        Assert.DoesNotContain(OurEvent, File.ReadAllText(stamped + "-2"));
    }

    [Fact]
    public void NoBackupIsNamedWhenThereWasNoFile()
    {
        var path = Path.Combine(NewFolder(), "settings.json");
        var r = new ClaudeSettingsFile(path).Install(Exe);
        Assert.True(r.Changed);
        Assert.DoesNotContain("copy", r.Message);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*" + ClaudeSettingsFile.BackupSuffix + "*"));
    }

    [Fact]
    public void OnlyTheInstalledLidAwakeTakesOurEntriesAtStart()
    {
        var path = Path.Combine(NewFolder(), "settings.json");
        File.WriteAllText(path, Theirs);
        var file = new ClaudeSettingsFile(path);
        file.Install(Exe);
        var before = File.ReadAllText(path);
        // A build or a ZIP copy somewhere else.
        var r = file.Update(@"C:\Users\me\Downloads\LidAwake-Windows\windows\app\LidAwake.exe", installed: Exe);
        Assert.True(r.Ok);
        Assert.False(r.Changed);
        Assert.Equal(before, File.ReadAllText(path));
        // The installed copy, moved: ours follow, after a backup of the file as it was.
        var backups = Directory.GetFiles(Path.GetDirectoryName(path)!, "*" + ClaudeSettingsFile.BackupSuffix + "*").Length;
        Assert.True(file.Update(Moved, installed: Moved).Changed);
        Assert.Equal(backups + 1, Directory.GetFiles(Path.GetDirectoryName(path)!, "*" + ClaudeSettingsFile.BackupSuffix + "*").Length);
        Assert.Equal(new[] { "say done", "\"D:/Apps/LidAwake/LidAwake.exe\" --claude-event" }, Commands(Load(path), "Stop"));
    }

    [Fact]
    public void TheStatusLineSavesNothingWithoutLidAwakesFolder()
    {
        var usageFile = Path.Combine(NewFolder(), "not-there", ClaudeUsage.FileName);
        var line = ClaudeHook.StatusLine(Encoding.UTF8.GetBytes("{\"rate_limits\":{\"five_hour\":{\"used_percentage\":12}}}"), usageFile, DateTimeOffset.UtcNow);
        Assert.Equal("Claude · session 12%", line);
        Assert.False(Directory.Exists(Path.GetDirectoryName(usageFile)));
    }
}
