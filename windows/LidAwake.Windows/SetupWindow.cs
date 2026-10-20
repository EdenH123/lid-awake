using System.Diagnostics;

namespace LidAwake;

// "Set up Lid Awake" (the Mac's Setup.swift): what this PC still needs, each with its fix, a
// box for the phone password and a QR code that opens the phone page on the phone. Opens by
// itself at start until a phone password is set; later from the tray (Set up phone…). One at a
// time (TrayApp keeps it in a SingleWindow). The Tailscale check runs off the UI thread, every
// 2 seconds while the window is shown.
sealed class SetupWindow : Form
{
    const int Width0 = 520;  // logical pixels, scaled for the screen's DPI (S)
    const int Pad = 20;
    public const string DownloadUrl = "https://tailscale.com/download/windows";
    public const string DnsUrl = "https://login.tailscale.com/admin/dns";
    public const string TaskbarSettings = "ms-settings:taskbar";
    public const string TrayTip = "Lid Awake lives by the clock. If you don't see its icon, click ^ next to the clock and drag it onto the taskbar, or turn it on in Settings → Personalization → Taskbar → Other system tray icons.";

    readonly SetupModel model;
    readonly Action turnOn;
    readonly Action allowScreen;
    readonly System.Windows.Forms.Timer tick = new() { Interval = 2000 };
    readonly Font titleFont, boldFont, markFont;
    readonly int inner;  // the content's width in pixels

