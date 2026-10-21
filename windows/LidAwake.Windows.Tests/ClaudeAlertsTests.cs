using System.Globalization;
using System.Text.Json;
using LidAwake;
using Xunit;

// Claude session alerts (the Mac's ClaudeAlerts.swift): only while the owner is away, at most one
// per session every 20 seconds, the summary only from a transcript under .claude\projects.
public class ClaudeAlertsTests : IDisposable
{
    readonly List<string> folders = new();
    readonly List<(string Title, string Body)> sent = new();
    readonly List<string> logged = new();
    double idle = 300;
    bool lid;
    DateTimeOffset now = DateTimeOffset.UtcNow;
    readonly string events;
    readonly string projects;

    public ClaudeAlertsTests()
    {
        events = Path.Combine(NewFolder(), "claude-events");
        projects = Path.Combine(NewFolder(), "projects");
        Directory.CreateDirectory(projects);
    }

    string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lidawake-alerts-test-" + Guid.NewGuid().ToString("N"));
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

    ClaudeAlerts Alerts() => new((title, body) =>
    {
        sent.Add((title, body));
        return Task.CompletedTask;
    }, () => lid, () => idle, events, projects, () => now, logged.Add);

    static JsonElement Event(object e) => JsonSerializer.SerializeToElement(e);

    static object Stop(string session = "s1", string? transcript = null) =>
        new Dictionary<string, object?> { ["hook_event_name"] = "Stop", ["session_id"] = session, ["cwd"] = @"C:\Users\me\src\lid-awake", ["transcript_path"] = transcript };

    static object Note(string type, string message, string session = "s1") =>
        new Dictionary<string, object?> { ["hook_event_name"] = "Notification", ["session_id"] = session, ["cwd"] = @"C:\Users\me\src\lid-awake", ["notification_type"] = type, ["message"] = message };

    [Fact]
    public void OnlyWhileTheOwnerIsAway()
    {
        var alerts = Alerts();
        idle = 30;
        Assert.Equal("skipped: you were at the PC", alerts.Handle(Event(Stop())));
        Assert.Empty(sent);
        Assert.Empty(logged);  // the usual case is not written down

        // The lid closed counts as away, whatever the input.
        lid = true;
        now = now.AddMinutes(1);
        alerts.Handle(Event(Stop()));
        Assert.Equal(("Claude finished", "lid-awake is done."), Assert.Single(sent));

        // Two minutes without input counts too.
        lid = false;
        idle = 121;
        now = now.AddMinutes(1);
        alerts.Handle(Event(Stop("s2")));
        Assert.Equal(2, sent.Count);
        Assert.Equal(2, logged.Count);
    }

    [Fact]
    public void AtMostOnePerSessionEvery20Seconds()
    {
        var alerts = Alerts();
        alerts.Handle(Event(Note("permission_prompt", "Claude needs your permission to use Bash")));
        Assert.Equal(("Claude needs your OK", "lid-awake: Claude needs your permission to use Bash"), Assert.Single(sent));
        now = now.AddSeconds(19);
        Assert.Equal("skipped: too soon after the last one", alerts.Handle(Event(Note("", "Claude has a question"))));
        // Another session isn't held back.
        alerts.Handle(Event(Note("", "Pick one", session: "s2")));
        Assert.Equal(("Claude needs you", "lid-awake: Pick one"), sent[^1]);
        now = now.AddSeconds(2);
        alerts.Handle(Event(Note("elicitation_dialog", "Pick another")));
        Assert.Equal(3, sent.Count);
        Assert.Contains(logged, l => l.Contains("too soon"));
    }

    [Fact]
    public void TheMacsWordsForEachKind()
    {
        var alerts = Alerts();
        Assert.Equal("skipped: hook re-run", alerts.Handle(Event(new Dictionary<string, object?> { ["hook_event_name"] = "Stop", ["stop_hook_active"] = true })));
        Assert.Equal("skipped: other event", alerts.Handle(Event(new Dictionary<string, object?> { ["hook_event_name"] = "PreToolUse" })));
        Assert.Equal("skipped: not a call for you", alerts.Handle(Event(Note("auth_success", "Signed in"))));

        alerts.Handle(Event(Note("idle_prompt", "Claude is waiting for your input", session: "w")));
        Assert.Equal(("Claude is waiting for you", "lid-awake: Claude is waiting for your input"), Assert.Single(sent));

        // "Still waiting" within 10 minutes of "finished" adds nothing.
        now = now.AddMinutes(1);
        alerts.Handle(Event(Stop("f")));
        now = now.AddMinutes(1);
        Assert.Equal("skipped: already said finished", alerts.Handle(Event(Note("idle_prompt", "Claude is waiting for your input", session: "f"))));
        now = now.AddMinutes(10);
        alerts.Handle(Event(Note("idle_prompt", "waiting", session: "f")));
        Assert.Equal("Claude is waiting for you", sent[^1].Title);

        // No session or folder: still an alert.
        now = now.AddMinutes(1);
        alerts.Handle(Event(new Dictionary<string, object?> { ["hook_event_name"] = "Notification", ["message"] = "" }));
        Assert.Equal(("Claude needs you", "Claude"), sent[^1]);
    }

