using System.Drawing.Imaging;
using System.Globalization;
using LidAwake;
using Xunit;
using Xunit.Abstractions;

// The tray's texts and pictures (TrayText, TrayNotices, TrayIcons), from plain values: the Mac's
// main.swift refresh, updateCountdown and rebuildDevicesMenu, without a tray.
public class TrayTextTests
{
    static readonly TrayActivity Idle = TrayActivity.None;
    static readonly TrayActivity Watched = new(1, false, false);
    static readonly TrayActivity Touched = new(1, true, false);
    static readonly TrayActivity TouchedUnseen = new(0, true, false);
    static readonly TrayActivity Camera = new(0, false, true);

    [Theory]
    [InlineData(5025, "1:23:45")]
    [InlineData(3600, "1:00:00")]
    [InlineData(1425.2, "23:46")]   // rounded up, as on the Mac
    [InlineData(59, "0:59")]
    [InlineData(0, "0:00")]
    [InlineData(-3, "0:00")]
    public void RemainingIsTheMacsCountdown(double seconds, string text) => Assert.Equal(text, TrayText.Remaining(seconds));

    [Fact]
    public void StatusLineOnOffAndTimer()
    {
        Assert.Equal("Off", TrayText.StatusLine(false, null, Idle));
        Assert.Equal("On, no timer", TrayText.StatusLine(true, null, Idle));
        Assert.Equal("Turns off in 1:23:45", TrayText.StatusLine(true, 5025, Idle));
        Assert.Equal("Off", TrayText.StatusLine(false, 5025, Idle));     // a countdown only counts while on
        Assert.Equal("On, no timer", TrayText.StatusLine(true, 0, Idle)); // time up: no countdown
    }

    // The icon's order: the camera, then control, then viewing, then the timer.
    [Fact]
    public void StatusLinePutsWhatThePhoneDoesFirst()
    {
        Assert.Equal("Screen being viewed from your phone", TrayText.StatusLine(true, 5025, Watched));
        Assert.Equal("Being controlled from your phone", TrayText.StatusLine(true, 5025, Touched));
        // Control with nobody watching (POST /input): the words match the hand.
        Assert.Equal("Being controlled from your phone", TrayText.StatusLine(true, 5025, TouchedUnseen));
        Assert.Equal("Camera being watched from your phone", TrayText.StatusLine(true, 5025, Camera));
        Assert.Equal("Camera being watched from your phone", TrayText.StatusLine(true, null, new TrayActivity(2, true, true)));
    }

    [Fact]
    public void IconFollowsTheMacsOrder()
    {
        Assert.Equal(TrayIconState.Off, TrayText.Icon(false, Idle));
        Assert.Equal(TrayIconState.On, TrayText.Icon(true, Idle));
        Assert.Equal(TrayIconState.Viewed, TrayText.Icon(true, Watched));
        Assert.Equal(TrayIconState.Controlled, TrayText.Icon(true, Touched));
        Assert.Equal(TrayIconState.Controlled, TrayText.Icon(true, TouchedUnseen));  // the hand shows without a viewer
        Assert.Equal(TrayIconState.Camera, TrayText.Icon(true, Camera));
        Assert.Equal(TrayIconState.Camera, TrayText.Icon(true, new TrayActivity(3, true, true)));
        // Whatever the switch says, what the phone does shows.
        Assert.Equal(TrayIconState.Viewed, TrayText.Icon(false, Watched));
    }

