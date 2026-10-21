using System.Globalization;
using System.Text.Json;

namespace LidAwake;

// Turns the phone's input commands (docs/API.md section 8) into SendInput calls, like
// RemoteInput in ScreenControl.swift on the Mac. Every command is checked and clamped here, so
// odd values can't do anything strange: unknown types and bad values are ignored. One command at
// a time (a lock), so a click always lands after the move before it.
public sealed class InputInjector
{
    // Longest text typed in one go, in grapheme clusters (as the Mac counts Swift Characters).
    public const int MaxText = 2000;
    // dx and dy are kept to -10000..10000.
    public const double Limit = 10_000;
    // Scroll pixels to wheel units: 120 units (one notch) per 100 pixels, the distance one notch
    // scrolls in Chrome and Edge, so a drag on the phone scrolls about as far as it moved.
    public const double WheelPerPixel = 1.2;

    public const uint MOUSEEVENTF_MOVE = 0x0001, MOUSEEVENTF_LEFTDOWN = 0x0002, MOUSEEVENTF_LEFTUP = 0x0004,
        MOUSEEVENTF_RIGHTDOWN = 0x0008, MOUSEEVENTF_RIGHTUP = 0x0010, MOUSEEVENTF_WHEEL = 0x0800,
        MOUSEEVENTF_HWHEEL = 0x1000, MOUSEEVENTF_ABSOLUTE = 0x8000;
    public const uint KEYEVENTF_EXTENDEDKEY = 0x0001, KEYEVENTF_KEYUP = 0x0002, KEYEVENTF_UNICODE = 0x0004,
        KEYEVENTF_SCANCODE = 0x0008;

    public const ushort VK_BACK = 0x08, VK_TAB = 0x09, VK_RETURN = 0x0D, VK_SHIFT = 0x10, VK_CONTROL = 0x11,
        VK_MENU = 0x12, VK_ESCAPE = 0x1B, VK_SPACE = 0x20, VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27,
        VK_DOWN = 0x28, VK_LWIN = 0x5B;

    // A key: its virtual-key code, its scan code, and whether it is an extended key (the arrows,
    // which have their own keys apart from the number pad's).
    public readonly record struct KeyCode(ushort Vk, ushort Scan, bool Extended = false);

    // The `key` names (section 8). "delete" is Backspace, as on the Mac.
    public static readonly IReadOnlyDictionary<string, KeyCode> Keys = new Dictionary<string, KeyCode>
    {
        ["return"] = new(VK_RETURN, 0x1C),
        ["tab"] = new(VK_TAB, 0x0F),
        ["space"] = new(VK_SPACE, 0x39),
        ["delete"] = new(VK_BACK, 0x0E),
        ["escape"] = new(VK_ESCAPE, 0x01),
        ["left"] = new(VK_LEFT, 0x4B, true),
        ["right"] = new(VK_RIGHT, 0x4D, true),
        ["down"] = new(VK_DOWN, 0x50, true),
        ["up"] = new(VK_UP, 0x48, true),
    };

    // `char`: the physical key at that place on a US keyboard, as a scan code. Windows turns a
    // scan code into a key with the layout in use, exactly like a real key press, so Ctrl+C is the
    // same key whatever input language is active (the Mac's charKeyCodes does the same).
    public static readonly IReadOnlyDictionary<char, ushort> CharScanCodes = new Dictionary<char, ushort>
    {
        ['`'] = 0x29, ['1'] = 0x02, ['2'] = 0x03, ['3'] = 0x04, ['4'] = 0x05, ['5'] = 0x06, ['6'] = 0x07,
        ['7'] = 0x08, ['8'] = 0x09, ['9'] = 0x0A, ['0'] = 0x0B, ['-'] = 0x0C, ['='] = 0x0D,
        ['q'] = 0x10, ['w'] = 0x11, ['e'] = 0x12, ['r'] = 0x13, ['t'] = 0x14, ['y'] = 0x15, ['u'] = 0x16,
        ['i'] = 0x17, ['o'] = 0x18, ['p'] = 0x19, ['['] = 0x1A, [']'] = 0x1B,
        ['a'] = 0x1E, ['s'] = 0x1F, ['d'] = 0x20, ['f'] = 0x21, ['g'] = 0x22, ['h'] = 0x23, ['j'] = 0x24,
        ['k'] = 0x25, ['l'] = 0x26, [';'] = 0x27, ['\''] = 0x28, ['\\'] = 0x2B,
        ['z'] = 0x2C, ['x'] = 0x2D, ['c'] = 0x2E, ['v'] = 0x2F, ['b'] = 0x30, ['n'] = 0x31, ['m'] = 0x32,
        [','] = 0x33, ['.'] = 0x34, ['/'] = 0x35, [' '] = 0x39,
    };

