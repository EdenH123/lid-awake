using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using NAudio.CoreAudioApi;

namespace LidAwake;

// One line of `LidAwake.exe --selftest`. NotHere: this PC doesn't have the thing (no battery, no
// lid, no Tailscale), which is never a failure. Required: a failure makes the exit code 1.
public enum CheckOutcome { Pass, Fail, NotHere }

public sealed record CheckResult(string Name, CheckOutcome State, string Detail, bool Required)
{
    public const string PassMark = "✓", FailMark = "✗", NotHereMark = "–";

    public string Line => State switch
    {
        CheckOutcome.Pass => PassMark,
        CheckOutcome.Fail => FailMark,
        _ => NotHereMark,
    } + " " + Name + ": " + Detail + (State == CheckOutcome.Fail && !Required ? " (optional)" : "");
}

// Who holds one of Lid Awake's ports on 127.0.0.1: nobody (Free), a program (its process name and
// id), or the port can't be used and nobody is listening (Problem: Windows' reason, for example a
// port range reserved by Hyper-V).
public readonly record struct PortUse(bool Free, string? Owner = null, int OwnerPid = 0, string? Problem = null);

// What the self-test reads, each through its own function so the tests can pass fakes. The real
// ones only read: nothing here changes a setting, turns anything on or opens the camera (the
// cameras and microphones are only listed). The screen is captured once, and an encoder is made
// and closed again.
public sealed class SelfTestProbes
{
    static string Local => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LidAwake");
    static string Roaming => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "LidAwake");
    static readonly Lazy<SystemReadings> readings = new(() => new SystemReadings());

    public Func<Tailnet.CliResult> TailscaleStatus { get; init; } = () => Tailnet.RunExe("status --json");
    public Func<int, PortUse> Port { get; init; } = PortCheck.Probe;
    public string SettingsFile { get; init; } = Path.Combine(Roaming, "settings.json");
    public string MachineFile { get; init; } = Path.Combine(Local, Settings.MachineFileName);
    public Func<(int ac, int dc)?> LidAction { get; init; } = () => KeepAwake.ReadLidAction();
    public Func<bool?> HasLid { get; init; } = () => KeepAwake.HasLid();
    public Func<PowerReading?> Power { get; init; } = () => readings.Value.Power();
    public Func<BatteryCharge?> Charge { get; init; } = BatteryCharge.Read;
    public Func<TemperatureReading> Temperatures { get; init; } = TemperatureSources.Read;
    public Func<ThermalZoneReading?> Zones { get; init; } = ThermalZoneReading.Read;
    public Func<MemoryReading?> Memory { get; init; } = () => readings.Value.Memory();
    // The kind of capture that works ("Desktop Duplication", "GDI") and its picture's size, or null and why not.
    public Func<(string? Kind, int Width, int Height, string? Problem)> Screen { get; init; } = ScreenProbe;
    // `allowHardware`: the encoder made, whether it is the graphics card's, or null and why not.
    public Func<bool, (string? Name, bool Hardware, string? Problem)> Encoder { get; init; } = EncoderProbe;
    // Null: Windows can't list them (no Media Foundation, no audio service).
    public Func<IReadOnlyList<string>?> Cameras { get; init; } = CameraNames;
    public Func<IReadOnlyList<string>?> Microphones { get; init; } = MicrophoneNames;
    // ClaudeHook.Setup's "status" exit code (0 set up, 1 not set up, 2 no Claude Code, 3 can't be read).
    public Func<int> ClaudeCode { get; init; } = () =>
        ClaudeHook.Setup("status", new ClaudeSettingsFile(), Environment.ProcessPath ?? "", _ => { });

    static (string?, int, int, string?) ScreenProbe()
    {
        Gdi.UseRealPixels();
        var problems = new List<string>();
        var grabbers = new ScreenGrabbers { Note = problems.Add };
        foreach (var level in new[] { 0, 1 })
        {
            try
            {
                using var grabber = grabbers.Open(level);
                if (grabber == null) continue;
                if (grabber.Grab(1280, 500, force: true) is { } image) return (grabber.Kind, image.Width, image.Height, null);
                problems.Add(grabber.Kind + " gave no picture");
            }
            catch (Exception e) { problems.Add(e.Message); }
        }
        return (null, 0, 0, problems.Count == 0 ? "no way of capturing the screen" : string.Join("; ", problems.Distinct()));
    }

    static (string?, bool, string?) EncoderProbe(bool allowHardware)
    {
        try
        {
            using var encoder = MfH264Encoder.Create(640, 360, 30, 1_000_000, allowHardware);
            return (encoder.Name, encoder.Hardware, null);
        }
        catch (Exception e) { return (null, false, e.Message); }
    }

    static IReadOnlyList<string>? CameraNames()
    {
        try
        {
            if (!Mf.Start()) return null;
            return CameraHardware.Cameras().Select(c => c.Name).ToList();
        }
        catch (Exception) { return null; }
    }

    static IReadOnlyList<string>? MicrophoneNames()
    {
        try
        {
            using var devices = new MMDeviceEnumerator();
            var names = new List<string>();
            foreach (var device in devices.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                using (device) names.Add(device.FriendlyName);
            }
            return names;
        }
        catch (Exception) { return null; }
    }
}

