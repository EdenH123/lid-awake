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

    // Known answers, computed outside .NET: printf 'a()' | openssl dgst -sha256 -binary | base64
    // (and the same for "\n b();\n"). The bytes between the tags are hashed exactly as written.
    [Fact]
    public void HashesAreTheKnownAnswers()
    {
        var html = "<script>a()</script><p>x</p><script>\n b();\n</script>";
        Assert.Equal("'sha256-qVpDBgj7bpq5hMAcGp3AOc79J3Y1Z4HvySTwKrWDoy4=' 'sha256-BGtj7gyCwuPHj2IUl3OAH2YdwjT9txpMiTb0b97i8ns='",
            PageHeaders.ScriptHashes(html));
        Assert.Equal(
            "default-src 'none'; script-src 'sha256-qVpDBgj7bpq5hMAcGp3AOc79J3Y1Z4HvySTwKrWDoy4='; style-src 'unsafe-inline'; "
            + "img-src 'self' blob:; connect-src 'self' wss://127.0.0.1; worker-src 'self'; manifest-src 'self'; "
            + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'",
            PageHeaders.Csp("<script>a()</script>", "127.0.0.1"));
    }

    // The browser hashes a script after turning CR LF (and a lone CR) into LF, so a page checked
    // out with Windows line ends must give the same hashes as the LF page, or no script runs.
    [Fact]
    public void WindowsLineEndsHashAsTheBrowserSeesThem()
    {
        var lf = "<script>\n b();\n c();\n</script>";
        Assert.Equal(PageHeaders.ScriptHashes(lf), PageHeaders.ScriptHashes(lf.Replace("\n", "\r\n")));
        Assert.Equal(PageHeaders.ScriptHashes(lf), PageHeaders.ScriptHashes(lf.Replace("\n", "\r")));
        Assert.Equal("a\nb\nc\n", PageHeaders.Lf("a\r\nb\rc\n"));
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
