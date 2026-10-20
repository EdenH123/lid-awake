using System.Runtime.InteropServices;

namespace LidAwake;

// The Win32 input structures SendInput takes. INPUT is a tagged union: on x64 it is type (4 bytes),
// 4 bytes of padding (the union holds a pointer-sized field, so it is 8-aligned), then a 32-byte
// union: 40 bytes in all (28 on x86). Sequential layout gives exactly the C layout on both, and
// the union is explicit, all three members at offset 0. A test checks the sizes and offsets.
[StructLayout(LayoutKind.Sequential)]
public struct INPUT
{
    public uint type;
    public InputUnion u;

    public const uint MOUSE = 0;
    public const uint KEYBOARD = 1;
}

[StructLayout(LayoutKind.Explicit)]
public struct InputUnion
{
    [FieldOffset(0)] public MOUSEINPUT mi;
    [FieldOffset(0)] public KEYBDINPUT ki;
    [FieldOffset(0)] public HARDWAREINPUT hi;
}

// 32 bytes on x64: dx, dy, mouseData, dwFlags, time (4 each), 4 bytes of padding, dwExtraInfo (8).
[StructLayout(LayoutKind.Sequential)]
public struct MOUSEINPUT
{
    public int dx;
    public int dy;
    public int mouseData;  // a DWORD in C; signed here, because wheel amounts can be negative
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

// 24 bytes on x64: wVk, wScan (2 each), dwFlags, time (4 each), 4 bytes of padding, dwExtraInfo (8).
[StructLayout(LayoutKind.Sequential)]
public struct KEYBDINPUT
{
    public ushort wVk;
    public ushort wScan;
    public uint dwFlags;
    public uint time;
    public IntPtr dwExtraInfo;
}

[StructLayout(LayoutKind.Sequential)]
public struct HARDWAREINPUT
{
    public uint uMsg;
    public ushort wParamL;
    public ushort wParamH;
}

// What InputInjector needs from Windows. The real one (NativeInput) calls SendInput and friends;
// the tests give a fake that records the INPUT structures, so nothing moves the real mouse.
public interface ISendInput
{
    // How many of the inputs Windows took (SendInput's answer); fewer than given: the rest were
    // blocked (UIPI, a desktop switch) and are not sent.
    uint Send(INPUT[] inputs);
    // GetAsyncKeyState: Windows considers this virtual key held right now.
    bool IsKeyDown(ushort vk);
    // The pointer, in the same coordinates as PrimaryScreen. false: not known.
    bool TryGetCursor(out int x, out int y);
    // The primary display's size in pixels; its top-left corner is always (0, 0).
    (int Width, int Height) PrimaryScreen();
    // How close together two clicks must be to make a double-click (GetDoubleClickTime).
    int DoubleClickMilliseconds();
    // A clock that only goes forward.
    long Milliseconds();
    // False while the input desktop is not the user's own (the UAC prompt's secure desktop, the
    // lock screen or the sign-in screen): then nothing is sent at all.
    bool OnUserDesktop();
    // The primary display's pixels per point (DisplayScale): a move's dx and dy are the computer's
    // points, as on the Mac (the trackpad's phone pixels times its speed gain). 1 unless the real
    // one says otherwise.
    double PixelsPerPoint() => 1.0;
}

// Locking, and turning the display off and on (POST /power). The tests give a fake.
// DisplayOff may take a while (it waits for the windows it tells); RemoteControl runs it in the
// background, one at a time. Wake only restarts the display idle timer: the pointer nudge that
// turns a display back on is the injector's (InputInjector.Nudge), in order with other input.
public interface IPowerActions
{
    void Lock();
    void DisplayOff();
    void Wake();
}

// The real calls. SendInput needs no permission for windows at Lid Awake's own integrity level
// (a normal user). Windows' UIPI silently drops input aimed at an elevated (administrator) window,
// and no program can type into the UAC secure desktop or the lock screen at all.
public sealed class NativeInput : ISendInput
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern uint SendInput(uint count, [MarshalAs(UnmanagedType.LPArray), In] INPUT[] inputs, int size);

