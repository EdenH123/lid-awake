namespace LidAwake;

// Lid Awake for Windows: a tray icon with the on/off switch. The phone page and its requests
// are the same as on the Mac (docs/API.md).
static class Program
{
    [STAThread]
    static void Main(string[] args)
    {
        // Claude Code's hooks and status line, and the installers' Claude setup, run LidAwake.exe
        // with a flag (ClaudeHook.cs): done here, before the single-instance check, never a tray.
        if (ClaudeHook.Run(args) is { } exitCode)
        {
            Environment.ExitCode = exitCode;
            return;
        }
        using var single = new Mutex(true, "local.lidawake", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        var settings = new Settings();
        // What is only true of this PC (the saved lid values and their power plan) is kept apart
        // from the roaming settings. Values an older version saved in the roaming file move first.
        var machine = Settings.ForMachine();
        KeepAwake.MigrateSavedLid(settings, machine);
        var auth = new Auth(settings);
        // This PC's Tailscale name and Serve state live in machine.json (moved there once).
        Tailnet.MoveToMachine(settings, machine);
        var tailnet = new Tailnet(machine);
        var log = new AccessLog();
        KeepAwake.Log = log.Write;
        WlanReader.Log = log.Write;
        // Put back what a crash or a forced stop left behind, before anything else (and before
        // any message could wait for a click): the lid action, and Serve if Lid Awake added it.
        KeepAwake.Off(machine);
        _ = tailnet.SetServed(false, WebServer.DefaultPort);
        // Learn this PC's Tailscale name in the background; the page is refused until it is known.
        Task.Run(tailnet.Refresh);
        // The server starts before the tray exists, so the tray knows whether it may serve the
        // page. Until the tray is there, the page answers as if Lid Awake were off.
        TrayApp? tray = null;
        // One StatusInfo for all requests: processor load and network speed are measured between
        // two requests. It is built on the web server's threads and touches no WinForms controls
        // (IsOn is a volatile field, and the timer is under its own lock).
        var readings = new SystemReadings();
        // The safety switches and the last temperatures read (battery and thermal zones, kept
        // apart), shared by /status, POST /limits and the tray's 15-second check. The temperatures
        // are only read off the UI thread.
        var limits = new Limits(settings);
        var heat = new HeatSensor(TemperatureSources.Read);
        var safety = new SafetyCheck(limits, heat, readings.Power);
        // The apps the owner picked for the phone (tray: Apps on phone…), and the Windows pieces behind them.
        var phoneApps = new PhoneApps(settings, new ProcessSource(), new WindowActions(), new WindowsIcons(), log);
        var startMenu = new StartMenuScanner(new ShortcutResolver());
        // The lid, the display and the screen's brightness behind a closed lid. The power
        // notifications come to a hidden window on this (the UI) thread; the screen work (WMI) is
        // done on the thread pool. A brightness a crash left lowered is put back right now.
        var screen = new LidScreen(new WmiBrightness(), new DisplayHold(), machine, log.Write);
        ScreenWatch.Connect(screen.SetWatching, screen.SetScreenOff);
        _ = screen.Recover();
        using var power = new PowerSettingWindow(screen.OnPowerSetting, log.Write);
        // The phone's mouse and keyboard on this PC, and lock / display off / wake (POST /input,
        // POST /power). Only through the owner's switches, which need the screen switch.
        // Nothing is accepted while Lid Awake is off (the sink checks it itself, for the socket).
        // With the lid closed, displayoff is refused (409); while the phone has turned the display
        // off, the lid screen doesn't hold it awake.
        var control = new RemoteControl(settings, () => tray?.IsOn == true, new InputInjector(new NativeInput()), new NativePower(),
            lidClosed: () => screen.LidClosed) { ScreenOffChanged = ScreenWatch.SetScreenOff };
        // A modifier key an earlier run left held is let go, as the Mac does at start-up.
        try { control.ReleaseModifiers(); }
        catch (Exception) { }
        // Screen viewing from the phone: the owner's switch (ScreenSwitch, also read by the control
        // above), the capture and encoders (only while someone watches), and the live socket on
        // 127.0.0.1:8788. "screenEncoder": "software" in settings.json skips the graphics card's
        // encoder. While watched (a phone on the socket, or the capture still running for a poll),
        // the lid screen keeps the display on.
        bool ScreenAllowed() => ScreenSwitch.IsOn(settings);
        // The screen and the camera use the same encoders (and the same "a hardware one failed"
        // memory, MfH264Encoder.HardwareFailed).
        bool hardwareEncoder = settings.Get<string>("screenEncoder") != "software";
        var grabbers = new ScreenGrabbers { Note = text => log.Write("Screen: " + text) };
        using var feed = new ScreenFeed(grabbers.Open,
            MfH264Encoder.Factory(allowHardware: hardwareEncoder),
            note: text => log.Write("Screen: " + text));
        feed.WatchingChanged += ScreenWatch.Capturing;
        var status = new StatusInfo(readings, limits, heat, phoneApps, ScreenAllowed) { Screen = screen, Control = control };
        // Phone alerts (Web Push). The key and the phones are this PC's own (machine.json); a new
        // password forgets the phones. Sign-ins are announced to them as `security` alerts.
        using var push = new WebPush(machine);
        auth.PasswordSet += push.RemoveAll;
        var alerts = new PhoneAlerts(push.Send, readings.Power, () => heat.Latest.Battery, () => limits.HeatGuard);
        // Claude plan usage (/status, the tray, the 90% alert) and the Claude session alerts (ClaudeAlerts.cs).
        var claudeUsage = new ClaudeUsage();
        status.Claude = claudeUsage;
        alerts.Claude = new ClaudeUsageAlert(claudeUsage, settings);
        using var claudeAlerts = new ClaudeAlerts((title, body) => push.Send(AlertKind.ClaudeSession, title, body),
            lidClosed: () => screen.LidClosed, log: log.Write);
        claudeAlerts.Start();
        // The camera and microphone (docs/API.md 5.4, 7.5), through its three seams: Task 18's
        // passkeys (a "camera" challenge, never a sign-in one), the screen's Media Foundation H.264
        // encoder, and the live socket (LiveServer hands it each phone's messages first, and tells
        // it when a socket closes). It needs the screen switch too (CameraSwitch). Nothing here
        // opens the camera: only a phone on the live socket with a ticket, while the owner's switch
        // is on. With the lid closed, a camera that can't open says so.
        ICameraPasskeys cameraPasskeys = new PasskeyCamera(auth.Passkeys);
        IVideoEncoderFactory cameraEncoders = new MfCameraEncoders(allowHardware: hardwareEncoder,
            note: log.Write);
        var cameraSwitch = new CameraSwitch(settings);
        status.CameraAllowed = () => cameraSwitch.Allowed;
        var cameraTickets = new CameraTickets();
        using var camera = new CameraService(cameraTickets, () => tray?.IsOn == true && cameraSwitch.Allowed,
            new CameraHardware(() => screen.LidClosed), cameraEncoders, new TalkPlayer(WasapiTalkOutput.Open), log);
        using var server = new WebServer(auth, tailnet, () => tray?.IsOn == true,
            () => status.Build(tray?.IsOn == true, tray?.Countdown),
            () => tray?.TurnOffFromPhone(), (title, body) =>
            {
                tray?.Announce(title, body);
                _ = push.SendSecurity(title, body);
            },
            log: log, setTimer: minutes => tray?.SetTimer(minutes),
            setLimit: (name, on) =>
            {
                // Stored here, on the web server's thread (a failed write answers 500), so the
                // next /status already shows it; then applied at the tray.
                limits.Set(name, on);
                tray?.LimitsChanged();
            },
            push: push, control: control, screen: feed, screenAllowed: ScreenAllowed);
        // The live socket (started below, after the page). Input commands on it go to the same
        // control as POST /input (7.6); camera messages go to the camera first, and a socket that
        // closes closes its camera.
        using var live = new LiveServer(auth, tailnet, () => tray?.IsOn == true, ScreenAllowed, feed, log: log, input: control,
            camera: camera);
        live.ViewersChanged += ScreenWatch.LiveViewers;
        // All three seams are there: "camera" is offered and its two routes exist (set before Start).
        if (CameraFeature.Ready(cameraPasskeys, cameraEncoders, live.CameraWired))
        {
            status.CameraFeature = true;
            server.Camera = new CameraAccess(cameraPasskeys, cameraTickets, () => cameraSwitch.Allowed);
        }
        camera.Start();
        // The graphs: a sample every minute while the app runs, off the UI thread.
        var history = new HistoryStore();
        server.History = history;
        using var sampler = new HistorySampler(history, readings, () => heat.Latest.Battery, processes: ProcessTable.Shared, report: new PowercfgReport())
            { FlushAtSessionEnd = true };
        server.Memory = new MemoryInfo(readings, ProcessTable.Shared);
        sampler.Start();
        server.Apps = phoneApps;
        string? startError = null;
        try { server.Start(); }
        catch (Exception e) { startError = e.Message; }
        string? liveError = null;
        try { live.Start(); }
        catch (Exception e) { liveError = e.Message; }
        tailnet.NotPublished = said => tray?.NotPublished(said);
        // The page works but the screen's /live isn't published: only the activity log says so.
        tailnet.LiveNotPublished = said => log.Write("Screen viewing not published by Tailscale (the phone page works): " + said);
        tray = new TrayApp(settings, machine, auth, new PhoneAccess(tailnet, server, live), log, limits, heat, safety, alerts, screen,
            phoneApps, startMenu, control, live, cameraSwitch, camera, tailnet, readings.Power, server.ScreenPolls,
            () => TrayText.MinutesToFull(readings.Charge()));
        // Claude in the tray (TrayClaude): its items after the safety limits, its "Claude 59% ·
        // 1h40m" as the tooltip's last line (the tray composes it), then attached, once the items
        // are in the menu (Attach finds the menu through them).
        var trayClaude = new TrayClaude(settings, claudeUsage, new ClaudeSettingsFile(), log.Write);
        tray.InsertAfterLimits(trayClaude.Items);
        tray.ExtraTooltip = trayClaude.TooltipText;
        trayClaude.Attach(tray.OnUi);
        // Told at the tray (not in a window that waits for a click), once the tray icon exists.
        if (startError != null) tray.PortInUse(startError);
        if (liveError != null) log.Write($"Live screen couldn't start on 127.0.0.1:{live.Port}: {liveError}");
        Application.Run(tray);
        tray.Dispose();
    }
}

sealed class TrayApp : ApplicationContext
{
    readonly Settings machine;  // the machine-state settings KeepAwake saves the lid values in
    readonly Settings settings;
    readonly LiveServer? live;
    // The owner's switch for screen viewing from the phone, off until ticked (ScreenSwitch).
    readonly ToolStripMenuItem screenItem = new("Allow screen from phone");
    readonly Auth auth;
    readonly PhoneAccess phone;
    readonly AccessLog log;
    readonly NotifyIcon icon;
    readonly SynchronizationContext ui;
    readonly ToolStripMenuItem toggle = new("Lid Awake on");
    readonly ToolStripMenuItem timerMenu = new("Turn off after");
    // The safety switches, with the Mac's wording.
    static readonly string BatteryTitle = $"Turn off below {Limits.BatteryLimit}% battery";
    static readonly string HeatTitle = $"Turn off if battery is hotter than {Limits.MaxBatteryTemp}°C";
    const string NoTemperature = " (no temperature on this PC)";
    readonly ToolStripMenuItem batteryItem = new(BatteryTitle);
    readonly ToolStripMenuItem heatItem = new(HeatTitle + NoTemperature) { Enabled = false };
    readonly Limits limits;
    readonly HeatSensor heat;
    readonly SafetyCheck safety;
    readonly PhoneApps? phoneApps;
    readonly IStartMenuSource? startMenu;
    // The phone alerts about Lid Awake itself: turned off (timer, safety limit), battery low, warm.
    readonly PhoneAlerts alerts;
    // While a turned-off alert is on its way (StopWithAlert): read by the safety check's thread.
    volatile bool stopping;
    // Changed by every SetAwake, so a turn-off that waited for the phones can tell it was overtaken.
    int generation;
    readonly LidScreen screen;
    // "Allow control from phone" (off to start with; it needs the screen switch).
    readonly RemoteControl? control;
    const string ControlTitle = "Allow control from phone";
    readonly ToolStripMenuItem controlItem = new(ControlTitle);
    // "Allow camera & microphone from phone", off by default; it needs the screen switch (Mac menu).
    const string CameraTitle = "Allow camera & microphone from phone";
    readonly ToolStripMenuItem cameraItem = new(CameraTitle);
    readonly CameraSwitch? cameraSwitch;
    readonly CameraService? camera;
    // "Set up Lid Awake" (Set up phone…), at most one; Tailscale is read for it in the background.
    readonly Tailnet? tailnet;
    readonly SingleWindow<SetupWindow> setup;
    // The battery and heat limits, every 15 seconds as on the Mac, on a thread-pool timer: the
    // temperature can take a few seconds to read. What it finds is applied on the UI thread.
    readonly System.Threading.Timer safetyTimer;
    // One check at a time; one asked for during a check runs right after it (never dropped).
    readonly Coalescer checks;
    // Turns off on the UI thread, deciding again there (two queued posts turn off once).
    readonly SafetyStop stopper;
    // Checked every couple of seconds on the UI thread: when the time is up, Lid Awake turns off.
    readonly System.Windows.Forms.Timer timerCheck = new() { Interval = 2000 };
    // Tailscale Serve is checked again every minute, as on the Mac (docs/API.md 9): added back
    // while on, and while off a removal that failed earlier is tried again.
    readonly System.Windows.Forms.Timer serveCheck = new() { Interval = 60_000 };
    // "The phone page isn't published yet" is shown once each time Lid Awake is turned on.
    bool warnedNotPublished;

