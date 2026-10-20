namespace LidAwake;

// Signing out or shutting down while Lid Awake is on. Windows does not wait long for an app while
// the session ends, so the slow parts start first, side by side: Serve's removal (through the same
// one-at-a-time path as Quit) and the brightness, both off this thread. Then the lid action is put
// back and the log line written, and only then the history's unsaved rows are written (off this
// thread too). All of them are then waited for together, at most a few seconds. The tray calls it
// from SystemEvents.SessionEnding; tests call it with a fake powercfg and Tailscale.
public static class SessionEnd
{
    public const string LogLine = "Turned off: signing out or shutting down";
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

    // `machine` is the machine-state settings (Settings.ForMachine) KeepAwake keeps its saved lid
    // values in. `unserve` starts removing Serve at once, PhoneAccess.Off (not Set(false), which
    // waits PhoneAccess.OffDelay first). `brightness` starts putting the screen's brightness back
    // (LidScreen.SetOn(false)), and `flush` writes the history (HistoryStore.Flush), both
    // optional. Never throws.
    public static void TurnOff(Settings machine, Func<Task> unserve, AccessLog? log, TimeSpan? maxWait = null,
                               Func<Task>? brightness = null, Action? flush = null)
    {
        var serve = Begin(unserve);
        var screen = Begin(brightness);
        try { KeepAwake.Off(machine); }
        catch (Exception) { }
        // Written before the wait, so the line is there even if Windows ends the app during it.
        try { log?.Write(LogLine); }
        catch (Exception) { }
        WaitAll(maxWait, serve, screen, Flush(flush));
    }

    // Only the Serve part (and the history), for a session that ends while Lid Awake is already
    // off: a removal may still be waiting its 8 seconds (PhoneAccess.OffDelay). PhoneAccess.Off
    // does nothing to a Serve setup that isn't Lid Awake's. Waits at most `maxWait`; never throws.
    public static void Unserve(Func<Task> unserve, TimeSpan? maxWait = null, Action? flush = null)
    {
        var serve = Begin(unserve);
        WaitAll(maxWait, serve, Flush(flush));
    }

    static Task Begin(Func<Task>? start)
    {
        if (start == null) return Task.CompletedTask;
        try { return start() ?? Task.CompletedTask; }
        catch (Exception e) { return Task.FromException(e); }
    }

    static Task Flush(Action? flush)
    {
        if (flush == null) return Task.CompletedTask;
        try { return Task.Run(flush); }
        catch (Exception e) { return Task.FromException(e); }
    }

    static void WaitAll(TimeSpan? maxWait, params Task[] tasks)
    {
        try { Task.WaitAll(tasks, maxWait ?? MaxWait); }
        catch (Exception) { }
    }
}
