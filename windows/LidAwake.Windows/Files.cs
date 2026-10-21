namespace LidAwake;

// Writing a file whole: to a temp file of its own first, flushed to the disk, then moved over the
// old one, so a crash or power cut mid-write can't leave a truncated file. A temp name of its own
// each time, so writers at once never share one. Used for the settings (Settings), History's file,
// Claude's usage file and Claude Code's settings (ClaudeUsage, ClaudeSettingsFile).
//
// Another program (an antivirus scan, the search indexer, a backup) may hold the old file open
// for a moment just after it was written, and replacing it fails meanwhile: the move is tried
// again a few times, about 0.6 seconds in all, before the failure counts.
static class Files
{
    static readonly int[] RetryMs = { 10, 20, 40, 80, 160, 320 };

    // Throws when it failed after the retries: the old file is left as it was, and the temp file
    // is removed.
    public static void WriteAtomic(string path, string text)
    {
        var tmp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var file = new FileStream(tmp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                var bytes = System.Text.Encoding.UTF8.GetBytes(text);
                file.Write(bytes, 0, bytes.Length);
                file.Flush(flushToDisk: true);
            }
            for (int attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(tmp, path, overwrite: true);
                    return;
                }
                catch (Exception e) when (attempt < RetryMs.Length && IsBusy(e))
                {
                    Thread.Sleep(RetryMs[attempt]);
                }
            }
        }
        catch (Exception)
        {
            try { File.Delete(tmp); }
            catch (Exception) { }
            throw;
        }
    }

    // The same, never throwing: false when it failed.
    public static bool TryWriteAtomic(string path, string text)
    {
        try
        {
            WriteAtomic(path, text);
            return true;
        }
        catch (Exception) { return false; }
    }

    const int SharingViolation = 32, LockViolation = 33;

    // Held by another program for now (access denied, a sharing or lock violation), as opposed to
    // a folder that is gone or a disk that is full.
    static bool IsBusy(Exception e) =>
        e is UnauthorizedAccessException
        || (e is IOException && (e.HResult & 0xFFFF) is SharingViolation or LockViolation);
}