    readonly ContextMenuStrip menu = new();
    // The greyed-out first line ("Turns off in 1:23:45", "Screen being viewed from your phone"…)
    // and the battery line, as on the Mac. Kept current every second while the menu is open.
    readonly ToolStripMenuItem statusLine = new(TrayText.Off) { Enabled = false };
    readonly ToolStripMenuItem infoLine = new(TrayText.NoBattery) { Enabled = false };
    // The line after the safety limits. Claude's items go right after it (InsertAfterLimits).
    readonly ToolStripSeparator afterLimits = new();
    // "Set phone password…", then "Change phone password…" once there is one.
    readonly ToolStripMenuItem passwordItem = new(TrayText.SetPassword);
    // "Signed-in devices" (rebuilt each time the menu opens).
    readonly ToolStripMenuItem devicesMenu = new("Signed-in devices");
    // GetSystemPowerStatus, for the battery line (none: "No battery info").
    readonly Func<PowerReading?>? power;
    // The batteries' own estimate of the minutes to full (BatteryCharge.MinutesToFull, read in the
    // background by SystemReadings); -1 when there is none, and the line says "charging".
    readonly Func<int>? minutesToFull;
    // Balloons and activity-log lines with the Mac's texts.
    readonly TrayNotices notices;
    // Every second, as the Mac's countdown timer: the icon (a privacy signal: what the phone is
    // doing shows within a second), the tooltip, and the two lines while the menu is open.
    readonly System.Windows.Forms.Timer activityTick = new() { Interval = 1000 };
    // The app's own icon, for the windows; the tray shows the drawn ones (TrayIcons), one per state,
    // size and taskbar colour, each drawn once.
    readonly Icon appIcon = LoadTrayIcon();
    readonly Dictionary<(TrayIconState State, int Size, bool Light), Icon> drawn = new();
    (TrayIconState State, int Size, bool Light)? shownIcon;
    // The taskbar's colour, read again only when Windows says a setting changed.
    bool lightTaskbar = TrayIcons.LightTaskbar();
    // GET /screen.jpg polls (the phone's still pictures): viewed, like a phone on the live socket.
    readonly ScreenPolls? screenPolls;

