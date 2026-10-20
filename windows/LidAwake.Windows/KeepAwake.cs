using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace LidAwake;

// What KeepAwake.On did to the lid action. NoLidSetting: this PC has no lid action to change (a
// desktop PC); nothing to tell the owner. Failed: a laptop whose lid action could not be set to
// "do nothing", so closing the lid may still make it sleep.
public enum LidResult { Changed, NoLidSetting, Failed }

// Lid closed and idle: the PC keeps running while Lid Awake is on. The lid action is a power-plan
// setting (powercfg), the idle part a request held by this app; both are put back when turned off.
// On and Off never throw: a powercfg failure leaves the saved values in place, so a later Off
// (or the next start) tries again, and the idle-sleep hold is always released by Off. On says
// what happened to the lid action, so the tray can warn when it could not be changed.
//
// The saved values (savedLidAc, savedLidDc) and the power plan they came from (savedLidScheme,
// its GUID) are facts about this PC, so they live in machine.json (Settings.ForMachine), not in
// the roaming settings. Off puts them back into that plan, even if another plan is active by then.
public static class KeepAwake
{
    const string Sub = "4f971e89-eebd-4455-a8de-9e59040e7347";      // SUB_BUTTONS
    const string LidAction = "5ca83367-6e45-459f-a27b-476b1d01c936"; // LIDACTION
    const string CurrentScheme = "SCHEME_CURRENT";
    const string SchemeKey = "savedLidScheme";
    // A power plan's GUID. Matching the GUID rather than the label keeps this independent of the
    // Windows display language.
    const string GuidPattern = @"[0-9a-fA-F]{8}-(?:[0-9a-fA-F]{4}-){3}[0-9a-fA-F]{12}";
    // No ES_AWAYMODE_REQUIRED: with it, Start -> Sleep only turns the screen and sound off and
    // the PC keeps running, which looks like sleep but is not.
    const uint ES_CONTINUOUS = 0x80000000, ES_SYSTEM_REQUIRED = 0x1;

    [DllImport("kernel32.dll")] static extern uint SetThreadExecutionState(uint flags);

    // SYSTEM_POWER_CAPABILITIES; its third byte is LidPresent. The buffer is bigger than the
    // structure (76 bytes) on purpose.
    [DllImport("powrprof.dll")]
    [return: MarshalAs(UnmanagedType.U1)]
    static extern bool GetPwrCapabilities([Out] byte[] capabilities);

    // On and Off can be called from the tray (UI thread) and from the web server's threads.
    static readonly object gate = new();
    static Thread? holder;
    static ManualResetEventSlim? release;

    // How powercfg is run: returns what it printed, throws when it fails. Tests swap this for a
    // fake so the logic can run without a lid.
    public static Func<string, string> Powercfg { get; set; } = RunPowercfg;

    // Whether this PC has a lid (null: Windows can't tell). Only asked when the lid action could
    // not be changed, to tell a desktop PC from a laptop. Tests swap this too.
    public static Func<bool?> HasLid { get; set; } = ReadLidPresent;

    internal static bool IsHolding { get { lock (gate) return holder != null; } }

    public static LidResult On(Settings settings)
    {
        lock (gate)
        {
            LidResult lid;
            try
            {
                lid = SetLidToDoNothing(settings);
            }
            catch (Exception)
            {
                // The lid part failed. Whatever was saved stays saved, so Off() can put it back.
                lid = LidResult.Failed;
            }
            // A PC without a lid has nothing to warn about.
            if (lid == LidResult.Failed && SafeHasLid() == false) lid = LidResult.NoLidSetting;
            // The idle-sleep hold does not depend on the lid setting: a PC whose lid setting
            // cannot be read still must not idle-sleep.
            if (holder != null) return lid;
            // The request belongs to a thread, so one quiet thread holds it until Off().
            var done = new ManualResetEventSlim(false);
            release = done;
            holder = new Thread(() => {
                SetThreadExecutionState(ES_CONTINUOUS | ES_SYSTEM_REQUIRED);
                done.Wait();
                SetThreadExecutionState(ES_CONTINUOUS);
            }) { IsBackground = true, Name = "keep-awake" };
            holder.Start();
            return lid;
        }
    }

    static bool? SafeHasLid()
    {
        try { return HasLid(); }
        catch (Exception) { return null; }
    }

    static bool? ReadLidPresent()
    {
        try
        {
            var capabilities = new byte[256];
            return GetPwrCapabilities(capabilities) ? (bool?)(capabilities[2] != 0) : null;
        }
        catch (Exception) { return null; }
    }

