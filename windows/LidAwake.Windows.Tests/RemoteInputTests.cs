using System.Runtime.InteropServices;
using System.Text.Json;
using LidAwake;
using Xunit;
using static LidAwake.InputInjector;

// A stand-in for SendInput and friends: it records every INPUT it is given, and nothing reaches
// the real mouse or keyboard.
public sealed class FakeInput : ISendInput
{
    public readonly List<INPUT[]> Calls = new();
    public int Width = 1920, Height = 1080;
    public int CursorX = 100, CursorY = 200;
    public bool HasCursor = true;
    public bool UserDesktop = true;
    public int DoubleClick = 500;
    public long Now = 10_000;

    public void Send(INPUT[] inputs) { lock (Calls) Calls.Add(inputs); }
    public bool TryGetCursor(out int x, out int y) { x = CursorX; y = CursorY; return HasCursor; }
    public (int Width, int Height) PrimaryScreen() => (Width, Height);
    public int DoubleClickMilliseconds() => DoubleClick;
    public long Milliseconds() => Now;
    public bool OnUserDesktop() => UserDesktop;

    public List<INPUT> All() { lock (Calls) return Calls.SelectMany(c => c).ToList(); }
}

public sealed class FakePower : IPowerActions
{
    public int Locks, Offs, Wakes;
    public void Lock() => Interlocked.Increment(ref Locks);
    public void DisplayOff() => Interlocked.Increment(ref Offs);
    public void Wake() => Interlocked.Increment(ref Wakes);
}

public class RemoteInputTests
{
    readonly FakeInput fake = new();
    readonly InputInjector injector;

    public RemoteInputTests() => injector = new InputInjector(fake);

    void Run(string json)
    {
        using var doc = JsonDocument.Parse(json);
        injector.Perform(doc.RootElement);
    }

    // One readable line per INPUT, so a sequence can be compared at a glance.
    public static string Show(INPUT i) => i.type == INPUT.MOUSE
        ? $"mouse {i.u.mi.dx},{i.u.mi.dy} data={i.u.mi.mouseData} flags={i.u.mi.dwFlags:X}"
        : $"key vk={i.u.ki.wVk:X2} scan={i.u.ki.wScan:X2} flags={i.u.ki.dwFlags:X}";

    List<string> Sent() => fake.All().Select(Show).ToList();

    static string M(int dx, int dy, uint flags, int data = 0) => Show(Mouse(dx, dy, data, flags));
    static string Down(KeyCode k) => Show(KeyInput(k, false));
    static string Up(KeyCode k) => Show(KeyInput(k, true));
    static string Abs(int x, int y) => M(x, y, MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE);
    static string U(char unit, bool up) => $"key vk=00 scan={(ushort)unit:X2} flags={(KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0)):X}";
    static readonly string LeftDown = M(0, 0, MOUSEEVENTF_LEFTDOWN), LeftUp = M(0, 0, MOUSEEVENTF_LEFTUP);
    static readonly string RightDown = M(0, 0, MOUSEEVENTF_RIGHTDOWN), RightUp = M(0, 0, MOUSEEVENTF_RIGHTUP);
    static KeyCode Char(char c) => new(0, CharScanCodes[c]);

    // ---- The structures SendInput reads ----

