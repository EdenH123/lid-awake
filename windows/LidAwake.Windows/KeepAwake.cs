using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LidAwake;

// Lid closed and idle: the PC keeps running while Lid Awake is on. The lid action is a power-plan
// setting (powercfg), the idle part a request held by this app; both are put back when turned off.
// On and Off never throw: a powercfg failure leaves the saved values in place, so a later Off
// (or the next start) tries again, and the idle-sleep hold is always released by Off.
public static class KeepAwake
{
    const string Sub = "4f971e89-eebd-4455-a8de-9e59040e7347";      // SUB_BUTTONS
    const string LidAction = "5ca83367-6e45-459f-a27b-476b1d01c936"; // LIDACTION
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1, ES_AWAYMODE_REQUIRED = 0x40;

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    // On and Off can be called from the tray (UI thread) and from the web server's threads.
    static readonly object gate = new();
    static Thread? holder;
    static ManualResetEventSlim? release;

    // How powercfg is run: returns what it printed, throws when it fails. Tests swap this for a
    // fake so the logic can run without a lid.
    public static Func<string, string> Powercfg { get; set; } = RunPowercfg;

    internal static bool IsHolding { get { lock (gate) return holder != null; } }

    public static void On(Settings settings)
    {
        lock (gate)
        {
            try
            {
                SetLidToDoNothing(settings);
            }
            catch (Exception)
            {
                // The lid part failed. Whatever was saved stays saved, so Off() can put it back.
            }
            // The idle-sleep hold does not depend on the lid setting: a PC whose lid setting
            // cannot be read still must not idle-sleep.
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
    }

    // Remember the current lid action once, then set it to "do nothing". If it cannot be read,
    // change nothing and save nothing, so Off() never "restores" a guessed value.
    static void SetLidToDoNothing(Settings settings)
    {
        // Both values must be saved to count: a crash between the two writes leaves only one,
        // and then the lid action has not been changed yet, so read it fresh.
        if (settings.Get<int?>("savedLidAc") is null || settings.Get<int?>("savedLidDc") is null)
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
        lock (gate)
        {
            try
            {
                // Only put back what On() saved: never touch a lid setting Lid Awake didn't change.
                if (settings.Get<int?>("savedLidAc") is int ac && settings.Get<int?>("savedLidDc") is int dc)
                {
                    SetLidAction(ac, dc);
                    // Forget the saved values only once the restore is confirmed.
                    var now = ReadLidAction();
                    if (now is { } n && n.ac == ac && n.dc == dc)
                    {
                        settings.Set<int?>("savedLidAc", null);
                        settings.Set<int?>("savedLidDc", null);
                    }
                }
            }
            catch (Exception)
            {
                // The restore failed. The saved values stay, so the next Off or start tries again.
            }
            finally
            {
                release?.Set();
                release = null;
                holder = null;
            }
        }
    }

    // The current plan's lid-close action for plugged in and on battery, or null when powercfg
    // fails or does not print both values.
    public static (int ac, int dc)? ReadLidAction()
    {
        try
        {
            return ParseLidAction(Powercfg($"/query SCHEME_CURRENT {Sub} {LidAction}"));
        }
        catch (Exception)
        {
            return null;
        }
    }

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

    // Runs powercfg and returns what it printed. Throws when it cannot start, does not finish
    // within 10 seconds (it is killed then) or exits with a non-zero code.
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
        var outputTask = p.StandardOutput.ReadToEndAsync();
        var errorTask = p.StandardError.ReadToEndAsync();
        if (!p.WaitForExit(10_000))
        {
            try { p.Kill(true); }
            catch (InvalidOperationException) { }
            throw new InvalidOperationException($"powercfg {args} did not finish in 10 seconds");
        }
        p.WaitForExit();  // lets the redirected streams finish
        var output = outputTask.Result;
        var error = errorTask.Result;
        if (p.ExitCode != 0)
            throw new InvalidOperationException($"powercfg {args} failed with exit code {p.ExitCode}: {error.Trim()} {output.Trim()}".Trim());
        return output;
    }
}