// `LidAwake.exe --selftest`, the Windows side of the Mac's self-check (main.swift): the things Lid
// Awake needs, each as a line that says in plain words whether it works. Required (a failure makes
// the exit code 1): the settings files, the two ports, Tailscale, the lid setting, the memory
// reading and the screen capture, which is what the Mac's self-check is about. The rest only tells
// (battery and temperature, the H.264 encoder, the camera and microphone, phone alerts, Claude
// Code): without them Lid Awake still works, with less. Anything this PC doesn't have (no battery,
// no lid, no Tailscale) is "not on this PC", never a failure.
public static class SelfTest
{
    public const string Title = "Lid Awake self-test";

    public static List<CheckResult> Run(SelfTestProbes p)
    {
        var results = new List<CheckResult>();
        var settings = FileCheck("Settings", p.SettingsFile, results);
        var machine = FileCheck("This PC's settings (machine.json)", p.MachineFile, results);
        Tailscale(Safe(p.TailscaleStatus, new Tailnet.CliResult(false, "")), results);
        results.Add(PortLine("Phone page port", WebServer.DefaultPort, p.Port));
        results.Add(PortLine("Live screen port", LiveServer.DefaultPort, p.Port));
        results.Add(Lid(Safe(p.LidAction, null), Safe(p.HasLid, null), machine));
        results.Add(Battery(Safe(p.Power, null), Safe(p.Charge, null)));
        results.Add(Temperature(Safe(p.Temperatures, TemperatureReading.None), Safe(p.Zones, null)));
        results.Add(Memory(Safe(p.Memory, null)));
        results.Add(Screen(Safe(p.Screen, (null, 0, 0, "the check failed"))));
        bool hardware = Text(settings?["screenEncoder"]) != "software";
        results.Add(Encoder(Safe(() => p.Encoder(hardware), (null, false, "the check failed")), hardware));
        results.Add(Devices("Camera", Safe(p.Cameras, null), "no camera", "Windows can't list the cameras (Media Foundation missing?)"));
        results.Add(Devices("Microphone", Safe(p.Microphones, null), "no microphone", "Windows can't list the microphones"));
        results.Add(Push(machine));
        results.Add(Claude(Safe(p.ClaudeCode, ClaudeHook.Refused)));
        return results;
    }

    // 0 when no required check failed, else 1.
    public static int ExitCode(IEnumerable<CheckResult> results) =>
        results.Any(r => r.Required && r.State == CheckOutcome.Fail) ? 1 : 0;