    // Older versions kept the saved lid values in the roaming settings. Move them to this PC's
    // machine.json: copied first, removed from the roaming file after, so a crash in between
    // cannot lose them. Values already in machine.json win. Never throws.
    public static void MigrateSavedLid(Settings roaming, Settings machine)
    {
        lock (gate)
        {
            try
            {
                if (roaming.Get<int?>("savedLidAc") is not int ac || roaming.Get<int?>("savedLidDc") is not int dc) return;
                if (machine.Get<int?>("savedLidAc") is null || machine.Get<int?>("savedLidDc") is null)
                {
                    // The old values come with no plan: a plan left over in machine.json is not theirs.
                    machine.Set<string?>(SchemeKey, null);
                    machine.Set("savedLidAc", ac);
                    machine.Set("savedLidDc", dc);
                }
                roaming.Set<int?>("savedLidAc", null);
                roaming.Set<int?>("savedLidDc", null);
            }
            catch (Exception)
            {
                // Whatever was not moved stays where it was, and is tried again at the next start.
            }
        }
    }

    // Remember the current lid action (and which power plan it is in) once, then set it to "do
    // nothing". If powercfg shows no lid action, change nothing and save nothing, so Off() never
    // "restores" a guessed value. Throws when powercfg fails; Failed when the lid action does not
    // read back as "do nothing".
    static LidResult SetLidToDoNothing(Settings settings)
    {
        // Both values must be saved to count: a crash between the two writes leaves only one,
        // and then the lid action has not been changed yet, so read it fresh.
        if (settings.Get<int?>("savedLidAc") is null || settings.Get<int?>("savedLidDc") is null)
        {
            var current = QueryLidAction(CurrentScheme);
            if (current is null) return LidResult.NoLidSetting;
            // The plan goes first: values without their plan would be put back into whichever
            // plan is active then. null (Windows would not say) means "the current plan" later.
            settings.Set<string?>(SchemeKey, ActiveScheme());
            settings.Set("savedLidAc", current.Value.ac);
            settings.Set("savedLidDc", current.Value.dc);
        }
        SetLidAction(CurrentScheme, 0, 0);  // 0 = do nothing
        var now = QueryLidAction(CurrentScheme);
        return now is { } n && n.ac == 0 && n.dc == 0 ? LidResult.Changed : LidResult.Failed;
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
                    var scheme = SchemeToRestore(settings);
                    SetLidAction(scheme, ac, dc);
                    // Forget the saved values only once the restore is confirmed, in that plan.
                    // The plan goes last: values without their plan would be restored elsewhere.
                    var now = ReadLidAction(scheme);
                    if (now is { } n && n.ac == ac && n.dc == dc)
                    {
                        settings.Set<int?>("savedLidAc", null);
                        settings.Set<int?>("savedLidDc", null);
                        settings.Set<string?>(SchemeKey, null);
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

    // The lid-close action of a power plan (the current one by default) for plugged in and on
    // battery, or null when powercfg fails or does not print both values.
    public static (int ac, int dc)? ReadLidAction(string scheme = "SCHEME_CURRENT")
    {
        try
        {
            return QueryLidAction(scheme);
        }
        catch (Exception)
        {
            return null;
        }
    }

    // Like ReadLidAction, but a powercfg failure throws, so On can tell it from "no lid action".
    static (int ac, int dc)? QueryLidAction(string scheme) => ParseLidAction(Powercfg($"/query {scheme} {Sub} {LidAction}"));

    // The GUID of the active power plan, or null when powercfg fails or prints none.
    static string? ActiveScheme()
    {
        try
        {
            var m = Regex.Match(Powercfg("/getactivescheme"), GuidPattern);
            return m.Success ? m.Value.ToLowerInvariant() : null;
        }
        catch (Exception) { return null; }
    }

    // The plan the saved values go back into: the one saved with them. The current plan when none
    // was saved (an older version, or Windows would not say) or when that plan is gone since.
    static string SchemeToRestore(Settings settings)
    {
        var saved = settings.Get<string>(SchemeKey);
        if (saved is null || !Regex.IsMatch(saved, "^" + GuidPattern + "$")) return CurrentScheme;
        try
        {
            if (!Powercfg("/list").Contains(saved, StringComparison.OrdinalIgnoreCase)) return CurrentScheme;
        }
        catch (Exception)
        {
            // Cannot tell whether it still exists: try it. A failure keeps the saved values.
        }
        return saved;
    }

    // The last two hex values in the output are the current AC and DC settings. Matching hex
    // values rather than label text keeps this independent of the Windows display language.
    public static (int ac, int dc)? ParseLidAction(string output)
    {
        var values = Regex.Matches(output, @"0x([0-9a-fA-F]{8})").Select(m => Convert.ToInt32(m.Groups[1].Value, 16)).ToList();
        if (values.Count < 2) return null;
        return (values[^2], values[^1]);
    }

    // Sets the values in `scheme`, then activates the current plan, which is what makes Windows
    // apply them. That never switches plans: it only re-applies the one that is active.
    static void SetLidAction(string scheme, int ac, int dc)
    {
        Powercfg($"/setacvalueindex {scheme} {Sub} {LidAction} {ac}");
        Powercfg($"/setdcvalueindex {scheme} {Sub} {LidAction} {dc}");
        Powercfg($"/setactive {CurrentScheme}");
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
