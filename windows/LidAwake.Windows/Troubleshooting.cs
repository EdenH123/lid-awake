using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace LidAwake;

// The troubleshooting flags of LidAwake.exe (the Mac's --selftest and --battery), handled in Main
// before the single-instance check, so they never start a tray and work while Lid Awake runs:
//   --selftest   SelfTest: a line per check, exit code 0 when every required one passed, else 1.
//   --battery    BatteryCheck: the raw battery readings. Exit code 0.
// The report is always saved, as %LOCALAPPDATA%\LidAwake\LidAwake-selftest.txt (or
// LidAwake-battery.txt), replacing the last one. It is also printed: to the output when that is
// redirected (a pipe or a file, as the tray and the tests run it), else to the console the command
// was typed in (LidAwake.exe is a windowed program, so it attaches to its parent's console; the
// prompt may already be back by then), and with no console at all the saved report opens in Notepad.
public static class Troubleshooting
{
    public const string SelfTestFlag = "--selftest";
    public const string BatteryFlag = "--battery";
    public const string SelfTestFile = "LidAwake-selftest.txt";
    public const string BatteryFile = "LidAwake-battery.txt";
    // How long the checks may take before the report says they didn't finish (exit code 1), and
    // how long the tray waits for the whole run.
    public static readonly TimeSpan MaxCheck = TimeSpan.FromSeconds(90);
    public static readonly TimeSpan MaxWait = TimeSpan.FromMinutes(2);
    public const string NotFinished = "The check didn't finish.";

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LidAwake");

    public static string ReportPath(string flag, string? folder = null) =>
        Path.Combine(folder ?? Folder, flag == BatteryFlag ? BatteryFile : SelfTestFile);

    // null: not one of these flags (Lid Awake starts as usual). Otherwise the exit code.
    public static int? Run(string[] args)
    {
        if (args.Length == 0 || args[0] is not (SelfTestFlag or BatteryFlag)) return null;
        ClaudeHook.EndStartingCursor();
        var flag = args[0];
        (string Text, int Code) made = ("", 1);
        // On a thread of its own in the multithreaded apartment, as the capture and the encoders
        // run in the app (Main's thread is single-threaded, for WinForms). A check that hangs (a
        // driver that never answers) is given up on: the thread is a background one, so the
        // process still ends.
        var work = new Thread(() =>
        {
            (string, int) result;
            try { result = Make(flag); }
            catch (Exception e) { result = ("The check stopped: " + e.Message + "\n", 1); }
            made = result;  // read only after Join returned true
        }) { IsBackground = true, Name = "Lid Awake " + flag };
        work.SetApartmentState(ApartmentState.MTA);
        work.Start();
        var (text, code) = work.Join(MaxCheck)
            ? made
            : ($"{NotFinished} It was still running after {MaxCheck.TotalSeconds:0} seconds.\n", 1);
        var path = Save(flag, text);
        Show(text, path);
        return code;
    }

    // The report and the exit code.
    public static (string Text, int Code) Make(string flag, SelfTestProbes? probes = null)
    {
        if (flag == BatteryFlag) return (BatteryCheck.Report(BatteryCheck.Read(), DateTimeOffset.Now), 0);
        var results = SelfTest.Run(probes ?? new SelfTestProbes());
        return (SelfTest.Report(results, DateTimeOffset.Now), SelfTest.ExitCode(results));
    }

    // Saved with Windows line ends, for Notepad. Null when it couldn't be saved.
    public static string? Save(string flag, string text, string? folder = null)
    {
        try
        {
            var path = ReportPath(flag, folder);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
            return path;
        }
        catch (Exception) { return null; }
    }

    static void Show(string text, string? path)
    {
        try
        {
            if (OutputRedirected())
            {
                using var output = Console.OpenStandardOutput();
                output.Write(Encoding.UTF8.GetBytes(text));
                return;
            }
            if (AttachConsole(AttachParentProcess))
            {
                try { WriteConsole("\r\n" + text.Replace("\r\n", "\n").Replace("\n", "\r\n")); }
                finally { FreeConsole(); }
                return;
            }
        }
        catch (Exception) { }
        if (path != null) Open(path);
    }

