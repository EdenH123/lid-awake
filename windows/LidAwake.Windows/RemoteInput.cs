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

    // Lets go of a button the phone was holding (it disconnected, or control was turned off).
    // Modifier keys are never left held: each command releases its own before it returns.
    public void ReleaseHeld()
    {
        lock (gate)
        {
            if (!leftDown) return;
            leftDown = false;
            if (native.OnUserDesktop()) native.Send(new[] { Mouse(0, 0, 0, MOUSEEVENTF_LEFTUP) });
        }
    }

    public bool LeftDown { get { lock (gate) return leftDown; } }

    // ---- The commands ----

    // Relative to where the pointer is, kept on the primary display. Sent as an absolute move to
    // the new place: a relative MOUSEEVENTF_MOVE would be scaled by the pointer speed and
    // acceleration settings, and could leave the primary display.
    void Move(JsonElement cmd)
    {
        var (w, h) = native.PrimaryScreen();
        if (w <= 0 || h <= 0) return;
        var (cx, cy) = Pointer(w, h);
        int x = Math.Clamp(cx + (int)Math.Round(Number(cmd, "dx")), 0, w - 1);
        int y = Math.Clamp(cy + (int)Math.Round(Number(cmd, "dy")), 0, h - 1);
        native.Send(new[] { MoveTo(x, y, w, h) });
    }

    void LeftButton(bool down)
    {
        if (down == leftDown) return;  // ignored if already down (or up)
        leftDown = down;
        native.Send(new[] { Mouse(0, 0, 0, down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP) });
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
        native.Send(inputs.ToArray());

        if (!right) leftDown = false;  // a left click leaves the button up
        chain = continues ? chain + presses : presses;
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
        if (inputs.Count > 0) native.Send(inputs.ToArray());
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
            if (ch is "\r\n" or "\n" or "\r") { native.Send(Press(Keys["return"])); continue; }
            if (ch == "\t") { native.Send(Press(Keys["tab"])); continue; }
            var inputs = new INPUT[ch.Length * 2];
            for (int i = 0; i < ch.Length; i++)
            {
                inputs[i] = Unicode(ch[i], up: false);
                inputs[ch.Length + i] = Unicode(ch[i], up: true);
            }
            native.Send(inputs);
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
            if (Gesture(cmd, name) is { } gesture) { native.Send(gesture); return; }
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
        native.Send(WithModifiers(mods, code));
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

// What the live socket needs to pass input on (docs/API.md 7.6), so it can be wired in when the
// screen branch lands. The socket does, for each input message, after its own "Lid Awake is on"
// check: Refusal() (an error sentence, or null), then its InputRateLimiter (drop silently),
// then TooLong (error TooMuchText), then "ping" → {"pong": true}, else Perform. When a phone that
// sent input leaves: ReleaseHeld().
public interface IInputSink
{
    // "Screen access is off." or the view-only sentence, or null when input is allowed.
    string? Refusal();
    bool TooLong(JsonElement command);
    // Wakes the screen and runs the command. Unknown types and bad values do nothing.
    void Perform(JsonElement command);
    void ReleaseHeld();
}

// The owner's switches, the input and the power actions, shared by POST /input, POST /power,
// /status, the tray and (later) the live socket.
public sealed class RemoteControl : IInputSink
{
    public const string ScreenKey = "remoteScreen";
    public const string ControlKey = "remoteControl";
    public const string ScreenOffText = "Screen access is off.";
    public const string ViewOnlyText = "View only: control from the phone is off in the Lid Awake menu on your PC.";
    public const string TooMuchText = "That's too much text to type at once.";
    // Accepted input wakes the screen at most this often (the input itself also wakes it).
    public const long WakeEvery = 2000;

    readonly Settings settings;
    readonly InputInjector injector;
    readonly IPowerActions power;
    readonly Func<long> clock;
    readonly object gate = new();
    long lastWake = long.MinValue;
    volatile bool screenOff;

    public RemoteControl(Settings settings, InputInjector injector, IPowerActions power, Func<long>? clock = null)
    {
        (this.settings, this.injector, this.power) = (settings, injector, power);
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    // The owner's screen switch (the screen feature adds its tray item; this only reads it).
    public bool ScreenAllowed => Read(ScreenKey) == true;

    // "Allow control from phone": needs the screen switch. Never set, it follows the screen
    // switch, as on the Mac; with nothing set both are off.
    public bool ControlAllowed => ScreenAllowed && (Read(ControlKey) ?? ScreenAllowed);

    // The phone turned the display off and hasn't woken it (/status screenOff).
    public bool ScreenOff => screenOff;

    // From the tray. Turned off, a button the phone was holding is let go.
    public void SetControl(bool on)
    {
        settings.Set(ControlKey, on);
        if (!on) injector.ReleaseHeld();
    }

    public string? Refusal() => !ScreenAllowed ? ScreenOffText : !ControlAllowed ? ViewOnlyText : null;

    public bool TooLong(JsonElement command) => InputInjector.TooLong(command);

    public void Perform(JsonElement command)
    {
        WakeSoon();
        injector.Perform(command);
    }

    public void ReleaseHeld() => injector.ReleaseHeld();

    public void Lock() => power.Lock();

    public void DisplayOff()
    {
        screenOff = true;
        power.DisplayOff();
    }

    public void Wake()
    {
        lock (gate) lastWake = clock();
        screenOff = false;
        power.Wake();
    }

    // Any accepted input wakes the screen (7.6), right away after a display-off.
    void WakeSoon()
    {
        long now = clock();
        lock (gate)
        {
            if (!screenOff && lastWake != long.MinValue && now - lastWake >= 0 && now - lastWake < WakeEvery) return;
            lastWake = now;
        }
        screenOff = false;
        power.Wake();
    }

    // A stored switch; a value that isn't true or false counts as not set.
    bool? Read(string key)
    {
        try { return settings.Get<bool?>(key); }
        catch (Exception) { return null; }
    }
}
