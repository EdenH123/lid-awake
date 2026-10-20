using LidAwake;
using Xunit;

public class TailnetTests : IDisposable
{
    const string StatusJson =
        "{\"Self\":{\"DNSName\":\"Desk-PC.tail1234.ts.net.\",\"UserID\":4242}," +
        "\"User\":{\"4242\":{\"LoginName\":\"Owner@Example.com\"}}}";

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly List<string> commands = new();

    Settings NewSettings() => new(folder);

    Tailnet NewTailnet(Func<string, string?> answer, Settings? settings = null) =>
        new(settings ?? NewSettings(), args => { commands.Add(args); return answer(args); });

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void RefreshLearnsNameAndOwnerLowercased()
    {
        var t = NewTailnet(_ => StatusJson);
        t.Refresh();
        Assert.Equal("desk-pc.tail1234.ts.net", t.Host);
        Assert.Equal("owner@example.com", t.Owner);
        Assert.Equal(new[] { "status --json" }, commands);
    }

    [Fact]
    public void RefreshKeepsWhatItKnewWhenTailscaleIsNotThere()
    {
        var settings = NewSettings();
        NewTailnet(_ => StatusJson, settings).Refresh();
        var t = NewTailnet(_ => null, settings);
        t.Refresh();
        Assert.Equal("desk-pc.tail1234.ts.net", t.Host);
        Assert.Equal("owner@example.com", t.Owner);
    }

    [Fact]
    public void RefreshIgnoresANameThatIsNotATailnetName()
    {
        var t = NewTailnet(_ => "{\"Self\":{\"DNSName\":\"\",\"UserID\":1}}");
        t.Refresh();
        Assert.Null(t.Host);
    }

    // Like Tailnet.update on the Mac: the name and the owner are saved together or not at all.
    [Fact]
    public void RefreshSavesNothingWithoutTheOwner()
    {
        var t = NewTailnet(_ => "{\"Self\":{\"DNSName\":\"desk-pc.tail1234.ts.net.\",\"UserID\":4242},\"User\":{}}");
        t.Refresh();
        Assert.Null(t.Host);
        Assert.Null(t.Owner);
    }

    [Fact]
    public void RefreshWithoutTheOwnerKeepsTheOldPairWhole()
    {
        var settings = NewSettings();
        NewTailnet(_ => StatusJson, settings).Refresh();
        var t = NewTailnet(_ => "{\"Self\":{\"DNSName\":\"other-pc.tail9999.ts.net.\",\"UserID\":7}}", settings);
        t.Refresh();
        Assert.Equal("desk-pc.tail1234.ts.net", t.Host);
        Assert.Equal("owner@example.com", t.Owner);
    }

    [Fact]
    public void NothingThrowsWhenTailscaleFails()
    {
        var t = NewTailnet(_ => throw new InvalidOperationException("boom"));
        t.Refresh();
        t.Serve(8787);
        t.Unserve();
        Assert.Null(t.Host);
    }

    [Fact]
    public void NothingThrowsOnGarbageOutput()
    {
        var t = NewTailnet(_ => "not json");
        t.Refresh();
        t.Serve(8787);
        Assert.Null(t.Host);
    }

    static string Mapped(int port) =>
        "{\"Web\":{\"pc.ts.net:443\":{\"Handlers\":{\"/\":{\"Proxy\":\"http://127.0.0.1:" + port + "\"}}}}}";

    // A Tailscale whose `serve status` shows our mapping once `serve --bg` has run, and prints
    // `said` for `serve --bg`.
    Tailnet ServingTailnet(string said = "", Settings? settings = null)
    {
        bool served = false;
        return NewTailnet(args =>
        {
            if (args == "status --json") return StatusJson;
            if (args.StartsWith("serve --bg ", StringComparison.Ordinal)) { served = true; return said; }
            if (args == "serve --https=443 off") { served = false; return ""; }
            if (args == "serve status --json") return served ? Mapped(8787) : "{}";
            return "";
        }, settings);
    }

    [Fact]
    public void ServeAddsTheMappingWhenMissingAndChecksItAfterwards()
    {
        var t = ServingTailnet();
        Assert.Null(t.Serve(8787));
        Assert.Equal(new[] { "serve status --json", "serve --bg 8787", "serve status --json" }, commands);
    }

    [Fact]
    public void ServeRemembersThatLidAwakeAddedTheMapping()
    {
        var settings = NewSettings();
        var t = ServingTailnet(settings: settings);
        t.Serve(8787);
        Assert.True(settings.Get<bool?>(Tailnet.ServedKey));
    }

