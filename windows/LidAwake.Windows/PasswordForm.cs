namespace LidAwake;

// Setting the phone password on the PC, shared by "Set phone password…" and the "Set up Lid
// Awake" window: the same minimum, the same messages and the same activity-log line. Auth signs
// every phone out, removes the Face ID / fingerprint keys and (PasswordSet) the alert phones.
static class PhonePassword
{
    public const string LogLine = "Phone password changed on the PC";

    // Null when saved (and logged), otherwise what to tell the person.
    public static string? Save(Auth auth, string password, string again, Action<string>? log)
    {
        if (password.Length < Auth.MinPasswordLength) return $"Use at least {Auth.MinPasswordLength} characters.";
        if (password != again) return "The two passwords do not match.";
        try
        {
            auth.SetPassword(password);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            return "Could not save: " + e.Message;
        }
        try { log?.Invoke(LogLine); }
        catch (Exception) { }
        return null;
    }
}

// "Set phone password…": two boxes and a Save button. The password is set here, on the
// computer, and has no web route (docs/API.md).
sealed class PasswordForm : Form
{
    readonly Auth auth;
    readonly Action<string>? log;
    readonly TextBox password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
    readonly TextBox again = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
    readonly Label error = new() { ForeColor = Color.Firebrick, AutoSize = false, Height = 36, Dock = DockStyle.Top };

    public PasswordForm(Auth auth, Action<string>? log = null)
    {
        this.auth = auth;
        this.log = log;
        Text = "Lid Awake phone password";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(340, 230);
        Padding = new Padding(12);

        var save = new Button { Text = "Save", Dock = DockStyle.Top, Height = 30 };
        save.Click += (_, _) => Save();
        AcceptButton = save;

        // Docked top to bottom: the control added last sits at the top.
        Controls.Add(save);
        Controls.Add(error);
        Controls.Add(again);
        Controls.Add(new Label { Text = "Type it again", AutoSize = false, Height = 20, Dock = DockStyle.Top });
        Controls.Add(password);
        Controls.Add(new Label { Text = $"New password ({Auth.MinPasswordLength} characters or more)", AutoSize = false, Height = 20, Dock = DockStyle.Top });
    }

    void Save()
    {
        if (PhonePassword.Save(auth, password.Text, again.Text, log) is { } problem)
        {
            error.Text = problem;
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
