using System.Globalization;

namespace LidAwake;

// What the phone is doing on this PC right now, for the tray: phones watching the screen
// (LiveServer.Viewers, plus one for recent /screen.jpg polls), a click or key from the phone in the
// last 5 seconds (RemoteControl.Controlling) and phones with the camera open (CameraService.Watchers).
public readonly record struct TrayActivity(int Viewers, bool Controlling, bool CameraWatched)
{
    public static readonly TrayActivity None = new(0, false, false);

    // The phone that falls back to still pictures (ScreenPolls) watches the screen too.
    public static TrayActivity Of(int socketViewers, bool polling, bool controlling, bool cameraWatched) =>
        new(Math.Max(0, socketViewers) + (polling ? 1 : 0), controlling, cameraWatched);
}

// GET /screen.jpg, the phone's still pictures when its live socket fails: the screen counts as
// viewed while a poll waits and for ViewedFor after the last one ended (a phone polls again at
// once, and each poll waits up to 2 seconds). `clock` is milliseconds that only go forward.
public sealed class ScreenPolls
{
    public const long ViewedFor = 3000;

    readonly Func<long> clock;
    readonly object gate = new();
    int waiting;
    long? lastEnded;

    public ScreenPolls(Func<long>? clock = null) => this.clock = clock ?? (() => Environment.TickCount64);

    public void Started()
    {
        lock (gate) waiting++;
    }

    public void Ended()
    {
        lock (gate)
        {
            if (waiting > 0) waiting--;
            lastEnded = clock();
        }
    }

    public bool Viewed
    {
        get
        {
            lock (gate)
            {
                if (waiting > 0) return true;
                long now = clock();
                return lastEnded is { } at && now - at >= 0 && now - at < ViewedFor;
            }
        }
    }
}

// The tray icon's five pictures, with the Mac's meaning (main.swift refresh): a moon while off, a
// cup while on, an eye while the screen is watched, a hand while the phone clicks or types, a
// video camera while the camera is watched.
public enum TrayIconState { Off, On, Viewed, Controlled, Camera }

// One line of the "Signed-in devices" submenu. `SessionId` is set on a device's line (a click signs
// it out); `Action` on the other clickable lines; a line with neither is a greyed-out header.
// `SeparatorBefore` draws a line above it.
public sealed record DeviceMenuLine(string Text, string? SessionId = null, DeviceMenuAction Action = DeviceMenuAction.None,
                                    bool SeparatorBefore = false)
{
    public bool Enabled => SessionId != null || Action != DeviceMenuAction.None;
}

public enum DeviceMenuAction { None, SignOutAll, ClearLockout, ShowLog }

// The tray's texts, worked out from plain values so they can be tested without a tray (the Mac's
// main.swift: refresh, updateCountdown, rebuildDevicesMenu).
public static class TrayText
{
    public const string On = "On, no timer";
    public const string Off = "Off";
    public const string Viewed = "Screen being viewed from your phone";
    public const string Controlled = "Being controlled from your phone";
    public const string CameraWatched = "Camera being watched from your phone";
    public const string NoBattery = "No battery info";
    public const string SetPassword = "Set phone password…";
    public const string ChangePassword = "Change phone password…";
    public const string NoTailscaleName = "Tailscale isn't connected yet, so the phone page has no address.";

    // Windows refuses a longer tray tooltip (NotifyIcon.Text).
    public const int MaxTooltip = 127;

    // The countdown: "1:23:45", or "23:45" under an hour (formatRemaining on the Mac).
    public static string Remaining(double seconds)
    {
        long total = Math.Max(0, (long)Math.Ceiling(seconds));
        long h = total / 3600, m = total % 3600 / 60, s = total % 60;
        return h > 0 ? $"{h}:{m:00}:{s:00}" : $"{m}:{s:00}";
    }

