using System.Management;
using System.Runtime.InteropServices;

namespace LidAwake;

// The built-in screen's brightness, as Windows reports it (WMI, root\WMI). Tests give LidScreen a
// fake. Both calls may throw; LidScreen treats that, and a null or false, as "nothing changed".
public interface IBrightness
{
    // The current and the lowest level (0 to 100), or null when this PC has no built-in screen
    // whose brightness Windows can set (a desktop PC, an external monitor only).
    BrightnessReading? Read();
    // Sets the level; false when Windows refused.
    bool Set(int level);
}

public readonly record struct BrightnessReading(int Current, int Lowest);

// Keeps the display from timing out while someone watches it from the phone. Tests give LidScreen
// a fake; the real one holds ES_DISPLAY_REQUIRED on a thread of its own.
public interface IDisplayHold
{
    void Hold(bool on);
}

// What Lid Awake does, and honestly reports, about the screen while the lid is closed. Windows
// does not let an app keep a laptop's built-in panel on behind a closed lid: on many laptops the
// panel (or the graphics driver) switches it off when the lid closes. What this does:
//  - Reports the lid (GUID_LIDSWITCH_STATE_CHANGE) and the display (GUID_CONSOLE_DISPLAY_STATE)
//    for /status: lidClosed, displayAsleep. The phone draws the PC dark for either.
//  - While on and someone watches (ScreenWatch), holds ES_DISPLAY_REQUIRED, so the display does
//    not time out.
//  - While on with the lid closed, sets the lowest brightness (WMI), after saving the old level
//    in machine.json; puts it back when the lid opens, when Lid Awake turns off or quits, when
//    the session ends, and at the next start after a crash.
// All the Windows work (WMI, the saved level) runs on the thread pool, one pass at a time, and
// each pass looks at the state as it is then: several events in a row cost one pass, and a pass
// can never undo a later event. Nothing here throws. If WMI refuses, nothing changes and one
// line is written to the activity log (again only after something works, then fails again).
public sealed class LidScreen
{
    // GUID_LIDSWITCH_STATE_CHANGE: data is 0 for closed, 1 for open.
    public static readonly Guid LidSwitch = new("BA3E0F4D-B817-4094-A2D1-D56379E6A0F3");
    // GUID_CONSOLE_DISPLAY_STATE: data is 0 for off, 1 for on, 2 for dimmed.
    public static readonly Guid ConsoleDisplay = new("6FE69556-704A-47A0-8F24-C28D936FDA47");

    // machine.json: the brightness to put back (a whole number, 0 to 100).
    public const string SavedKey = "savedBrightness";

    readonly IBrightness brightness;
    readonly IDisplayHold hold;
    readonly Settings machine;
    readonly Action<string>? log;
    readonly Func<Action, Task> run;
    readonly Func<TimeSpan, Task> delay;

    readonly object gate = new();   // the state below
    bool lidClosed;
    int displayState = 1;           // 1: on, until Windows says otherwise
    bool on;
    bool watching;
    bool screenOff;
    int retryGen;                   // changes with every outside event: a waiting retry is then stale
    int retries;                    // retries of a failed put-back since the last outside event