    [Fact]
    public void TooltipIsMadeOfParts()
    {
        Assert.Equal("Lid Awake: off", TrayText.Tooltip(false, null, Idle));
        Assert.Equal("Lid Awake: on", TrayText.Tooltip(true, null, Idle));
        Assert.Equal("Lid Awake: on\nTurns off in 1:23:45", TrayText.Tooltip(true, 5025, Idle));
        Assert.Equal("Lid Awake: on\nTurns off in 1:23:45\n59% · 1h40m", TrayText.Tooltip(true, 5025, Idle, "59% · 1h40m"));
        Assert.Equal("Lid Awake: off\n59% · 1h40m", TrayText.Tooltip(false, 5025, Idle, " 59% · 1h40m "));
        Assert.Equal("Lid Awake: on", TrayText.Tooltip(true, null, Idle, "  "));
        Assert.Equal("Lid Awake: your screen is being viewed from your phone", TrayText.Tooltip(true, null, Watched));
        Assert.Equal("Lid Awake: your PC is being controlled from your phone\nTurns off in 0:30", TrayText.Tooltip(true, 30, Touched));
        Assert.Equal("Lid Awake: your camera is being watched from your phone", TrayText.Tooltip(true, null, Camera));
        Assert.Equal("Lid Awake: your PC is being controlled from your phone", TrayText.Tooltip(true, null, TouchedUnseen));
        // Never more than Windows takes.
        var longest = TrayText.Tooltip(true, 5025, Touched, new string('x', 300));
        Assert.Equal(TrayText.MaxTooltip, longest.Length);
        Assert.StartsWith("Lid Awake: your PC is being controlled", longest);
    }

    // Cut to Windows' limit without leaving half of an emoji at the end.
    [Fact]
    public void TooltipCutKeepsCharactersWhole()
    {
        var at = TrayText.MaxTooltip - "Lid Awake: on\n".Length - 1;  // the emoji starts on the last place
        var tip = TrayText.Tooltip(true, null, Idle, new string('a', at) + "😀 more");
        Assert.Equal(TrayText.MaxTooltip - 1, tip.Length);
        Assert.False(char.IsHighSurrogate(tip[^1]));
        Assert.Equal("ab", TrayText.Cut("ab😀", 3));
        Assert.Equal("ab😀", TrayText.Cut("ab😀", 4));
        Assert.Equal("abc", TrayText.Cut("abc", 10));
    }

    // A phone that falls back to still pictures (GET /screen.jpg) is a viewer too.
    [Fact]
    public void PollingCountsAsViewing()
    {
        var polled = TrayActivity.Of(0, polling: true, controlling: false, cameraWatched: false);
        Assert.Equal(1, polled.Viewers);
        Assert.Equal(TrayIconState.Viewed, TrayText.Icon(true, polled));
        Assert.Equal("Screen being viewed from your phone", TrayText.StatusLine(true, 5025, polled));
        Assert.Equal("Lid Awake: your screen is being viewed from your phone", TrayText.Tooltip(true, null, polled));
        Assert.Equal(3, TrayActivity.Of(2, true, false, false).Viewers);
        Assert.Equal(TrayActivity.None, TrayActivity.Of(0, false, false, false));
    }

    static PowerReading Power(byte ac, byte flag, byte percent) => new(ac, flag, percent, -1);

    [Fact]
    public void InfoLineSaysWhatIsKnown()
    {
        Assert.Equal("No battery info", TrayText.InfoLine(null, null));
        Assert.Equal("No battery info", TrayText.InfoLine(Power(1, 128, 255), null));  // a desktop PC
        Assert.Equal("31.2°C", TrayText.InfoLine(Power(1, 128, 255), 31.24));
        Assert.Equal("Battery 80% · full in 1h 5m · 31.2°C", TrayText.InfoLine(Power(1, 8, 80), 31.24, 65));
        Assert.Equal("Battery 80% · full in 0h 40m", TrayText.InfoLine(Power(1, 8, 80), null, 40));
        Assert.Equal("Battery 80% · charging", TrayText.InfoLine(Power(1, 8, 80), null));
        Assert.Equal("Battery 80% · charging", TrayText.InfoLine(Power(1, 8, 80), null, 0));
        Assert.Equal("Battery 100% (plugged in)", TrayText.InfoLine(Power(1, 1, 100), null));
        Assert.Equal("Battery 55% · 36.1°C", TrayText.InfoLine(Power(0, 0, 55), 36.06));
        Assert.Equal("Battery 55%", TrayText.InfoLine(Power(255, 0, 55), null));  // Windows can't tell the plug
        Assert.Equal("Battery 55%", TrayText.InfoLine(Power(0, 0, 55), null, 30));  // to full only while charging
    }

