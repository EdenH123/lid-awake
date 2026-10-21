namespace LidAwake;

// "Set phone password…": two boxes and a Save button. The password is set here, on the
// computer, and has no web route (docs/API.md).
sealed class PasswordForm : Form
{
    readonly Auth auth;
    readonly TextBox password = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
    readonly TextBox again = new() { UseSystemPasswordChar = true, Dock = DockStyle.Top };
    readonly Label error = new() { ForeColor = Color.Firebrick, AutoSize = false, Height = 36, Dock = DockStyle.Top };

    public PasswordForm(Auth auth)
    {
        this.auth = auth;
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
        if (password.Text.Length < Auth.MinPasswordLength)
        {
            error.Text = $"Use at least {Auth.MinPasswordLength} characters.";
            return;
        }
        if (password.Text != again.Text)
        {
            error.Text = "The two passwords do not match.";
            return;
        }
        try
        {
            auth.SetPassword(password.Text);
        }
        catch (Exception e) when (e is ArgumentException or IOException or UnauthorizedAccessException)
        {
            error.Text = "Could not save: " + e.Message;
            return;
        }
        DialogResult = DialogResult.OK;
        Close();
    }
}