    // Raised on the UI thread each time the menu opens, after Lid Awake's own items are brought up
    // to date (for the Claude task's items). A handler that throws is ignored.
    public event Action? MenuOpening;

    // More for the tooltip, after Lid Awake's own lines (Claude usage, "59% · 1h40m"), read every
    // second on the UI thread. Null or empty: nothing. A getter that throws is ignored.
    public Func<string?>? ExtraTooltip { get; set; }

    // Written on the UI thread, read by the web server's threads.
    volatile bool isOn;
    public bool IsOn => isOn;

    // "Turn off after": its own lock makes it safe to read and change from the web server's threads.
    public AutoOff Countdown { get; }

    public TrayApp(Settings settings, Settings machine, Auth auth, PhoneAccess phone, AccessLog log,
                   Limits limits, HeatSensor heat, SafetyCheck safety, PhoneAlerts alerts, LidScreen screen,
                   PhoneApps? apps = null, IStartMenuSource? startMenu = null, RemoteControl? control = null,
                   LiveServer? live = null, CameraSwitch? cameraSwitch = null, CameraService? camera = null,
                   Tailnet? tailnet = null, Func<PowerReading?>? power = null, ScreenPolls? screenPolls = null,
                   Func<int>? minutesToFull = null)
    {
        this.tailnet = tailnet;
        this.power = power;
        this.minutesToFull = minutesToFull;
        this.screenPolls = screenPolls;
        notices = new TrayNotices(log.Write, (title, text, warning) =>
            icon!.ShowBalloonTip(warning ? 10000 : 5000, title, text, warning ? ToolTipIcon.Warning : ToolTipIcon.Info));
        setup = new SingleWindow<SetupWindow>(MakeSetupWindow, w => !w.IsDisposed, w =>
        {
            if (!w.Visible) w.Show();
            if (w.WindowState == FormWindowState.Minimized) w.WindowState = FormWindowState.Normal;
            w.Activate();
        });
        this.cameraSwitch = cameraSwitch;
        this.camera = camera;
        phoneApps = apps;
        this.startMenu = startMenu;
        this.control = control;
        this.machine = machine;
        this.settings = settings;
        this.live = live;
        this.auth = auth;
        this.phone = phone;
        this.log = log;
        this.limits = limits;
        this.heat = heat;
        this.safety = safety;
        this.alerts = alerts;
        this.screen = screen;
        Countdown = new AutoOff(settings);
        checks = new Coalescer(CheckSafety);
        // The phones are told first; StopWithAlert sets `stopping`, so isOn reads false from then on.
        stopper = new SafetyStop(() => isOn && !stopping, safety, why => StopWithAlert(why.AlertReason, () =>
        {
            SetAwake(false);
            log.Write(why.LogOff);
            // icon is assigned below, before the timer starts or anything can post this.
            icon!.ShowBalloonTip(10000, "Lid Awake turned off", why.OffBody, ToolTipIcon.Warning);
        }));
        safetyTimer = new System.Threading.Timer(_ => checks.Request(read: true), null, Timeout.Infinite, Timeout.Infinite);
        toggle.Click += (_, _) =>
        {
            if (isOn) { SetAwake(false); return; }
            TurnOn();
        };
        batteryItem.Click += (_, _) => FlipLimit(Limits.Battery, !limits.BatteryGuard);
        heatItem.Click += (_, _) => FlipLimit(Limits.Heat, !limits.HeatGuard);
        ShowLimits();
        foreach (var (minutes, title) in AutoOff.Choices)
        {
            var choice = minutes;  // the menu item keeps its own copy
            var item = new ToolStripMenuItem(title) { Tag = choice };
            item.Click += (_, _) => SetTimer(choice);
            timerMenu.DropDownItems.Add(item);
        }
        ShowTimerChoice();
        timerCheck.Tick += (_, _) => CheckTimer();
        timerCheck.Start();
        // In the background: the UI thread never waits for Tailscale.
        serveCheck.Tick += (_, _) => phone.Recheck();
        serveCheck.Start();
        // The Mac's menu, in its order: the switch and its status line; the timer and the safety
        // limits; (Claude's items); the battery line, the phone page and the phone's switches; the
        // password, setup, devices and apps.
        menu.Items.Add(toggle);
        menu.Items.Add(statusLine);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(timerMenu);
        menu.Items.Add(batteryItem);
        menu.Items.Add(heatItem);
        menu.Items.Add(afterLimits);
        menu.Items.Add(infoLine);
        menu.Items.Add("Open live page", null, (_, _) => OpenLivePage());
        // "Allow screen from phone" (off to start with), then "Allow control from phone", which needs it.
        if (live != null)
        {
            screenItem.Checked = ScreenSwitch.IsOn(settings);
            screenItem.Click += (_, _) => SetScreenAllowed(!ScreenSwitch.IsOn(settings));
            menu.Items.Add(screenItem);
        }
        if (control != null)
        {
            controlItem.Click += (_, _) => FlipControl();
            menu.Items.Add(controlItem);
            ShowControl();
        }
        // "Allow camera & microphone from phone", which needs the screen switch too.
        cameraItem.Click += (_, _) => FlipCamera();
        if (cameraSwitch != null) menu.Items.Add(cameraItem);
        ShowCamera();
        // Shown as they are now, and read again in the background (the item follows by Changed).
        menu.Opening += (_, _) => { ShowLimits(); ShowScreen(); ShowControl(); ShowCamera(); RefreshTemperature(); };
        menu.Items.Add(new ToolStripSeparator());
        passwordItem.Click += (_, _) => ShowPasswordForm();
        menu.Items.Add(passwordItem);
        menu.Items.Add("Set up phone…", null, (_, _) => ShowSetup());
        menu.Items.Add(devicesMenu);
        menu.Items.Add(passkeysMenu);
        menu.Opening += (_, _) =>
        {
            ShowPasskeys();
            ShowDevices();
            ShowPassword();
            ShowActivity(menuOpening: true);
            try { MenuOpening?.Invoke(); }
            catch (Exception) { }
        };
        ShowPasskeys();
        ShowDevices();
        ShowPassword();
        if (phoneApps != null && startMenu != null) menu.Items.Add("Apps on phone…", null, (_, _) => ShowAppsForm());
        menu.Items.Add("Show activity log", null, (_, _) => ShowActivityLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Lid Awake", null, (_, _) =>
        {
            // Wait (a little) for Serve to be removed, through the same one-at-a-time path as
            // turning off, so a turn-on still being applied cannot add it back after it. At once,
            // not after PhoneAccess.OffDelay as a plain turn-off.
            SetAwake(false);
            var unserved = phone.Off();
            // ... and for the screen's brightness to be put back (done off this thread).
            try { Task.WaitAll(new[] { unserved, screen.SetOn(false) }, TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
            icon!.Visible = false;
            CloseSetup();
            ExitThread();
        });
        icon = new NotifyIcon { Icon = appIcon, Text = "Lid Awake", ContextMenuStrip = menu, Visible = true };
        // A lid action or Serve left over from a crash was put back in Main, before the tray.
        // Nothing is reachable from the phone until Lid Awake is turned on.
        // The tray may only be changed on its own thread, and the web server runs on others.
        // WinForms installs its context once the first control exists; make sure it is there.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ui = SynchronizationContext.Current!;
        // The drawn icon and the tooltip from now on, every second.
        ShowActivity();
        activityTick.Tick += (_, _) => ShowActivity();
        activityTick.Start();
        // Tailscale Funnel found on and turned off (on a background thread): said at the tray.
        if (tailnet != null) tailnet.FunnelTurnedOff += () => OnUi(notices.FunnelTurnedOff);
        // Signing out or shutting down while on. Subscribed here, after the context above, so the
        // handler runs on this (the UI) thread. SystemEvents' handlers are static: removed again
        // in ExitThreadCore.
        try { Microsoft.Win32.SystemEvents.SessionEnding += OnSessionEnding; }
        catch (Exception) { }
        // Light or dark taskbar chosen in Settings: the icon follows.
        try { Microsoft.Win32.SystemEvents.UserPreferenceChanged += OnPreferenceChanged; }
        catch (Exception) { }
        // The heat item follows whether a temperature can be read. The first read starts now, in
        // the background, and then every 15 seconds (once a minute while off).
        heat.Changed += () => OnUi(ShowLimits);
        safetyTimer.Change(TimeSpan.Zero, HeatSensor.CheckEvery);
        // A new PC: the setup window opens by itself until a phone password is set (the page
        // refuses every sign-in without one), once the tray is running.
        if (SetupModel.OpensAtStart(auth.HasPassword)) OnUi(ShowSetup);
    }

    // ---- Set up Lid Awake (the Mac's Setup.swift) ----

    SetupWindow MakeSetupWindow() => new(
        new SetupModel(auth, log.Write,
            () => tailnet?.CheckSetup() ?? new Tailnet.SetupState(false, false, false),
            () => tailnet?.Host, () => isOn, () => ScreenSwitch.IsOn(settings)),
        turnOn: TurnOn, allowScreen: () => SetScreenAllowed(true), screenFeature: live != null, icon: appIcon);

    // Opened again, the window there is comes to the front.
    void ShowSetup()
    {
        try { setup.Show(); }
        catch (Exception e) { log.Write("Set up Lid Awake couldn't open: " + e.Message); }
    }

    void CloseSetup()
    {
        try { setup.Current?.Dispose(); }
        catch (Exception) { }
    }

    // A turn-on from the tray or the setup window: refused, with a balloon, when a limit is reached.
    void TurnOn()
    {
        if (isOn || !TurnOnAllowed()) return;
        SetAwake(true);
        AskForLocation();
    }

    protected override void ExitThreadCore()
    {
        try { Microsoft.Win32.SystemEvents.SessionEnding -= OnSessionEnding; }
        catch (Exception) { }
        try { Microsoft.Win32.SystemEvents.UserPreferenceChanged -= OnPreferenceChanged; }
        catch (Exception) { }
        timerCheck.Stop();
        serveCheck.Stop();
        activityTick.Stop();
        try { safetyTimer.Dispose(); }
        catch (Exception) { }
        try { control?.ReleaseHeld(); }
        catch (Exception) { }
        CloseSetup();
        base.ExitThreadCore();
    }

    // Windows is signing out or shutting down: turn off now, so the lid action is put back and the
    // page is not left on Tailscale, waiting at most a few seconds (SessionEnd).
    void OnSessionEnding(object? sender, Microsoft.Win32.SessionEndingEventArgs e)
    {
        // Already off: only a removal still waiting its 8 seconds, done now.
        if (!isOn) { SessionEnd.Unserve(phone.Off); return; }
        isOn = false;
        Countdown.Stop();
        CloseSockets();
        try
        {
            toggle.Checked = false;
            ShowActivity();
        }
        catch (Exception) { }
        camera?.StopAll();
        // The brightness goes back at the same time (off this thread), not after the rest.
        var screenDone = screen.SetOn(false);
        SessionEnd.TurnOff(machine, phone.Off, log);
        try { screenDone.Wait(TimeSpan.FromSeconds(2)); }
        catch (Exception) { }
    }

    // ---- Camera & microphone from the phone (the Mac's toggleCamera) ----

    void FlipCamera()
    {
        if (cameraSwitch == null) return;
        bool on = !cameraSwitch.Allowed;
        try { cameraSwitch.Set(on); }
        catch (Exception) { }  // the settings file couldn't be written: the switch stays as it was
        if (cameraSwitch.FromPhone == on)
            log.Write(on ? "Camera & microphone from phone allowed on the PC" : "Camera & microphone from phone turned off");
        ShowCamera();
        // Turned off: a phone watching now is closed at once, with the switch-off sentence.
        if (camera != null) Task.Run(camera.Tick);
    }

    // Ticked only with the screen switch on too, and greyed out without it, as on the Mac.
    void ShowCamera()
    {
        if (cameraSwitch == null) return;
        bool viewing = cameraSwitch.RemoteScreen;
        cameraItem.Checked = cameraSwitch.Allowed;
        cameraItem.Enabled = viewing;
        cameraItem.Text = viewing ? CameraTitle : CameraTitle + " (needs screen viewing)";
    }

    // The phone page's port was taken when Lid Awake started (Main).
    public void PortInUse(string error)
    {
        log.Write($"Phone page couldn't start on 127.0.0.1:{phone.Port}: {error}");
        OnUi(() => icon.ShowBalloonTip(10000, "Phone page not available",
            $"Lid Awake couldn't open its phone page: port {phone.Port} is in use by another program.", ToolTipIcon.Warning));
    }

    // Tailscale did not publish the page (Tailnet.NotPublished, from a background thread). What it
    // printed (a link to allow Serve or HTTPS, usually) goes to the activity log, once per turn-on.
    public void NotPublished(string said) => OnUi(() =>
    {
        if (!isOn || warnedNotPublished) return;
        warnedNotPublished = true;
        log.Write("Phone page not published by Tailscale: " + said);
        icon.ShowBalloonTip(10000, "Phone page not published",
            "The phone page isn't published yet. Open the activity log for the link from Tailscale.", ToolTipIcon.Warning);
    });

    // Run something on the UI thread (the web server calls this from its own threads).
    public void OnUi(Action a)
    {
        try { ui.Post(_ => a(), null); }
        catch (Exception) { }
    }

    // Choose how long Lid Awake stays on (0 = never); while on, the countdown restarts from now.
    // The length is kept at once, under the timer's own lock, so the phone's next /status already
    // shows it; the menu's tick marks are changed on the UI thread. Called from the menu and from
    // the web server's threads.
    public void SetTimer(int minutes)
    {
        Countdown.Set(minutes);
        OnUi(ShowTimerChoice);
    }

    void ShowTimerChoice()
    {
        var chosen = Countdown.Minutes;
        foreach (ToolStripMenuItem item in timerMenu.DropDownItems)
            item.Checked = item.Tag is int minutes && minutes == chosen;
    }

    void CheckTimer()
    {
        if (!isOn || stopping || !Countdown.Expired()) return;
        StopWithAlert("Timer finished", () =>
        {
            SetAwake(false);  // also ends the countdown, so this happens once
            icon.ShowBalloonTip(5000, "Lid Awake turned off", "Timer finished. Your PC will sleep normally again.", ToolTipIcon.Info);
        });
    }

    // Tell the phones first (the PC may sleep right after), then turn off on the UI thread once
    // they have answered (at most 8 seconds), as on the Mac. Meanwhile the timer and the safety
    // check hold back. If Lid Awake was turned off or on again in the meantime, that wins.
    void StopWithAlert(string reason, Action turnOff)
    {
        if (stopping) return;
        stopping = true;
        int at = generation;
        _ = alerts.TurnedOff(reason).ContinueWith(_ => OnUi(() =>
        {
            stopping = false;
            if (at == generation && isOn) turnOff();
        }), TaskScheduler.Default);
    }

    // ---- Safety limits (main.swift safetyStop, the 15-second check, the two menu items) ----

    // A turn-on from the tray: refused, with a balloon saying why, when a limit is already
    // reached. Quick (the cached temperature and GetSystemPowerStatus), so fine on the UI thread.
    bool TurnOnAllowed()
    {
        if (safety.Now() is not { } why) return true;
        log.Write(why.LogRefused);
        icon.ShowBalloonTip(10000, "Lid Awake not turned on", why.RefusedBody, ToolTipIcon.Warning);
        RefreshTemperature();  // so the next try decides on a current reading
        return false;
    }

    // Read the temperatures again soon, in the background, unless they were read in the last
    // 10 seconds. A failure is only "no reading".
    void RefreshTemperature() => Task.Run(() =>
    {
        try { heat.Refresh(HeatSensor.Fresh); }
        catch (Exception) { }
    });

    // Off the UI thread, one at a time (through `checks`). From the timer (`read`): read the
    // temperatures (every 15 s while on, every minute while off). Then, while on, turn off on the
    // UI thread if a limit is reached; SafetyStop decides again there, so a switch turned off (or
    // Lid Awake turned off) in the meantime is respected. A check after a switch change uses the
    // last readings. A failed temperature read is only "no reading".
    void CheckSafety(bool read)
    {
        bool on = isOn;
        if (read) heat.Refresh(on ? HeatSensor.Fresh : HeatSensor.WhileOff);
        if (!on || stopping) return;
        // A limit reached turns off; otherwise the early warnings to the phones (low battery, warm).
        if (safety.Now() != null) OnUi(() => stopper.Run());
        else alerts.Check();
    }

    // A switch ticked in the tray menu.
    void FlipLimit(string name, bool on)
    {
        try { limits.Set(name, on); }
        catch (Exception) { }  // the settings file couldn't be written: the switch stays as it was
        LimitsChanged();
    }

    // A switch was changed (in the menu, or from the phone by POST /limits on the web server's
    // threads): the menu shows it, and it applies right away, as on the Mac.
    public void LimitsChanged()
    {
        OnUi(ShowLimits);
        Task.Run(() => checks.Request(read: false));
    }

    void ShowLimits()
    {
        batteryItem.Checked = limits.BatteryGuard;
        heatItem.Checked = limits.HeatGuard;
        // No temperature on this PC: the switch can't protect anything, so it is greyed out
        // (it keeps its stored setting, which the phone can still change).
        bool available = heat.Available;
        heatItem.Enabled = available;
        heatItem.Text = available ? HeatTitle : HeatTitle + NoTemperature;
    }

    // ---- Control from the phone ----

    // Ticked only while both switches are on; greyed out while the screen switch is off, as on
    // the Mac, because control needs it.
    void ShowControl()
    {
        if (control == null) return;
        bool viewing = control.ScreenAllowed;
        controlItem.Enabled = viewing;
        controlItem.Checked = control.ControlAllowed;
        controlItem.Text = viewing ? ControlTitle : ControlTitle + " (needs screen viewing)";
    }

    void FlipControl()
    {
        if (control == null || !control.ScreenAllowed) return;
        bool before = control.ControlAllowed;
        try { control.SetControl(!before); }
        catch (Exception) { }  // the settings file couldn't be written: the switch stays as it was
        // Logged as on the Mac (toggleControl), only when it really changed.
        if (control.ControlAllowed != before) notices.ControlChanged(!before);
        ShowControl();
    }

    // Open the activity log in whatever the PC opens .log files with, or Notepad.
    void ShowActivityLog()
    {
        if (!File.Exists(log.FilePath)) log.Write("Activity log started");
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(log.FilePath) { UseShellExecute = true }); }
        catch (Exception)
        {
            try { System.Diagnostics.Process.Start("notepad.exe", "\"" + log.FilePath + "\""); }
            catch (Exception) { }
        }
    }