    readonly SetupRow tailscaleRow, addressRow, passwordRow;
    readonly SetupRow? screenRow;
    readonly Button download = new() { Text = "Download Tailscale", AutoSize = true };
    readonly Button openTailscale = new() { Text = "Open Tailscale", AutoSize = true };
    readonly Button openDns = new() { Text = "Open Tailscale settings", AutoSize = true };
    readonly TextBox password = new() { UseSystemPasswordChar = true, PlaceholderText = "New password" };
    readonly TextBox again = new() { UseSystemPasswordChar = true, PlaceholderText = "Repeat password" };
    readonly Label passwordProblem = new() { AutoSize = true, ForeColor = Color.Firebrick, Visible = false };
    readonly Button savePassword = new() { Text = "Save password", AutoSize = true };
    readonly Panel qrHolder = new() { Margin = Padding.Empty };
    readonly PictureBox qr = new() { SizeMode = PictureBoxSizeMode.Normal, Dock = DockStyle.Fill, BackColor = Color.White };
    readonly Label qrPlaceholder = new()
    {
        Text = "The code appears when Tailscale is ready", TextAlign = ContentAlignment.MiddleCenter,
        Dock = DockStyle.Fill, BackColor = Color.FromArgb(236, 236, 236), ForeColor = Color.DimGray,
    };
    readonly Label awakeStep = new() { AutoSize = true };
    readonly Button turnOnButton = new() { Text = "Turn Lid Awake on", AutoSize = true };
    readonly FlowLayoutPanel addressLine = new() { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
    readonly TextBox address = new() { ReadOnly = true };
    readonly Button copy = new() { Text = "Copy address", AutoSize = true };
    readonly Button allowScreenButton = new() { Text = "Allow screen from phone", AutoSize = true };
    string? shownQr;
    bool shownFaded;

    // `screenFeature`: this PC can show its screen on the phone (the optional row).
    public SetupWindow(SetupModel model, Action turnOn, Action allowScreen, bool screenFeature, Icon? icon = null)
    {
        this.model = model;
        this.turnOn = turnOn;
        this.allowScreen = allowScreen;
        SuspendLayout();
        Text = "Set up Lid Awake";
        if (icon != null) Icon = icon;
        // Sizes below are in logical pixels, scaled here for the DPI (the app is system-DPI aware).
        AutoScaleMode = AutoScaleMode.None;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        BackColor = SystemColors.Window;
        titleFont = new Font(Font.FontFamily, 14f, FontStyle.Bold);
        boldFont = new Font(Font, FontStyle.Bold);
        markFont = new Font("Segoe UI Symbol", 12f);
        inner = S(Width0) - 2 * S(Pad);

        var root = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Padding = new Padding(S(Pad)), Margin = Padding.Empty,
        };
        root.Controls.Add(new Label { Text = "Set up Lid Awake", Font = titleFont, AutoSize = true, Margin = new Padding(0, 0, 0, S(2)) });
        root.Controls.Add(Wrapped("A few things this PC needs, then your phone.", inner, SystemColors.GrayText, new Padding(0, 0, 0, S(10))));

        // Where the tray icon is: Windows often hides a new one under the ^ arrow.
        var tip = Column(new Padding(0, 0, 0, S(14)));
        tip.Controls.Add(Wrapped(TrayTip, inner, SystemColors.GrayText, Padding.Empty));
        var taskbar = new Button { Text = "Open taskbar settings", AutoSize = true, Margin = new Padding(0, S(4), 0, 0) };
        taskbar.Click += (_, _) => Open(TaskbarSettings);
        tip.Controls.Add(taskbar);
        root.Controls.Add(tip);

        tailscaleRow = new SetupRow("Tailscale", inner, boldFont, markFont, S);
        download.Click += (_, _) => Open(DownloadUrl);
        openTailscale.Click += (_, _) => Open(File.Exists(Tailnet.AppExe) ? Tailnet.AppExe : DownloadUrl);
        tailscaleRow.Fix.Controls.Add(download);
        tailscaleRow.Fix.Controls.Add(openTailscale);
        root.Controls.Add(tailscaleRow);

        addressRow = new SetupRow("Private web address", inner, boldFont, markFont, S);
        openDns.Click += (_, _) => Open(DnsUrl);
        addressRow.Fix.Controls.Add(openDns);
        root.Controls.Add(addressRow);

        passwordRow = new SetupRow("Phone password", inner, boldFont, markFont, S);
        password.Width = again.Width = S(240);
        password.Margin = again.Margin = new Padding(0, 0, 0, S(6));
        passwordProblem.MaximumSize = new Size(inner - S(SetupRow.MarkWidth), 0);
        savePassword.Click += (_, _) => SavePassword();
        foreach (Control c in new Control[] { password, again, passwordProblem, savePassword }) passwordRow.Fix.Controls.Add(c);
        root.Controls.Add(passwordRow);

        root.Controls.Add(Line());

        // The QR code, and beside it the steps on the phone.
        int qrBox = S(QrImage.TargetSize);
        qrHolder.Size = new Size(qrBox, qrBox);
        qrHolder.Controls.Add(qr);
        qrHolder.Controls.Add(qrPlaceholder);
        int stepsWidth = inner - qrBox - S(16) - S(24);  // room for a slightly larger code (whole-pixel modules)
        var steps = Column(new Padding(S(16), 0, 0, 0));
        steps.Controls.Add(new Label { Text = "On your phone", Font = boldFont, AutoSize = true, Margin = new Padding(0, 0, 0, S(6)) });
        steps.Controls.Add(Step(1, new Label { Text = "Install Tailscale and sign in with the same account.", AutoSize = true }, stepsWidth));
        steps.Controls.Add(Step(2, awakeStep, stepsWidth));
        turnOnButton.Margin = new Padding(S(26), 0, 0, S(6));
        turnOnButton.Click += (_, _) => { turnOn(); UpdateNow(); };
        steps.Controls.Add(turnOnButton);
        steps.Controls.Add(Step(3, new Label { Text = "Point the Camera at this code and open the link (Safari on iPhone, your browser on Android).", AutoSize = true }, stepsWidth));
        steps.Controls.Add(Step(4, new Label { Text = "Add it to the Home Screen (iPhone: Share → Add to Home Screen; Android: browser menu → Add to Home screen), then sign in with your password.", AutoSize = true }, stepsWidth));
        var qrRow = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, S(10)) };
        qrRow.Controls.Add(qrHolder);
        qrRow.Controls.Add(steps);
        root.Controls.Add(qrRow);

        // The address, to select or copy (the fallback: type it on the phone).
        address.Width = inner - S(130);
        address.Margin = new Padding(0, S(1), S(8), 0);
        copy.Margin = Padding.Empty;
        copy.Click += (_, _) => CopyAddress();
        addressLine.Controls.Add(address);
        addressLine.Controls.Add(copy);
        root.Controls.Add(addressLine);

        if (screenFeature)
        {
            root.Controls.Add(Line());
            screenRow = new SetupRow("See and control the screen (optional)", inner, boldFont, markFont, S);
            allowScreenButton.Click += (_, _) => { allowScreen(); UpdateNow(); };
            screenRow.Fix.Controls.Add(allowScreenButton);
            root.Controls.Add(screenRow);
        }

        Controls.Add(root);
        tick.Tick += (_, _) => Recheck();
        // The timer runs only while the window is shown.
        VisibleChanged += (_, _) =>
        {
            if (Visible) { Recheck(); tick.Start(); }
            else tick.Stop();
        };
        ShowState();
        ResumeLayout(true);
    }

    int S(int logical) => (int)Math.Round(logical * DeviceDpi / 96.0);

    static Label Wrapped(string text, int width, Color color, Padding margin) =>
        new() { Text = text, AutoSize = true, MaximumSize = new Size(width, 0), ForeColor = color, Margin = margin };

    static FlowLayoutPanel Column(Padding margin) => new()
    {
        FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = margin,
    };

    Label Line() => new() { BorderStyle = BorderStyle.Fixed3D, AutoSize = false, Height = 2, Width = inner, Margin = new Padding(0, S(4), 0, S(14)) };

    // A numbered step: the number in a small grey box, the text wrapped beside it.
    FlowLayoutPanel Step(int n, Label text, int width)
    {
        var row = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, 0, 0, S(6)) };
        row.Controls.Add(new Label
        {
            Text = n.ToString(System.Globalization.CultureInfo.InvariantCulture), Font = boldFont, TextAlign = ContentAlignment.MiddleCenter,
            AutoSize = false, Size = new Size(S(20), S(20)), BackColor = Color.FromArgb(228, 228, 228), Margin = new Padding(0, 0, S(6), 0),
        });
        text.MaximumSize = new Size(width - S(26), 0);
        text.Margin = Padding.Empty;
        row.Controls.Add(text);
        return row;
    }

    // The quick state now, and Tailscale in the background (one check at a time).
    void Recheck()
    {
        model.RefreshLocal();
        model.StartCheck()?.ContinueWith(t =>
        {
            try
            {
                BeginInvoke(new Action(() =>
                {
                    if (IsDisposed) return;
                    model.Apply(t.Result);
                    ShowState();
                }));
            }
            catch (Exception) { }  // closed meanwhile
        }, TaskScheduler.Default);
        ShowState();
    }

    void UpdateNow()
    {
        model.RefreshLocal();
        ShowState();
    }

    void ShowState()
    {
        SuspendLayout();
        var ts = model.Tailscale;
        tailscaleRow.Set(ts.Installed && ts.SignedIn, model.TailscaleDetail);
        download.Visible = !ts.Installed;
        openTailscale.Visible = ts.Installed && !ts.SignedIn;
        addressRow.Set(ts.HttpsOn, model.AddressDetail);
        openDns.Visible = ts.SignedIn && !ts.HttpsOn;
        addressRow.Fix.Visible = openDns.Visible;
        passwordRow.Set(model.HasPassword, model.PasswordDetail);
        AcceptButton = model.HasPassword ? null : savePassword;
        awakeStep.Text = model.AwakeStep;
        turnOnButton.Visible = model.ShowsTurnOn;
        ShowQr(model.PageUrl, faded: !model.Ready);
        address.Text = model.PageUrl ?? "";
        addressLine.Visible = model.PageUrl != null;
        screenRow?.Set(model.ScreenAllowed, model.ScreenDetail);
        ResumeLayout(true);
    }

    // The code is drawn again only when the address or its strength changes.
    void ShowQr(string? url, bool faded)
    {
        if (url == shownQr && faded == shownFaded && (url == null) == (qr.Image == null)) return;
        shownQr = url;
        shownFaded = faded;
        var old = qr.Image;
        Bitmap? image = null;
        try { image = QrImage.Render(url, S(QrImage.TargetSize), faded); }
        catch (Exception) { }
        qr.Image = image;
        old?.Dispose();
        qr.Visible = image != null;
        qrPlaceholder.Visible = image == null;
        int side = image?.Width ?? S(QrImage.TargetSize);
        qrHolder.Size = new Size(side, side);
    }

    void SavePassword()
    {
        var problem = model.SavePassword(password.Text, again.Text);
        passwordProblem.Text = problem ?? "";
        passwordProblem.Visible = problem != null;
        if (problem == null)
        {
            password.Text = "";
            again.Text = "";
        }
        ShowState();
    }

    void CopyAddress()
    {
        if (model.PageUrl is not { } url) return;
        try
        {
            Clipboard.SetText(url);
            copy.Text = "Copied";
        }
        catch (Exception) { }  // the clipboard is busy: the address can still be selected
    }

    static void Open(string target)
    {
        try { Process.Start(new ProcessStartInfo(target) { UseShellExecute = true }); }
        catch (Exception) { }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            tick.Stop();
            tick.Dispose();
            qr.Image?.Dispose();
            titleFont.Dispose();
            boldFont.Dispose();
            markFont.Dispose();
        }
        base.Dispose(disposing);
    }
}