    // `mods`. "cmd" is the page's first modifier button: on Windows the shortcut key is Ctrl, so
    // "cmd" and "ctrl" are the same key here (section 8, the table per computer).
    public static readonly KeyCode Ctrl = new(VK_CONTROL, 0x1D), Shift = new(VK_SHIFT, 0x2A),
        Alt = new(VK_MENU, 0x38), Win = new(VK_LWIN, 0x5B, true);

    readonly ISendInput native;
    readonly object gate = new();
    // True while the phone holds the left button for a drag (mousedown, then moves, then mouseup).
    bool leftDown;
    // The clicks just sent, for double-clicks (see Click).
    bool lastRight;
    long lastClickAt;
    int chain;

    public InputInjector(ISendInput native) => this.native = native;

    // Grapheme clusters, as a person counts characters (a family emoji is one).
    public static int TextLength(string text) => new StringInfo(text).LengthInTextElements;

    // True when the command's `text` is too long to type (answered 413, or an error on the socket).
    public static bool TooLong(JsonElement command) =>
        command.ValueKind == JsonValueKind.Object && command.TryGetProperty("text", out var t)
        && t.ValueKind == JsonValueKind.String && TextLength(t.GetString() ?? "") > MaxText;

    public void Perform(JsonElement cmd)
    {
        if (cmd.ValueKind != JsonValueKind.Object) return;
        if (!cmd.TryGetProperty("type", out var t) || t.ValueKind != JsonValueKind.String) return;
        lock (gate)
        {
            // The UAC prompt, the lock screen: never typed into or clicked (Windows wouldn't let
            // the input through anyway).
            if (!native.OnUserDesktop()) return;
            switch (t.GetString())
            {
                case "move": Move(cmd); break;
                case "mousedown": LeftButton(true); break;
                case "mouseup": LeftButton(false); break;
                case "click": Click(cmd); break;
                case "scroll": Scroll(cmd); break;
                case "text": TypeText(cmd); break;
                case "key": PressKey(cmd); break;
                default: break;  // "ping" and anything unknown
            }
        }
    }

    // Lets go of a button the phone was holding (it disconnected, or control was turned off),
    // and of any modifier key Windows still considers held. Each command releases its own keys,
    // so that is only ever left over from a batch Windows cut short.
    public void ReleaseHeld()
    {
        lock (gate)
        {
            bool wasDown = leftDown;
            leftDown = false;
            if (!native.OnUserDesktop()) return;
            if (wasDown) Inject(new[] { Mouse(0, 0, 0, MOUSEEVENTF_LEFTUP) });
            ReleaseStuckModifiers();
        }
    }

    // At start-up, as the Mac does: a modifier key an earlier run (or a crash mid-shortcut) left
    // marked as held is let go, so later clicks aren't Ctrl- or Alt-clicks. Only keys Windows
    // reports as down get a key-up.
    public void ReleaseModifiers()
    {
        lock (gate)
        {
            if (native.OnUserDesktop()) ReleaseStuckModifiers();
        }
    }