    [StructLayout(LayoutKind.Sequential)]
    struct POINT { public int X; public int Y; }

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    static extern int GetSystemMetrics(int index);

    [DllImport("user32.dll")]
    static extern uint GetDoubleClickTime();

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr OpenInputDesktop(uint flags, bool inherit, uint access);

    [DllImport("user32.dll", SetLastError = true)]
    static extern bool CloseDesktop(IntPtr desktop);

    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool GetUserObjectInformation(IntPtr obj, int index, [Out] char[] info, int length, out int needed);

    const int SM_CXSCREEN = 0, SM_CYSCREEN = 1, UOI_NAME = 2;
    const uint DESKTOP_READOBJECTS = 0x0001;

    [DllImport("user32.dll")]
    static extern short GetAsyncKeyState(int vk);

    public uint Send(INPUT[] inputs)
    {
        if (inputs.Length == 0) return 0;
        return SendInput((uint)inputs.Length, inputs, Marshal.SizeOf<INPUT>());
    }

    public bool IsKeyDown(ushort vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

    public bool TryGetCursor(out int x, out int y)
    {
        if (GetCursorPos(out var p)) { x = p.X; y = p.Y; return true; }
        x = y = 0;
        return false;
    }

    public (int Width, int Height) PrimaryScreen() => (GetSystemMetrics(SM_CXSCREEN), GetSystemMetrics(SM_CYSCREEN));

    public double PixelsPerPoint() => DisplayScale.PixelsPerPoint();

    public int DoubleClickMilliseconds() => (int)Math.Min(GetDoubleClickTime(), 5000u);

    public long Milliseconds() => Environment.TickCount64;

    // The input desktop is "Default" for the signed-in user's desktop. It can't even be opened
    // while the secure desktop ("Winlogon": UAC prompt, lock screen) has the input.
    public bool OnUserDesktop()
    {
        var desktop = OpenInputDesktop(0, false, DESKTOP_READOBJECTS);
        if (desktop == IntPtr.Zero) return false;
        try
        {
            var name = new char[256];
            if (!GetUserObjectInformation(desktop, UOI_NAME, name, name.Length * 2, out _)) return false;
            return new string(name).TrimEnd('\0').Equals("Default", StringComparison.OrdinalIgnoreCase);
        }
        finally { CloseDesktop(desktop); }
    }
}

public sealed class NativePower : IPowerActions
{
    [DllImport("user32.dll", SetLastError = true)]
    static extern bool LockWorkStation();

    [DllImport("user32.dll", SetLastError = true)]
    static extern IntPtr SendMessageTimeout(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam, uint flags, uint timeout, out IntPtr result);

    [DllImport("kernel32.dll")]
    static extern uint SetThreadExecutionState(uint flags);

    static readonly IntPtr HWND_BROADCAST = new(0xFFFF);
    const uint WM_SYSCOMMAND = 0x0112, SMTO_ABORTIFHUNG = 0x0002;
    const int SC_MONITORPOWER = 0xF170;
    const uint ES_DISPLAY_REQUIRED = 0x00000002;

    public void Lock() => LockWorkStation();

    // SendMessage(HWND_BROADCAST, WM_SYSCOMMAND, SC_MONITORPOWER, 2), as SendMessageTimeout so a hung
    // window can't hold it up for long. Called off the request thread (RemoteControl).
    public void DisplayOff() =>
        SendMessageTimeout(HWND_BROADCAST, WM_SYSCOMMAND, new IntPtr(SC_MONITORPOWER), new IntPtr(2), SMTO_ABORTIFHUNG, 1000, out _);

    // "Someone is here": the display idle timer starts again.
    public void Wake() => SetThreadExecutionState(ES_DISPLAY_REQUIRED);
}
