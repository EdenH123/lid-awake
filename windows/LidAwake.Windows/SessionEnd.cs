namespace LidAwake;

// Signing out or shutting down while Lid Awake is on. Windows does not wait long for an app while
// the session ends, so this puts the lid action back, writes the log line, and then waits at most
// a few seconds for Serve to be removed (through the same one-at-a-time path as Quit). The tray
// calls it from SystemEvents.SessionEnding; tests call it with a fake powercfg and Tailscale.
public static class SessionEnd
{
    public const string LogLine = "Turned off: signing out or shutting down";
    public static readonly TimeSpan MaxWait = TimeSpan.FromSeconds(3);

    // `machine` is the machine-state settings (Settings.ForMachine) KeepAwake keeps its saved lid
    // values in. `unserve` starts removing Serve at once, PhoneAccess.Off (not Set(false), which
    // waits PhoneAccess.OffDelay first). Never throws.
    public static void TurnOff(Settings machine, Func<Task> unserve, AccessLog? log, TimeSpan? maxWait = null)
    {
        KeepAwake.Off(machine);
        // Written before the wait, so the line is there even if Windows ends the app during it.
        try { log?.Write(LogLine); }
        catch (Exception) { }
        Unserve(unserve, maxWait);
    }

    // Only the Serve part, for a session that ends while Lid Awake is already off: a removal may
    // still be waiting its 8 seconds (PhoneAccess.OffDelay). PhoneAccess.Off does nothing to a
    // Serve setup that isn't Lid Awake's. Waits at most `maxWait`; never throws.
    public static void Unserve(Func<Task> unserve, TimeSpan? maxWait = null)
    {
        try { unserve().Wait(maxWait ?? MaxWait); }
        catch (Exception) { }
    }
}