    // Left and right Ctrl, Shift, Alt and Windows keys.
    public static readonly KeyCode[] ModifierKeys =
    {
        new(0xA2, 0x1D), new(0xA3, 0x1D, true), new(0xA0, 0x2A), new(0xA1, 0x36),
        new(0xA4, 0x38), new(0xA5, 0x38, true), new(VK_LWIN, 0x5B, true), new(0x5C, 0x5C, true),
    };

    void ReleaseStuckModifiers()
    {
        var ups = ModifierKeys.Where(k => native.IsKeyDown(k.Vk)).Select(k => KeyInput(k, up: true)).ToArray();
        if (ups.Length > 0) native.Send(ups);
    }

    // A pointer move to where the pointer already is: a display the phone turned off comes back
    // on, and nothing visibly moves. Inside the same lock as the commands, so it can never land
    // between a move's reading of the pointer and its move (and undo it). On another display the
    // pointer is out of reach of an absolute move: a relative move of nothing still counts.
    public void Nudge()
    {
        lock (gate)
        {
            if (!native.OnUserDesktop()) return;
            var (w, h) = native.PrimaryScreen();
            if (w <= 0 || h <= 0 || !native.TryGetCursor(out var x, out var y)) return;
            bool onPrimary = x >= 0 && x < w && y >= 0 && y < h;
            Inject(new[] { onPrimary ? MoveTo(x, y, w, h) : Mouse(0, 0, 0, MOUSEEVENTF_MOVE) });
        }
    }

    // SendInput, checked: when Windows takes only part of a batch (UIPI blocked it, or the
    // desktop switched), every key and button the batch presses is let go again, in reverse, so
    // nothing stays held. False: not all of it was sent.
    bool Inject(INPUT[] batch)
    {
        if (batch.Length == 0) return true;
        uint sent = native.Send(batch);
        if (sent >= batch.Length) return true;
        var ups = new List<INPUT>();
        var seen = new HashSet<(uint, ushort, ushort, uint)>();
        for (int i = batch.Length - 1; i >= 0; i--)
        {
            var b = batch[i];
            INPUT? up = null;
            if (b.type == INPUT.KEYBOARD && (b.u.ki.dwFlags & (KEYEVENTF_KEYUP | KEYEVENTF_UNICODE)) == 0)
                up = new INPUT
                {
                    type = INPUT.KEYBOARD,
                    u = new InputUnion { ki = new KEYBDINPUT { wVk = b.u.ki.wVk, wScan = b.u.ki.wScan, dwFlags = (b.u.ki.dwFlags & (KEYEVENTF_EXTENDEDKEY | KEYEVENTF_SCANCODE)) | KEYEVENTF_KEYUP } },
                };
            else if (b.type == INPUT.MOUSE && (b.u.mi.dwFlags & MOUSEEVENTF_LEFTDOWN) != 0) up = Mouse(0, 0, 0, MOUSEEVENTF_LEFTUP);
            else if (b.type == INPUT.MOUSE && (b.u.mi.dwFlags & MOUSEEVENTF_RIGHTDOWN) != 0) up = Mouse(0, 0, 0, MOUSEEVENTF_RIGHTUP);
            if (up is { } release && seen.Add(release.type == INPUT.MOUSE ? (INPUT.MOUSE, (ushort)0, (ushort)0, release.u.mi.dwFlags)
                                                                           : (INPUT.KEYBOARD, release.u.ki.wVk, release.u.ki.wScan, release.u.ki.dwFlags)))
                ups.Add(release);
        }
        if (ups.Count > 0) native.Send(ups.ToArray());
        return false;
    }

    public bool LeftDown { get { lock (gate) return leftDown; } }

    // ---- The commands ----

