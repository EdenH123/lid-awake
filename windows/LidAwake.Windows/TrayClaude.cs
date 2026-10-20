using System.Globalization;

namespace LidAwake;

// Claude plan usage in the tray (main.swift updateClaudeUsage and its menu items). Windows can't
// draw text beside a tray icon, so the Mac's "59% · 1h40m" goes into the icon's tooltip instead,
// after the tooltip's own text, while "Show Claude usage by the clock" is ticked (claudeInBar, on
// by default). The menu has a greyed line "Claude: Session 59%, resets 15:10 · Week 84%, resets
// Fri 22:59". Numbers past their reset time are hidden until fresh ones arrive. The third item
// sets Claude Code up to send its numbers and session events here (ClaudeSettingsFile).
//
// The tray puts Items in its menu and shows TooltipText() as the tooltip's last line; until it
// can, Attach(onUi, icon) adds that line to the icon's tooltip itself (ShowTip).
public sealed class TrayClaude
{
    public const string BarTitle = "Show Claude usage by the clock";
    public const string HooksTitle = "Claude alerts and usage from Claude Code…";
    public const string InBarKey = "claudeInBar";
    public const string Checking = "Claude usage: checking…";
    // NotifyIcon.Text's limit.
    public const int MaxTip = 127;

    readonly Settings settings;
    readonly ClaudeUsage usage;
    readonly ClaudeSettingsFile claudeSettings;
    readonly Action<string> log;
    readonly ToolStripMenuItem line = new(Checking) { Enabled = false };
    readonly ToolStripMenuItem barItem = new(BarTitle);
    readonly ToolStripMenuItem hooksItem = new(HooksTitle);
    // Every 2 seconds the tooltip; every 30 seconds (15 ticks) the saved numbers, as on the Mac.
    readonly System.Windows.Forms.Timer tick = new() { Interval = 2000 };
    int ticks;
    NotifyIcon? icon;
    string? composed;
    string? tipExtra;

    public TrayClaude(Settings settings, ClaudeUsage usage, ClaudeSettingsFile claudeSettings, Action<string> log)
    {
        (this.settings, this.usage, this.claudeSettings, this.log) = (settings, usage, claudeSettings, log);
        barItem.Click += (_, _) => FlipInBar();
        hooksItem.Click += (_, _) => FlipHooks();
        tick.Tick += (_, _) => OnTick();
    }

    public bool InBar
    {
        get
        {
            try { return settings.Get<bool?>(InBarKey) ?? true; }
            catch (Exception) { return true; }
        }
    }

    // ---- The words (pure, for the tests) ----

    public readonly record struct Texts(string Line, string? Bar);

    // The menu line, and "59% · 1h40m" for the session (null: nothing to show).
    public static Texts Describe(ClaudeUsage.Snapshot? usage, DateTimeOffset now, TimeZoneInfo zone, CultureInfo culture)
    {
        long unix = now.ToUnixTimeSeconds();
        (int Pct, DateTimeOffset? Resets)? Window(ClaudeUsage.Window? w)
        {
            if (w is not { } x) return null;
            // Past its reset time the number is old news.
            if (x.ResetsAt > 0 && x.ResetsAt <= unix) return null;
            return ((int)Math.Round(x.Pct, MidpointRounding.AwayFromZero), x.ResetsAt > 0 ? DateTimeOffset.FromUnixTimeSeconds(x.ResetsAt) : null);
        }
        var session = Window(usage?.FiveHour);
        var week = Window(usage?.SevenDay);
        string? bar = session is { } s ? $"{s.Pct}%" + (s.Resets is { } r ? " · " + ClaudeUsage.ShortDuration(r - now) : "") : null;
        var parts = new List<string>();
        if (session is { } a) parts.Add($"Session {a.Pct}%" + (a.Resets is { } ra ? ", resets " + ClaudeUsage.When(ra, now, zone, culture) : ""));
        if (week is { } b) parts.Add($"Week {b.Pct}%" + (b.Resets is { } rb ? ", resets " + ClaudeUsage.When(rb, now, zone, culture) : ""));
        return new Texts(parts.Count == 0 ? Checking : "Claude: " + string.Join(" · ", parts), bar);
    }

    // The tooltip's own text first, then the Claude part on its own line, within 127 characters:
    // the Claude part is shortened (or left out) before the tooltip's own text is.
    public static string Fit(string baseText, string? extra, int max = MaxTip)
    {
        var own = Cut(baseText ?? "", max);
        if (string.IsNullOrEmpty(extra)) return own;
        int room = max - own.Length - 1;
        if (room >= extra.Length) return own + "\n" + extra;
        return room >= 4 ? own + "\n" + Cut(extra, room) : own;
    }