// One row: a tick (done) or an empty circle, the title, what to do, and the fix while not done.
sealed class SetupRow : TableLayoutPanel
{
    public const int MarkWidth = 28;
    readonly Label mark;
    readonly Label detail;
    public FlowLayoutPanel Fix { get; }

    public SetupRow(string title, int width, Font bold, Font markFont, Func<int, int> s)
    {
        ColumnCount = 2;
        RowCount = 1;
        AutoSize = true;
        AutoSizeMode = AutoSizeMode.GrowAndShrink;
        Margin = new Padding(0, 0, 0, s(12));
        ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, s(MarkWidth)));
        ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        mark = new Label { AutoSize = true, Font = markFont, Margin = Padding.Empty };
        var body = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = Padding.Empty };
        body.Controls.Add(new Label { Text = title, Font = bold, AutoSize = true, Margin = new Padding(0, 0, 0, s(2)) });
        detail = new Label { AutoSize = true, MaximumSize = new Size(width - s(MarkWidth), 0), ForeColor = SystemColors.GrayText, Margin = Padding.Empty };
        body.Controls.Add(detail);
        Fix = new FlowLayoutPanel { FlowDirection = FlowDirection.TopDown, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Margin = new Padding(0, s(6), 0, 0) };
        body.Controls.Add(Fix);
        Controls.Add(mark, 0, 0);
        Controls.Add(body, 1, 0);
    }

    public void Set(bool done, string text)
    {
        mark.Text = done ? "✔" : "○";
        mark.ForeColor = done ? Color.ForestGreen : SystemColors.GrayText;
        detail.Text = text;
        Fix.Visible = !done;
    }
}