    public static string Report(IReadOnlyList<CheckResult> results, DateTimeOffset now)
    {
        var sb = new StringBuilder();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";
        sb.Append(Title).Append(" · ").Append(now.ToString("yyyy-MM-dd HH:mm:ss zzz", CultureInfo.InvariantCulture)).Append('\n');
        sb.Append("Lid Awake ").Append(version).Append(" · ").Append(RuntimeInformation.OSDescription.Trim()).Append('\n');
        sb.Append('\n');
        foreach (var r in results) sb.Append(r.Line).Append('\n');
        sb.Append('\n');
        var failed = results.Where(r => r.Required && r.State == CheckOutcome.Fail).Select(r => r.Name).ToList();
        int optional = results.Count(r => !r.Required && r.State == CheckOutcome.Fail);
        sb.Append(failed.Count == 0
            ? "All required checks passed."
            : $"{failed.Count} required {(failed.Count == 1 ? "check" : "checks")} failed: {string.Join(", ", failed)}.");
        if (optional > 0) sb.Append($" {optional} optional {(optional == 1 ? "check" : "checks")} didn't pass.");
        sb.Append('\n');
        sb.Append(CheckResult.PassMark).Append(" works · ").Append(CheckResult.FailMark).Append(" doesn't work · ")
          .Append(CheckResult.NotHereMark).Append(" not on this PC\n");
        return sb.ToString();
    }

    static T Safe<T>(Func<T> read, T otherwise)
    {
        try { return read(); }
        catch (Exception) { return otherwise; }
    }

    static string? Text(JsonNode? node) => node is JsonValue v && v.TryGetValue<string>(out var s) ? s : null;

    // Read only (never Settings, which would make the folder and move a damaged file aside).
    // A file that isn't there yet is fine: Lid Awake makes it when it first needs it.
    static JsonObject? FileCheck(string name, string path, List<CheckResult> results)
    {
        if (!File.Exists(path))
        {
            results.Add(new(name, CheckOutcome.Pass, "not made yet (Lid Awake makes it when it needs it)", true));
            return null;
        }
        try
        {
            if (JsonNode.Parse(File.ReadAllText(path)) is not JsonObject o)
            {
                results.Add(new(name, CheckOutcome.Fail, "damaged: not a JSON object; Lid Awake sets it aside and starts afresh", true));
                return null;
            }
            results.Add(new(name, CheckOutcome.Pass, $"readable, {o.Count} {(o.Count == 1 ? "value" : "values")}", true));
            return o;
        }
        catch (JsonException)
        {
            results.Add(new(name, CheckOutcome.Fail, "damaged: not valid JSON; Lid Awake sets it aside and starts afresh", true));
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            results.Add(new(name, CheckOutcome.Fail, "can't be read: " + e.Message, true));
        }
        return null;
    }

    static void Tailscale(Tailnet.CliResult status, List<CheckResult> results)
    {
        const string Installed = "Tailscale installed", SignedIn = "Tailscale signed in", Https = "Tailscale HTTPS name";
        var (state, name, backend) = Tailnet.ReadSetup(status);
        if (!state.Installed)
        {
            results.Add(new(Installed, CheckOutcome.NotHere, "not on this PC (install it from tailscale.com: the phone reaches the PC through it)", true));
            results.Add(new(SignedIn, CheckOutcome.NotHere, "not on this PC (needs Tailscale)", true));
            results.Add(new(Https, CheckOutcome.NotHere, "not on this PC (needs Tailscale)", true));
            return;
        }
        results.Add(new(Installed, CheckOutcome.Pass, "yes", true));
        if (!status.Ok)
        {
            var why = status.TimedOut ? "it didn't answer in time" : "it didn't answer; is it running?";
            results.Add(new(SignedIn, CheckOutcome.Fail, why, true));
            results.Add(new(Https, CheckOutcome.Fail, "unknown (" + why + ")", true));
            return;
        }
        var said = StayAwakeLog.Clean(backend) ?? "nothing";
        results.Add(state.SignedIn
            ? new CheckResult(SignedIn, CheckOutcome.Pass, "yes", true)
            : new CheckResult(SignedIn, CheckOutcome.Fail, $"no (Tailscale says {said}): open Tailscale and sign in", true));
        if (state.HttpsOn && name != null) results.Add(new(Https, CheckOutcome.Pass, name, true));
        else if (!state.SignedIn) results.Add(new(Https, CheckOutcome.Fail, "unknown until Tailscale is signed in", true));
        else results.Add(new(Https, CheckOutcome.Fail,
            "HTTPS certificates are off for your tailnet: turn on MagicDNS and HTTPS Certificates in Tailscale's admin console (DNS)", true));
    }