    // Relative to where the pointer is, kept on the primary display. Sent as an absolute move to
    // the new place: a relative MOUSEEVENTF_MOVE would be scaled by the pointer speed and
    // acceleration settings, and could leave the primary display. dx and dy are the computer's
    // points, as on the Mac (the trackpad sends the phone's pixels times a speed gain): times the
    // display's scale, so the pointer moves as far as on a Mac at the same scaling.
    void Move(JsonElement cmd)
    {
        var (w, h) = native.PrimaryScreen();
        if (w <= 0 || h <= 0) return;
        var (cx, cy) = Pointer(w, h);
        double perPoint = native.PixelsPerPoint();
        if (!double.IsFinite(perPoint) || perPoint <= 0) perPoint = 1;
        int x = Math.Clamp(cx + (int)Math.Round(Number(cmd, "dx") * perPoint), 0, w - 1);
        int y = Math.Clamp(cy + (int)Math.Round(Number(cmd, "dy") * perPoint), 0, h - 1);
        Inject(new[] { MoveTo(x, y, w, h) });
    }

    // A press Windows refused leaves the state as it was (a refused press was also let go).
    void LeftButton(bool down)
    {
        if (down == leftDown) return;  // ignored if already down (or up)
        if (Inject(new[] { Mouse(0, 0, 0, down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP) })) leftDown = down;
    }

    // Windows tells a double-click from two clicks by their timing and place, and has no click
    // count to send. The page sends `count` 1 for the first tap and 2 for the second tap of a
    // double-tap, so each command is ONE press and release; `count` only fills in the clicks that
    // didn't come before it (a lone count 2 sends two). A click that continues a double-click is
    // made where the one before it was, so a finger that landed a little apart still double-clicks.
    void Click(JsonElement cmd)
    {
        bool right = cmd.TryGetProperty("button", out var b) && b.ValueKind == JsonValueKind.String && b.GetString() == "right";
        int count = Count(cmd);
        long now = native.Milliseconds();
        bool continues = count > 1 && chain > 0 && right == lastRight
            && now - lastClickAt >= 0 && now - lastClickAt <= native.DoubleClickMilliseconds();
        int presses = continues ? Math.Max(1, count - chain) : count;

        var inputs = new List<INPUT>();
        var mods = Modifiers(cmd);
        foreach (var m in mods) inputs.Add(KeyInput(m, up: false));
        if (!continues && At(cmd) is { } p) inputs.Add(Mouse(p.X, p.Y, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE));
        for (int i = 0; i < presses; i++)
        {
            inputs.Add(Mouse(0, 0, 0, right ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_LEFTDOWN));
            inputs.Add(Mouse(0, 0, 0, right ? MOUSEEVENTF_RIGHTUP : MOUSEEVENTF_LEFTUP));
        }
        for (int i = mods.Count - 1; i >= 0; i--) inputs.Add(KeyInput(mods[i], up: true));
        bool all = Inject(inputs.ToArray());

        if (!right) leftDown = false;  // a left click leaves the button up
        chain = !all ? 0 : continues ? chain + presses : presses;
        lastRight = right;
        lastClickAt = now;
    }

    // Pixels, passed on as wheel units (WheelPerPixel). The content follows the finger: a drag
    // down (positive dy) scrolls up, which is a positive WHEEL; a drag right (positive dx) shows
    // what is to the left, which is a negative HWHEEL.
    void Scroll(JsonElement cmd)
    {
        var inputs = new List<INPUT>();
        if (At(cmd) is { } p) inputs.Add(Mouse(p.X, p.Y, 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE));
        int vertical = (int)Math.Round(Number(cmd, "dy") * WheelPerPixel);
        int horizontal = -(int)Math.Round(Number(cmd, "dx") * WheelPerPixel);
        if (vertical != 0) inputs.Add(Mouse(0, 0, vertical, MOUSEEVENTF_WHEEL));
        if (horizontal != 0) inputs.Add(Mouse(0, 0, horizontal, MOUSEEVENTF_HWHEEL));
        if (inputs.Count > 0) Inject(inputs.ToArray());
    }