    // A put-back that failed (Windows may not be ready just after the lid opens) is tried again
    // after these delays, off the UI thread, and then left to the next event.
    public static readonly TimeSpan[] RetryDelays = { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) };

    readonly object pass = new();   // one pass at a time; guards what follows
    bool holding;
    bool lowered;
    bool failureLogged;

    // `run` starts a pass off the calling thread (the default: the thread pool); `delay` waits
    // before a retry (Task.Delay). Tests run the first on the spot and control the second.
    public LidScreen(IBrightness brightness, IDisplayHold hold, Settings machine, Action<string>? log = null,
                     Func<Action, Task>? run = null, Func<TimeSpan, Task>? delay = null)
    {
        this.brightness = brightness;
        this.hold = hold;
        this.machine = machine;
        this.log = log;
        this.run = run ?? (a => Task.Run(a));
        this.delay = delay ?? (d => Task.Delay(d));
    }

    public bool LidClosed { get { lock (gate) return lidClosed; } }
    public bool DisplayAsleep { get { lock (gate) return displayState == 0; } }
    public bool ScreenOff { get { lock (gate) return screenOff; } }

    // A power-setting notification (PowerSettingWindow, on the UI thread). Anything that is not
    // one of the two settings, or has no data, is ignored.
    public void OnPowerSetting(Guid setting, byte[] data)
    {
        if (data.Length < 4) return;
        uint value = BitConverter.ToUInt32(data, 0);
        if (setting == LidSwitch) SetLid(value == 0);
        else if (setting == ConsoleDisplay && value <= 2) SetDisplay((int)value);
    }

    public void SetLid(bool closed)
    {
        lock (gate)
        {
            if (lidClosed == closed) return;
            lidClosed = closed;
        }
        Schedule();
    }

    public void SetDisplay(int state)
    {
        lock (gate)
        {
            if (displayState == state) return;
            displayState = state;
        }
        // The display coming back on (the lid opened, WMI is ready again) is a good moment to put
        // back a brightness that could not be put back before. Otherwise /status is all it changes.
        if (state == 1) Schedule();
    }

    // The phone turned the display off (POST /power displayoff, the control task): the display
    // must not be held awake then. ScreenWatch.SetScreenOff is the hook.
    public void SetScreenOff(bool value)
    {
        lock (gate)
        {
            if (screenOff == value) return;
            screenOff = value;
        }
        Schedule();
    }

    // Lid Awake turned on or off (the tray). The task is done when everything that had to be put
    // back has been: Quit and the end of the session wait for it, a little.
    public Task SetOn(bool value)
    {
        lock (gate) on = value;
        return Schedule();
    }

    // Someone is, or is no longer, watching the screen (ScreenWatch).
    public void SetWatching(bool value)
    {
        lock (gate)
        {
            if (watching == value) return;
            watching = value;
        }
        Schedule();
    }

    // At start: puts back a brightness that a crash or a forced stop left lowered.
    public Task Recover() => Schedule();

    // An event from outside: a pass now. It also makes any waiting retry stale (this pass is
    // the newer look at the state) and gives the next failure its retries again.
    Task Schedule()
    {
        lock (gate)
        {
            retryGen++;
            retries = 0;
        }
        return Start();
    }

    Task Start()
    {
        try { return run(Pass); }
        catch (Exception) { return Task.CompletedTask; }
    }

    // After a failed put-back: one more pass in a few seconds, at most RetryDelays.Length times
    // for one outside event. Nothing waits on this thread.
    void RetryRestoreSoon()
    {
        TimeSpan wait;
        int generation;
        lock (gate)
        {
            if (retries >= RetryDelays.Length) return;
            wait = RetryDelays[retries++];
            generation = retryGen;
        }
        try
        {
            _ = delay(wait).ContinueWith(_ =>
            {
                bool current;
                lock (gate) current = generation == retryGen;
                if (current) Start();
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
        catch (Exception) { }
    }

    void Note(string text)
    {
        try { log?.Invoke(text); }
        catch (Exception) { }
    }

    // Says it once; the next line is said only after something worked in between.
    void Fail(string text)
    {
        if (failureLogged) return;
        failureLogged = true;
        Note(text);
    }

    // The state as it is now, applied. Idempotent, so any number of events can share a pass.
    void Pass()
    {
        try
        {
            lock (pass)
            {
                bool wantHold, wantLow;
                lock (gate)
                {
                    wantHold = on && watching && !screenOff;
                    wantLow = on && lidClosed;
                }
                ApplyHold(wantHold);
                if (wantLow) Lower(); else Restore();
            }
        }
        catch (Exception) { }
    }

    void ApplyHold(bool want)
    {
        if (want == holding) return;
        try
        {
            hold.Hold(want);
            holding = want;
        }
        catch (Exception) { }
    }

    // The lid is closed while on: save the level (once) and set the lowest.
    void Lower()
    {
        if (lowered) return;
        bool savedNow = false;
        try
        {
            if (brightness.Read() is not { } reading)
            {
                Fail("Screen brightness not changed: this PC has no built-in screen brightness to set");
                return;
            }
            // A level already saved is the real one (a crash left it lowered): keep it.
            if (machine.Get<int?>(SavedKey) is null)
            {
                machine.Set(SavedKey, Math.Clamp(reading.Current, 0, 100));
                savedNow = true;
            }
            if (!brightness.Set(Math.Clamp(reading.Lowest, 0, 100)))
                throw new InvalidOperationException("Windows refused");
            lowered = true;
            failureLogged = false;
        }
        catch (TimeoutException e)
        {
            // Windows may still apply the change later, so it is not known that nothing was
            // lowered: the saved level stays, and the next put-back (or start) uses it.
            Fail("Screen brightness not changed: " + e.Message);
        }
        catch (Exception e)
        {
            // Windows said no: nothing was lowered, so nothing is waiting to be put back.
            if (savedNow) Forget();
            Fail("Screen brightness not changed: " + e.Message);
        }
    }

    // Put the saved level back, if there is one: the lid opened, Lid Awake turned off or quit, the
    // session ends, or this is the first pass after a crash. No saved level: no WMI call.
    void Restore()
    {
        try
        {
            if (machine.Get<int?>(SavedKey) is not int saved)
            {
                lowered = false;
                return;
            }
            if (!brightness.Set(Math.Clamp(saved, 0, 100)))
                throw new InvalidOperationException("Windows refused");
            // Forgotten only once it is confirmed, so a failure is tried again.
            Forget();
            lowered = false;
            failureLogged = false;
        }
        catch (Exception e)
        {
            Fail("Screen brightness not put back: " + e.Message);
            RetryRestoreSoon();
        }
    }

    void Forget()
    {
        try { machine.Set<int?>(SavedKey, null); }
        catch (Exception) { }
    }
}

// What the screen viewing calls when its watchers change (Program: LiveViewers from the live
// socket, Capturing from the capture). Without a listener nothing happens. Program connects it
// to the LidScreen.
public static class ScreenWatch
{
    static volatile Action<bool>? listener;
    static volatile Action<bool>? offListener;

    // `watching` hears SetWatching; `screenOff` (optional) hears SetScreenOff.
    public static void Connect(Action<bool>? watching, Action<bool>? screenOff = null)
    {
        listener = watching;
        offListener = screenOff;
    }

    // The phone turned the display off (true) or woke it (false): while it is off, the display is
    // not held awake. The control task calls this where it sets its own screenOff.
    public static void SetScreenOff(bool off)
    {
        try { offListener?.Invoke(off); }
        catch (Exception) { }
    }

    public static void SetWatching(bool watching)
    {
        try { listener?.Invoke(watching); }
        catch (Exception) { }
    }

    // The screen's two sources, joined: phones on the live socket (LiveServer.ViewersChanged) and
    // the capture (ScreenFeed.WatchingChanged, which also runs for /screen.jpg and a little after
    // the last watcher). Watched while either says so, so one going quiet can't drop the hold
    // the other still needs. Told under one lock, so the last thing told is the truth.
    static readonly object sources = new();
    static int liveViewers;
    static bool capturing;

    public static void LiveViewers(int count)
    {
        lock (sources)
        {
            liveViewers = Math.Max(0, count);
            SetWatching(liveViewers > 0 || capturing);
        }
    }

    public static void Capturing(bool on)
    {
        lock (sources)
        {
            capturing = on;
            SetWatching(liveViewers > 0 || capturing);
        }
    }

    // For the tests: back to nobody watching, without telling anyone.
    internal static void ResetSources()
    {
        lock (sources) { liveViewers = 0; capturing = false; }
    }
}

// The real brightness: WMI, root\WMI. WmiMonitorBrightness has one instance per built-in panel
// (none on a desktop PC); WmiMonitorBrightnessMethods.WmiSetBrightness sets it. Every call is
// bounded: the WMI timeouts, and a hard limit on the wait for the call. Call it off the UI thread.
public sealed class WmiBrightness : IBrightness
{
    const string Scope = @"\\.\root\WMI";
    static readonly TimeSpan CallLimit = TimeSpan.FromSeconds(8);
    static readonly TimeSpan WmiTimeout = TimeSpan.FromSeconds(5);

    static ManagementScope Connect()
    {
        var scope = new ManagementScope(Scope, new ConnectionOptions { Timeout = WmiTimeout });
        scope.Connect();
        return scope;
    }

    static ManagementObjectSearcher Search(ManagementScope scope, string query) =>
        new(scope, new ObjectQuery(query), new System.Management.EnumerationOptions { Timeout = WmiTimeout, ReturnImmediately = false });

    // One call at a time, in the order asked: a call that timed out may still finish later, and
    // then it finishes before the next one starts, never after it.
    readonly OrderedCalls calls = new();

    T Bounded<T>(Func<T> work) => calls.Run(() =>
    {
        try { return work(); }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.Timedout)
        {
            throw new TimeoutException("Windows did not answer in time");
        }
    }, CallLimit);

    public BrightnessReading? Read() => Bounded(ReadNow);

    public bool Set(int level) => Bounded(() => SetNow(level));

    static BrightnessReading? ReadNow()
    {
        var scope = Connect();
        using var searcher = Search(scope, "SELECT CurrentBrightness, Level FROM WmiMonitorBrightness WHERE Active = TRUE");
        using var found = searcher.Get();
        foreach (ManagementBaseObject panel in found)
        {
            using (panel)
            {
                if (panel["CurrentBrightness"] is not byte current) continue;
                int lowest = panel["Level"] is byte[] { Length: > 0 } levels ? levels.Min() : 0;
                return new BrightnessReading(current, lowest);
            }
        }
        return null;
    }

    static bool SetNow(int level)
    {
        var scope = Connect();
        using var searcher = Search(scope, "SELECT * FROM WmiMonitorBrightnessMethods WHERE Active = TRUE");
        using var found = searcher.Get();
        bool any = false;
        foreach (ManagementBaseObject methods in found)
        {
            using (methods)
            {
                // WmiSetBrightness(Timeout uint32, Brightness uint8): 1 is the seconds a fade may take.
                ((ManagementObject)methods).InvokeMethod("WmiSetBrightness", new object[] { 1u, (byte)Math.Clamp(level, 0, 100) });
                any = true;
            }
        }
        return any;
    }
}

// Runs calls one after another, in the order asked, each waited for at most `limit` (the time
// queued behind an earlier call counts). A call that is given up on still runs, and still in its
// place, so a late finish can never land after a call asked for later.
public sealed class OrderedCalls
{
    readonly object gate = new();
    Task last = Task.CompletedTask;

    // Throws TimeoutException when the call has not finished in time, and the call's own
    // exception when it fails.
    public T Run<T>(Func<T> work, TimeSpan limit)
    {
        Task<T> mine;
        lock (gate)
        {
            mine = last.ContinueWith(_ => work(), CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            last = mine;
        }
        try
        {
            if (!mine.Wait(limit)) throw new TimeoutException("Windows did not answer in time");
        }
        catch (AggregateException e)
        {
            throw e.InnerException ?? e;
        }
        return mine.Result;
    }
}

// ES_DISPLAY_REQUIRED, held by a thread of its own (the request belongs to the thread that made
// it), the way KeepAwake holds the idle-sleep request. Hold(false) lets the display time out again.
public sealed class DisplayHold : IDisplayHold
{
    const uint ES_CONTINUOUS = 0x80000000, ES_DISPLAY_REQUIRED = 0x2;

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    readonly object gate = new();
    Thread? holder;
    ManualResetEventSlim? release;

    public void Hold(bool on)
    {
        lock (gate)
        {
            if (on)
            {
                if (holder != null) return;
                var done = new ManualResetEventSlim(false);
                release = done;
                holder = new Thread(() =>
                {
                    SetThreadExecutionState(ES_CONTINUOUS | ES_DISPLAY_REQUIRED);
                    done.Wait();
                    SetThreadExecutionState(ES_CONTINUOUS);
                }) { IsBackground = true, Name = "keep-display" };
                holder.Start();
            }
            else
            {
                release?.Set();
                release = null;
                holder = null;
            }
        }
    }
}

// A hidden message-only window that Windows sends the lid and display notifications to
// (RegisterPowerSettingNotification). Create it on the UI thread, which pumps its messages;
// Dispose unregisters. Each notification is a POWERBROADCAST_SETTING: a GUID, then DataLength
// (a DWORD), then Data.
public sealed class PowerSettingWindow : NativeWindow, IDisposable
{
    const int WM_POWERBROADCAST = 0x0218;
    const int PBT_POWERSETTINGCHANGE = 0x8013;
    const uint DEVICE_NOTIFY_WINDOW_HANDLE = 0;
    const int MaxData = 64;
    static readonly IntPtr HWND_MESSAGE = new(-3);

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr RegisterPowerSettingNotification(IntPtr recipient, ref Guid powerSetting, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool UnregisterPowerSettingNotification(IntPtr handle);

    readonly Action<Guid, byte[]> onSetting;
    readonly List<IntPtr> registrations = new();

    public PowerSettingWindow(Action<Guid, byte[]> onSetting, Action<string>? log = null)
    {
        this.onSetting = onSetting;
        try
        {
            CreateHandle(new CreateParams { Caption = "Lid Awake power settings", Parent = HWND_MESSAGE });
            foreach (var setting in new[] { LidScreen.LidSwitch, LidScreen.ConsoleDisplay })
            {
                var guid = setting;
                var handle = RegisterPowerSettingNotification(Handle, ref guid, DEVICE_NOTIFY_WINDOW_HANDLE);
                if (handle != IntPtr.Zero) registrations.Add(handle);
                else log?.Invoke($"Couldn't listen for lid and screen changes (Windows error {Marshal.GetLastWin32Error()})");
            }
        }
        catch (Exception e)
        {
            // No notifications: the lid and screen are reported as open and on, nothing else breaks.
            log?.Invoke("Couldn't listen for lid and screen changes: " + e.Message);
        }
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == WM_POWERBROADCAST && m.WParam.ToInt64() == PBT_POWERSETTINGCHANGE)
        {
            try
            {
                if (Read(m.LParam) is { } setting) onSetting(setting.Guid, setting.Data);
            }
            catch (Exception) { }
            m.Result = (IntPtr)1;
            return;
        }
        base.WndProc(ref m);
    }

    // POWERBROADCAST_SETTING at `lParam`: copies it out (at most 20 + MaxData bytes) and parses it.
    static (Guid Guid, byte[] Data)? Read(IntPtr lParam)
    {
        if (lParam == IntPtr.Zero) return null;
        int length = Marshal.ReadInt32(lParam, 16);
        if (length < 0 || length > MaxData) return null;
        var raw = new byte[HeaderSize + length];
        Marshal.Copy(lParam, raw, 0, raw.Length);
        return Parse(raw);
    }

    const int HeaderSize = 20;

    // The layout: the setting's Guid at 0 (16 bytes), DataLength (a DWORD) at 16, Data at 20.
    // null when the bytes are shorter than that says.
    internal static (Guid Guid, byte[] Data)? Parse(byte[] raw)
    {
        if (raw.Length < HeaderSize) return null;
        int length = BitConverter.ToInt32(raw, 16);
        if (length < 0 || length > MaxData || raw.Length < HeaderSize + length) return null;
        return (new Guid(raw.AsSpan(0, 16)), raw.AsSpan(HeaderSize, length).ToArray());
    }

    public void Dispose()
    {
        foreach (var handle in registrations)
        {
            try { UnregisterPowerSettingNotification(handle); }
            catch (Exception) { }
        }
        registrations.Clear();
        try { DestroyHandle(); }
        catch (Exception) { }
    }
}