    [Fact]
    public void TheInputStructuresHaveTheWin32Layout()
    {
        bool x64 = IntPtr.Size == 8;
        Assert.Equal(x64 ? 40 : 28, Marshal.SizeOf<INPUT>());
        Assert.Equal(IntPtr.Size, (int)Marshal.OffsetOf<INPUT>(nameof(INPUT.u)));
        Assert.Equal(x64 ? 32 : 24, Marshal.SizeOf<MOUSEINPUT>());
        Assert.Equal(x64 ? 24 : 16, Marshal.SizeOf<KEYBDINPUT>());
        Assert.Equal(8, Marshal.SizeOf<HARDWAREINPUT>());
        Assert.Equal(x64 ? 32 : 24, Marshal.SizeOf<InputUnion>());
        Assert.Equal(x64 ? 24 : 20, (int)Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.dwExtraInfo)));
        Assert.Equal(x64 ? 16 : 12, (int)Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.dwExtraInfo)));
        Assert.Equal(12, (int)Marshal.OffsetOf<MOUSEINPUT>(nameof(MOUSEINPUT.dwFlags)));
        Assert.Equal(4, (int)Marshal.OffsetOf<KEYBDINPUT>(nameof(KEYBDINPUT.dwFlags)));
    }

    // The one real call: reading where the pointer is moves nothing, and answers on a desktop.
    [Fact]
    public void TheRealCallsThatOnlyReadDoNotThrow()
    {
        var native = new NativeInput();
        native.TryGetCursor(out _, out _);
        var (w, h) = native.PrimaryScreen();
        Assert.True(w >= 0 && h >= 0);
        Assert.True(native.DoubleClickMilliseconds() >= 0);
        native.OnUserDesktop();
    }

    // ---- Pointer ----

    [Fact]
    public void AbsolutePositionsLandOnTheSamePixelWhenWindowsConvertsThemBack()
    {
        foreach (var size in new[] { 1, 2, 768, 1080, 1366, 1920, 2560, 3840 })
            for (int p = 0; p < size; p++)
            {
                int abs = ToAbsolute(p, size);
                Assert.InRange(abs, 0, 65535);
                Assert.Equal(p, (int)((long)abs * size >> 16));
            }
        Assert.Equal(0, ToAbsolute(0, 1920));
        Assert.Equal(0, ToAbsolute(5, 0));
    }

    [Fact]
    public void MoveIsRelativeToThePointerAndSentAsAnAbsoluteMove()
    {
        Run("""{"type":"move","dx":10,"dy":-20}""");
        Assert.Equal(new[] { Show(MoveTo(110, 180, 1920, 1080)) }, Sent());
    }

    [Fact]
    public void MoveIsKeptOnThePrimaryDisplay()
    {
        Run("""{"type":"move","dx":1e9,"dy":-1e9}""");
        Run("""{"type":"move","dx":-5000,"dy":5000}""");
        fake.CursorX = 5000; fake.CursorY = -300;  // on another display
        Run("""{"type":"move","dx":1,"dy":1}""");
        Assert.Equal(new[]
        {
            Show(MoveTo(1919, 0, 1920, 1080)),
            Show(MoveTo(0, 1079, 1920, 1080)),
            Show(MoveTo(1919, 0, 1920, 1080)),
        }, Sent());
    }

    [Fact]
    public void BadNumbersCountAsZero()
    {
        // 1e400 is not finite as a double; strings, null and objects are not numbers.
        Run("""{"type":"move","dx":1e400,"dy":"7"}""");
        Run("""{"type":"move","dx":null,"dy":{"a":1}}""");
        Run("""{"type":"move","dx":-1e400}""");
        Run("""{"type":"move"}""");
        Assert.All(Sent(), s => Assert.Equal(Show(MoveTo(100, 200, 1920, 1080)), s));
        Assert.Equal(4, Sent().Count);
    }

    [Fact]
    public void MoveWithoutAPointerStartsInTheMiddle()
    {
        fake.HasCursor = false;
        Run("""{"type":"move","dx":1,"dy":1}""");
        Assert.Equal(new[] { Show(MoveTo(961, 541, 1920, 1080)) }, Sent());
    }

    [Fact]
    public void MoveWithNoScreenDoesNothing()
    {
        fake.Width = 0;
        Run("""{"type":"move","dx":1,"dy":1}""");
        Assert.Empty(Sent());
    }

    [Fact]
    public void MouseDownAndUpAreSentOnceEach()
    {
        Run("""{"type":"mousedown"}""");
        Run("""{"type":"mousedown"}""");
        Assert.True(injector.LeftDown);
        Run("""{"type":"move","dx":5,"dy":0}""");
        Run("""{"type":"mouseup"}""");
        Run("""{"type":"mouseup"}""");
        Assert.False(injector.LeftDown);
        Assert.Equal(new[] { LeftDown, Show(MoveTo(105, 200, 1920, 1080)), LeftUp }, Sent());
    }

    [Fact]
    public void ReleaseHeldLetsGoOfAHeldButtonOnly()
    {
        injector.ReleaseHeld();
        Assert.Empty(Sent());
        Run("""{"type":"mousedown"}""");
        injector.ReleaseHeld();
        injector.ReleaseHeld();
        Assert.Equal(new[] { LeftDown, LeftUp }, Sent());
        Assert.False(injector.LeftDown);
    }

    // ---- Clicks ----

    [Fact]
    public void ClickAtFractionsOfThePrimaryDisplay()
    {
        Run("""{"type":"click","x":0.5,"y":1}""");
        fake.Now += 10_000;
        Run("""{"type":"click","x":-3,"y":7}""");
        fake.Now += 10_000;
        Run("""{"type":"click","x":0,"y":0.25}""");
        Assert.Equal(new[]
        {
            Abs(32768, 65535), LeftDown, LeftUp,
            Abs(0, 65535), LeftDown, LeftUp,
            Abs(0, 16384), LeftDown, LeftUp,
        }, Sent());
    }

    [Fact]
    public void ClickWithoutBothFractionsClicksAtThePointer()
    {
        Run("""{"type":"click"}""");
        fake.Now += 10_000;
        Run("""{"type":"click","x":0.5}""");
        fake.Now += 10_000;
        Run("""{"type":"click","x":0.5,"y":1e400}""");
        Assert.Equal(new[] { LeftDown, LeftUp, LeftDown, LeftUp, LeftDown, LeftUp }, Sent());
    }

    [Fact]
    public void RightClick()
    {
        Run("""{"type":"click","button":"right","x":1,"y":0}""");
        fake.Now += 10_000;
        Run("""{"type":"click","button":"middle"}""");
        Assert.Equal(new[] { Abs(65535, 0), RightDown, RightUp, LeftDown, LeftUp }, Sent());
    }

    [Fact]
    public void ALoneCountSendsThatManyClicks()
    {
        foreach (var (json, n) in new[] { ("2", 2), ("3", 3), ("9", 3), ("0", 1), ("-4", 1), ("2.5", 1), ("\"2\"", 1), ("2.0", 2) })
        {
            fake.Calls.Clear();
            fake.Now += 10_000;  // well apart: nothing to continue
            Run($$"""{"type":"click","count":{{json}}}""");
            Assert.Equal(Enumerable.Range(0, n).SelectMany(_ => new[] { LeftDown, LeftUp }), Sent());
        }
    }

    // The page's double-tap: count 1, then count 2 a moment later, a little apart. One press
    // each, the second where the first was, so Windows sees exactly a double-click.
    [Fact]
    public void ThePagesDoubleTapIsOneDoubleClickNotThreeClicks()
    {
        Run("""{"type":"click","count":1,"x":0.5,"y":0.5}""");
        fake.Now += 200;
        Run("""{"type":"click","count":2,"x":0.51,"y":0.5}""");
        fake.Now += 200;
        Run("""{"type":"click","count":3,"x":0.52,"y":0.5}""");
        Assert.Equal(new[] { Abs(32768, 32768), LeftDown, LeftUp, LeftDown, LeftUp, LeftDown, LeftUp }, Sent());
    }

    [Fact]
    public void ACountTooLateOrForTheOtherButtonStartsAgain()
    {
        Run("""{"type":"click","count":1}""");
        fake.Now += 501;
        Run("""{"type":"click","count":2}""");  // too late: two of its own
        Assert.Equal(6, Sent().Count);
        fake.Calls.Clear();
        fake.Now += 100;
        Run("""{"type":"click","button":"right","count":2}""");  // the other button
        Assert.Equal(new[] { RightDown, RightUp, RightDown, RightUp }, Sent());
        fake.Calls.Clear();
        fake.Now += 100;
        Run("""{"type":"click","count":1}""");
        fake.Now += 100;
        Run("""{"type":"click","count":1}""");  // count 1 never continues: one press
        Assert.Equal(new[] { LeftDown, LeftUp, LeftDown, LeftUp }, Sent());
    }

    [Fact]
    public void ModifiersAreHeldAroundAClick()
    {
        Run("""{"type":"click","x":0,"y":0,"mods":["cmd","alt"]}""");
        Assert.Equal(new[] { Down(Ctrl), Down(Alt), Abs(0, 0), LeftDown, LeftUp, Up(Alt), Up(Ctrl) }, Sent());
    }

    [Fact]
    public void ALeftClickLetsGoOfAHeldButton()
    {
        Run("""{"type":"mousedown"}""");
        Run("""{"type":"click"}""");
        Assert.False(injector.LeftDown);
    }

    // ---- Scrolling ----

    [Fact]
    public void ScrollPixelsBecomeWheelUnitsAndFollowTheFinger()
    {
        Run("""{"type":"scroll","dx":0,"dy":100}""");
        Run("""{"type":"scroll","dx":100,"dy":0}""");
        Run("""{"type":"scroll","dx":-50,"dy":-50}""");
        Run("""{"type":"scroll","dx":0,"dy":1e9}""");
        Assert.Equal(new[]
        {
            M(0, 0, MOUSEEVENTF_WHEEL, 120),
            M(0, 0, MOUSEEVENTF_HWHEEL, -120),
            M(0, 0, MOUSEEVENTF_WHEEL, -60), M(0, 0, MOUSEEVENTF_HWHEEL, 60),
            M(0, 0, MOUSEEVENTF_WHEEL, 12000),
        }, Sent());
    }

    [Fact]
    public void ScrollAtAPointMovesThereFirst()
    {
        Run("""{"type":"scroll","dx":0,"dy":10,"x":0.5,"y":0.5}""");
        Assert.Equal(new[] { Abs(32768, 32768), M(0, 0, MOUSEEVENTF_WHEEL, 12) }, Sent());
    }

    [Fact]
    public void ScrollByNothingSendsNothing()
    {
        Run("""{"type":"scroll","dx":0,"dy":0}""");
        Run("""{"type":"scroll","dx":0.2,"dy":"x"}""");
        Assert.Empty(Sent());
    }

    // ---- Text ----

    [Fact]
    public void TextIsTypedAsUnicodeOneCharacterAtATime()
    {
        Run("""{"type":"text","text":"hé"}""");
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(new[] { U('h', false), U('h', true), U('é', false), U('é', true) }, Sent());
    }

    [Fact]
    public void AnEmojiIsItsSurrogatePair()
    {
        Run("""{"type":"text","text":"😀"}""");  // U+1F600 = D83D DE00
        Assert.Single(fake.Calls);
        Assert.Equal(new[] { U('\uD83D', false), U('\uDE00', false), U('\uD83D', true), U('\uDE00', true) }, Sent());
    }

    [Fact]
    public void AFamilyEmojiAndAnAccentWrittenInTwoPartsAreOneCharacterEach()
    {
        const string family = "👨‍👩‍👧‍👦";
        Run(JsonSerializer.Serialize(new { type = "text", text = family + "é" }));
        Assert.Equal(2, fake.Calls.Count);
        Assert.Equal(family.Length * 2, fake.Calls[0].Length);
        Assert.Equal(4, fake.Calls[1].Length);
        Assert.Equal(1, TextLength(family));
        Assert.Equal(1, TextLength("é"));
    }

    [Fact]
    public void LineBreaksAndTabsArePressedAsKeys()
    {
        Run(JsonSerializer.Serialize(new { type = "text", text = "a\r\nb\nc\rd\t" }));
        var ret = InputInjector.Keys["return"];
        var tab = InputInjector.Keys["tab"];
        Assert.Equal(new[]
        {
            U('a', false), U('a', true), Down(ret), Up(ret),
            U('b', false), U('b', true), Down(ret), Up(ret),
            U('c', false), U('c', true), Down(ret), Up(ret),
            U('d', false), U('d', true), Down(tab), Up(tab),
        }, Sent());
    }

    [Fact]
    public void TextLongerThan2000CharactersIsNotTyped()
    {
        Run(JsonSerializer.Serialize(new { type = "text", text = new string('a', 2001) }));
        Assert.Empty(Sent());
        Run(JsonSerializer.Serialize(new { type = "text", text = new string('a', 2000) }));
        Assert.Equal(2000, fake.Calls.Count);
    }

    [Fact]
    public void TooLongCountsGraphemeClusters()
    {
        string Family(int n) => string.Concat(Enumerable.Repeat("👨‍👩‍👧‍👦", n));
        JsonElement T(string s) => JsonDocument.Parse(JsonSerializer.Serialize(new { type = "text", text = s })).RootElement;
        Assert.False(InputInjector.TooLong(T(Family(2000))));
        Assert.True(InputInjector.TooLong(T(Family(2001))));
        Assert.False(InputInjector.TooLong(T(new string('a', 2000))));
        Assert.True(InputInjector.TooLong(T(new string('a', 2001))));
        Assert.False(InputInjector.TooLong(JsonDocument.Parse("""{"type":"text","text":5}""").RootElement));
        Assert.False(InputInjector.TooLong(JsonDocument.Parse("[1]").RootElement));
    }

    [Fact]
    public void TextThatIsNotAStringIsIgnored()
    {
        Run("""{"type":"text","text":12}""");
        Run("""{"type":"text"}""");
        Assert.Empty(Sent());
    }

    // ---- Keys ----

    [Fact]
    public void NamedKeysAreTheirVirtualKeysAndTheArrowsAreExtended()
    {
        var expected = new Dictionary<string, (ushort Vk, ushort Scan, bool Ext)>
        {
            ["return"] = (0x0D, 0x1C, false), ["tab"] = (0x09, 0x0F, false), ["space"] = (0x20, 0x39, false),
            ["delete"] = (0x08, 0x0E, false), ["escape"] = (0x1B, 0x01, false),
            ["left"] = (0x25, 0x4B, true), ["right"] = (0x27, 0x4D, true), ["down"] = (0x28, 0x50, true), ["up"] = (0x26, 0x48, true),
        };
        Assert.Equal(expected.Keys.OrderBy(k => k), InputInjector.Keys.Keys.OrderBy(k => k));
        foreach (var (name, (vk, scan, ext)) in expected)
        {
            fake.Calls.Clear();
            Run($$"""{"type":"key","key":"{{name}}"}""");
            var sent = fake.All();
            Assert.Equal(2, sent.Count);
            Assert.All(sent, i => { Assert.Equal(INPUT.KEYBOARD, i.type); Assert.Equal(vk, i.u.ki.wVk); Assert.Equal(scan, i.u.ki.wScan); });
            Assert.Equal(ext ? KEYEVENTF_EXTENDEDKEY : 0u, sent[0].u.ki.dwFlags);
            Assert.Equal((ext ? KEYEVENTF_EXTENDEDKEY : 0u) | KEYEVENTF_KEYUP, sent[1].u.ki.dwFlags);
        }
    }

    [Fact]
    public void CharsAreSentByTheirUsScanCode()
    {
        var expected = new Dictionary<char, ushort>
        {
            ['a'] = 0x1E, ['s'] = 0x1F, ['d'] = 0x20, ['f'] = 0x21, ['g'] = 0x22, ['h'] = 0x23, ['j'] = 0x24, ['k'] = 0x25, ['l'] = 0x26,
            ['q'] = 0x10, ['w'] = 0x11, ['e'] = 0x12, ['r'] = 0x13, ['t'] = 0x14, ['y'] = 0x15, ['u'] = 0x16, ['i'] = 0x17, ['o'] = 0x18, ['p'] = 0x19,
            ['z'] = 0x2C, ['x'] = 0x2D, ['c'] = 0x2E, ['v'] = 0x2F, ['b'] = 0x30, ['n'] = 0x31, ['m'] = 0x32,
            ['1'] = 0x02, ['2'] = 0x03, ['3'] = 0x04, ['4'] = 0x05, ['5'] = 0x06, ['6'] = 0x07, ['7'] = 0x08, ['8'] = 0x09, ['9'] = 0x0A, ['0'] = 0x0B,
            [' '] = 0x39, ['='] = 0x0D, ['-'] = 0x0C, [']'] = 0x1B, ['['] = 0x1A, ['\''] = 0x28, [';'] = 0x27, ['\\'] = 0x2B,
            [','] = 0x33, ['/'] = 0x35, ['.'] = 0x34, ['`'] = 0x29,
        };
        Assert.Equal(expected.Keys.OrderBy(k => k), CharScanCodes.Keys.OrderBy(k => k));
        foreach (var (ch, scan) in expected)
        {
            fake.Calls.Clear();
            Run(JsonSerializer.Serialize(new { type = "key", @char = ch.ToString() }));
            Assert.Equal(new[]
            {
                $"key vk=00 scan={scan:X2} flags={KEYEVENTF_SCANCODE:X}",
                $"key vk=00 scan={scan:X2} flags={KEYEVENTF_SCANCODE | KEYEVENTF_KEYUP:X}",
            }, Sent());
        }
    }

    [Fact]
    public void AnUpperCaseLetterAddsShift()
    {
        Run("""{"type":"key","char":"C"}""");
        Assert.Equal(new[] { Down(Shift), Down(Char('c')), Up(Char('c')), Up(Shift) }, Sent());
        fake.Calls.Clear();
        Run("""{"type":"key","char":"V","mods":["shift","cmd"]}""");  // Shift once
        Assert.Equal(new[] { Down(Shift), Down(Ctrl), Down(Char('v')), Up(Char('v')), Up(Ctrl), Up(Shift) }, Sent());
    }

    [Fact]
    public void ModifiersArePressedInOrderAndReleasedInReverse()
    {
        Run("""{"type":"key","key":"tab","mods":["alt","cmd","shift"]}""");
        var tab = InputInjector.Keys["tab"];
        Assert.Equal(new[] { Down(Alt), Down(Ctrl), Down(Shift), Down(tab), Up(tab), Up(Shift), Up(Ctrl), Up(Alt) }, Sent());
    }

    [Fact]
    public void CmdIsCtrlAndEachModifierIsPressedOnce()
    {
        Run("""{"type":"key","char":"c","mods":["cmd","ctrl","fn",3,"cmd"]}""");
        Assert.Equal(new[] { Down(Ctrl), Down(Char('c')), Up(Char('c')), Up(Ctrl) }, Sent());
        fake.Calls.Clear();
        Run("""{"type":"key","char":"c","mods":"cmd"}""");  // not a list: no modifiers
        Assert.Equal(new[] { Down(Char('c')), Up(Char('c')) }, Sent());
    }

    [Fact]
    public void AKnownKeyNameWinsOverChar()
    {
        Run("""{"type":"key","key":"escape","char":"a"}""");
        Run("""{"type":"key","key":"f13","char":"a"}""");
        var esc = InputInjector.Keys["escape"];
        Assert.Equal(new[] { Down(esc), Up(esc), Down(Char('a')), Up(Char('a')) }, Sent());
    }

    [Fact]
    public void UnknownKeysAndCharsAreIgnored()
    {
        Run("""{"type":"key","key":"f13"}""");
        Run("""{"type":"key","char":"!"}""");
        Run("""{"type":"key","char":"é"}""");
        Run("""{"type":"key","char":""}""");
        Run("""{"type":"key","char":5}""");
        Run("""{"type":"key"}""");
        Assert.Empty(Sent());
    }

    [Fact]
    public void OnlyTheFirstCharacterOfCharCounts()
    {
        Run("""{"type":"key","char":"ab"}""");
        Assert.Equal(new[] { Down(Char('a')), Up(Char('a')) }, Sent());
    }

    // The page's desktop gestures: Control plus an arrow.
    [Fact]
    public void CtrlArrowGesturesSwitchDesktopsOrOpenTaskView()
    {
        Run("""{"type":"key","key":"left","mods":["ctrl"]}""");
        Run("""{"type":"key","key":"right","mods":["ctrl"]}""");
        var left = InputInjector.Keys["left"];
        var right = InputInjector.Keys["right"];
        var tab = InputInjector.Keys["tab"];
        Assert.Equal(new[]
        {
            Down(Win), Down(Ctrl), Down(left), Up(left), Up(Ctrl), Up(Win),
            Down(Win), Down(Ctrl), Down(right), Up(right), Up(Ctrl), Up(Win),
        }, Sent());
        fake.Calls.Clear();
        Run("""{"type":"key","key":"up","mods":["ctrl"]}""");
        Run("""{"type":"key","key":"down","mods":["ctrl"]}""");
        Assert.Equal(new[] { Down(Win), Down(tab), Up(tab), Up(Win), Down(Win), Down(tab), Up(tab), Up(Win) }, Sent());
        fake.Calls.Clear();
        // "cmd" is Ctrl too, but the page means the shortcut key: Ctrl+Left, a word to the left.
        Run("""{"type":"key","key":"left","mods":["cmd"]}""");
        Run("""{"type":"key","key":"return","mods":["ctrl"]}""");
        var ret = InputInjector.Keys["return"];
        Assert.Equal(new[] { Down(Ctrl), Down(left), Up(left), Up(Ctrl), Down(Ctrl), Down(ret), Up(ret), Up(Ctrl) }, Sent());
    }

    // ---- Everything else ----

    [Fact]
    public void UnknownCommandsAreIgnored()
    {
        foreach (var json in new[] { """{"type":"ping"}""", """{"type":"jump"}""", """{"type":5}""", """{}""", "[]", "\"move\"", "null", """{"Type":"click"}""" })
            Run(json);
        Assert.Empty(Sent());
    }

    [Fact]
    public void NothingIsSentWhileTheSecureDesktopHasTheInput()
    {
        fake.UserDesktop = false;
        foreach (var json in new[] { """{"type":"move","dx":1,"dy":1}""", """{"type":"click"}""", """{"type":"text","text":"x"}""",
                                     """{"type":"key","key":"return"}""", """{"type":"mousedown"}""", """{"type":"scroll","dy":5}""" })
            Run(json);
        Assert.Empty(Sent());
        Assert.False(injector.LeftDown);
    }

    // ---- The socket's rate cap ----

    [Fact]
    public void AtMost240MessagesASecond()
    {
        var limiter = new InputRateLimiter();
        for (int i = 0; i < 240; i++) Assert.True(limiter.TryTake(5_000 + i));
        Assert.False(limiter.TryTake(5_500));
        Assert.False(limiter.TryTake(5_999));
        Assert.True(limiter.TryTake(6_000));  // a new second
        for (int i = 1; i < 240; i++) Assert.True(limiter.TryTake(6_100));
        Assert.False(limiter.TryTake(6_100));
        Assert.True(limiter.TryTake(60_000));
    }

    [Fact]
    public void TheRateCapStartsAgainIfTheClockGoesBack()
    {
        var limiter = new InputRateLimiter();
        for (int i = 0; i < 241; i++) limiter.TryTake(5_000);
        Assert.True(limiter.TryTake(1_000));
    }
}

