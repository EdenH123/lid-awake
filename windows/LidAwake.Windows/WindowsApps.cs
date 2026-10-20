using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;

namespace LidAwake;

// The Windows side of the phone's apps (PhoneApps): the Start menu scan, shortcut resolution, the
// process list, window actions and icons. Each sits behind a small interface so tests use fakes.

// The Win32 calls, in one place.
static class Win32Apps
{
    delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetWindow(IntPtr hWnd, uint cmd);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] static extern int GetWindowLong(IntPtr hWnd, int index);
    [DllImport("user32.dll", EntryPoint = "GetWindowTextLengthW")] static extern int GetWindowTextLength(IntPtr hWnd);
    [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);
    [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] static extern bool BringWindowToTop(IntPtr hWnd);
    [DllImport("user32.dll")] static extern bool AllowSetForegroundWindow(int processId);
    [DllImport("user32.dll")] static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool attach);
    [DllImport("user32.dll")] public static extern bool DestroyIcon(IntPtr hIcon);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern uint PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, uint count, uint flags);
    [DllImport("dwmapi.dll")] static extern int DwmGetWindowAttribute(IntPtr hWnd, int attribute, out int value, int size);
    [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint access, bool inherit, uint processId);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);

    const int GwOwner = 4;
    const int GwlExStyle = -20;
    const int WsExToolWindow = 0x80;
    const int DwmaCloaked = 14;
    const int SwShow = 5;
    const int SwShowMinNoActive = 7;
    const int SwMinimize = 6;
    const int SwRestore = 9;
    const uint ProcessQueryLimitedInformation = 0x1000;
    const int ErrorInsufficientBuffer = 122;

    // The full path of the exe of a process, or null (a protected process, or one that is gone).
    public static string? ExePath(uint pid)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return null;
        try
        {
            // 1024 characters almost always do; a longer path (up to 32 K) gets one retry.
            foreach (var capacity in new[] { 1024, 32768 })
            {
                var text = new StringBuilder(capacity);
                uint size = (uint)capacity;
                if (QueryFullProcessImageNameW(handle, 0, text, ref size)) return text.ToString(0, (int)size);
                if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;
            }
            return null;
        }
        finally { CloseHandle(handle); }
    }

    // The ordinary windows now, one per process (the top-most): visible, with no owner, not a tool
    // window, with a title, and not hidden by Windows (a suspended app's window is "cloaked").
    public static Dictionary<uint, IntPtr> Windows()
    {
        var found = new Dictionary<uint, IntPtr>();
        EnumWindowsProc callback = (hWnd, _) =>
        {
            try
            {
                if (!IsWindowVisible(hWnd) || GetWindow(hWnd, GwOwner) != IntPtr.Zero) return true;
                if ((GetWindowLong(hWnd, GwlExStyle) & WsExToolWindow) != 0) return true;
                if (GetWindowTextLength(hWnd) == 0) return true;
                if (DwmGetWindowAttribute(hWnd, DwmaCloaked, out var cloaked, sizeof(int)) == 0 && cloaked != 0) return true;
                GetWindowThreadProcessId(hWnd, out var pid);
                found.TryAdd(pid, hWnd);
            }
            catch (Exception) { }
            return true;
        };
        EnumWindows(callback, IntPtr.Zero);
        GC.KeepAlive(callback);
        return found;
    }

    public static void Minimize(IntPtr window) => ShowWindow(window, SwShowMinNoActive);

    // Brings a window to the front. Windows' foreground lock refuses SetForegroundWindow from a
    // program the person is not using, so, in order: (1) AllowSetForegroundWindow(ASFW_ANY) and a
    // plain SetForegroundWindow; (2) if that did not work, attach this thread's input to the
    // current foreground window's thread (AttachThreadInput), which lifts the lock, and try again;
    // (3) last, minimize and restore the window, which Windows lets through.
    public static void Bring(IntPtr window)
    {
        ShowWindow(window, IsIconic(window) ? SwRestore : SwShow);
        AllowSetForegroundWindow(-1);
        SetForegroundWindow(window);
        if (GetForegroundWindow() == window) return;

        var current = GetForegroundWindow();
        uint currentThread = current == IntPtr.Zero ? 0 : GetWindowThreadProcessId(current, out _);
        uint mine = GetCurrentThreadId();
        bool attached = currentThread != 0 && currentThread != mine && AttachThreadInput(mine, currentThread, true);
        try
        {
            BringWindowToTop(window);
            SetForegroundWindow(window);
        }
        finally
        {
            if (attached) AttachThreadInput(mine, currentThread, false);
        }
        if (GetForegroundWindow() == window) return;

        ShowWindow(window, SwMinimize);
        ShowWindow(window, SwRestore);
        SetForegroundWindow(window);
    }

    public static void AllowAnyForeground() => AllowSetForegroundWindow(-1);
}