    // The tray's third argument is the batteries' own reading (SystemReadings.Charge, as /status
    // minutesToFull): with minutes the line says "full in", without a reading yet "charging".
    [Fact]
    public void InfoLineShowsFullInFromTheChargeReading()
    {
        // 25 000 of 50 000 mWh at 12 500 mW: two hours.
        var charge = new BatteryCharge(new[] { new BatteryCell(BatteryCharge.OnLine | BatteryCharge.Charging, 25_000, 12_500, 50_000) });
        Assert.Equal(120, TrayText.MinutesToFull(charge));
        Assert.Equal("Battery 50% · full in 2h 0m", TrayText.InfoLine(Power(1, 8, 50), null, TrayText.MinutesToFull(charge)));
        // 49 000 of 50 000 at 7 000 mW: nine minutes.
        var nearly = new BatteryCharge(new[] { new BatteryCell(BatteryCharge.OnLine | BatteryCharge.Charging, 49_000, 7_000, 50_000) });
        Assert.Equal("Battery 98% · full in 0h 9m · 30.0°C", TrayText.InfoLine(Power(1, 8, 98), 30.0, TrayText.MinutesToFull(nearly)));
        // Not read yet, or a rate the driver doesn't know: "charging".
        Assert.Equal(-1, TrayText.MinutesToFull(null));
        Assert.Equal("Battery 50% · charging", TrayText.InfoLine(Power(1, 8, 50), null, TrayText.MinutesToFull(null)));
        var unknownRate = new BatteryCharge(new[] { new BatteryCell(BatteryCharge.Charging, 25_000, BatteryCharge.UnknownRate, 50_000) });
        Assert.Equal("Battery 50% · charging", TrayText.InfoLine(Power(1, 8, 50), null, TrayText.MinutesToFull(unknownRate)));
    }

    [Fact]
    public void InfoLineUsesAPointWhateverTheLanguage()
    {
        var before = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("de-DE");
            Assert.Equal("Battery 70% · 29.5°C", TrayText.InfoLine(Power(0, 0, 70), 29.5));
        }
        finally { CultureInfo.CurrentCulture = before; }
    }

    [Fact]
    public void PasswordItemSaysSetThenChange()
    {
        Assert.Equal("Set phone password…", TrayText.PasswordTitle(false));
        Assert.Equal("Change phone password…", TrayText.PasswordTitle(true));
    }

    [Fact]
    public void LivePageNeedsTheTailscaleName()
    {
        Assert.Null(TrayText.LivePageUrl(null));
        Assert.Null(TrayText.LivePageUrl(""));
        Assert.Null(TrayText.LivePageUrl("not a host"));
        Assert.Null(TrayText.LivePageUrl("pc.ts.net/../x"));
        Assert.Equal("https://desk-pc.tail1234.ts.net/", TrayText.LivePageUrl("desk-pc.tail1234.ts.net"));
    }
}

// The "Signed-in devices" submenu, from a real Auth, and the lockout and sign-out rules it uses.
public class TrayDevicesTests : IDisposable
{
    const string Good = "correct horse battery";
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    DateTimeOffset clock = DateTimeOffset.FromUnixTimeSeconds(1_792_000_000);  // October 2026
    readonly Auth auth;

