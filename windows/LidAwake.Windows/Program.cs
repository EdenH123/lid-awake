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
        var tray = new TrayApp(settings, auth, tailnet);
        // Learn this PC's Tailscale name in the background; the page is refused until it is known.
        Task.Run(tailnet.Refresh);
        // `status` is replaced by the full status in a later task.
        using var server = new WebServer(auth, tailnet, () => tray.IsOn,
            () => new { os = "windows", lidAwake = tray.IsOn },
            () => tray.OnUi(() => tray.SetAwake(false)), tray.Announce);
        try { server.Start(); }
        catch (Exception e)
        {
            MessageBox.Show("Lid Awake could not open its phone page on 127.0.0.1:8787. Another program may be using that port.\n\n" + e.Message,
                "Lid Awake");
        }
        Application.Run(tray);
    }
}

sealed class TrayApp : ApplicationContext
{
    readonly Settings settings;
    readonly Auth auth;
    readonly Tailnet tailnet;
    readonly NotifyIcon icon;
    readonly SynchronizationContext ui;
    readonly ToolStripMenuItem toggle = new("Lid Awake on");

    // Written on the UI thread, read by the web server's threads.
    volatile bool isOn;
    public bool IsOn => isOn;

    public TrayApp(Settings settings, Auth auth, Tailnet tailnet)
    {
        this.settings = settings;
        this.auth = auth;
        this.tailnet = tailnet;
        toggle.Click += (_, _) => SetAwake(!isOn);
        var menu = new ContextMenuStrip();
        menu.Items.Add(toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Set phone password…", null, (_, _) => ShowPasswordForm());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Lid Awake", null, (_, _) =>
        {
            SetAwake(false);
            tailnet.Unserve();  // now, not in the background: the app is about to end
            icon!.Visible = false;
            ExitThread();
        });
        icon = new NotifyIcon { Icon = LoadTrayIcon(), Text = "Lid Awake", ContextMenuStrip = menu, Visible = true };
        // Put back a lid action left over from a crash.
        KeepAwake.Off(settings);
        // Nothing is reachable from the phone until Lid Awake is turned on.
        tailnet.SetServed(false);
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

    public void SetAwake(bool on)
    {
        if (on) KeepAwake.On(settings); else KeepAwake.Off(settings);
        isOn = on;
        toggle.Checked = on;
        // Serve the page to the phone only while on, as on the Mac.
        tailnet.SetServed(on);
        icon.Text = on ? "Lid Awake: on" : "Lid Awake: off";
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
                MessageBox.Show("Phone password saved. Every phone was signed out.", "Lid Awake");
        }
        finally { passwordFormOpen = false; }
    }
}