    static CheckResult PortLine(string what, int port, Func<int, PortUse> probe)
    {
        var name = $"{what} {port.ToString(CultureInfo.InvariantCulture)}";
        PortUse use;
        try { use = probe(port); }
        catch (Exception e) { use = new PortUse(false, Problem: e.Message); }
        if (use.Free) return new(name, CheckOutcome.Pass, "free for Lid Awake (it isn't running)", true);
        if (use.Owner is { } owner)
        {
            bool ours = string.Equals(owner, "LidAwake", StringComparison.OrdinalIgnoreCase);
            return ours
                ? new CheckResult(name, CheckOutcome.Pass, "in use by Lid Awake (it's running)", true)
                : new CheckResult(name, CheckOutcome.Fail, $"in use by another program ({owner}); Lid Awake needs it", true);
        }
        return new(name, CheckOutcome.Fail, "can't be used: " + (use.Problem ?? "Windows refused it"), true);
    }

    public static string LidActionName(int value) => value switch
    {
        0 => "do nothing",
        1 => "sleep",
        2 => "hibernate",
        3 => "shut down",
        _ => "value " + value.ToString(CultureInfo.InvariantCulture),
    };

    static CheckResult Lid((int ac, int dc)? action, bool? hasLid, JsonObject? machine)
    {
        const string Name = "Lid setting (powercfg)";
        if (action is { } a)
        {
            var detail = $"closing the lid: {LidActionName(a.ac)} when plugged in, {LidActionName(a.dc)} on battery";
            if (machine?["savedLidAc"] is JsonValue) detail += "; Lid Awake has changed it (it's on, or it stopped without putting it back: starting it puts it back)";
            if (hasLid == false) detail += "; this PC has no lid";
            return new(Name, CheckOutcome.Pass, detail, true);
        }
        if (hasLid == false) return new(Name, CheckOutcome.NotHere, "not on this PC (no lid)", true);
        return new(Name, CheckOutcome.Fail, "powercfg couldn't read what closing the lid does", true);
    }

    static CheckResult Battery(PowerReading? power, BatteryCharge? charge)
    {
        const string Name = "Battery";
        if (power is not { } p) return new(Name, CheckOutcome.Fail, "Windows didn't say", false);
        if ((p.BatteryFlag & 128) != 0) return new(Name, CheckOutcome.NotHere, "not on this PC (no battery)", false);
        if (!StatusInfo.HasBattery(p)) return new(Name, CheckOutcome.NotHere, "not on this PC (Windows can't tell the battery's state)", false);
        int toFull = TrayText.MinutesToFull(charge);
        return new(Name, CheckOutcome.Pass, TrayText.InfoLine(p, null, toFull), false);
    }

    static string Celsius(double c) => TemperatureMath.OneDecimal(c).ToString("0.0", CultureInfo.InvariantCulture) + " °C";

    static CheckResult Temperature(TemperatureReading t, ThermalZoneReading? zones)
    {
        const string Name = "Temperature";
        var parts = new List<string>();
        if (t.Battery is { } b) parts.Add("battery " + Celsius(b));
        if (t.Zone is { } z) parts.Add("PC " + Celsius(z));
        if (zones?.Hottest is { } h) parts.Add("thermal zones " + Celsius(h));
        if (parts.Count == 0)
            return new(Name, CheckOutcome.NotHere, "not on this PC (no sensor Windows lets Lid Awake read; the heat limit is greyed out)", false);
        parts.Add("state " + ThermalState.Name(t.Zone, zones));
        return new(Name, CheckOutcome.Pass, string.Join(", ", parts), false);
    }

    static CheckResult Memory(MemoryReading? m)
    {
        const string Name = "Memory";
        if (m is not { } r || r.Total == 0) return new(Name, CheckOutcome.Fail, "Windows didn't say how much memory there is", true);
        double gb = 1 << 30;
        var used = (r.Total - Math.Min(r.Available, r.Total)) / gb;
        return new(Name, CheckOutcome.Pass,
            $"{used.ToString("0.0", CultureInfo.InvariantCulture)} of {(r.Total / gb).ToString("0.0", CultureInfo.InvariantCulture)} GB used", true);
    }