    // As Unicode, one character (grapheme cluster) at a time, whatever the keyboard layout: every
    // UTF-16 unit of it pressed, then released (an emoji outside the BMP is its two surrogates, in
    // order). A line break is the Return key and a tab the Tab key, as apps expect from typing.
    void TypeText(JsonElement cmd)
    {
        if (!cmd.TryGetProperty("text", out var t) || t.ValueKind != JsonValueKind.String) return;
        var text = t.GetString() ?? "";
        if (TextLength(text) > MaxText) return;
        var e = StringInfo.GetTextElementEnumerator(text);
        while (e.MoveNext())
        {
            var ch = e.GetTextElement();
            if (ch is "\r\n" or "\n" or "\r") { Inject(Press(Keys["return"])); continue; }
            if (ch == "\t") { Inject(Press(Keys["tab"])); continue; }
            var inputs = new INPUT[ch.Length * 2];
            for (int i = 0; i < ch.Length; i++)
            {
                inputs[i] = Unicode(ch[i], up: false);
                inputs[ch.Length + i] = Unicode(ch[i], up: true);
            }
            Inject(inputs);
        }
    }

    // A named key, or a `char` at its US position (an upper-case letter adds Shift), pressed and
    // released with the modifiers held around it: pressed in the order given, released in reverse.
    void PressKey(JsonElement cmd)
    {
        var mods = Modifiers(cmd);
        KeyCode code;
        string? name = cmd.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String ? k.GetString() : null;
        if (name != null && Keys.TryGetValue(name, out var named))
        {
            code = named;
            if (Gesture(cmd, name) is { } gesture) { Inject(gesture); return; }
        }
        else if (cmd.TryGetProperty("char", out var c) && c.ValueKind == JsonValueKind.String
                 && FirstCharacter(c.GetString() ?? "") is { Length: 1 } s)
        {
            char ch = s[0];
            if (ch is >= 'A' and <= 'Z') { ch = char.ToLowerInvariant(ch); if (!mods.Contains(Shift)) mods.Add(Shift); }
            if (!CharScanCodes.TryGetValue(ch, out var scan)) return;
            code = new KeyCode(0, scan);
        }
        else return;
        Inject(WithModifiers(mods, code));
    }