    [Fact]
    public void ServeUsesTheGivenPort()
    {
        // Mapped to another port is not mapped to ours.
        var t = NewTailnet(_ => Mapped(8787));
        t.Serve(9123);
        Assert.Equal(new[] { "serve status --json", "serve --bg 9123", "serve status --json" }, commands);
    }

    // 8787 must not count as mapped because 87870 (or 18787) is.
    [Fact]
    public void TheMappingMustBeExactlyOurPort()
    {
        Assert.True(Tailnet.Maps(Mapped(8787), 8787));
        Assert.False(Tailnet.Maps(Mapped(87870), 8787));
        Assert.False(Tailnet.Maps(Mapped(18787), 8787));
        Assert.False(Tailnet.Maps("{\"Proxy\":\"http://10.127.0.0.1:8787\"}", 8787));
        Assert.False(Tailnet.Maps("", 8787));

        var t = NewTailnet(_ => Mapped(87870));
        Assert.NotNull(t.Serve(8787));
        Assert.Contains("serve --bg 8787", commands);
    }

    [Fact]
    public void AMissingMappingAfterServeIsReported()
    {
        // Tailscale accepted the command but serve status still shows nothing of ours.
        var t = NewTailnet(_ => "{}");
        var reported = new List<string>();
        t.NotPublished = reported.Add;
        Assert.NotNull(t.Serve(8787));
        t.SetServed(true, 8787).Wait();
        Assert.Single(reported);
    }

    [Fact]
    public void ALinkFromTailscaleIsReportedWithWhatItPrinted()
    {
        const string consent = "Serve is not enabled on your tailnet.\nTo enable, visit:\n\n         https://login.tailscale.com/f/serve?node=n1234\n";
        var t = ServingTailnet(consent);
        var reported = new List<string>();
        t.NotPublished = reported.Add;
        t.SetServed(true, 8787).Wait();
        var said = Assert.Single(reported);
        Assert.Contains("https://login.tailscale.com/f/serve?node=n1234", said);
        // On one line, for the activity log.
        Assert.DoesNotContain("\n", said);
    }

    [Fact]
    public void ThePagesOwnAddressIsNotALinkToFollow()
    {
        // What `serve --bg` prints when it works: the page's own address and where it points.
        const string ok = "Available within your tailnet:\n\nhttps://desk-pc.tail1234.ts.net/\n|-- proxy http://127.0.0.1:8787\n\nServe started and running in the background.";
        Assert.False(Tailnet.AsksForConsent(ok));
        Assert.True(Tailnet.AsksForConsent("To enable, visit: https://login.tailscale.com/f/https?node=n1"));
        var t = ServingTailnet(ok);
        var reported = new List<string>();
        t.NotPublished = reported.Add;
        t.SetServed(true, 8787).Wait();
        Assert.Empty(reported);
    }

    // A Tailscale whose `serve --bg` never finishes (it waits for the owner to allow HTTPS), and
    // whose `serve status` shows the mapping afterwards anyway.
    Tailnet TimingOutTailnet(string printed)
    {
        bool ran = false;
        return Tailnet.WithCli(NewSettings(), args =>
        {
            if (args.StartsWith("serve --bg ", StringComparison.Ordinal))
            {
                ran = true;
                return new Tailnet.CliResult(false, "", printed, TimedOut: true);
            }
            return new Tailnet.CliResult(true, args == "serve status --json" && ran ? Mapped(8787) : "{}");
        });
    }

    [Fact]
    public void ATimeoutIsReported()
    {
        // A command that never finished is not trusted, even with a mapping in place.
        Assert.Equal("Tailscale did not answer in time.", TimingOutTailnet("").Serve(8787));
        Assert.Equal("Waiting for HTTPS: https://login.tailscale.com/f/https?node=n1",
            TimingOutTailnet("Waiting for HTTPS:\n  https://login.tailscale.com/f/https?node=n1\n").Serve(8787));
    }

    [Fact]
    public void NothingIsReportedWhenTailscaleIsNotInstalled()
    {
        var settings = NewSettings();
        var t = Tailnet.WithCli(settings, _ => new Tailnet.CliResult(false, "", Missing: true));
        Assert.Null(t.Serve(8787));
        Assert.Null(settings.Get<bool?>(Tailnet.ServedKey));
    }

    [Fact]
    public void WhatTailscaleSaidIsCutTo300Characters()
    {
        var said = Tailnet.Brief(new string('x', 1000));
        Assert.Equal(Tailnet.MaxSaid, said.Length);
        Assert.Equal(300, Tailnet.MaxSaid);
        Assert.Equal("a b c", Tailnet.Brief("  a\r\n  b\tc  "));
    }