    // The greyed-out first line of the menu, in the icon's order: the camera first, then control
    // (also POST /input with no screen watched, so the words match the hand), then viewing, then
    // the timer, then on or off. `remaining`: seconds left on the "Turn off after" timer
    // (AutoOff.Remaining: null with no timer running).
    public static string StatusLine(bool on, double? remaining, TrayActivity activity)
    {
        if (activity.CameraWatched) return CameraWatched;
        if (activity.Controlling) return Controlled;
        if (activity.Viewers > 0) return Viewed;
        if (on && remaining is { } left && left > 0) return "Turns off in " + Remaining(left);
        return on ? On : Off;
    }

    // The icon, in the Mac's order: camera, hand, eye, then cup or moon. (A hand shows even with no
    // screen watched: the phone's touches are input whether it watches or not.)
    public static TrayIconState Icon(bool on, TrayActivity activity) =>
        activity.CameraWatched ? TrayIconState.Camera
        : activity.Controlling ? TrayIconState.Controlled
        : activity.Viewers > 0 ? TrayIconState.Viewed
        : on ? TrayIconState.On : TrayIconState.Off;

    // The tooltip's first part (the Mac's baseToolTip and label): what the phone is doing, in the
    // icon's order, else on or off. The camera has its own words here, since a tooltip is all
    // Windows shows by the icon.
    public static string TooltipStatus(bool on, TrayActivity activity) =>
        activity.CameraWatched ? "Lid Awake: your camera is being watched from your phone"
        : activity.Controlling ? "Lid Awake: your PC is being controlled from your phone"
        : activity.Viewers > 0 ? "Lid Awake: your screen is being viewed from your phone"
        : on ? "Lid Awake: on" : "Lid Awake: off";

    // The tooltip, one part per line: the status, the countdown while a timer runs (the Mac shows it
    // beside the icon; Windows has no room there), then `extra` (another feature's line, such as
    // Claude usage; null or empty: none). Cut to what Windows accepts.
    public static string Tooltip(bool on, double? remaining, TrayActivity activity, string? extra = null)
    {
        var parts = new List<string> { TooltipStatus(on, activity) };
        if (on && remaining is { } left && left > 0) parts.Add("Turns off in " + Remaining(left));
        if (!string.IsNullOrWhiteSpace(extra)) parts.Add(extra.Trim());
        return Cut(string.Join("\n", parts), MaxTooltip);
    }

    // At most `max` characters, never splitting an emoji or other character made of two halves.
    public static string Cut(string text, int max)
    {
        if (text.Length <= max) return text;
        int end = Math.Max(0, max);
        if (end > 0 && char.IsHighSurrogate(text[end - 1])) end--;
        return text[..end];
    }

    // "Battery 80% · full in 1h 5m · 31.2°C" (the Mac's info line), from GetSystemPowerStatus and
    // the battery's own temperature. What isn't known is left out. `minutesToFull`: -1 (or 0) when
    // Windows gives no estimate.
    public static string InfoLine(PowerReading? power, double? batteryTemp, int minutesToFull = -1)
    {
        var parts = new List<string>();
        if (power is { } p && StatusInfo.HasBattery(p))
        {
            bool charging = (p.BatteryFlag & 8) != 0;
            var line = $"Battery {p.BatteryLifePercent}%";
            if (charging && minutesToFull > 0) line += $" · full in {minutesToFull / 60}h {minutesToFull % 60}m";
            else if (charging) line += " · charging";
            else if (p.AcLineStatus == 1) line += " (plugged in)";
            parts.Add(line);
        }
        if (batteryTemp is { } t)
            parts.Add(TemperatureMath.OneDecimal(t).ToString("0.0", CultureInfo.InvariantCulture) + "°C");
        return parts.Count == 0 ? NoBattery : string.Join(" · ", parts);
    }

    public static string PasswordTitle(bool hasPassword) => hasPassword ? ChangePassword : SetPassword;