    // The page's desktop gestures send Control plus an arrow (Mac: switch desktop, Mission
    // Control, App windows). On Windows they do the same jobs: Win+Ctrl+Left/Right switches
    // virtual desktop, and up or down opens Task View (Win+Tab). Only for exactly ["ctrl"]:
    // "cmd" (also Ctrl here) with an arrow stays Ctrl+arrow, a jump by word in text.
    INPUT[]? Gesture(JsonElement cmd, string name)
    {
        if (!cmd.TryGetProperty("mods", out var m) || m.ValueKind != JsonValueKind.Array) return null;
        var list = m.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.String).Select(x => x.GetString()).ToList();
        if (list.Count == 0 || list.Any(x => x != "ctrl")) return null;
        return name switch
        {
            "left" or "right" => WithModifiers(new List<KeyCode> { Win, Ctrl }, Keys[name]),
            "up" or "down" => WithModifiers(new List<KeyCode> { Win }, Keys["tab"]),
            _ => null,
        };
    }

    // ---- Building blocks (public for the tests and for NativePower) ----

    public static INPUT Mouse(int dx, int dy, int data, uint flags) =>
        new() { type = INPUT.MOUSE, u = new InputUnion { mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags } } };

    // A pixel of the primary display as an absolute position, 0..65535 across it. Rounded up, so
    // Windows' own conversion back (times the width, divided by 65536) lands on that very pixel.
    public static int ToAbsolute(int pixel, int size) =>
        size <= 0 ? 0 : (int)Math.Min(65535, ((long)pixel * 65536 + size - 1) / size);

    public static INPUT MoveTo(int x, int y, int width, int height) =>
        Mouse(ToAbsolute(x, width), ToAbsolute(y, height), 0, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE);

    public static INPUT KeyInput(KeyCode key, bool up)
    {
        uint flags = (key.Extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
        // A key with no virtual-key code is a `char`: sent by its scan code (its place).
        if (key.Vk == 0) flags |= KEYEVENTF_SCANCODE;
        return new() { type = INPUT.KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wVk = key.Vk, wScan = key.Scan, dwFlags = flags } } };
    }

    static INPUT Unicode(char unit, bool up) =>
        new() { type = INPUT.KEYBOARD, u = new InputUnion { ki = new KEYBDINPUT { wScan = unit, dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0) } } };

    static INPUT[] Press(KeyCode key) => new[] { KeyInput(key, false), KeyInput(key, true) };

    static INPUT[] WithModifiers(List<KeyCode> mods, KeyCode key)
    {
        var inputs = new List<INPUT>();
        foreach (var m in mods) inputs.Add(KeyInput(m, false));
        inputs.Add(KeyInput(key, false));
        inputs.Add(KeyInput(key, true));
        for (int i = mods.Count - 1; i >= 0; i--) inputs.Add(KeyInput(mods[i], true));
        return inputs.ToArray();
    }

    // ---- Reading the command ----

    // `mods`, in the order given, each key once ("cmd" and "ctrl" are both Ctrl). Others ignored.
    static List<KeyCode> Modifiers(JsonElement cmd)
    {
        var mods = new List<KeyCode>();
        if (!cmd.TryGetProperty("mods", out var m) || m.ValueKind != JsonValueKind.Array) return mods;
        foreach (var item in m.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String) continue;
            KeyCode? key = item.GetString() switch
            {
                "ctrl" or "cmd" => Ctrl,
                "shift" => Shift,
                "alt" => Alt,
                _ => null,
            };
            if (key is { } k && !mods.Contains(k)) mods.Add(k);
        }
        return mods;
    }

    // A number from the phone, kept to -limit..limit; missing, not a number or not finite: 0.
    static double Number(JsonElement cmd, string name, double limit = Limit) =>
        cmd.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d)
            ? Math.Clamp(d, -limit, limit) : 0;

    // `x` and `y`, fractions of the primary display (top-left origin, clamped to 0..1), as
    // absolute positions 0..65535. Either one missing or not finite: none (at the pointer).
    static (int X, int Y)? At(JsonElement cmd)
    {
        if (!Fraction(cmd, "x", out var x) || !Fraction(cmd, "y", out var y)) return null;
        return ((int)Math.Round(x * 65535), (int)Math.Round(y * 65535));
    }

    static bool Fraction(JsonElement cmd, string name, out double value)
    {
        value = 0;
        if (!cmd.TryGetProperty(name, out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d) || !double.IsFinite(d)) return false;
        value = Math.Clamp(d, 0, 1);
        return true;
    }

    // `count` 1..3, default 1. A number that isn't whole (2.5) counts as missing, as on the Mac.
    static int Count(JsonElement cmd)
    {
        if (!cmd.TryGetProperty("count", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetDouble(out var d)
            || !double.IsFinite(d) || d != Math.Floor(d)) return 1;
        return (int)Math.Clamp(d, 1, 3);
    }

    static string FirstCharacter(string s)
    {
        var e = StringInfo.GetTextElementEnumerator(s);
        return e.MoveNext() ? e.GetTextElement() : "";
    }

    // The pointer, or the middle of the primary display when Windows can't say.
    (int X, int Y) Pointer(int w, int h) =>
        native.TryGetCursor(out var x, out var y) ? (x, y) : (w / 2, h / 2);
}

// At most 240 input messages a second on one live socket (7.6); extra ones are dropped. A window
// starts with the first message and lasts one second, as on the Mac. One per socket.
public sealed class InputRateLimiter
{
    public const int PerSecond = 240;
    readonly object gate = new();
    long windowStart = long.MinValue;
    int count;

