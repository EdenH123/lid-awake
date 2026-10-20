namespace LidAwake;

// "Apps on phone…": a checklist of the Start menu's programs. The ticked ones are the apps the
// phone can open, switch to and quit (docs/API.md 5.6), in the order they were picked; new ones go
// to the end. Apps picked earlier that the scan no longer finds stay in the list, ticked.
sealed class AppsForm : Form
{
    readonly CheckedListBox list = new() { CheckOnClick = true, Dock = DockStyle.Fill, IntegralHeight = false };
    readonly Label note = new() { AutoSize = false, Height = 36, Dock = DockStyle.Top, Text = "Looking for apps…" };
    readonly List<StartApp> picked;
    readonly Button save = new() { Text = "Save", Width = 90, Height = 30, DialogResult = DialogResult.OK, Enabled = false };
    List<StartApp> shown = new();

    // The ticked apps, in the order to store them.
    public List<StartApp> Result { get; private set; } = new();

    // `scan` finds the Start menu's programs; it runs in the background (it can take a second or two).
    public AppsForm(IReadOnlyList<StartApp> picked, Func<IReadOnlyList<StartApp>> scan)
    {
        this.picked = picked.ToList();
        Text = "Lid Awake: apps on phone";
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterScreen;
        ClientSize = new Size(420, 480);
        MinimumSize = new Size(360, 320);
        Padding = new Padding(12);

        var cancel = new Button { Text = "Cancel", Width = 90, Height = 30, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { FlowDirection = FlowDirection.RightToLeft, Dock = DockStyle.Bottom, Height = 40 };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(save);
        save.Click += (_, _) => Collect();
        AcceptButton = save;
        CancelButton = cancel;

        // Docked: the fill goes first (lowest), then the bottom, then the top.
        Controls.Add(list);
        Controls.Add(buttons);
        Controls.Add(note);

        // Shown at once with the apps picked so far, then the scan fills in the rest.
        Fill(this.picked);
        Shown += (_, _) => Task.Run(() =>
        {
            IReadOnlyList<StartApp> found;
            try { found = scan(); }
            catch (Exception) { found = Array.Empty<StartApp>(); }
            try { BeginInvoke(new Action(() => Fill(Merge(this.picked, found), done: true))); }
            catch (Exception) { }  // the window was closed meanwhile
        });
    }

    // The picked apps first, in order, then the others by name.
    static List<StartApp> Merge(List<StartApp> picked, IReadOnlyList<StartApp> found)
    {
        var all = new List<StartApp>(picked);
        var known = new HashSet<string>(picked.Select(p => PhoneApps.Id(p.Exe)));
        foreach (var f in found)
            if (known.Add(PhoneApps.Id(f.Exe))) all.Add(f);
        return all;
    }

    void Fill(List<StartApp> apps, bool done = false)
    {
        var ticked = new HashSet<string>(picked.Select(p => PhoneApps.Id(p.Exe)));
        // Keep what the person already ticked or unticked while the scan was running.
        if (shown.Count > 0)
            ticked = new HashSet<string>(Enumerable.Range(0, shown.Count).Where(i => list.GetItemChecked(i)).Select(i => PhoneApps.Id(shown[i].Exe)));
        list.Items.Clear();
        shown = apps;
        foreach (var app in apps) list.Items.Add(app.Name, ticked.Contains(PhoneApps.Id(app.Exe)));
        save.Enabled = done;
        note.Text = done
            ? (apps.Count == 0 ? "No apps found in the Start menu." : "Tick the apps to show on your phone.")
            : "Looking for apps…";
    }

    void Collect()
    {
        var chosen = new List<StartApp>();
        for (int i = 0; i < shown.Count; i++)
            if (list.GetItemChecked(i)) chosen.Add(shown[i]);
        Result = chosen;
    }
}