    // The "Allow screen from phone" switch. Turning it off closes every live socket at once (the
    // Mac does the same) and lets go of anything the phone held; /screen.jpg, POST /input and
    // POST /power answer 403 from then on. The control item follows (it needs this switch).
    void SetScreenAllowed(bool allowed)
    {
        try { ScreenSwitch.Set(settings, allowed); }
        catch (Exception) { return; }  // the settings file couldn't be written: the switch stays as it was
        ShowScreen();
        ShowControl();
        ShowCamera();
        if (!allowed) CloseSockets();
        log.Write(allowed ? "Screen from phone allowed on the PC" : "Screen from phone turned off on the PC");
    }

    void ShowScreen() => screenItem.Checked = ScreenSwitch.IsOn(settings);

    // Every live socket closes, a button or modifier key the phone was holding is let go
    // (CloseAll does that too; this also covers a POST /input drag with no socket), and the
    // phone's "display off" is forgotten. Lid Awake off, the screen switch off, the session ending.
    void CloseSockets()
    {
        try { live?.CloseAll(); }
        catch (Exception) { }
        try { control?.ReleaseHeld(); }
        catch (Exception) { }
        // The phone's "display off" ends with it (without waking the display).
        try { control?.ClearScreenOff(); }
        catch (Exception) { }
    }