    // `now` in milliseconds from a clock that only goes forward. False: drop this message.
    public bool TryTake(long now)
    {
        lock (gate)
        {
            if (windowStart == long.MinValue || now - windowStart >= 1000 || now < windowStart) { windowStart = now; count = 0; }
            count++;
            return count <= PerSecond;
        }
    }
}

// What the live socket needs to pass input on (docs/API.md 7.6; LiveServer.HandOffInput). The
// socket does, for each input message: Refusal() (an error sentence, or
// null; it covers "Lid Awake is on" too), then its InputRateLimiter (drop silently), then
// TooLong (error TooMuchText), then "ping" → {"pong": true}, else Perform. When a phone that sent
// input leaves: ReleaseHeld(). Perform checks Refusal() again itself, so a caller that forgets
// can't type into a PC whose owner turned it off.
public interface IInputSink
{
    // "Screen access is off." (also while Lid Awake is off, as the Mac's socket answers) or the
    // view-only sentence, or null when input is allowed.
    string? Refusal();
    bool TooLong(JsonElement command);
    // Wakes the screen and runs the command. Refused (Refusal() not null), unknown types and bad
    // values do nothing.
    void Perform(JsonElement command);
    void ReleaseHeld();
}

// The owner's switches, the input and the power actions, shared by POST /input, POST /power,
// /status, the tray and the live socket.
public sealed class RemoteControl : IInputSink
{
    // The screen switch is ScreenSwitch's (LiveServer.cs); this is its key, for the tests.
    public const string ScreenKey = ScreenSwitch.Key;
    public const string ControlKey = "remoteControl";
    public const string ScreenOffText = "Screen access is off.";
    public const string ViewOnlyText = "View only: control from the phone is off in the Lid Awake menu on your PC.";
    public const string TooMuchText = "That's too much text to type at once.";
    // POST /power displayoff with the lid closed (5.10; it names no computer, so it is the Mac's text).
    public const string LidClosedText = "The lid is closed: the screen couldn't be turned back on from your phone until you open the lid, so it stays on (at the lowest brightness).";
    // Accepted input restarts the display idle timer at most this often (the input itself also
    // counts as someone being there).
    public const long WakeEvery = 2000;
    // How long a wake waits for a display-off still being broadcast.
    static readonly TimeSpan WaitForOff = TimeSpan.FromSeconds(3);

    readonly Settings settings;
    readonly Func<bool> isOn;
    readonly InputInjector injector;
    readonly IPowerActions power;
    readonly Func<long> clock;
    readonly Func<bool> lidClosed;
    readonly object gate = new();
    readonly object offGate = new();
    long lastWake = long.MinValue;
    long? lastInput;  // under gate: when the phone's last accepted command was carried out
    volatile bool screenOff;
    Task? displayOff;  // the display-off broadcast in flight, if any

    // `isOn`: Lid Awake is on (the tray switch); while off nothing is accepted. `lidClosed`: the
    // lid state from the lid-closed screen (none: open), for displayoff's 409.
    public RemoteControl(Settings settings, Func<bool> isOn, InputInjector injector, IPowerActions power,
                         Func<long>? clock = null, Func<bool>? lidClosed = null)
    {
        (this.settings, this.isOn, this.injector, this.power) = (settings, isOn, injector, power);
        this.clock = clock ?? (() => Environment.TickCount64);
        this.lidClosed = lidClosed ?? (() => false);
    }

    // The owner's screen switch (the tray's "Allow screen from phone"; this only reads it).
    public bool ScreenAllowed => ScreenSwitch.IsOn(settings);

    // "Allow control from phone": needs the screen switch. Never set, it follows the screen
    // switch, as on the Mac; with nothing set both are off.
    public bool ControlAllowed => ScreenAllowed && (Read(ControlKey) ?? ScreenAllowed);