    // At most `max` characters, never half of a surrogate pair, "…" when cut.
    static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        int keep = max - 1;
        if (keep > 0 && char.IsHighSurrogate(text[keep - 1])) keep--;
        return text[..keep] + "…";
    }

    // ---- In the tray ----

    // The menu items, for the tray to put after the safety limits: the greyed line, "Show Claude
    // usage by the clock", and "Claude alerts and usage from Claude Code…". Opening the menu they
    // are in reads the numbers again (asking Claude Code at most once a minute).
    public ToolStripItem[] Items => new ToolStripItem[] { line, barItem, hooksItem };

    // The tooltip's last line while "Show Claude usage by the clock" is on ("Claude 59% · 1h40m"),
    // or null. For the tray's tooltip; cheap (the numbers are read every 30 seconds, not here).
    public string? TooltipText() => tipExtra;

    // On the UI thread, once the items are in the menu. `onUi` posts to it (new numbers arrive on
    // the thread pool). With `icon`, this keeps the Claude line at the end of the icon's tooltip
    // itself, every 2 seconds; without one, the tray shows TooltipText() itself.
    public void Attach(Action<Action> onUi, NotifyIcon? icon = null)
    {
        this.icon = icon;
        if (line.Owner is ToolStripDropDown menu)
            menu.Opening += (_, _) =>
            {
                usage.Refresh(ClaudeUsage.AskOnMenu);
                Show();
                ShowHooks();
            };
        usage.Saved += () => onUi(Show);
        if (InBar) usage.Refresh(TimeSpan.Zero);
        Show();
        ShowHooks();
        tick.Start();
        // Lid Awake moved (an update to another folder): our entries in Claude Code's settings follow.
        var exe = Environment.ProcessPath;
        if (exe != null)
            Task.Run(() =>
            {
                try
                {
                    var r = claudeSettings.Update(exe);
                    if (r.Changed) log("Claude Code's settings now run Lid Awake from " + exe);
                }
                catch (Exception) { }
            });
    }

    public void Stop() => tick.Stop();

    void OnTick()
    {
        if (++ticks % 15 == 0 && InBar)
        {
            usage.Refresh(ClaudeUsage.AskForTray);
            Show();
        }
        ShowTip();
    }

    // Reads the saved numbers into the menu line and the tooltip.
    public void Show()
    {
        Texts t;
        try { t = Describe(usage.Read(), DateTimeOffset.UtcNow, TimeZoneInfo.Local, CultureInfo.CurrentCulture); }
        catch (Exception) { t = new Texts(Checking, null); }
        line.Text = t.Line;
        barItem.Checked = InBar;
        tipExtra = InBar && t.Bar != null ? "Claude " + t.Bar : null;
        ShowTip();
    }

    // A tooltip someone else set since (Lid Awake on or off) is the new base; the Claude part goes after it.
    void ShowTip()
    {
        if (icon == null) return;
        try
        {
            var current = icon.Text ?? "";
            string own = current == composed && baseText != null ? baseText : current;
            baseText = own;
            var tip = Fit(own, tipExtra);
            composed = tip;
            if (tip != current) icon.Text = tip;
        }
        catch (Exception) { }
    }

    string? baseText;

    void ShowHooks()
    {
        try { hooksItem.Checked = claudeSettings.State() == ClaudeSettingsFile.Setup.On; }
        catch (Exception) { }
    }

    void FlipInBar()
    {
        bool on = !InBar;
        try { settings.Set(InBarKey, on); }
        catch (Exception) { return; }  // the settings file couldn't be written: the switch stays as it was
        if (on) usage.Refresh(ClaudeUsage.AskOnMenu);
        Show();
    }

    // Changing Claude Code's settings file only ever happens here (or in the installer), when the
    // owner asks. Turning it on says what will change first.
    void FlipHooks()
    {
        try { FlipHooksNow(); }
        catch (Exception e)
        {
            // Never WinForms' own error window: a plain message, and nothing else happens.
            try { MessageBox.Show("Claude Code's settings couldn't be changed: " + e.Message, "Lid Awake", MessageBoxButtons.OK, MessageBoxIcon.Warning); }
            catch (Exception) { }
        }
        ShowHooks();
    }

    void FlipHooksNow()
    {
        bool on = claudeSettings.State() == ClaudeSettingsFile.Setup.On;
        ClaudeSettingsFile.Result r;
        if (!on)
        {
            var answer = MessageBox.Show(
                "Lid Awake will add itself to Claude Code's settings file:\n\n" + claudeSettings.FilePath + "\n\n" +
                "Then Claude Code tells Lid Awake its usage after each reply, and your phone gets an alert when Claude " +
                "finishes or needs you while you're away from the PC.\n\n" +
                "Lid Awake saves a copy of the file first (settings.json" + ClaudeSettingsFile.BackupSuffix + ") and changes nothing else. " +
                "If you have your own status line, it stays. Click this item again to take Lid Awake out.",
                "Lid Awake", MessageBoxButtons.OKCancel, MessageBoxIcon.Information);
            if (answer != DialogResult.OK) return;
            var exe = Environment.ProcessPath ?? "";
            r = claudeSettings.Install(exe);
            if (r.Ok && r.Changed) log("Added Lid Awake to Claude Code's settings (hooks" + (r.TheirStatusLine ? "; your own status line kept" : " and status line") + ")");
        }
        else
        {
            r = claudeSettings.Remove();
            if (r.Ok && r.Changed) log("Took Lid Awake out of Claude Code's settings");
        }
        MessageBox.Show(r.Message, "Lid Awake", MessageBoxButtons.OK, r.Ok ? MessageBoxIcon.Information : MessageBoxIcon.Warning);
    }
}