    public TrayDevicesTests()
    {
        auth = new Auth(new Settings(folder), iterations: 1000, now: () => clock);
        auth.SetPassword(Good);
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    List<DeviceMenuLine> Menu() => TrayText.Devices(auth.Sessions(Array.Empty<string>()), auth.IsLockedOut, CultureInfo.InvariantCulture);

    static string Day(DateTimeOffset at) => at.ToLocalTime().ToString("d MMM", CultureInfo.InvariantCulture);

    [Fact]
    public void NoDevices()
    {
        var lines = Menu();
        Assert.Equal(new[] { "No devices signed in", "Show activity log" }, lines.Select(l => l.Text));
        Assert.False(lines[0].Enabled);
        Assert.Equal(DeviceMenuAction.ShowLog, lines[1].Action);
        Assert.True(lines[1].SeparatorBefore);
    }

    [Fact]
    public void EachDeviceNewestFirstWithItsDayAndHow()
    {
        var first = clock;
        auth.Login(Good, "iPhone");
        clock = clock.AddDays(2);
        auth.LoginWithPasskey("key-1", "Android phone");
        var lines = Menu();
        Assert.Equal(new[]
        {
            "Signed in (click to sign out)",
            $"Android phone · {Day(clock)} · Face ID or fingerprint",
            $"iPhone · {Day(first)} · password",
            "Sign out all devices",
            "Show activity log",
        }, lines.Select(l => l.Text));
        Assert.Equal(new[] { false, true, true, true, true }, lines.Select(l => l.Enabled));
        Assert.All(lines.Skip(1).Take(2), l => Assert.Equal(Auth.SessionIdLength, l.SessionId!.Length));
        Assert.Equal(DeviceMenuAction.SignOutAll, lines[3].Action);
        Assert.False(lines[3].SeparatorBefore);

        // A click on a device signs out that one only, from the PC (no caller to keep).
        Assert.Equal(1, auth.SignOut(lines[2].SessionId, Array.Empty<string>()));
        var after = Menu();
        Assert.Equal(new[] { "Signed in (click to sign out)", $"Android phone · {Day(clock)} · Face ID or fingerprint", "Show activity log" },
            after.Select(l => l.Text));  // one left: no "Sign out all devices"
    }

    [Fact]
    public void SignOutAllSignsOutEveryDevice()
    {
        var mine = auth.Login(Good, "iPhone").Token!;
        auth.Login(Good, "iPad");
        auth.LoginWithPasskey("key-1", "Android phone");
        Assert.Equal(3, auth.SignOutAll());
        Assert.False(auth.IsValid(mine));
        Assert.Empty(auth.Sessions(Array.Empty<string>()));
        Assert.Equal(0, auth.SignOutAll());
        Assert.Equal("No devices signed in", Menu()[0].Text);
    }

    [Fact]
    public void LockoutShowsTheAllowAgainLineUntilClearedOrOver()
    {
        Assert.False(auth.IsLockedOut);
        Assert.DoesNotContain(Menu(), l => l.Action == DeviceMenuAction.ClearLockout);
        for (int i = 0; i < Auth.MaxTries; i++) auth.Login("nope", "Phone");
        Assert.True(auth.IsLockedOut);
        var line = Assert.Single(Menu(), l => l.Action == DeviceMenuAction.ClearLockout);
        Assert.Equal("Allow password sign-in again (after wrong tries)", line.Text);
        Assert.True(line.SeparatorBefore);
        Assert.Equal(LoginResult.Kind.Locked, auth.Login(Good, "Phone").Result);

        auth.ClearLockout();
        Assert.False(auth.IsLockedOut);
        Assert.DoesNotContain(Menu(), l => l.Action == DeviceMenuAction.ClearLockout);
        // The wrong tries start again from nothing: one more wrong try is only wrong.
        Assert.Equal(LoginResult.Kind.Wrong, auth.Login("nope", "Phone").Result);
        Assert.Equal(LoginResult.Kind.Ok, auth.Login(Good, "Phone").Result);
    }

    [Fact]
    public void LockoutEndsByItself()
    {
        for (int i = 0; i < Auth.MaxTries; i++) auth.Login("nope", "Phone");
        Assert.True(auth.IsLockedOut);
        clock = clock.AddSeconds(59);
        Assert.True(auth.IsLockedOut);
        clock = clock.AddSeconds(1);
        Assert.False(auth.IsLockedOut);
    }

    [Fact]
    public void ClearingWithoutALockoutChangesNothing()
    {
        auth.ClearLockout();
        Assert.False(auth.IsLockedOut);
        Assert.Equal(LoginResult.Kind.Ok, auth.Login(Good, "Phone").Result);
    }
}

// The balloons and activity-log lines, with the Mac's texts.
public class TrayNoticesTests
{
    readonly List<string> logged = new();
    readonly List<(string Title, string Text, bool Warning)> balloons = new();
    readonly TrayNotices notices;