    // A balloon from the tray icon: "New sign-in to Lid Awake".
    public void Announce(string title, string body) =>
        OnUi(() => icon.ShowBalloonTip(5000, title, body, ToolTipIcon.Info));

    // The app icon, for the setup window (and the tray until its drawn icon is shown); the plain
    // application icon if the file is missing.
    static Icon LoadTrayIcon()
    {
        try
        {
            using var big = new Bitmap(Path.Combine(AppContext.BaseDirectory, "icon-512.png"));
            using var small = new Bitmap(big, 32, 32);
            return TrayIcons.Owned(small);
        }
        catch (Exception) { return (Icon)SystemIcons.Application.Clone(); }
    }

    // Returns when Serve is changed, in the background; the tray does not wait for it.
    public Task SetAwake(bool on)
    {
        var lid = LidResult.Changed;
        if (on) lid = KeepAwake.On(machine); else KeepAwake.Off(machine);
        // Brightness behind a closed lid, and the display hold while watched: both only while on.
        _ = screen.SetOn(on);
        // The countdown starts when it goes from off to on, and ends when it goes off.
        generation++;
        if (on && !isOn) { Countdown.Start(); warnedNotPublished = false; alerts.TurnedOn(); }
        else if (!on) Countdown.Stop();
        isOn = on;
        // Off (from the tray, the phone, the timer, a safety limit, or Quit): the camera closes now,
        // after isOn is false, so a phone asking for the camera meanwhile is refused.
        if (!on) camera?.StopAll();
        toggle.Checked = on;
        ShowActivity();
        // Off: every live socket closes now, not only at the socket's 2-second check, and nothing
        // the phone was holding stays held.
        if (!on) CloseSockets();
        // Serve the page to the phone only while on, as on the Mac, and only if it is our page.
        if (on && !phone.PageRunning)
            icon.ShowBalloonTip(5000, "Phone page not available",
                $"Lid Awake is on, but the phone page couldn't start: port {phone.Port} is in use by another program.", ToolTipIcon.Warning);
        // A desktop PC has no lid action (NoLidSetting): nothing to say.
        if (on && lid == LidResult.Failed)
            icon.ShowBalloonTip(10000, "Lid setting not changed",
                "Couldn't change what closing the lid does. Keep the lid open, or see Lid Awake's help.", ToolTipIcon.Warning);
        return phone.Set(on);
    }

