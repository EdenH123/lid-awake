namespace LidAwake;

// Lid Awake for Windows: a tray icon with the on/off switch. The phone page and its requests
// are the same as on the Mac (docs/API.md).
static class Program
{
    [STAThread]
    static void Main()
    {
        using var single = new Mutex(true, "local.lidawake", out bool first);
        if (!first) return;
        ApplicationConfiguration.Initialize();
        var settings = new Settings();
        var auth = new Auth(settings);
        var tailnet = new Tailnet(settings);
        var log = new AccessLog();
        // Learn this PC's Tailscale name in the background; the page is refused until it is known.
        Task.Run(tailnet.Refresh);
        // The server starts before the tray exists, so the tray knows whether it may serve the
        // page. Until the tray is there, the page answers as if Lid Awake were off.
        TrayApp? tray = null;
        // One StatusInfo for all requests: processor load and network speed are measured between
        // two requests. It is built on the web server's threads and touches no WinForms controls
        // (IsOn is a volatile field, and the timer is under its own lock).
        var status = new StatusInfo(new SystemReadings());
        using var server = new WebServer(auth, tailnet, () => tray?.IsOn == true,
            () => status.Build(tray?.IsOn == true, tray?.Countdown),
            () => tray?.OnUi(() => tray?.SetAwake(false)), (title, body) => tray?.Announce(title, body),
            log: log, setTimer: minutes => tray?.SetTimer(minutes));
        try { server.Start(); }
        catch (Exception e)
        {
            MessageBox.Show($"Lid Awake could not open its phone page on 127.0.0.1:{server.Port}. Another program may be using that port.\n\n" + e.Message,
                "Lid Awake");
        }
        tray = new TrayApp(settings, auth, new PhoneAccess(tailnet, server), log);
        Application.Run(tray);
    }
}

sealed class TrayApp : ApplicationContext
{
    readonly Settings settings;
    readonly Auth auth;
    readonly PhoneAccess phone;
    readonly AccessLog log;
    readonly NotifyIcon icon;
    readonly SynchronizationContext ui;
    readonly ToolStripMenuItem toggle = new("Lid Awake on");
    readonly ToolStripMenuItem timerMenu = new("Turn off after");
    // Checked every couple of seconds on the UI thread: when the time is up, Lid Awake turns off.
    readonly System.Windows.Forms.Timer timerCheck = new() { Interval = 2000 };

    // Written on the UI thread, read by the web server's threads.
    volatile bool isOn;
    public bool IsOn => isOn;

    // "Turn off after": its own lock makes it safe to read and change from the web server's threads.
    public AutoOff Countdown { get; }

    public TrayApp(Settings settings, Auth auth, PhoneAccess phone, AccessLog log)
    {
        this.settings = settings;
        this.auth = auth;
        this.phone = phone;
        this.log = log;
        Countdown = new AutoOff(settings);
        toggle.Click += (_, _) => SetAwake(!isOn);
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
        var menu = new ContextMenuStrip();
        menu.Items.Add(toggle);
        menu.Items.Add(timerMenu);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Set phone password…", null, (_, _) => ShowPasswordForm());
        menu.Items.Add("Show activity log", null, (_, _) => ShowActivityLog());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Lid Awake", null, (_, _) =>
        {
            // Wait (a little) for Serve to be removed, through the same one-at-a-time path as
            // turning off, so a turn-on still being applied cannot add it back after it.
            var unserved = SetAwake(false);
            try { unserved.Wait(TimeSpan.FromSeconds(5)); }
            catch (Exception) { }
            icon!.Visible = false;
            ExitThread();
        });
        icon = new NotifyIcon { Icon = LoadTrayIcon(), Text = "Lid Awake", ContextMenuStrip = menu, Visible = true };
        // Put back a lid action left over from a crash.
        KeepAwake.Off(settings);
        // Nothing is reachable from the phone until Lid Awake is turned on.
        phone.Set(false);
        // The tray may only be changed on its own thread, and the web server runs on others.
        // WinForms installs its context once the first control exists; make sure it is there.
        if (SynchronizationContext.Current is null)
            SynchronizationContext.SetSynchronizationContext(new WindowsFormsSynchronizationContext());
        ui = SynchronizationContext.Current!;
    }

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
        if (!isOn || !Countdown.Expired()) return;
        SetAwake(false);  // also ends the countdown, so this happens once
        icon.ShowBalloonTip(5000, "Lid Awake turned off", "Timer finished. Your PC will sleep normally again.", ToolTipIcon.Info);
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

    // A balloon from the tray icon: "New sign-in to Lid Awake".
    public void Announce(string title, string body) =>
        OnUi(() => icon.ShowBalloonTip(5000, title, body, ToolTipIcon.Info));

    // The app icon, small enough for the tray; the plain application icon if the file is missing.
    static Icon LoadTrayIcon()
    {
        try
        {
            using var big = new Bitmap(Path.Combine(AppContext.BaseDirectory, "icon-512.png"));
            using var small = new Bitmap(big, 32, 32);
            return Icon.FromHandle(small.GetHicon());
        }
        catch (Exception) { return SystemIcons.Application; }
    }

    // Returns when Serve is changed, in the background; the tray does not wait for it.
    public Task SetAwake(bool on)
    {
        if (on) KeepAwake.On(settings); else KeepAwake.Off(settings);
        // The countdown starts when it goes from off to on, and ends when it goes off.
        if (on && !isOn) Countdown.Start();
        else if (!on) Countdown.Stop();
        isOn = on;
        toggle.Checked = on;
        icon.Text = on ? "Lid Awake: on" : "Lid Awake: off";
        // Serve the page to the phone only while on, as on the Mac, and only if it is our page.
        if (on && !phone.PageRunning)
            icon.ShowBalloonTip(5000, "Phone page not available",
                $"Lid Awake is on, but the phone page couldn't start: port {phone.Port} is in use by another program.", ToolTipIcon.Warning);
        return phone.Set(on);
    }

    bool passwordFormOpen;

    void ShowPasswordForm()
    {
        if (passwordFormOpen) return;
        passwordFormOpen = true;
        try
        {
            using var form = new PasswordForm(auth);
            if (form.ShowDialog() == DialogResult.OK)
            {
                log.Write("Phone password changed on the PC");
                MessageBox.Show("Phone password saved. Every phone was signed out.", "Lid Awake");
            }
        }
        finally { passwordFormOpen = false; }
    }
}
