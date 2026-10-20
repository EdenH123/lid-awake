using System.Globalization;
using System.Text.Json;
using LidAwake;
using Xunit;

// Claude usage in the tray (main.swift updateClaudeUsage), and in /status.
public class TrayClaudeTests : IDisposable
{
    readonly List<string> folders = new();
    static readonly TimeZoneInfo Utc = TimeZoneInfo.Utc;
    static readonly CultureInfo Invariant = CultureInfo.InvariantCulture;
    // Tuesday 20 October 2026, 12:00 UTC.
    static readonly DateTimeOffset Noon = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    static readonly long In100Minutes = Noon.AddMinutes(100).ToUnixTimeSeconds();
    static readonly long Friday = new DateTimeOffset(2026, 10, 23, 22, 59, 0, TimeSpan.Zero).ToUnixTimeSeconds();

    string NewFolder()
    {
        var folder = Path.Combine(Path.GetTempPath(), "lidawake-tray-test-" + Guid.NewGuid().ToString("N"));
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

    [Fact]
    public void TheMenuLineAndTheTooltipText()
    {
        var usage = new ClaudeUsage.Snapshot(1, new ClaudeUsage.Window(58.5, In100Minutes), new ClaudeUsage.Window(84.4, Friday));
        var t = TrayClaude.Describe(usage, Noon, Utc, Invariant);
        Assert.Equal("Claude: Session 59%, resets 13:40 · Week 84%, resets Fri 22:59", t.Line);
        Assert.Equal("59% · 1h40m", t.Bar);
    }

    [Fact]
    public void NoNumbersIsCheckingAndOldOnesAreHidden()
    {
        Assert.Equal(new TrayClaude.Texts("Claude usage: checking…", null), TrayClaude.Describe(null, Noon, Utc, Invariant));
        // The session reset an hour ago: hidden; the week stays.
        var stale = new ClaudeUsage.Snapshot(1, new ClaudeUsage.Window(99, Noon.AddHours(-1).ToUnixTimeSeconds()), new ClaudeUsage.Window(10, Friday));
        var t = TrayClaude.Describe(stale, Noon, Utc, Invariant);
        Assert.Equal("Claude: Week 10%, resets Fri 22:59", t.Line);
        Assert.Null(t.Bar);
        // No reset time known: the percent alone.
        var bare = TrayClaude.Describe(new ClaudeUsage.Snapshot(0, new ClaudeUsage.Window(7, 0), null), Noon, Utc, Invariant);
        Assert.Equal("Claude: Session 7%", bare.Line);
        Assert.Equal("7%", bare.Bar);
        Assert.Equal("Claude usage: checking…", TrayClaude.Describe(new ClaudeUsage.Snapshot(0, null, null), Noon, Utc, Invariant).Line);
    }

    [Fact]
    public void TheTooltipKeepsItsOwnTextFirstWithin127Characters()
    {
        Assert.Equal("Lid Awake: on\nClaude 59% · 1h40m", TrayClaude.Fit("Lid Awake: on", "Claude 59% · 1h40m"));
        Assert.Equal("Lid Awake: on", TrayClaude.Fit("Lid Awake: on", null));

        // A long own text: the Claude part is shortened, then left out.
        var long1 = new string('a', 110);
        var fitted = TrayClaude.Fit(long1, "Claude 59% · 1h40m and a little more");
        Assert.Equal(127, fitted.Length);
        Assert.StartsWith(long1 + "\nClaude", fitted);
        Assert.EndsWith("…", fitted);
        var long2 = new string('b', 124);
        Assert.Equal(long2, TrayClaude.Fit(long2, "Claude 59% · 1h40m"));
        // Its own text alone too long: cut, never more than 127.
        var tooLong = new string('c', 200);
        Assert.Equal(127, TrayClaude.Fit(tooLong, "Claude 5%").Length);
        // Never half of an emoji.
        var emoji = new string('d', 125) + "👍👍";
        var cut = TrayClaude.Fit(emoji, null);
        Assert.True(cut.Length <= 127);
        Assert.False(char.IsHighSurrogate(cut[^2]));
        Assert.Equal(127, TrayClaude.MaxTip);
    }

    [Fact]
    public void TheItemsAndTheSwitch()
    {
        var settings = new Settings(NewFolder());
        var tray = new TrayClaude(settings, new ClaudeUsage(NewFolder(), () => null), new ClaudeSettingsFile(Path.Combine(NewFolder(), "s.json")), _ => { });
        Assert.True(tray.InBar);  // on by default, as on the Mac
        Assert.Equal(new[] { "Claude usage: checking…", TrayClaude.BarTitle, TrayClaude.HooksTitle }, tray.Items.Select(i => i.Text).ToArray());
        Assert.False(tray.Items[0].Enabled);
        Assert.Null(tray.TooltipText());
        settings.Set(TrayClaude.InBarKey, false);
        Assert.False(tray.InBar);
    }

    // ---- /status ----

    [Fact]
    public async Task StatusSendsTheNumbersAndClaudeInFeaturesOnlyWhenWired()
    {
        var folder = NewFolder();
        File.WriteAllText(Path.Combine(folder, ClaudeUsage.FileName),
            "{\"rate_limits\":{\"five_hour\":{\"used_percentage\":74,\"resets_at\":1760010000}},\"updated\":1760000000}");
        // Asked just now (a fake that answers nothing), so /status only reads the file.
        var usage = new ClaudeUsage(folder, () => null);
        await usage.Refresh(TimeSpan.Zero);

        var plain = new StatusInfo(new FakeReadings());
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(plain.Build(true))))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("claude").ValueKind);
            Assert.DoesNotContain("claude", doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        }

        var wired = new StatusInfo(new FakeReadings()) { Claude = usage };
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(wired.Build(true))))
        {
            var c = doc.RootElement.GetProperty("claude");
            Assert.Equal(1760000000, c.GetProperty("updated").GetInt64());
            Assert.Equal(74, c.GetProperty("fiveHour").GetProperty("pct").GetDouble());
            Assert.Equal(1760010000, c.GetProperty("fiveHour").GetProperty("resetsAt").GetInt64());
            Assert.False(c.TryGetProperty("sevenDay", out _));
            Assert.Equal("claude", doc.RootElement.GetProperty("features").EnumerateArray().Last().GetString());
        }

        // No file: null, still "claude" in features (the PC can send it once Claude Code runs).
        var none = new StatusInfo(new FakeReadings()) { Claude = new ClaudeUsage(NewFolder(), () => null) };
        using (var doc = JsonDocument.Parse(JsonSerializer.Serialize(none.Build(true))))
        {
            Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("claude").ValueKind);
            Assert.Contains("claude", doc.RootElement.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        }
    }
}