    // Windows 11 asks once, on the first Wi-Fi read, whether Lid Awake may use Location (the
    // phone's signal bars need it). Turning on from the tray menu is when the owner is at the PC,
    // so read once now, in the background, and ignore the result.
    static void AskForLocation() => Task.Run(() =>
    {
        try { WlanReader.Read(null); }
        catch (Exception) { }
    });

    // ---- The status line, the icon, the tooltip and the battery line (main.swift refresh) ----

    // Claude (its usage line and its "show in tooltip" switch) goes here: right after the safety
    // limits, followed by its own separator, as on the Mac. Call once, on the UI thread, before
    // Application.Run.
    public void InsertAfterLimits(params ToolStripItem[] items)
    {
        if (items.Length == 0) return;
        int at = menu.Items.IndexOf(afterLimits) + 1;
        foreach (var item in items) menu.Items.Insert(at++, item);
        menu.Items.Insert(at, new ToolStripSeparator());
    }

    // What the phone is doing on this PC now. A source that fails counts as idle.
    TrayActivity Activity()
    {
        int viewers = 0;
        bool controlling = false, cameraOn = false;
        bool polling = false;
        try { viewers = live?.Viewers ?? 0; }
        catch (Exception) { }
        try { polling = screenPolls?.Viewed ?? false; }
        catch (Exception) { }
        try { controlling = control?.Controlling ?? false; }
        catch (Exception) { }
        try { cameraOn = (camera?.Watchers ?? 0) > 0; }
        catch (Exception) { }
        return TrayActivity.Of(viewers, polling, controlling, cameraOn);
    }