    static CheckResult Screen((string? Kind, int Width, int Height, string? Problem) s)
    {
        const string Name = "Screen capture";
        if (s.Kind == null) return new(Name, CheckOutcome.Fail, "none works: " + (s.Problem ?? "unknown"), true);
        return new(Name, CheckOutcome.Pass, $"{s.Kind}, {s.Width}x{s.Height}", true);
    }

    static CheckResult Encoder((string? Name, bool Hardware, string? Problem) e, bool hardwareAllowed)
    {
        const string Name = "H.264 encoder";
        if (e.Name == null)
            return new(Name, CheckOutcome.Fail, "none works, so the phone gets still pictures instead of video: " + (e.Problem ?? "unknown"), false);
        var kind = e.Hardware ? "the graphics card's" : hardwareAllowed ? "Windows' own (software)" : "Windows' own (software, as settings.json asks)";
        return new(Name, CheckOutcome.Pass, $"{e.Name}, {kind}", false);
    }

    static CheckResult Devices(string name, IReadOnlyList<string>? found, string none, string unknown)
    {
        if (found == null) return new(name, CheckOutcome.Fail, unknown, false);
        if (found.Count == 0) return new(name, CheckOutcome.NotHere, $"not on this PC ({none})", false);
        return new(name, CheckOutcome.Pass, string.Join(", ", found.Select(n => AccessLog.Clean(n)).Take(4))
            + (found.Count > 4 ? $" and {found.Count - 4} more" : ""), false);
    }

    static CheckResult Push(JsonObject? machine)
    {
        const string Name = "Phone alerts (Web Push)";
        var stored = Text(machine?[WebPush.KeyName]);
        if (string.IsNullOrEmpty(stored)) return new(Name, CheckOutcome.Pass, "no key yet (made when a phone first turns alerts on)", false);
        if (!WebPush.KeyWorks(stored))
            return new(Name, CheckOutcome.Fail, "the stored key is damaged: Lid Awake makes a new one, and each phone must turn alerts on again", false);
        int phones = machine?[WebPush.ListName] is JsonArray list ? list.Count : 0;
        return new(Name, CheckOutcome.Pass, $"key present, {phones} {(phones == 1 ? "phone gets" : "phones get")} alerts", false);
    }

    static CheckResult Claude(int code)
    {
        const string Name = "Claude Code";
        return code switch
        {
            ClaudeHook.SetUp => new(Name, CheckOutcome.Pass, "found, alerts and usage set up", false),
            ClaudeHook.NotSetUp => new(Name, CheckOutcome.Pass, "found, alerts and usage not set up (tray: Claude alerts and usage from Claude Code…)", false),
            ClaudeHook.NoClaudeCode => new(Name, CheckOutcome.NotHere, "not on this PC", false),
            _ => new(Name, CheckOutcome.Fail, "its settings file (.claude\\settings.json) can't be read", false),
        };
    }
}

