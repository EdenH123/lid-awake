using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LidAwake;

// Lid closed and idle: the PC keeps running while Lid Awake is on. The lid action is a power-plan
// setting (powercfg), the idle part a request held by this app; both are put back when turned off.
public static class KeepAwake
{
    const string Sub = "4f971e89-eebd-4455-a8de-9e59040e7347";      // SUB_BUTTONS
    const string LidAction = "5ca83367-6e45-459f-a27b-476b1d01c936"; // LIDACTION
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_AWAYMODE_REQUIRED = 0x40;

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    static Thread? holder;
    static ManualResetEventSlim? release;

    // How powercfg is run. Tests swap this for a fake so the logic can run without a lid.
    public static Func<string, string> Powercfg { get; set; } = RunPowercfg;

    public static void On(Settings settings)
    {
        SetLidToDoNothing(settings);
        // The idle-sleep hold does not depend on the lid setting: a PC whose lid setting cannot
        // be read still must not idle-sleep.
        if (holder != null) return;
        // The request belongs to a thread, so one quiet thread holds it until Off().
        var done = new ManualResetEventSlim(false);
        release = done;
        holder = new Thread(() => {
            SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED | ES_AWAYMODE_REQUIRED);
            done.Wait();
            SetThreadExecutionState(ES_CONTINUOUS);
        }) { IsBackground = true, Name = "keep-awake" };
        holder.Start();
    }

    // Remember the current lid action once, then set it to "do nothing". If it cannot be read,
    // change nothing and save nothing, so Off() never "restores" a guessed value.
    static void SetLidToDoNothing(Settings settings)
    {
        if (settings.Get<int?>("savedLidAc") is null)
        {
            var current = ReadLidAction();
            if (current is null) return;
            settings.Set("savedLidAc", current.Value.ac);
            settings.Set("savedLidDc", current.Value.dc);
        }
        SetLidAction(0, 0);  // 0 = do nothing
    }

    public static void Off(Settings settings)
    {
        // Only put back what On() saved: never touch a lid setting Lid Awake didn't change.
        if (settings.Get<int?>("savedLidAc") is int ac && settings.Get<int?>("savedLidDc") is int dc)
        {
            SetLidAction(ac, dc);
            settings.Set<int?>("savedLidAc", null);
            settings.Set<int?>("savedLidDc", null);
        }
        release?.Set();
        release = null;
        holder = null;
    }

    // The current plan's lid-close action for plugged in and on battery, or null when powercfg
    // does not print both values.
    public static (int ac, int dc)? ReadLidAction() =>
        ParseLidAction(Powercfg($"/query SCHEME_CURRENT {Sub} {LidAction}"));

    // The last two hex values in the output are the current AC and DC settings. Matching hex
    // values rather than label text keeps this independent of the Windows display language.
    public static (int ac, int dc)? ParseLidAction(string output)
    {
        var values = Regex.Matches(output, @"0x([0-9a-fA-F]{8})").Select(m => Convert.ToInt32(m.Groups[1].Value, 16)).ToList();
        if (values.Count < 2) return null;
        return (values[^2], values[^1]);
    }

    static void SetLidAction(int ac, int dc)
    {
        Powercfg($"/setacvalueindex SCHEME_CURRENT {Sub} {LidAction} {ac}");
        Powercfg($"/setdcvalueindex SCHEME_CURRENT {Sub} {LidAction} {dc}");
        Powercfg("/setactive SCHEME_CURRENT");
    }

    public static string RunPowercfg(string args)
    {
        using var p = Process.Start(new ProcessStartInfo("powercfg.exe", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        // Read both streams so neither can fill up and block powercfg.
        var error = p.StandardError.ReadToEndAsync();
        var output = p.StandardOutput.ReadToEnd();
        error.Wait();
        p.WaitForExit();
        return output;
    }
}