    // Every second, on every turn on or off, and when the menu opens: the icon and the tooltip,
    // and while the menu is open (or opening) its status and battery lines.
    // Never throws: it runs every second, and one failure must not bring up WinForms' error window
    // every second.
    void ShowActivity(bool menuOpening = false)
    {
        try { ShowActivityNow(menuOpening); }
        catch (Exception) { }
    }

    void ShowActivityNow(bool menuOpening)
    {
        var activity = Activity();
        var remaining = isOn ? Countdown.Remaining() : null;
        ShowIcon(TrayText.Icon(isOn, activity));
        string? extra = null;
        try { extra = ExtraTooltip?.Invoke(); }
        catch (Exception) { }
        ShowTooltip(TrayText.Tooltip(isOn, remaining, activity, extra));
        if (!menuOpening && !menu.Visible) return;
        SetText(statusLine, TrayText.StatusLine(isOn, remaining, activity));
        SetText(infoLine, TrayText.InfoLine(SafePower(), heat.Latest.Battery, SafeMinutesToFull()));
    }

    PowerReading? SafePower()
    {
        try { return power?.Invoke(); }
        catch (Exception) { return null; }
    }

    int SafeMinutesToFull()
    {
        try { return minutesToFull?.Invoke() ?? -1; }
        catch (Exception) { return -1; }
    }

    static void SetText(ToolStripItem item, string text)
    {
        if (item.Text != text) item.Text = text;
    }

    // The drawn icon for this state, at the tray's size and for the taskbar's colour. If drawing
    // fails, the app's own icon is shown instead (never the previous state's picture, which
    // would tell the owner something that is no longer true).
    void ShowIcon(TrayIconState state)
    {
        var key = (State: state, Size: TrayIcons.SizeFor(SystemInformation.SmallIconSize.Width), Light: lightTaskbar);
        if (shownIcon == key) return;
        if (!drawn.TryGetValue(key, out var picture))
        {
            try
            {
                picture = TrayIcons.Make(key.State, key.Size, key.Light);
                drawn[key] = picture;
            }
            catch (Exception) { picture = appIcon; }
        }
        icon.Icon = picture;
        shownIcon = key;
    }

    void ShowTooltip(string text)
    {
        if (icon.Text == text) return;
        try { icon.Text = text; }
        // An older limit (63 characters): the start of it.
        catch (ArgumentException) { icon.Text = TrayText.Cut(text, 63); }
    }

    // Windows says a setting changed (the light or dark taskbar among them).
    void OnPreferenceChanged(object? sender, Microsoft.Win32.UserPreferenceChangedEventArgs e) => OnUi(() =>
    {
        lightTaskbar = TrayIcons.LightTaskbar();
        ShowActivity();
    });

    // After Application.Run returns (Main), not in ExitThreadCore: Quit is a click in the menu, which
    // must not be disposed under its own click.
    protected override void Dispose(bool disposing)
    {
        if (disposing) DisposeTray();
        base.Dispose(disposing);
    }

    bool trayDisposed;

    // The tray icon, its menu, the timer and every icon drawn go.
    void DisposeTray()
    {
        if (trayDisposed) return;
        trayDisposed = true;
        try
        {
            activityTick.Dispose();
            icon.Visible = false;
            icon.Dispose();
            menu.Dispose();
            foreach (var picture in drawn.Values) picture.Dispose();
            drawn.Clear();
            appIcon.Dispose();
        }
        catch (Exception) { }
    }