    // Notepad from Windows' own folder, never one found on the search path.
    static string Notepad => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "notepad.exe");

    // A file in whatever the PC opens it with (.log and .txt: Notepad, usually), else Notepad.
    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception)
        {
            try { Process.Start(new ProcessStartInfo(Notepad, "\"" + path + "\"") { UseShellExecute = false }); }
            catch (Exception) { }
        }
    }

    // The tray's "Run self-test" and "Battery check": this exe with the flag, its output read (so
    // it doesn't open Notepad itself), then the saved report shown. Off the UI thread; at most
    // MaxWait. Never throws.
    public static void RunAndShow(string flag, Action<string>? log = null)
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrEmpty(exe)) return;
            var path = ReportPath(flag);
            // The last run's report must never be shown as this one's: it goes first, and a report
            // is only taken when it was written after this run started.
            var started = DateTime.UtcNow;
            try { File.Delete(path); }
            catch (Exception) { }
            var info = new ProcessStartInfo(exe, flag)
            {
                UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, StandardOutputEncoding = Encoding.UTF8,
            };
            string? printed = null;
            using (var p = Process.Start(info))
            {
                if (p != null)
                {
                    var output = p.StandardOutput.ReadToEndAsync();
                    if (!p.WaitForExit((int)MaxWait.TotalMilliseconds))
                    {
                        try { p.Kill(true); }
                        catch (Exception) { }
                        log?.Invoke($"The {flag} check didn't finish in {MaxWait.TotalMinutes:0} minutes");
                    }
                    try { printed = output.Wait(2000) ? output.Result : null; }
                    catch (Exception) { }
                }
            }
            var (fresh, text) = Outcome(path, started, printed);
            if (fresh != null) { Open(fresh); return; }
            // Not saved by this run: what it printed, or that it didn't finish, from a temp file.
            var temp = Path.Combine(Path.GetTempPath(), Path.GetFileName(path));
            File.WriteAllText(temp, text!.Replace("\r\n", "\n").Replace("\n", "\r\n"), new UTF8Encoding(true));
            Open(temp);
        }
        catch (Exception e) { log?.Invoke($"The {flag} check couldn't run: {e.Message}"); }
    }

    // What the tray shows after a run that started at `startedUtc`: the saved report when this run
    // wrote it (Path), else what the run printed, else NotFinished (Text).
    public static (string? Path, string? Text) Outcome(string reportPath, DateTime startedUtc, string? printed)
    {
        try
        {
            if (File.Exists(reportPath) && File.GetLastWriteTimeUtc(reportPath) >= startedUtc) return (reportPath, null);
        }
        catch (Exception) { }
        return (null, string.IsNullOrWhiteSpace(printed) ? NotFinished + "\n" : printed);
    }

    // The standard output is a pipe or a file (the tray, a test, "| more", "> report.txt").
    static bool OutputRedirected()
    {
        var handle = GetStdHandle(StdOutputHandle);
        if (handle == IntPtr.Zero || handle == new IntPtr(-1)) return false;
        uint type = GetFileType(handle);
        return type is FileTypeDisk or FileTypePipe;
    }

    // WriteConsoleW to CONOUT$: Unicode (the ✓ and ✗) whatever the console's code page.
    static void WriteConsole(string text)
    {
        using var console = CreateFileW("CONOUT$", GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (console.IsInvalid) return;
        for (int at = 0; at < text.Length;)
        {
            int n = Math.Min(8192, text.Length - at);
            if (!WriteConsoleW(console, text.Substring(at, n), (uint)n, out uint written, IntPtr.Zero) || written == 0) return;
            at += (int)written;
        }
    }

    const int AttachParentProcess = -1;
    const int StdOutputHandle = -11;
    const uint FileTypeDisk = 1, FileTypePipe = 3;
    const uint GenericRead = 0x80000000, GenericWrite = 0x40000000, FileShareRead = 1, FileShareWrite = 2, OpenExisting = 3;

    [DllImport("kernel32.dll", SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool AttachConsole(int processId);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool FreeConsole();

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern IntPtr GetStdHandle(int which);

    [DllImport("kernel32.dll", ExactSpelling = true)]
    static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool WriteConsoleW(SafeFileHandle console, string text, uint length, out uint written, IntPtr reserved);
}
