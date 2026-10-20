using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;

namespace LidAwake;

// Troubleshooting lines, for finding out why something went wrong (the Mac writes the same kind of
// lines to ~/Library/Logs/LidAwake-stream.log, through StreamStats.swift's note):
// %LOCALAPPDATA%\LidAwake\LidAwake-diagnostics.log. It is the same kind of file as the activity log
// (AccessLog: the time with its offset, two spaces, the text; over 1 MB the file is kept as .old and
// a new one started). What goes in: one line per screen or camera session when it ends
// (StreamStats), the phone's stay-awake state when it changes (StayAwakeLog), what each push service
// answered to a phone alert (PushLine), screen capture and encoder fallbacks, and Tailscale Serve
// errors. Never a password, token, cookie, key, message text or IP address: callers write none, and
// every line also goes through Scrub, which keeps only the host of a link and hides addresses and
// long key-like strings. Never throws.
public sealed class DiagnosticsLog
{
    public const string FileName = "LidAwake-diagnostics.log";

    readonly AccessLog file;

    public DiagnosticsLog(string? folder = null, long maxBytes = AccessLog.DefaultMaxBytes, Func<DateTimeOffset>? now = null) =>
        file = new AccessLog(folder, maxBytes, now, FileName);

    public string FilePath => file.FilePath;

    public void Write(string text)
    {
        try { file.Write(Scrub(AccessLog.Clean(text))); }
        catch (Exception) { }
    }

    // The newest lines first (the tests).
    public List<string> Recent(int count) => file.Recent(count);

    static readonly Regex Link = new(@"\b[a-z][a-z0-9+.\-]*://(?:[^\s/?#@""'<>\[]*@)?(\[[^\]\s]*\]|[^\s/:?#""'<>\[]+)(?::\d+)?([^\s""'<>]*)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex Email = new(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9\-]+(?:\.[A-Za-z0-9\-]+)*\.[A-Za-z]{2,}", RegexOptions.CultureInvariant);
    // The user's folder name may have spaces: everything up to the next slash goes.
    static readonly Regex UserFolder = new(@"\b[A-Za-z]:[\\/]Users[\\/][^\\/""'<>]+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    static readonly Regex V4 = new(@"(?<![\d.])\d{1,3}(?:\.\d{1,3}){3}(?![\d.])", RegexOptions.CultureInvariant);
    static readonly Regex V6 = new(@"(?<![\w:])[0-9A-Fa-f]*:[0-9A-Fa-f:.]*(?:%\w+)?(?![\w:])", RegexOptions.CultureInvariant);
    static readonly Regex KeyLike = new(@"[A-Za-z0-9_\-+/=]{40,}", RegexOptions.CultureInvariant);

    public const string HiddenAddress = "(address)";
    public const string Hidden = "(hidden)";
    public const string HiddenAccount = "(account)";
    public const string HiddenProfile = "%USERPROFILE%";

    // A link becomes its host ("web.push.apple.com/…"; a bracketed IPv6 host "(address)"); an
    // email address (a Tailscale login, say) "(account)"; C:\Users\<name> "%USERPROFILE%"; an IP
    // address other than the PC's own (127.0.0.1, ::1) "(address)"; 40 or more letters, digits and
    // base64 signs in a row (a key, a token, a cookie) "(hidden)".
    public static string Scrub(string? text)
    {
        var s = text ?? "";
        s = Link.Replace(s, m => LinkHost(m.Groups[1].Value) + (m.Groups[2].Value.Length > 1 ? "/…" : ""));
        s = Email.Replace(s, HiddenAccount);
        s = UserFolder.Replace(s, HiddenProfile);
        s = V4.Replace(s, m => IPAddress.TryParse(m.Value, out var a) && !IPAddress.IsLoopback(a) ? HiddenAddress : m.Value);
        s = V6.Replace(s, m => m.Value.Count(c => c == ':') >= 2 && IPAddress.TryParse(m.Value, out var a)
            && a.AddressFamily == AddressFamily.InterNetworkV6 && !IPAddress.IsLoopback(a) ? HiddenAddress : m.Value);
        return KeyLike.Replace(s, Hidden);
    }

    static string LinkHost(string host) =>
        host.StartsWith('[') && !(IPAddress.TryParse(host.Trim('[', ']'), out var a) && IPAddress.IsLoopback(a)) ? HiddenAddress : host;

    // What a push service answered for one phone alert: the alert's kind, the service's host (never
    // the rest of the endpoint, which identifies the phone, nor its keys or the signed token) and the
    // status code, or why there was none.
    public static string PushLine(string kind, string endpoint, int status, string? failure = null)
    {
        var host = Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) ? uri.Host : "?";
        var what = AlertKind.IsKind(kind) ? kind : "?";
        return status > 0
            ? $"alert {what} → {host} {status.ToString(CultureInfo.InvariantCulture)}"
            : $"alert {what} → {host} not delivered ({failure ?? "no answer"})";
    }

    // The push service didn't answer: in a few words, never the exception's text (it can hold the URL).
    public static string Failure(Exception e) => e switch
    {
        TaskCanceledException or TimeoutException => $"no answer in {WebPush.SendTimeout.TotalSeconds:0} s",
        HttpRequestException { InnerException: SocketException s } => "network: " + s.SocketErrorCode,
        HttpRequestException => "network error",
        _ => e.GetType().Name,
    };
}

// The phone's "keep the screen on" state (the page's wake lock), which GET /status?wl=… carries
// (docs/API.md 5.2): a line when it changes for that caller, as on the Mac (Server.swift). The
// caller is told apart by Tailscale's X-Forwarded-For, which is never written.
public sealed class StayAwakeLog
{
    public const int MaxLength = 60;
    // Forgotten all at once past this many callers, so a stream of new ones can't grow it.
    public const int MaxCallers = 64;