    [Fact]
    public async Task TheRecheckAddsServeBackWhenItWentMissing()
    {
        var t = ServingTailnet();
        await t.SetServed(true, 8787);
        Assert.Equal(1, commands.Count(c => c == "serve --bg 8787"));
        // Still there: the minute check only looks.
        await t.Recheck();
        Assert.Equal(1, commands.Count(c => c == "serve --bg 8787"));
        // Removed by someone else: the next check adds it back, and reads the name again.
        t.Unserve();
        commands.Clear();
        await t.Recheck();
        Assert.Equal(new[] { "status --json", "serve status --json", "serve --bg 8787", "serve status --json" }, commands);
    }

    [Fact]
    public async Task TheRecheckDoesNothingWhileOff()
    {
        var t = ServingTailnet();
        await t.SetServed(true, 8787);
        await t.SetServed(false, 8787);
        commands.Clear();
        await t.Recheck();
        Assert.Empty(commands);
    }

    [Fact]
    public async Task SetServedAppliesInTheBackgroundAndCanBeWaitedFor()
    {
        var t = ServingTailnet();
        await t.SetServed(true, 8787);
        Assert.Equal(new[] { "status --json", "serve status --json", "serve --bg 8787", "serve status --json" }, commands);
        commands.Clear();
        await t.SetServed(false, 8787);
        Assert.Equal(new[] { "serve --https=443 off" }, commands);
    }

    // Turning off removes only what Lid Awake added: the owner's own Serve setup stays.
    [Fact]
    public async Task OffLeavesAServeSetupLidAwakeDidNotAdd()
    {
        var settings = NewSettings();
        var t = NewTailnet(args => args == "status --json" ? StatusJson : Mapped(8787), settings);
        await t.SetServed(true, 8787);
        Assert.DoesNotContain("serve --bg 8787", commands);
        await t.SetServed(false, 8787);
        Assert.DoesNotContain("serve --https=443 off", commands);
    }

    [Fact]
    public async Task OffRemovesWhatLidAwakeAddedAndForgetsIt()
    {
        var settings = NewSettings();
        settings.Set(Tailnet.ServedKey, true);  // left from before a crash
        var t = NewTailnet(_ => "", settings);
        await t.SetServed(false, 8787);
        Assert.Equal(new[] { "serve --https=443 off" }, commands);
        Assert.False(settings.Get<bool?>(Tailnet.ServedKey));
        // Already forgotten: a second off leaves Serve alone.
        commands.Clear();
        await t.SetServed(false, 8787);
        Assert.Empty(commands);
    }

    [Fact]
    public async Task TheLatestWishWins()
    {
        var settings = NewSettings();
        settings.Set(Tailnet.ServedKey, true);
        var t = NewTailnet(args => args == "status --json" ? StatusJson : "{}", settings);
        var on = t.SetServed(true, 8787);
        var off = t.SetServed(false, 8787);
        await Task.WhenAll(on, off);
        Assert.Equal("serve --https=443 off", commands.Last());
    }

    [Fact]
    public void ServeDoesNothingWhenAlreadyMapped()
    {
        var t = NewTailnet(_ => Mapped(8787));
        Assert.Null(t.Serve(8787));
        Assert.Equal(new[] { "serve status --json" }, commands);
    }

    [Fact]
    public void ServeTurnsFunnelOff()
    {
        var t = NewTailnet(_ => "{\"AllowFunnel\":{\"pc.ts.net:443\":true}}");
        t.Serve(8787);
        Assert.Contains("funnel --https=443 off", commands);
    }

    // The tray says so (a balloon and a log line), as the Mac does; only when Funnel was on, and a
    // handler that throws doesn't stop the page from being published.
    [Fact]
    public void FunnelTurnedOffIsToldOnlyWhenItWasOn()
    {
        var told = 0;
        var off = NewTailnet(_ => Mapped(8787));
        off.FunnelTurnedOff += () => told++;
        off.Serve(8787);
        Assert.Equal(0, told);

        commands.Clear();
        var on = NewTailnet(args => args.StartsWith("serve status", StringComparison.Ordinal) ? "{\"AllowFunnel\":{\"pc.ts.net:443\":true}}" : "");
        on.FunnelTurnedOff += () => told++;
        on.FunnelTurnedOff += () => throw new InvalidOperationException("tray gone");
        on.Serve(8787);
        Assert.Equal(1, told);
        Assert.Contains("funnel --https=443 off", commands);
        Assert.Contains("serve --bg 8787", commands);
    }

