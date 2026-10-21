namespace LidAwake;

// Whether the phone can reach the page through Tailscale Serve. Serve is turned on only while
// our own web server holds its port: if another program has taken 127.0.0.1:8787, Serve would
// publish that program to the owner's phone, and the phone would send it the password and the
// session cookie. The same goes for the live socket on 8788 (/live), which gets the cookie in its
// handshake: it is mapped only while our own LiveServer holds that port.
public sealed class PhoneAccess
{
    // After turning off, Serve stays this long, so an open phone page hears "Lid Awake is off"
    // (the server already answers 423 to everything else) before the address goes away, as on the Mac.
    public static readonly TimeSpan OffDelay = TimeSpan.FromSeconds(8);

    readonly Tailnet tailnet;
    readonly WebServer server;
    readonly LiveServer? live;
    readonly Func<TimeSpan, CancellationToken, Task> delay;
    // Every wish is handed to Tailnet under this lock, so a removal that was waiting can never be
    // applied after a turn-on that came later.
    readonly object gate = new();
    CancellationTokenSource? pendingOff;
    Task pendingTask = Task.CompletedTask;
    bool served;  // the last wish handed to Tailnet was "on"

    // `delay` waits (Task.Delay; tests pass one they finish themselves).
    public PhoneAccess(Tailnet tailnet, WebServer server, LiveServer? live = null, Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        (this.tailnet, this.server, this.live) = (tailnet, server, live);
        this.delay = delay ?? ((wait, cancel) => Task.Delay(wait, cancel));
    }

    // False when the phone page is not running (its port was taken by another program).
    public bool PageRunning => server.IsRunning;

    public int Port => server.Port;

    // On: serve the page if our server is running, otherwise make sure our mapping is gone.
    // Off: remove what Lid Awake added (Tailnet.ServedKey), OffDelay later when it was on; a
    // turn-on meanwhile keeps it. Both go through Tailnet's one-at-a-time path; the task ends when
    // this call has been applied (or, for a removal that was waiting, when a turn-on replaced it).
    // Turned off again while a removal waits: that removal stays as it is (it isn't put off).
    public Task Set(bool on)
    {
        lock (gate)
        {
            if (!on && pendingOff != null) return pendingTask;
            CancelPendingOff();
            if (on || !served) return Apply(on);
            var wait = new CancellationTokenSource();
            pendingOff = wait;
            pendingTask = OffLater(wait);
            return pendingTask;
        }
    }

    // Off at once, with no wait: for Quit and for signing out or shutting down, which only wait a
    // few seconds for Serve to be removed.
    public Task Off()
    {
        lock (gate)
        {
            CancelPendingOff();
            return Apply(false);
        }
    }

    // The check every 60 seconds while on: Serve is added back if it went missing.
    public Task Recheck()
    {
        tailnet.LivePort = LivePort();
        return tailnet.Recheck();
    }

    async Task OffLater(CancellationTokenSource wait)
    {
        try { await delay(OffDelay, wait.Token); }
        catch (OperationCanceledException) { return; }
        catch (Exception) { }  // a wait that failed still removes it
        Task applied;
        lock (gate)
        {
            // Replaced by a later turn-on (or Off) while waiting.
            if (pendingOff != wait) return;
            pendingOff = null;
            wait.Dispose();
            applied = Apply(false);
        }
        await applied;
    }

    // Under `gate`.
    void CancelPendingOff()
    {
        if (pendingOff is not { } wait) return;
        pendingOff = null;
        try { wait.Cancel(); }
        catch (Exception) { }
        wait.Dispose();
    }

    // Under `gate`.
    Task Apply(bool on)
    {
        tailnet.LivePort = LivePort();
        bool serve = on && server.IsRunning;
        served = serve;
        return tailnet.SetServed(serve, server.Port);
    }

    // The live socket's port while our LiveServer holds it (and the page is ours), else 0.
    int LivePort() => live is { IsRunning: true } l && server.IsRunning ? l.Port : 0;
}