    // A submenu's old items are disposed before it is filled again.
    static void ClearItems(ToolStripMenuItem submenu)
    {
        var old = submenu.DropDownItems.Cast<ToolStripItem>().ToList();
        submenu.DropDownItems.Clear();
        foreach (var item in old) item.Dispose();
    }

    void ShowPassword()
    {
        bool has;
        try { has = auth.HasPassword; }
        catch (Exception) { has = false; }
        SetText(passwordItem, TrayText.PasswordTitle(has));
    }

    // "Open live page": https://<pc>.ts.net/ in the default browser (the page isn't offered on
    // 127.0.0.1), or a balloon while Tailscale hasn't given this PC its name.
    void OpenLivePage()
    {
        var url = TrayText.LivePageUrl(tailnet?.Host);
        if (url == null) { notices.NoLivePageAddress(); return; }
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true }); }
        catch (Exception e) { log.Write("The phone page couldn't open in the browser: " + e.Message); }
    }

    // POST /awake from the phone (on the web server's thread; it wrote the activity-log line):
    // off at the tray, with the Mac's "Turned off from your phone". Already off: nothing to say.
    public void TurnOffFromPhone() => OnUi(() =>
    {
        if (!isOn) return;
        SetAwake(false);
        notices.TurnedOffFromPhone();
    });

    // ---- Signed-in devices (main.swift rebuildDevicesMenu) ----

    void ShowDevices()
    {
        ClearItems(devicesMenu);
        List<Auth.SessionInfo> sessions;
        bool locked;
        try
        {
            sessions = auth.Sessions(Array.Empty<string>());
            locked = auth.IsLockedOut;
        }
        catch (Exception) { sessions = new(); locked = false; }
        foreach (var line in TrayText.Devices(sessions, locked))
        {
            if (line.SeparatorBefore) devicesMenu.DropDownItems.Add(new ToolStripSeparator());
            var item = new ToolStripMenuItem(line.Text) { Enabled = line.Enabled };
            var (id, action) = (line.SessionId, line.Action);  // the menu item keeps its own copy
            if (id != null) item.Click += (_, _) => SignOutDevice(id);
            else if (action != DeviceMenuAction.None) item.Click += (_, _) => DeviceAction(action);
            devicesMenu.DropDownItems.Add(item);
        }
    }

    // Signed out from the PC: its live socket closes at the socket's next 2-second check.
    void SignOutDevice(string id)
    {
        try { auth.SignOut(id, Array.Empty<string>()); }
        catch (Exception) { return; }  // the settings file couldn't be written: nothing changed
        notices.SignedOutDevice();
    }

    void DeviceAction(DeviceMenuAction action)
    {
        try
        {
            switch (action)
            {
                case DeviceMenuAction.SignOutAll:
                    auth.SignOutAll();
                    notices.SignedOutAllDevices();
                    break;
                case DeviceMenuAction.ClearLockout:
                    auth.ClearLockout();
                    notices.LockoutCleared();
                    break;
                case DeviceMenuAction.ShowLog:
                    ShowActivityLog();
                    break;
            }
        }
        catch (Exception) { }  // the settings file couldn't be written: nothing changed
    }

    // ---- Face ID / fingerprint sign-ins (main.swift removeFaceID) ----

    readonly ToolStripMenuItem passkeysMenu = new("Face ID and fingerprint");

    // One item per key, "iPhone · added 20 Oct 2026": clicking it removes the key.
    void ShowPasskeys()
    {
        ClearItems(passkeysMenu);
        IReadOnlyList<Passkeys.Info> keys;
        try { keys = auth.Passkeys.List; }
        catch (Exception) { keys = Array.Empty<Passkeys.Info>(); }
        passkeysMenu.DropDownItems.Add(new ToolStripMenuItem(
            keys.Count == 0 ? "No Face ID or fingerprint sign-ins" : "Face ID and fingerprint sign-ins (click to remove)") { Enabled = false });
        foreach (var key in keys)
        {
            var id = key.Id;  // the menu item keeps its own copy
            var added = key.Added is { } a ? "added " + a.ToLocalTime().ToString("d MMM yyyy") : "added earlier";
            passkeysMenu.DropDownItems.Add(new ToolStripMenuItem($"{key.Device} · {added}", null, (_, _) => RemovePasskey(id)));
        }
    }

    // Forget one key, and sign out the devices that used it.
    void RemovePasskey(string id)
    {
        try
        {
            auth.Passkeys.Remove(id);
            auth.SignOutPasskey(id);
        }
        catch (Exception) { return; }  // the settings file couldn't be written: nothing changed
        log.Write("Removed a Face ID or fingerprint sign-in on the PC");
        icon.ShowBalloonTip(5000, "Face ID or fingerprint removed", "That device will need the password.", ToolTipIcon.Info);
        ShowPasskeys();
    }

    bool appsFormOpen;

    // "Apps on phone…": tick the Start menu programs the phone may open, switch to and quit.
    void ShowAppsForm()
    {
        if (appsFormOpen || phoneApps == null || startMenu == null) return;
        appsFormOpen = true;
        try
        {
            using var form = new AppsForm(phoneApps.Picks(), startMenu.Scan);
            if (form.ShowDialog() == DialogResult.OK)
            {
                phoneApps.SetPicks(form.Result);
                log.Write($"Apps on the phone changed on the PC ({form.Result.Count} apps)");
            }
        }
        catch (Exception e)
        {
            MessageBox.Show("Could not save the apps: " + e.Message, "Lid Awake");
        }
        finally { appsFormOpen = false; }
    }

    bool passwordFormOpen;

    void ShowPasswordForm()
    {
        if (passwordFormOpen) return;
        passwordFormOpen = true;
        try
        {
            // The form writes the activity-log line itself (PhonePassword, shared with the setup window).
            using var form = new PasswordForm(auth, log.Write);
            if (form.ShowDialog() == DialogResult.OK)
            {
                ShowPassword();
                MessageBox.Show("Phone password saved. Every phone was signed out.", "Lid Awake");
            }
        }
        finally { passwordFormOpen = false; }
    }
}
