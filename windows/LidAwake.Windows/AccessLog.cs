using System.Globalization;
using System.Text;

namespace LidAwake;

// Who did what from the phone, for the owner to look back on: one line each in
// %LOCALAPPDATA%\LidAwake\LidAwake-access.log (the previous 1 MB is kept as .old), like
// AccessLog.swift on the Mac. A line is the time with its offset, two spaces, then the text.
// Called from the web server's threads and from the tray, so every read and write is under a lock.
// A log that cannot be written must never break the request that wanted to write to it.
public sealed class AccessLog
{
    public const string FileName = "LidAwake-access.log";
    public const long DefaultMaxBytes = 1_000_000;
    public const int MaxTextLength = 300;

    readonly string path;
    readonly long maxBytes;
    readonly Func<DateTimeOffset> now;
    readonly object gate = new();

    public AccessLog(string? folder = null, long maxBytes = DefaultMaxBytes, Func<DateTimeOffset>? now = null)
    {
        folder ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LidAwake");
        path = Path.Combine(folder, FileName);
        this.maxBytes = maxBytes;
        this.now = now ?? (() => DateTimeOffset.Now);
    }

    public string FilePath => path;

    // Control characters removed, cut at 300 characters (as on the Mac, which counts Unicode scalars).
    public static string Clean(string text)
    {
        var sb = new StringBuilder();
        int count = 0;
        foreach (var rune in (text ?? "").EnumerateRunes())
        {
            if (Rune.IsControl(rune)) continue;
            if (count++ >= MaxTextLength) break;
            sb.Append(rune.ToString());
        }
        return sb.ToString();
    }

    public void Write(string text)
    {
        var line = now().ToString("yyyy-MM-dd'T'HH:mm:sszzz", CultureInfo.InvariantCulture) + "  " + Clean(text) + "\n";
        lock (gate)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                var info = new FileInfo(path);
                if (info.Exists && info.Length > maxBytes)
                    File.Move(path, path + ".old", overwrite: true);
                File.AppendAllText(path, line, new UTF8Encoding(false));
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    // The newest lines first, for the phone's Settings page. Empty if there is no log.
    public List<string> Recent(int count)
    {
        lock (gate)
        {
            try
            {
                if (!File.Exists(path)) return new();
                var lines = File.ReadAllText(path, Encoding.UTF8)
                    .Split('\n')
                    .Select(l => l.TrimEnd('\r'))
                    .Where(l => l.Length > 0)
                    .ToList();
                lines.Reverse();
                return lines.Take(count).ToList();
            }
            catch (IOException) { return new(); }
            catch (UnauthorizedAccessException) { return new(); }
        }
    }
}