    public TrayNoticesTests() => notices = new TrayNotices(logged.Add, (t, b, w) => balloons.Add((t, b, w)));

    [Fact]
    public void TurnedOffFromThePhoneIsABalloonOnly()
    {
        // The web server writes "Lid Awake turned off from the phone" itself.
        notices.TurnedOffFromPhone();
        Assert.Empty(logged);
        Assert.Equal(("Lid Awake turned off", "Turned off from your phone. Your PC will sleep normally again.", false), Assert.Single(balloons));
    }

    [Fact]
    public void SignOutAllIsLoggedAndSaid()
    {
        notices.SignedOutAllDevices();
        Assert.Equal(new[] { "Signed out all devices from the PC menu" }, logged);
        Assert.Equal("Every device is signed out of the phone page.", Assert.Single(balloons).Text);
    }

    [Fact]
    public void FunnelIsLoggedAndSaidAsAWarning()
    {
        notices.FunnelTurnedOff();
        Assert.Equal(new[] { "Tailscale Funnel was on for this PC; turned it off" }, logged);
        var b = Assert.Single(balloons);
        Assert.Equal("Tailscale Funnel was on, which would put your phone page on the open internet. Lid Awake turned it off.", b.Text);
        Assert.True(b.Warning);
    }

    [Fact]
    public void QuietOnesAreOnlyLogged()
    {
        notices.SignedOutDevice();
        notices.LockoutCleared();
        notices.ControlChanged(true);
        notices.ControlChanged(false);
        Assert.Equal(new[]
        {
            "Signed out a device from the PC menu",
            "Password sign-in allowed again from the PC menu",
            "Control from phone allowed on the PC",
            "Control from phone turned off (view only)",
        }, logged);
        Assert.Empty(balloons);
    }

    [Fact]
    public void NoAddressYetIsABalloon()
    {
        notices.NoLivePageAddress();
        Assert.Empty(logged);
        Assert.Equal("Tailscale isn't connected yet, so the phone page has no address.", Assert.Single(balloons).Text);
    }
}

// The drawn icons. How they look can only be judged by eye: TRAY-ICONS prints a PNG of all of them
// (four times bigger, on a dark and a light taskbar) as base64.
public class TrayIconsTests
{
    readonly ITestOutputHelper output;

    public TrayIconsTests(ITestOutputHelper output) => this.output = output;

    static readonly TrayIconState[] States = Enum.GetValues<TrayIconState>();

    [Theory]
    [InlineData(16, 16)]
    [InlineData(17, 20)]
    [InlineData(20, 20)]
    [InlineData(24, 24)]
    [InlineData(30, 32)]
    [InlineData(48, 32)]
    public void SizeSnapsToADrawnSize(int smallIcon, int drawn) => Assert.Equal(drawn, TrayIcons.SizeFor(smallIcon));

    static int Opaque(Bitmap b)
    {
        int n = 0;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                if (b.GetPixel(x, y).A > 128) n++;
        return n;
    }

    static string Fingerprint(Bitmap b)
    {
        var sb = new System.Text.StringBuilder();
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
                sb.Append(b.GetPixel(x, y).A > 128 ? '#' : '.');
        return sb.ToString();
    }

    [Fact]
    public void EveryStateIsDrawnDistinctAtEverySize()
    {
        foreach (var size in TrayIcons.Sizes)
        {
            var seen = new HashSet<string>();
            foreach (var state in States)
            {
                using var dark = TrayIcons.Draw(state, size, lightTaskbar: false);
                using var light = TrayIcons.Draw(state, size, lightTaskbar: true);
                Assert.Equal(size, dark.Width);
                Assert.Equal(size, dark.Height);
                int ink = Opaque(dark);
                // Something to see, and transparent around it.
                Assert.InRange(ink, size * size / 10, size * size * 3 / 4);
                Assert.True(seen.Add(Fingerprint(dark)), $"{state} at {size} looks like another state");
                Assert.Equal(Fingerprint(dark), Fingerprint(light));
                Assert.Equal(0, dark.GetPixel(0, 0).A);
                // White on a dark taskbar, near-black on a light one.
                var (d, l) = (Darkest(dark, inkOnly: true), Darkest(light, inkOnly: true));
                Assert.True(d > 200, $"{state} at {size} on dark: {d}");
                Assert.True(l < 60, $"{state} at {size} on light: {l}");
            }
        }
    }