    readonly object gate = new();
    readonly Dictionary<string, string> last = new(StringComparer.Ordinal);

    // Letters, digits, spaces and ":()-" only, at most 60 characters (the Mac's rule). Null: nothing left.
    public static string? Clean(string? raw)
    {
        if (string.IsNullOrEmpty(raw)) return null;
        var sb = new StringBuilder();
        foreach (var c in raw)
        {
            if (sb.Length >= MaxLength) break;
            if (char.IsLetterOrDigit(c) || " :()-".Contains(c)) sb.Append(c);
        }
        return sb.Length == 0 ? null : sb.ToString();
    }

    // The line to write when this caller's state is new or changed; otherwise null.
    public string? Seen(string? caller, string? raw)
    {
        if (Clean(raw) is not { } state) return null;
        var who = caller ?? "";
        lock (gate)
        {
            if (last.TryGetValue(who, out var before) && before == state) return null;
            if (!last.ContainsKey(who) && last.Count >= MaxCallers) last.Clear();
            last[who] = state;
        }
        return "phone stay-awake: " + state;
    }
}

// One screen or camera session, from the first picture asked for until it stops: how long, how much
// went out, and what made it. Line() is written to the diagnostics log when the session ends (the
// Mac writes a summary every 2 seconds instead). Safe from any thread.
public sealed class StreamStats
{
    readonly string what;
    readonly Func<long> clock;
    readonly long started;
    readonly object gate = new();
    int frames, pictures, keyframes, fallbacks;
    long bytes;
    string? capture;
    readonly List<string> encoders = new();

    // `what` is "screen" or "camera"; `clock` counts milliseconds (the tests pass their own).
    public StreamStats(string what, Func<long>? clock = null)
    {
        this.what = what;
        this.clock = clock ?? (() => Stopwatch.GetTimestamp() * 1000 / Stopwatch.Frequency);
        started = this.clock();
    }

    // An encoded video frame sent out.
    public void Frame(int size, bool key)
    {
        lock (gate)
        {
            frames++;
            bytes += size;
            if (key) keyframes++;
        }
    }

    // A still picture (JPEG) sent out instead of video.
    public void Picture(int size)
    {
        lock (gate)
        {
            pictures++;
            bytes += size;
        }
    }

    public void Capture(string kind) { lock (gate) capture = kind; }

    // The encoder now in use. When another one was in use before in this session, that one's name
    // is returned and it counts as a fallback (a hardware encoder failed, say); otherwise null.
    public string? Encoder(string name)
    {
        lock (gate)
        {
            var before = encoders.Count > 0 ? encoders[^1] : null;
            if (before == name) return null;
            encoders.Add(name);
            if (before == null) return null;
            fallbacks++;
            return before;
        }
    }

    public void Fallback() { lock (gate) fallbacks++; }

    public string Line()
    {
        lock (gate) return Format(what, clock() - started, frames, pictures, bytes, keyframes, capture, encoders, fallbacks);
    }

    // "screen session: 2m05s, 3612 frames (28.9 fps), 41.2 MB, 12 keyframes, capture Desktop
    // Duplication, encoder H264 Encoder MFT, 0 fallbacks"; still pictures are counted with the
    // frames and also said apart, and several encoders are listed in the order they were used.
    public static string Format(string what, long milliseconds, int frames, int pictures, long bytes, int keyframes,
                                string? capture, IReadOnlyList<string> encoders, int fallbacks)
    {
        var inv = CultureInfo.InvariantCulture;
        int all = frames + pictures;
        double seconds = Math.Max(0, milliseconds) / 1000.0;
        var fps = seconds > 0 ? (all / seconds).ToString("0.0", inv) : "0.0";
        var sb = new StringBuilder();
        sb.Append(what).Append(" session: ").Append(Duration(milliseconds)).Append(", ");
        sb.Append(all.ToString(inv)).Append(all == 1 ? " frame (" : " frames (").Append(fps).Append(" fps");
        if (pictures > 0) sb.Append(", ").Append(pictures.ToString(inv)).Append(" still");
        sb.Append("), ").Append(Size(bytes));
        sb.Append(", ").Append(keyframes.ToString(inv)).Append(keyframes == 1 ? " keyframe" : " keyframes");
        if (capture != null) sb.Append(", capture ").Append(capture);
        sb.Append(", encoder ").Append(encoders.Count == 0 ? "none" : string.Join(" then ", encoders));
        sb.Append(", ").Append(fallbacks.ToString(inv)).Append(fallbacks == 1 ? " fallback" : " fallbacks");
        return sb.ToString();
    }

    // 45s, 2m05s, 1h02m05s.
    public static string Duration(long milliseconds)
    {
        long s = Math.Max(0, milliseconds) / 1000;
        var inv = CultureInfo.InvariantCulture;
        if (s < 60) return s.ToString(inv) + "s";
        if (s < 3600) return (s / 60).ToString(inv) + "m" + (s % 60).ToString("00", inv) + "s";
        return (s / 3600).ToString(inv) + "h" + (s / 60 % 60).ToString("00", inv) + "m" + (s % 60).ToString("00", inv) + "s";
    }

    // 812 B, 41.2 KB, 41.2 MB (powers of 1000).
    public static string Size(long bytes)
    {
        var inv = CultureInfo.InvariantCulture;
        if (bytes < 1000) return bytes.ToString(inv) + " B";
        if (bytes < 1_000_000) return (bytes / 1000.0).ToString("0.0", inv) + " KB";
        return (bytes / 1_000_000.0).ToString("0.0", inv) + " MB";
    }
}
