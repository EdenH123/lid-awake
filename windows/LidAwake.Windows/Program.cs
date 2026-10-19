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
        Application.Run(new TrayApp(settings, new Auth(settings)));
    }
}

sealed class TrayApp : ApplicationContext
{
    readonly Settings settings;
    readonly Auth auth;
    readonly NotifyIcon icon;
    readonly ToolStripMenuItem toggle = new("Lid Awake on");

    public bool IsOn { get; private set; }

    public TrayApp(Settings settings, Auth auth)
    {
        this.settings = settings;
        this.auth = auth;
        toggle.Click += (_, _) => SetAwake(!IsOn);
        var menu = new ContextMenuStrip();
        menu.Items.Add(toggle);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Set phone password…", null, (_, _) => ShowPasswordForm());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit Lid Awake", null, (_, _) => { SetAwake(false); icon!.Visible = false; ExitThread(); });
        icon = new NotifyIcon { Icon = SystemIcons.Application, Text = "Lid Awake", ContextMenuStrip = menu, Visible = true };
        // Put back a lid action left over from a crash.
        KeepAwake.Off(settings);
    }

    public void SetAwake(bool on)
    {
        if (on) KeepAwake.On(settings); else KeepAwake.Off(settings);
        IsOn = on;
        toggle.Checked = on;
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