// Who listens on 127.0.0.1 (or every address) on a TCP port: GetExtendedTcpTable's listeners with
// their process ids. Nobody: the port is bound for a moment to see that Windows allows it.
public static class PortCheck
{
    [DllImport("iphlpapi.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint GetExtendedTcpTable(IntPtr table, ref int size, [MarshalAs(UnmanagedType.Bool)] bool order,
        int addressFamily, int tableClass, uint reserved);

    const int AfInet = 2, TcpTableOwnerPidListener = 3;
    const uint ErrorInsufficientBuffer = 122;
    const uint Loopback = 0x0100007F;  // 127.0.0.1 as the table stores it (network byte order)

    public static PortUse Probe(int port)
    {
        if (ListenerPid(port) is { } pid)
        {
            string owner;
            try
            {
                using var p = Process.GetProcessById(pid);
                owner = p.ProcessName;
            }
            catch (Exception) { owner = "process " + pid.ToString(CultureInfo.InvariantCulture); }
            return new PortUse(false, owner, pid);
        }
        try
        {
            var listener = new TcpListener(IPAddress.Loopback, port) { ExclusiveAddressUse = true };
            listener.Start();
            listener.Stop();
            return new PortUse(true);
        }
        catch (SocketException e) { return new PortUse(false, Problem: e.Message); }
    }

    // MIB_TCPTABLE_OWNER_PID: a DWORD count, then rows of six DWORDs: state, local address, local
    // port (network byte order in the low 16 bits), remote address, remote port, owning process id.
    static int? ListenerPid(int port)
    {
        int size = 0;
        GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
        for (int attempt = 0; attempt < 3 && size > 0; attempt++)
        {
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                uint result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidListener, 0);
                if (result == ErrorInsufficientBuffer) continue;
                if (result != 0) return null;
                int rows = Marshal.ReadInt32(buffer);
                for (int i = 0; i < rows; i++)
                {
                    var row = buffer + 4 + i * 24;
                    uint local = (uint)Marshal.ReadInt32(row, 4);
                    int raw = Marshal.ReadInt32(row, 8);
                    int rowPort = ((raw & 0xFF) << 8) | ((raw >> 8) & 0xFF);
                    if (rowPort == port && local is Loopback or 0) return Marshal.ReadInt32(row, 20);
                }
                return null;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        return null;
    }
}

// `LidAwake.exe --battery`, the Mac's battery check: everything Windows says about the batteries,
// raw, and what Lid Awake makes of it. Only reads.
public static class BatteryCheck
{
    public const string Title = "Lid Awake battery check";

    public sealed record Readings(PowerReading? Power, BatteryCharge? Charge, double? BatterySensor, double? AcpiZone,
                                  ThermalZoneReading? Zones);

    // Slow (it opens each battery and asks WMI, a few seconds at most).
    public static Readings Read()
    {
        static T? Safe<T>(Func<T?> read) where T : class { try { return read(); } catch (Exception) { return null; } }
        PowerReading? power = null;
        try { power = new SystemReadings().Power(); }
        catch (Exception) { }
        var heat = TemperatureSources.Read();
        return new Readings(power, Safe(BatteryCharge.Read), heat.Battery, heat.Zone, Safe(ThermalZoneReading.Read));
    }

    // Minutes until empty at the rate the discharging batteries lose charge now, -1 when unknown
    // (none discharging, a capacity or rate unknown, or the batteries count in units of their own).
    public static int MinutesToEmpty(BatteryCharge charge)
    {
        long held = 0, rate = 0;
        bool discharging = false;
        foreach (var c in charge.Cells)
        {
            if (c.Relative || c.Capacity == BatteryCharge.UnknownCapacity) return -1;
            held += c.Capacity;
            if ((c.PowerState & BatteryCharge.Discharging) == 0) continue;
            discharging = true;
            if (c.Rate == BatteryCharge.UnknownRate || c.Rate >= 0) return -1;
            rate += -(long)c.Rate;
        }
        if (!discharging || rate <= 0) return -1;
        double minutes = Math.Round(held * 60.0 / rate);
        return minutes > BatteryCharge.MaxMinutes ? -1 : (int)minutes;
    }

    static string Flags(byte flag)
    {
        if (flag == 255) return "unknown";
        var words = new List<string>();
        if ((flag & 1) != 0) words.Add("high");
        if ((flag & 2) != 0) words.Add("low");
        if ((flag & 4) != 0) words.Add("critical");
        if ((flag & 8) != 0) words.Add("charging");
        if ((flag & 128) != 0) words.Add("no battery");
        return words.Count == 0 ? "none set" : string.Join(", ", words);
    }

    static string State(uint s)
    {
        var words = new List<string>();
        if ((s & BatteryCharge.OnLine) != 0) words.Add("on the power line");
        if ((s & BatteryCharge.Discharging) != 0) words.Add("discharging");
        if ((s & BatteryCharge.Charging) != 0) words.Add("charging");
        return words.Count == 0 ? "idle" : string.Join(", ", words);
    }