// The owner's switches and the socket's entry point (IInputSink).
public class RemoteControlTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Settings settings;
    readonly FakeInput fake = new();
    readonly FakePower power = new();
    long now = 100_000;
    readonly RemoteControl control;

    public RemoteControlTests()
    {
        settings = new Settings(folder);
        control = new RemoteControl(settings, new InputInjector(fake), power, () => now);
    }

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    static JsonElement J(string json) => JsonDocument.Parse(json).RootElement.Clone();

    [Fact]
    public void BothSwitchesAreOffToStartWith()
    {
        Assert.False(control.ScreenAllowed);
        Assert.False(control.ControlAllowed);
        Assert.Equal("Screen access is off.", control.Refusal());
    }

    [Fact]
    public void ControlNeedsTheScreenSwitchAndFollowsItUntilSet()
    {
        settings.Set("remoteControl", true);
        Assert.False(control.ControlAllowed);
        Assert.Equal("Screen access is off.", control.Refusal());

        settings.Set<bool?>("remoteControl", null);
        settings.Set("remoteScreen", true);
        Assert.True(control.ControlAllowed);  // never set: follows the screen switch, as on the Mac
        Assert.Null(control.Refusal());

        control.SetControl(false);
        Assert.False(settings.Get<bool?>("remoteControl") ?? true);
        Assert.False(control.ControlAllowed);
        Assert.Equal("View only: control from the phone is off in the Lid Awake menu on your PC.", control.Refusal());

        control.SetControl(true);
        Assert.True(control.ControlAllowed);
    }

    [Fact]
    public void ASwitchThatIsNotABooleanCountsAsUnset()
    {
        settings.Set("remoteScreen", "yes");
        Assert.False(control.ScreenAllowed);
        settings.Set("remoteScreen", true);
        settings.Set("remoteControl", 7);
        Assert.True(control.ControlAllowed);
    }

    [Fact]
    public void TurningControlOffLetsGoOfAHeldButton()
    {
        settings.Set("remoteScreen", true);
        control.Perform(J("""{"type":"mousedown"}"""));
        control.SetControl(false);
        Assert.Equal(2, fake.All().Count);
        Assert.Equal(InputInjector.MOUSEEVENTF_LEFTUP, fake.All()[1].u.mi.dwFlags);
    }

    [Fact]
    public void AcceptedInputWakesTheScreenAtMostEveryTwoSeconds()
    {
        control.Perform(J("""{"type":"ping"}"""));
        Assert.Equal(1, power.Wakes);
        now += 500;
        control.Perform(J("""{"type":"move","dx":1,"dy":1}"""));
        Assert.Equal(1, power.Wakes);
        now += 2000;
        control.Perform(J("""{"type":"move","dx":1,"dy":1}"""));
        Assert.Equal(2, power.Wakes);
    }

    [Fact]
    public void AfterDisplayOffTheNextInputWakesItAtOnce()
    {
        control.Perform(J("""{"type":"ping"}"""));
        control.DisplayOff();
        Assert.True(control.ScreenOff);
        Assert.Equal(1, power.Offs);
        now += 10;
        control.Perform(J("""{"type":"ping"}"""));
        Assert.Equal(2, power.Wakes);
        Assert.False(control.ScreenOff);
    }

    [Fact]
    public void WakeAndLockGoToThePowerActions()
    {
        control.DisplayOff();
        control.Wake();
        control.Lock();
        Assert.False(control.ScreenOff);
        Assert.Equal((1, 1, 1), (power.Offs, power.Wakes, power.Locks));
    }

    [Fact]
    public void TheSinkChecksTextLength()
    {
        IInputSink sink = control;
        Assert.True(sink.TooLong(J(JsonSerializer.Serialize(new { type = "text", text = new string('x', 2001) }))));
        Assert.False(sink.TooLong(J("""{"type":"text","text":"ok"}""")));
    }

    [Fact]
    public void StatusReportsControlOnlyWithBothSwitches()
    {
        var info = new StatusInfo(new FakeReadings()) { Control = control };
        var s = JsonDocument.Parse(JsonSerializer.Serialize(info.Build(true))).RootElement;
        Assert.False(s.GetProperty("remoteControl").GetBoolean());
        Assert.True(s.GetProperty("controlPermission").GetBoolean());
        Assert.Contains("control", s.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.Single(s.GetProperty("features").EnumerateArray(), f => f.GetString() == "control");

        settings.Set("remoteScreen", true);
        control.DisplayOff();
        s = JsonDocument.Parse(JsonSerializer.Serialize(info.Build(true))).RootElement;
        Assert.True(s.GetProperty("remoteControl").GetBoolean());
        Assert.True(s.GetProperty("screenOff").GetBoolean());
        StatusInfoTests.AssertEveryKeyWithItsKind(s);

        settings.Set("remoteControl", false);
        s = JsonDocument.Parse(JsonSerializer.Serialize(info.Build(true))).RootElement;
        Assert.False(s.GetProperty("remoteControl").GetBoolean());
    }

    [Fact]
    public void StatusWithoutControlHasNoControlFeature()
    {
        var s = JsonDocument.Parse(JsonSerializer.Serialize(new StatusInfo(new FakeReadings()).Build(true))).RootElement;
        Assert.DoesNotContain("control", s.GetProperty("features").EnumerateArray().Select(f => f.GetString()));
        Assert.False(s.GetProperty("controlPermission").GetBoolean());
    }
}