    // The phone turned the display off and hasn't woken it (/status screenOff).
    public bool ScreenOff => screenOff;

    // Told each time ScreenOff changes, in order (Program: ScreenWatch.SetScreenOff, so the
    // display isn't held awake while the phone has turned it off).
    public Action<bool>? ScreenOffChanged { get; init; }

    public bool LidClosed => Safe(lidClosed);

    // The phone clicked or typed in the last few seconds: the tray icon shows a hand meanwhile, as
    // the Mac's menu bar does (LiveServer.swift's activity, 5 seconds since the last input).
    public const long ControllingFor = 5000;

    public bool Controlling
    {
        get
        {
            lock (gate)
            {
                long now = clock();
                return lastInput is { } at && now - at >= 0 && now - at < ControllingFor;
            }
        }
    }

    // From the tray. Turned off, a button the phone was holding is let go.
    public void SetControl(bool on)
    {
        settings.Set(ControlKey, on);
        if (!on) injector.ReleaseHeld();
    }

    public string? Refusal() =>
        !Safe(isOn) || !ScreenAllowed ? ScreenOffText : !ControlAllowed ? ViewOnlyText : null;

    public bool TooLong(JsonElement command) => InputInjector.TooLong(command);

    public void Perform(JsonElement command)
    {
        if (Refusal() != null) return;
        lock (gate) lastInput = clock();
        WakeSoon();
        injector.Perform(command);
    }

    public void ReleaseHeld() => injector.ReleaseHeld();

    // Lid Awake turned off, or the screen switch went off: the phone's "display off" no longer
    // stands (the next on starts afresh, and the lid screen may hold the display again). Nothing
    // is woken: the display stays as it is.
    public void ClearScreenOff() => SetScreenOff(false);

    // At start-up: a modifier key left marked as held is let go (main.swift does the same).
    public void ReleaseModifiers() => injector.ReleaseModifiers();

    public void Lock() => power.Lock();

    // In the background, one broadcast at a time: a repeat while one is still going is ignored.
    public void DisplayOff()
    {
        SetScreenOff(true);
        lock (gate)
        {
            if (displayOff is { IsCompleted: false }) return;
            displayOff = Task.Run(() =>
            {
                try { power.DisplayOff(); }
                catch (Exception) { }
            });
        }
    }

    // Waits for a display-off still going (so it can't turn the display off again right after),
    // then wakes the display.
    public void Wake()
    {
        WaitForDisplayOff();
        lock (gate) lastWake = clock();
        SetScreenOff(false);
        power.Wake();
        injector.Nudge();
    }

    // Any accepted input wakes the screen (7.6). After a display-off at once, with the nudge
    // (a ping or an unknown command sends no input of its own); otherwise the input itself is
    // someone being there, and the idle timer restarts at most every WakeEvery.
    void WakeSoon()
    {
        bool wasOff = screenOff;
        long now = clock();
        lock (gate)
        {
            if (!wasOff && lastWake != long.MinValue && now - lastWake >= 0 && now - lastWake < WakeEvery) return;
            lastWake = now;
        }
        if (wasOff) WaitForDisplayOff();
        SetScreenOff(false);
        power.Wake();
        if (wasOff) injector.Nudge();
    }

    void SetScreenOff(bool value)
    {
        lock (offGate)
        {
            if (screenOff == value) return;
            screenOff = value;
            try { ScreenOffChanged?.Invoke(value); }
            catch (Exception) { }
        }
    }

    void WaitForDisplayOff()
    {
        Task? running;
        lock (gate) running = displayOff;
        try { running?.Wait(WaitForOff); }
        catch (Exception) { }
    }

    // A stored switch; a value that isn't true or false counts as not set.
    bool? Read(string key)
    {
        try { return settings.Get<bool?>(key); }
        catch (Exception) { return null; }
    }

    static bool Safe(Func<bool> f)
    {
        try { return f(); }
        catch (Exception) { return false; }
    }
}
