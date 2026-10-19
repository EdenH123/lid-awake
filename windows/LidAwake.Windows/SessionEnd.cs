namespace LidAwake;

// Signing out or shutting down while Lid Awake is on. Windows does not wait long for an app while
// the session ends, so this puts the lid action back, writes the log line, and then waits at most
// a few seconds for Serve to be removed (through the same one-at-a-time path as Quit). The tray
// calls it from SystemEvents.SessionEnding; tests call it with a fake powercfg and Tailscale.
public static class SessionEnd
{
    public const string LogLine = "Turned off: signing out or shutting down";
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

    // `unserve` starts removing Serve, e.g. PhoneAccess.Set(false). Never throws.
    public static void TurnOff(Settings settings, Func<Task> unserve, AccessLog? log, TimeSpan? maxWait = null)
    {
        KeepAwake.Off(settings);
        // Written before the wait, so the line is there even if Windows ends the app during it.
        try { log?.Write(LogLine); }
        catch (Exception) { }
        try { unserve().Wait(maxWait ?? MaxWait); }
        catch (Exception) { }
    }
}