// The running programs, from one pass over the processes and one over the windows.
public sealed class ProcessSource : IProcessSource
{
    // Shell and system hosts that own windows but are not apps the person opened.
    static readonly HashSet<string> NotApps = new(StringComparer.OrdinalIgnoreCase)
    {
        "ApplicationFrameHost.exe", "TextInputHost.exe", "ShellExperienceHost.exe",
        "StartMenuExperienceHost.exe", "SearchHost.exe", "LockApp.exe",
    };

    static readonly ConcurrentDictionary<string, string> names = new(StringComparer.OrdinalIgnoreCase);

    public IReadOnlyList<RunningApp> Running()
    {
        var windows = Win32Apps.Windows();
        int self = Environment.ProcessId;
        var result = new List<RunningApp>();
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                var exe = Win32Apps.ExePath((uint)process.Id);
                if (exe == null) continue;
                bool hasWindow = windows.ContainsKey((uint)process.Id) && !NotApps.Contains(Path.GetFileName(exe));
                result.Add(new RunningApp(exe, hasWindow ? NameOf(exe) : Path.GetFileNameWithoutExtension(exe), hasWindow, process.Id == self));
            }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        return result;
    }

    // The name Windows shows for the program (its file description), else the exe's name. Also
    // the names in the memory window and the processor history.
    internal static string NameOf(string exe) => names.GetOrAdd(exe, path =>
    {
        try
        {
            var description = FileVersionInfo.GetVersionInfo(path).FileDescription;
            if (!string.IsNullOrWhiteSpace(description)) return description.Trim();
        }
        catch (Exception) { }
        return Path.GetFileNameWithoutExtension(path);
    });
}

// Open, front, minimize and quit. Quit sends the window a close request (CloseMainWindow), like
// the X button: the program may still ask to save, and nothing is ever killed.
public sealed class WindowActions : IWindowActions
{
    public void Start(string exe)
    {
        // Defence in depth against a hand-edited settings file: only an existing .exe is started,
        // never a document, script or link. PhoneApps logs the refusal.
        if (!exe.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !File.Exists(exe))
            throw new InvalidOperationException("not a program that exists");
        // Lets the new program take the foreground, if Windows allows it.
        Win32Apps.AllowAnyForeground();
        Process.Start(new ProcessStartInfo(exe)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exe) ?? "",
        })?.Dispose();
    }

    public bool Focus(string exe)
    {
        var window = WindowOf(exe);
        if (window == IntPtr.Zero) return false;
        Win32Apps.Bring(window);
        return true;
    }

    public bool Hide(string exe)
    {
        var window = WindowOf(exe);
        if (window == IntPtr.Zero) return false;
        Win32Apps.Minimize(window);
        return true;
    }

    public QuitResult Quit(string exe)
    {
        var result = QuitResult.NotOpen;
        foreach (var process in ProcessesOf(exe))
        {
            try
            {
                if (process.CloseMainWindow()) result = QuitResult.Asked;
                else if (result == QuitResult.NotOpen) result = QuitResult.NoWindow;
            }
            catch (Exception) { if (result == QuitResult.NotOpen) result = QuitResult.NoWindow; }
            finally { process.Dispose(); }
        }
        return result;
    }

    static IntPtr WindowOf(string exe)
    {
        var windows = Win32Apps.Windows();
        foreach (var process in ProcessesOf(exe))
        {
            try
            {
                if (windows.TryGetValue((uint)process.Id, out var window)) return window;
            }
            finally { process.Dispose(); }
        }
        return IntPtr.Zero;
    }

    // The processes started from this exe (the caller disposes them).
    static List<Process> ProcessesOf(string exe)
    {
        var mine = new List<Process>();
        var wanted = PhoneApps.Normalize(exe);
        foreach (var process in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
        {
            var path = Win32Apps.ExePath((uint)process.Id);
            if (path != null && string.Equals(PhoneApps.Normalize(path), wanted, StringComparison.OrdinalIgnoreCase)) mine.Add(process);
            else process.Dispose();
        }
        return mine;
    }
}

// An exe's icon as a PNG. The big icon inside the exe is asked for first (up to 256 px); if that
// fails, the one Windows shows for the file. Smaller icons are scaled up to `size`.
public sealed class WindowsIcons : IAppIcons
{
    public byte[]? Png(string exe, int size)
    {
        return Try(() => FromExe(exe, size)) ?? Try(() => FromAssociated(exe, size));
    }

    static byte[]? Try(Func<byte[]?> make)
    {
        try { return make(); }
        catch (Exception) { return null; }
    }

