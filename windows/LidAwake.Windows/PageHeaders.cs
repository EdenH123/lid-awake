using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace LidAwake;

// The page may only run its own inline scripts and talk to this computer, as on the Mac
// (docs/API.md 2.6 and 2.8).
public static class PageHeaders
{
    // One 'sha256-...' for the text between every bare <script> and the next </script>, with line
    // ends as the browser sees them: its HTML parser turns CR LF and a lone CR into LF before it
    // hashes a script, so a page checked out with Windows line ends must be hashed the same way.
    public static string ScriptHashes(string html) => string.Join(" ",
        Regex.Matches(html, "<script>([\\s\\S]*?)</script>")
             .Select(m => "'sha256-" + Convert.ToBase64String(SHA256.HashData(Encoding.UTF8.GetBytes(Lf(m.Groups[1].Value)))) + "'"));

    // CR LF and lone CR to LF, as the HTML parser does with the page it is given.
    public static string Lf(string text) => text.Replace("\r\n", "\n").Replace('\r', '\n');

    public static string Csp(string html, string host) => CspFromHashes(ScriptHashes(html), host);

    // For callers that hashed the page once, at start.
    public static string CspFromHashes(string hashes, string host) =>
        $"default-src 'none'; script-src {hashes}; style-src 'unsafe-inline'; "
        + $"img-src 'self' blob:; connect-src 'self' wss://{host}; worker-src 'self'; manifest-src 'self'; "
        + "base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    // On every HTTP response.
    public static readonly IReadOnlyDictionary<string, string> Common = new Dictionary<string, string>
    {
        ["Cache-Control"] = "no-store",
        ["X-Content-Type-Options"] = "nosniff",
        ["X-Frame-Options"] = "DENY",
        ["Cross-Origin-Resource-Policy"] = "same-origin",
        ["Referrer-Policy"] = "no-referrer",
    };
}