    static string Minutes(int m) => m < 0 ? "unknown" : $"{m / 60}h {m % 60:00}m ({m.ToString(CultureInfo.InvariantCulture)} min)";

    static string Celsius(double? c, string none) =>
        c is { } v ? TemperatureMath.OneDecimal(v).ToString("0.0", CultureInfo.InvariantCulture) + " °C" : none;

    public static string Report(Readings r, DateTimeOffset now)
    {
        var inv = CultureInfo.InvariantCulture;
        var sb = new StringBuilder();
        void Line(string text) => sb.Append(text).Append('\n');
        Line(Title + " · " + now.ToString("yyyy-MM-dd HH:mm:ss zzz", inv));
        Line("");
        if (r.Power is { } p)
        {
            var ac = p.AcLineStatus switch { 0 => "no (on battery)", 1 => "yes", _ => "unknown" };
            Line($"on AC: {ac} (ACLineStatus {p.AcLineStatus.ToString(inv)})");
            Line($"charge: {(p.BatteryLifePercent <= 100 ? p.BatteryLifePercent.ToString(inv) + "%" : "unknown")} (BatteryLifePercent {p.BatteryLifePercent.ToString(inv)})");
            Line($"battery flag: {Flags(p.BatteryFlag)} (BatteryFlag 0x{p.BatteryFlag.ToString("X2", inv)})");
            Line($"Windows' time left: {(p.LifeSeconds >= 0 ? Minutes((int)(p.LifeSeconds / 60)) : "unknown")}");
        }
        else Line("GetSystemPowerStatus: no answer");
        if (r.Charge is { } charge)
        {
            Line($"batteries: {charge.Cells.Count.ToString(inv)}");
            for (int i = 0; i < charge.Cells.Count; i++)
            {
                var c = charge.Cells[i];
                var unit = c.Relative ? "units of its own" : "mWh";
                var held = c.Capacity == BatteryCharge.UnknownCapacity ? "unknown" : c.Capacity.ToString(inv);
                var full = c.FullCapacity is 0 or BatteryCharge.UnknownCapacity ? "unknown" : c.FullCapacity.ToString(inv);
                var share = c.Capacity != BatteryCharge.UnknownCapacity && c.FullCapacity is not (0 or BatteryCharge.UnknownCapacity)
                    ? $" ({(c.Capacity * 100.0 / c.FullCapacity).ToString("0", inv)}%)" : "";
                var rate = c.Rate == BatteryCharge.UnknownRate ? "unknown" : (c.Rate > 0 ? "+" : "") + c.Rate.ToString(inv) + (c.Relative ? "" : " mW");
                Line($"battery {(i + 1).ToString(inv)}: {State(c.PowerState)}, capacity {held} of {full} {unit}{share}, rate {rate}");
            }
            Line($"minutes to full: {Minutes(charge.MinutesToFull())}");
            Line($"minutes to empty: {Minutes(MinutesToEmpty(charge))}");
            var charged = charge.Charged() switch { true => "yes", false => "no", null => "can't tell" };
            Line("charged: " + charged);
        }
        else Line("batteries: the list couldn't be read");
        Line($"temperature, battery sensor (IOCTL_BATTERY_QUERY_INFORMATION): {Celsius(r.BatterySensor, "none (this battery doesn't report one)")}");
        Line($"temperature, thermal zones (MSAcpi_ThermalZoneTemperature): {Celsius(r.AcpiZone, "none (most PCs need administrator rights for it)")}");
        var limit = r.Zones?.PassiveLimit is { } l ? l.ToString(inv) + "%" : "none";
        Line($"temperature, zone counters (ThermalZoneInformation): {Celsius(r.Zones?.Hottest, "none")}, passive limit {limit}");
        Line($"thermalState: {ThermalState.Name(r.AcpiZone, r.Zones)}");
        var stop = Safety.Decide(true, true, r.Power, new TemperatureReading(r.BatterySensor, r.AcpiZone));
        Line($"safety stop with both limits on: {stop?.Short ?? "none"}");
        return sb.ToString();
    }
}
