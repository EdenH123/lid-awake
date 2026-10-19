namespace LidAwake;

// "Turn off after": the chosen length (kept across restarts) and when the current run ends, as
// in main.swift on the Mac. Changing the length while on restarts the countdown from now. All of
// it is under one lock, so the web server's threads can read and change it while the tray's timer
// checks it on the UI thread.
public sealed class AutoOff
{
    // The same choices as the Mac menu and docs/API.md POST /timer; 0 = never.
    public static readonly (int Minutes, string Title)[] Choices =
    {
        (0, "Never"), (15, "15 minutes"), (30, "30 minutes"), (60, "1 hour"), (120, "2 hours"), (240, "4 hours"), (480, "8 hours"),
    };

    public static bool IsChoice(int minutes) => Choices.Any(c => c.Minutes == minutes);

    readonly Settings settings;
    readonly Func<DateTimeOffset> now;
    readonly object gate = new();
    int minutes;
    bool running;
    DateTimeOffset? end;

    public AutoOff(Settings settings, Func<DateTimeOffset>? now = null)
    {
        this.settings = settings;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        var saved = settings.Get<int?>("timerMinutes") ?? 0;
        minutes = IsChoice(saved) ? saved : 0;
    }

    // The chosen length in minutes (0 = never).
    public int Minutes
    {
        get { lock (gate) return minutes; }
    }

    // Choose a length. While Lid Awake is on, the countdown restarts from now.
    public void Set(int chosen)
    {
        if (!IsChoice(chosen)) throw new ArgumentOutOfRangeException(nameof(chosen), "Not one of the timer choices.");
        lock (gate)
        {
            settings.Set("timerMinutes", chosen);
            minutes = chosen;
            if (running) end = EndFromNow(chosen);
        }
    }

    // Lid Awake was turned on: the countdown starts from now.
    public void Start()
    {
        lock (gate)
        {
            running = true;
            end = EndFromNow(minutes);
        }
    }

    // Lid Awake was turned off: no countdown.
    public void Stop()
    {
        lock (gate)
        {
            running = false;
            end = null;
        }
    }

    // Seconds left, only while on, with a timer, and in the future (docs/API.md 6.1).
    public double? Remaining()
    {
        lock (gate)
        {
            if (!running || end is not { } e) return null;
            var left = (e - now()).TotalSeconds;
            return left > 0 ? left : null;
        }
    }

    // True once the time is up while on.
    public bool Expired()
    {
        lock (gate) return running && end is { } e && now() >= e;
    }

    DateTimeOffset? EndFromNow(int chosen) => chosen > 0 ? now().AddMinutes(chosen) : null;
}