    [Fact]
    public void UnserveRemovesEverything()
    {
        var t = NewTailnet(_ => "");
        t.Unserve();
        Assert.Equal(new[] { "serve --https=443 off" }, commands);
    }

    // The flag that says "Lid Awake added the mapping" is cleared only when the mapping is really
    // gone: the off command worked, or `serve status` (which itself worked) no longer shows it.
    const string Off = "serve --https=443 off";
    const string StatusCommand = "serve status --json";

    [Fact]
    public async Task AFailedRemovalKeepsTheFlagAndTheNextCallsRetry()
    {
        var settings = NewSettings();
        settings.Set(Tailnet.ServedKey, true);
        bool offWorks = false;
        var t = NewTailnet(args => args == Off ? (offWorks ? "" : null) : args == StatusCommand ? Mapped(8787) : "", settings);

        // Turning off: the command fails and the mapping is still there.
        await t.SetServed(false, 8787);
        Assert.True(settings.Get<bool?>(Tailnet.ServedKey));
        Assert.Equal(new[] { Off, StatusCommand }, commands);

        // The minute check (the tray runs it while off, too) tries again.
        commands.Clear();
        await t.Recheck();
        Assert.True(settings.Get<bool?>(Tailnet.ServedKey));
        Assert.Equal(new[] { Off, StatusCommand }, commands);

        // So does the next turn-off or start; once it works the flag is cleared.
        offWorks = true;
        commands.Clear();
        await t.SetServed(false, 8787);
        Assert.Equal(new[] { Off }, commands);
        Assert.False(settings.Get<bool?>(Tailnet.ServedKey));

        // Nothing left to retry.
        commands.Clear();
        await t.Recheck();
        Assert.Empty(commands);
    }

    [Fact]
    public void ASuccessfulRemovalClearsTheFlag()
    {
        var settings = NewSettings();
        settings.Set(Tailnet.ServedKey, true);
        var t = NewTailnet(_ => "", settings);
        t.UnserveIfOurs();
        Assert.False(settings.Get<bool?>(Tailnet.ServedKey));
        Assert.Equal(new[] { Off }, commands);
    }

    [Fact]
    public void AFailedCommandWithNoMappingLeftClearsTheFlag()
    {
        // The off command failed, but the status shows nothing of ours: it is gone anyway.
        var settings = NewSettings();
        settings.Set(Tailnet.ServedKey, true);
        var t = NewTailnet(args => args == Off ? null : "{}", settings);
        t.UnserveIfOurs();
        Assert.False(settings.Get<bool?>(Tailnet.ServedKey));
        Assert.Equal(new[] { Off, StatusCommand }, commands);
    }

    [Fact]
    public void AMappingToAnotherPortDoesNotKeepTheFlag()
    {
        var settings = NewSettings();
        settings.Set(Tailnet.ServedKey, true);
        var t = NewTailnet(args => args == Off ? null : Mapped(87870), settings);
        t.UnserveIfOurs();
        Assert.False(settings.Get<bool?>(Tailnet.ServedKey));
    }

    [Fact]
    public void WhenTheStatusCannotBeReadEitherTheFlagStays()
    {
        var settings = NewSettings();
        settings.Set(Tailnet.ServedKey, true);
        var t = NewTailnet(_ => null, settings);
        t.UnserveIfOurs();
        Assert.True(settings.Get<bool?>(Tailnet.ServedKey));
    }

    [Fact]
    public void LoopbackIsOnlyThePcItself()
    {
        Assert.True(Tailnet.IsLoopback("127.0.0.1"));
        Assert.True(Tailnet.IsLoopback("localhost"));
        Assert.False(Tailnet.IsLoopback("[::1]"));
        Assert.False(Tailnet.IsLoopback("pc.example.ts.net"));
    }

    [Fact]
    public void OwnerNeedsTheRightNameAndAccount()
    {
        var t = NewTailnet(_ => StatusJson);
        t.Refresh();
        Assert.True(t.IsOwner("desk-pc.tail1234.ts.net", "OWNER@example.com"));
        Assert.False(t.IsOwner("desk-pc.tail1234.ts.net", "someone@example.com"));
        Assert.False(t.IsOwner("other.tail1234.ts.net", "owner@example.com"));
        Assert.False(t.IsOwner("desk-pc.tail1234.ts.net", null));
    }

    [Fact]
    public void NobodyIsOwnerBeforeTheNameIsKnown()
    {
        Assert.False(NewTailnet(_ => null).IsOwner("pc.example.ts.net", "owner@example.com"));
    }
}