    // The brightness of the most opaque pixel.
    static int Darkest(Bitmap b, bool inkOnly)
    {
        Color best = Color.Transparent;
        for (int y = 0; y < b.Height; y++)
            for (int x = 0; x < b.Width; x++)
            {
                var c = b.GetPixel(x, y);
                if (c.A > best.A) best = c;
            }
        return inkOnly ? (best.R + best.G + best.B) / 3 : best.A;
    }

    [Fact]
    public void IconsOwnTheirHandle()
    {
        foreach (var state in States)
        {
            var icon = TrayIcons.Make(state, 20, false);
            Assert.Equal(20, icon.Width);
            Assert.NotEqual(IntPtr.Zero, icon.Handle);
            using var back = icon.ToBitmap();
            Assert.True(Opaque(back) > 0);
            icon.Dispose();
        }
    }

    [Fact]
    public void PrintsAllIconsForAPersonToLookAt()
    {
        const int zoom = 4, cell = 32 * zoom + 8;
        using var sheet = new Bitmap(TrayIcons.Sizes.Length * cell, States.Length * 2 * cell, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(sheet))
        {
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            for (int row = 0; row < States.Length * 2; row++)
            {
                bool light = row % 2 == 1;
                using var back = new SolidBrush(light ? Color.FromArgb(243, 243, 243) : Color.FromArgb(32, 32, 32));
                g.FillRectangle(back, 0, row * cell, sheet.Width, cell);
                for (int col = 0; col < TrayIcons.Sizes.Length; col++)
                {
                    int size = TrayIcons.Sizes[col];
                    using var icon = TrayIcons.Draw(States[row / 2], size, light);
                    g.DrawImage(icon, new Rectangle(col * cell + 4, row * cell + 4, size * zoom, size * zoom));
                }
            }
        }
        using var png = new MemoryStream();
        sheet.Save(png, ImageFormat.Png);
        output.WriteLine("TRAY-ICONS: rows " + string.Join(", ", States) + " (dark, then light); columns " + string.Join(", ", TrayIcons.Sizes) + " px, 4x");
        output.WriteLine("TRAY-ICONS-PNG: " + Convert.ToBase64String(png.ToArray()));
    }
}

// GET /screen.jpg polls: viewed while one waits and for 3 seconds after the last one ended.
public class ScreenPollsTests
{
    long now = 50_000;

    [Fact]
    public void ViewedWhileWaitingAndForThreeSecondsAfter()
    {
        var polls = new ScreenPolls(() => now);
        Assert.False(polls.Viewed);
        polls.Started();
        now += 10_000;  // a poll that waits long still counts
        Assert.True(polls.Viewed);
        polls.Ended();
        Assert.True(polls.Viewed);
        now += ScreenPolls.ViewedFor - 1;
        Assert.True(polls.Viewed);
        now += 1;
        Assert.False(polls.Viewed);
    }

    [Fact]
    public void TwoAtOnceCountUntilBothEnd()
    {
        var polls = new ScreenPolls(() => now);
        polls.Started();
        polls.Started();
        polls.Ended();
        now += ScreenPolls.ViewedFor;
        Assert.True(polls.Viewed);  // the second still waits
        polls.Ended();
        Assert.True(polls.Viewed);
        now += ScreenPolls.ViewedFor;
        Assert.False(polls.Viewed);
        polls.Ended();  // an extra end counts as a poll that just ended
        Assert.True(polls.Viewed);
    }
}