    // https://<pc>.ts.net/ once Tailscale has given this PC its name; null before (or for a stored
    // value that isn't a plain host name).
    public static string? LivePageUrl(string? host) =>
        !string.IsNullOrWhiteSpace(host) && Uri.CheckHostName(host) == UriHostNameType.Dns ? $"https://{host}/" : null;

    // "Signed-in devices", as rebuildDevicesMenu on the Mac: every session (newest first) with the
    // day it signed in and how; "Sign out all devices" with more than one; "Allow password sign-in
    // again" only while wrong tries lock sign-in; then the activity log. (The Face ID and
    // fingerprint keys have their own submenu.)
    public static List<DeviceMenuLine> Devices(IReadOnlyList<Auth.SessionInfo> sessions, bool lockedOut, CultureInfo? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var lines = new List<DeviceMenuLine>
        {
            new(sessions.Count == 0 ? "No devices signed in" : "Signed in (click to sign out)"),
        };
        foreach (var s in sessions)
        {
            var day = DateTimeOffset.FromUnixTimeSeconds(s.Created).ToLocalTime().ToString("d MMM", culture);
            var how = s.FaceID ? "Face ID or fingerprint" : "password";
            lines.Add(new($"{s.Device} · {day} · {how}", SessionId: s.Id));
        }
        if (sessions.Count > 1) lines.Add(new("Sign out all devices", Action: DeviceMenuAction.SignOutAll));
        if (lockedOut)
            lines.Add(new("Allow password sign-in again (after wrong tries)", Action: DeviceMenuAction.ClearLockout, SeparatorBefore: true));
        lines.Add(new("Show activity log", Action: DeviceMenuAction.ShowLog, SeparatorBefore: true));
        return lines;
    }
}

// What the tray says at the PC and writes in the activity log, with the Mac's texts (main.swift
// notify and AccessLog.write). `balloon` shows a tray balloon (title, text, warning?).
public sealed class TrayNotices
{
    public const string OffTitle = "Lid Awake turned off";
    public const string OffFromPhone = "Turned off from your phone. Your PC will sleep normally again.";
    public const string SignedOutAllLog = "Signed out all devices from the PC menu";
    public const string SignedOutAllTitle = "Signed out";
    public const string SignedOutAll = "Every device is signed out of the phone page.";
    public const string SignedOutOneLog = "Signed out a device from the PC menu";
    public const string LockoutClearedLog = "Password sign-in allowed again from the PC menu";
    public const string FunnelLog = "Tailscale Funnel was on for this PC; turned it off";
    public const string FunnelTitle = "Tailscale Funnel turned off";
    public const string Funnel = "Tailscale Funnel was on, which would put your phone page on the open internet. Lid Awake turned it off.";
    public const string ControlOnLog = "Control from phone allowed on the PC";
    public const string ControlOffLog = "Control from phone turned off (view only)";
    public const string NoAddressTitle = "No phone page address yet";

    readonly Action<string> log;
    readonly Action<string, string, bool> balloon;

    public TrayNotices(Action<string> log, Action<string, string, bool> balloon) => (this.log, this.balloon) = (log, balloon);

    // The phone turned Lid Awake off (POST /awake; the web server already wrote the log line).
    public void TurnedOffFromPhone() => balloon(OffTitle, OffFromPhone, false);

    public void SignedOutAllDevices()
    {
        log(SignedOutAllLog);
        balloon(SignedOutAllTitle, SignedOutAll, false);
    }

    public void SignedOutDevice() => log(SignedOutOneLog);

    public void LockoutCleared() => log(LockoutClearedLog);

    public void FunnelTurnedOff()
    {
        log(FunnelLog);
        balloon(FunnelTitle, Funnel, true);
    }

    public void ControlChanged(bool allowed) => log(allowed ? ControlOnLog : ControlOffLog);

    public void NoLivePageAddress() => balloon(NoAddressTitle, TrayText.NoTailscaleName, false);
}
