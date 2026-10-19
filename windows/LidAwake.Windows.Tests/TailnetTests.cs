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

    [Fact]
    public void NothingThrowsWhenTailscaleFails()
    {
        var t = NewTailnet(_ => throw new InvalidOperationException("boom"));
        t.Refresh();
        t.Serve();
        t.Unserve();
        Assert.Null(t.Host);
    }

    [Fact]
    public void NothingThrowsOnGarbageOutput()
    {
        var t = NewTailnet(_ => "not json");
        t.Refresh();
        t.Serve();
        Assert.Null(t.Host);
    }

    [Fact]
    public void ServeAddsTheMappingWhenMissing()
    {
        var t = NewTailnet(_ => "{}");
        t.Serve();
        Assert.Equal(new[] { "serve status --json", "serve --bg 8787" }, commands);
    }

    [Fact]
    public void ServeDoesNothingWhenAlreadyMapped()
    {
        var t = NewTailnet(_ => "{\"Web\":{\"pc.ts.net:443\":{\"Handlers\":{\"/\":{\"Proxy\":\"http://127.0.0.1:8787\"}}}}}");
        t.Serve();
        Assert.Equal(new[] { "serve status --json" }, commands);
    }

    [Fact]
    public void ServeTurnsFunnelOff()
    {
        var t = NewTailnet(_ => "{\"AllowFunnel\":{\"pc.ts.net:443\":true}}");
        t.Serve();
        Assert.Contains("funnel --https=443 off", commands);
    }

    [Fact]
    public void UnserveRemovesEverything()
    {
        var t = NewTailnet(_ => "");
        t.Unserve();
        Assert.Equal(new[] { "serve --https=443 off" }, commands);
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