    static byte[]? FromExe(string exe, int size)
    {
        var handles = new IntPtr[1];
        var ids = new int[1];
        int ask = Math.Min(size, 256);
        uint count = Win32Apps.PrivateExtractIcons(exe, 0, ask, ask, handles, ids, 1, 0);
        if (count == 0 || count == uint.MaxValue || handles[0] == IntPtr.Zero) return null;
        try
        {
            using var icon = Icon.FromHandle(handles[0]);
            return Encode(icon, size);
        }
        finally { Win32Apps.DestroyIcon(handles[0]); }
    }

    static byte[]? FromAssociated(string exe, int size)
    {
        using var icon = Icon.ExtractAssociatedIcon(exe);
        return icon == null ? null : Encode(icon, size);
    }

    static byte[] Encode(Icon icon, int size)
    {
        using var source = icon.ToBitmap();
        using var square = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(square))
        {
            g.Clear(Color.Transparent);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.SmoothingMode = SmoothingMode.HighQuality;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            g.DrawImage(source, new Rectangle(0, 0, size, size));
        }
        using var stream = new MemoryStream();
        square.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}

// What a .lnk points to, through WScript.Shell (late bound, so no COM interop declarations). One
// WScript.Shell object is made on the first shortcut and reused until Dispose (the scan disposes
// it at its end), so reading a Start menu does not create hundreds of them.
public sealed class ShortcutResolver : IShortcutResolver, IDisposable
{
    readonly object gate = new();
    object? shell;

    public string? Target(string shortcutPath)
    {
        lock (gate)
        {
            object? link = null;
            try
            {
                shell ??= CreateShell();
                if (shell == null) return null;
                dynamic wsh = shell;
                link = wsh.CreateShortcut(shortcutPath);
                dynamic shortcut = link!;
                string target = shortcut.TargetPath;
                return string.IsNullOrWhiteSpace(target) ? null : Environment.ExpandEnvironmentVariables(target);
            }
            catch (Exception) { return null; }
            finally
            {
                if (link != null && Marshal.IsComObject(link)) Marshal.ReleaseComObject(link);
            }
        }
    }

    static object? CreateShell()
    {
        var type = Type.GetTypeFromProgID("WScript.Shell");
        return type == null ? null : Activator.CreateInstance(type);
    }

    // Releases the shell; a later Target makes a new one.
    public void Dispose()
    {
        lock (gate)
        {
            if (shell != null && Marshal.IsComObject(shell)) Marshal.ReleaseComObject(shell);
            shell = null;
        }
    }
}

// The programs in the Start menu, from the shortcuts in the user's and the all-users Programs
// folders, each resolved to its exe. Shortcuts to anything but an existing .exe are left out, and
// so are uninstallers; a program found twice is listed once, the user's own shortcut first.
public sealed class StartMenuScanner : IStartMenuSource
{
    readonly IShortcutResolver resolver;
    readonly List<string> folders;
    readonly Func<string, bool> exists;

    public StartMenuScanner(IShortcutResolver resolver, IEnumerable<string>? folders = null, Func<string, bool>? exists = null)
    {
        this.resolver = resolver;
        this.folders = (folders ?? DefaultFolders()).ToList();
        this.exists = exists ?? File.Exists;
    }

    public static IEnumerable<string> DefaultFolders() =>
        new[]
        {
            Environment.GetFolderPath(Environment.SpecialFolder.Programs),
            Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms),
        }.Where(f => !string.IsNullOrEmpty(f));

    public IReadOnlyList<StartApp> Scan()
    {
        try { return ScanFolders(); }
        finally { (resolver as IDisposable)?.Dispose(); }
    }

    IReadOnlyList<StartApp> ScanFolders()
    {
        var found = new Dictionary<string, StartApp>(StringComparer.OrdinalIgnoreCase);
        var options = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true };
        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder)) continue;
            IEnumerable<string> links;
            try { links = Directory.EnumerateFiles(folder, "*.lnk", options).OrderBy(l => l, StringComparer.OrdinalIgnoreCase).ToList(); }
            catch (Exception) { continue; }
            foreach (var link in links)
            {
                string? target;
                try { target = resolver.Target(link); }
                catch (Exception) { continue; }
                if (target == null || !target.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
                bool there;
                try { there = exists(target); }
                catch (Exception) { continue; }
                if (!there) continue;
                var name = Path.GetFileNameWithoutExtension(link);
                if (IsUninstaller(name, target)) continue;
                var exe = PhoneApps.Normalize(target);
                found.TryAdd(exe, new StartApp(name, exe));
            }
        }
        return found.Values.OrderBy(a => a.Name, StringComparer.OrdinalIgnoreCase).ToList();
    }

    static bool IsUninstaller(string name, string target) =>
        name.Contains("uninstall", StringComparison.OrdinalIgnoreCase)
        || Path.GetFileName(target).StartsWith("unins", StringComparison.OrdinalIgnoreCase);
}