    string Transcript(string folder, params string[] replies)
    {
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".jsonl");
        var lines = new List<string> { JsonSerializer.Serialize(new { type = "user", message = new { content = "hi" } }) };
        foreach (var r in replies)
            lines.Add(JsonSerializer.Serialize(new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", name = "Bash" }, new { type = "text", text = r } } } }));
        lines.Add(JsonSerializer.Serialize(new { type = "assistant", message = new { content = new object[] { new { type = "tool_use", name = "Read" } } } }));
        File.WriteAllText(path, string.Join("\n", lines) + "\n");
        return path;
    }

    [Fact]
    public void TheSummaryComesFromATranscriptUnderProjectsOnly()
    {
        var alerts = Alerts();
        var inside = Transcript(Path.Combine(projects, "C--Users-me-src"), "First.", "All **done**: the `tests` pass.");
        alerts.Handle(Event(Stop("a", inside)));
        Assert.Equal(("Claude finished", "lid-awake: All done: the tests pass."), sent[^1]);

        // Case doesn't matter on Windows.
        now = now.AddMinutes(1);
        alerts.Handle(Event(Stop("a", inside.ToUpperInvariant())));
        Assert.Equal("lid-awake: All done: the tests pass.", sent[^1].Body);

        // Outside .claude\projects, a ".." way out, or not a .jsonl: no summary.
        var outside = Transcript(Path.Combine(NewFolder(), "elsewhere"), "Secret.");
        var sneaky = Path.Combine(projects, "..", Path.GetFileName(Path.GetDirectoryName(outside)!));
        var notJsonl = Path.ChangeExtension(Transcript(Path.Combine(projects, "x"), "Hidden."), ".txt");
        File.Move(Path.ChangeExtension(notJsonl, ".jsonl"), notJsonl);
        // A network or device path is refused as written, before anything is opened, even one that
        // names the right file.
        var unc = @"\\localhost\" + inside[0] + "$" + inside[2..];
        var device = @"\\?\" + inside;
        var dotDevice = @"\\.\" + inside;
        foreach (var path in new[] { outside, Path.Combine(sneaky, Path.GetFileName(outside)), notJsonl, Path.Combine(projects, "missing.jsonl"),
                                     unc, device, dotDevice, "relative\\x.jsonl" })
        {
            now = now.AddMinutes(1);
            alerts.Handle(Event(Stop("a", path)));
            Assert.Equal("lid-awake is done.", sent[^1].Body);
        }
    }

    [Fact]
    public void ALinkOutOfProjectsIsFollowedAndRefused()
    {
        var outside = Path.Combine(NewFolder(), "real");
        var target = Transcript(outside, "Secret.");
        var link = Path.Combine(projects, "link");
        try { Directory.CreateSymbolicLink(link, outside); }
        catch (Exception e)
        {
            Console.WriteLine("RUNNER: no symbolic links here (" + e.Message + "); the link case is not checked");
            return;
        }
        var alerts = Alerts();
        alerts.Handle(Event(Stop("l", Path.Combine(link, Path.GetFileName(target)))));
        Assert.Equal("lid-awake is done.", sent[^1].Body);
    }

    [Fact]
    public void ScanReadsEachFileOnceAndDeletesIt()
    {
        ClaudeHook.EnsurePrivateFolder(events);
        File.WriteAllText(Path.Combine(events, "1.json"), JsonSerializer.Serialize(Stop("one")));
        File.WriteAllText(Path.Combine(events, "2.json"), "{\"hook_event_name\":\"Stop\",\"session_id\":\"big\",\"pad\":\"" + new string('x', 64 * 1024) + "\"}");
        var stale = Path.Combine(events, "3.json");
        File.WriteAllText(stale, JsonSerializer.Serialize(Stop("stale")));
        File.SetLastWriteTimeUtc(stale, DateTime.UtcNow.AddMinutes(-6));
        File.WriteAllText(Path.Combine(events, "4.json"), "[\"not an object\"]");
        File.WriteAllText(Path.Combine(events, "5.json"), "broken");
        var unfinished = Path.Combine(events, "6.tmp");
        File.WriteAllText(unfinished, JsonSerializer.Serialize(Stop("tmp")));
        // A hook stopped half-way a while ago: its .tmp goes.
        var leftOver = Path.Combine(events, "7.tmp");
        File.WriteAllText(leftOver, "{");
        File.SetLastWriteTimeUtc(leftOver, DateTime.UtcNow.AddMinutes(-2));

        Alerts().Scan();
        Assert.Equal(("Claude finished", "lid-awake is done."), Assert.Single(sent));
        Assert.Equal(new[] { unfinished }, Directory.GetFiles(events));
    }

    [Fact]
    public void LinesAreCutAtAWord()
    {
        Assert.Equal("one two three", ClaudeAlerts.Clean("  one\n two\t three ", 20));
        Assert.Equal("one two…", ClaudeAlerts.Clean("one two three", 9));
        Assert.Equal("abcdefghij…", ClaudeAlerts.Clean("abcdefghijklmnop", 10));
        // An emoji is one character, never cut in half.
        Assert.Equal(5, new StringInfo(ClaudeAlerts.Clean("👍👍👍👍👍👍", 5).TrimEnd('…')).LengthInTextElements);
    }

    [Fact]
    public void IdleTimeIsReadFromWindows()
    {
        var seconds = IdleTime.Seconds();
        Assert.True(seconds >= 0);
        Console.WriteLine($"RUNNER: idle for {seconds:0.0} s");
    }
}
