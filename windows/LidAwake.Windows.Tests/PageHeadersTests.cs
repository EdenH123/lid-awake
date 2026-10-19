using LidAwake;
using Xunit;

public class PageHeadersTests
{
    [Fact]
    public void HashesEveryInlineScript()
    {
        var html = "<script>a()</script><p>x</p><script>\n b();\n</script>";
        var hashes = PageHeaders.ScriptHashes(html);
        Assert.Equal(2, hashes.Split(' ').Length);
        Assert.Contains("'sha256-", hashes);
        var csp = PageHeaders.Csp(html, "pc.tail1234.ts.net");
        Assert.Contains("connect-src 'self' wss://pc.tail1234.ts.net", csp);
    }

    [Fact]
    public void ScriptsWithAttributesAreNotHashed()
    {
        // The page may only run scripts with no attributes, as on the Mac.
        Assert.Equal("", PageHeaders.ScriptHashes("<script src=\"x.js\"></script>"));
    }

    [Fact]
    public void CspHasTheFixedParts()
    {
        var csp = PageHeaders.Csp("<script>a()</script>", "pc.example.ts.net");
        Assert.StartsWith("default-src 'none'; script-src 'sha256-", csp);
        Assert.EndsWith("base-uri 'none'; form-action 'none'; frame-ancestors 'none'", csp);
        Assert.Contains("style-src 'unsafe-inline'; img-src 'self' blob:;", csp);
        Assert.Contains("worker-src 'self'; manifest-src 'self';", csp);
    }
}
