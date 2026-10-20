using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using QRCoder;

namespace LidAwake;

// What the "Set up Lid Awake" window shows (SetupWindow), kept apart from the window as
// SetupModel is on the Mac (Setup.swift), so it can be tested. Windows has no keep-awake
// permission to ask for, so the Mac's sleep-rule row is left out.
sealed class SetupModel
{
    readonly Auth auth;
    readonly Action<string>? log;
    readonly Func<Tailnet.SetupState> check;
    readonly Func<string?> host;
    readonly Func<bool> isAwake;
    readonly Func<bool> screenAllowed;
    int checking;  // 1 while a Tailscale check is running (one at a time)

    // `check` runs tailscale.exe (slow: only off the UI thread); `host` is this PC's Tailscale
    // name as the app knows it (Tailnet.Host), the same name the page itself is served under.
    public SetupModel(Auth auth, Action<string>? log, Func<Tailnet.SetupState> check, Func<string?> host,
                      Func<bool> isAwake, Func<bool> screenAllowed)
    {
        this.auth = auth;
        this.log = log;
        this.check = check;
        this.host = host;
        this.isAwake = isAwake;
        this.screenAllowed = screenAllowed;
        RefreshLocal();
    }

    public Tailnet.SetupState Tailscale { get; private set; }
    // Only while signed in, as on the Mac: a name remembered from earlier may not work now.
    public string? Host { get; private set; }
    public bool HasPassword { get; private set; }
    public bool Awake { get; private set; }
    public bool ScreenAllowed { get; private set; }

    // A Tailscale check's result, applied on the UI thread (Apply).
    public readonly record struct Checked(Tailnet.SetupState State, string? Host);

    // The quick things, read on the UI thread.
    public void RefreshLocal()
    {
        HasPassword = auth.HasPassword;
        Awake = isAwake();
        ScreenAllowed = screenAllowed();
    }

    // Starts the Tailscale check on the thread pool, unless one is still running (then null).
    // The task never fails. Its result is applied with Apply, which also allows the next check.
    public Task<Checked>? StartCheck()
    {
        if (Interlocked.Exchange(ref checking, 1) == 1) return null;
        try
        {
            return Task.Run(() =>
            {
                try
                {
                    var state = check();
                    return new Checked(state, state.SignedIn ? host() : null);
                }
                catch (Exception) { return new Checked(default, null); }
            });
        }
        catch (Exception)
        {
            Volatile.Write(ref checking, 0);
            return null;
        }
    }

    public void Apply(Checked result)
    {
        Tailscale = result.State;
        Host = string.IsNullOrWhiteSpace(result.Host) ? null : result.Host;
        Volatile.Write(ref checking, 0);
    }

    // The phone page's address, https://<pc>.<tailnet>.ts.net/, which the QR code holds.
    public string? PageUrl => PageAddress(Host);

    public static string? PageAddress(string? host) => string.IsNullOrWhiteSpace(host) ? null : $"https://{host}/";

    // Everything the phone needs: the QR code is shown at full strength.
    public bool Ready => Tailscale.SignedIn && Tailscale.HttpsOn && HasPassword;

    // At start the window opens by itself until a phone password is set; later from the tray.
    public static bool OpensAtStart(bool hasPassword) => !hasPassword;

    // Through the same path as "Set phone password…" (PhonePassword). Null when saved.
    public string? SavePassword(string password, string again)
    {
        var problem = PhonePassword.Save(auth, password, again, log);
        RefreshLocal();
        return problem;
    }

    public const string PasswordSet = "Set. Change it from the tray menu.";

    public string TailscaleDetail =>
        !Tailscale.Installed ? "Install Tailscale and sign in. It's the private link between your phone and this PC."
        : Tailscale.SignedIn ? "Signed in."
        : "Open Tailscale (its icon is by the clock, or in the Start menu) and sign in.";

    public string AddressDetail => Tailscale.HttpsOn
        ? PageUrl ?? "Ready."
        : "In Tailscale's settings, turn on MagicDNS and HTTPS Certificates (once for your account).";

    public string PasswordDetail => HasPassword
        ? PasswordSet
        : $"Your phone asks for it before showing anything. At least {Auth.MinPasswordLength} characters.";

    public string AwakeStep => Awake
        ? "Lid Awake is on, so the page is open."
        : "Turn Lid Awake on: the page only opens while it's on.";

    public bool ShowsTurnOn => !Awake && Ready;

    public string ScreenDetail => ScreenAllowed
        ? "Allowed. Turn it off any time in the Lid Awake menu (untick Allow control from phone if you only want to watch)."
        : "Lets your phone show this PC's screen and use it as a trackpad.";
}

// The phone page's address as a QR code (QRCoder, error correction M), drawn here from its
// module matrix: each module a sharp square of whole pixels, on white, with the 4-module white
// quiet zone QRCoder includes.
static class QrImage
{
    // About this many logical pixels wide (the window scales it for the screen's DPI).
    public const int TargetSize = 180;

    // True for a dark module, [row, column], quiet zone included. Null for no text.
    public static bool[,]? Modules(string? text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        using var generator = new QRCodeGenerator();
        using var data = generator.CreateQrCode(text, QRCodeGenerator.ECCLevel.M);
        var matrix = data.ModuleMatrix;
        int n = matrix.Count;
        var modules = new bool[n, n];
        for (int y = 0; y < n; y++)
            for (int x = 0; x < n; x++)
                modules[y, x] = matrix[y][x];
        return modules;
    }

    // A whole number of pixels per module, as close to `targetPixels` across as that allows.
    public static int Scale(int modules, int targetPixels) =>
        Math.Max(1, (int)Math.Round(targetPixels / (double)Math.Max(1, modules)));

    // Null for no text. `faded`: light grey modules, for while setup isn't finished.
    public static Bitmap? Render(string? text, int targetPixels, bool faded = false)
    {
        if (Modules(text) is not { } modules) return null;
        int n = modules.GetLength(0);
        int scale = Scale(n, targetPixels);
        int size = n * scale;
        byte dark = faded ? (byte)191 : (byte)0;  // black at a quarter strength on white, like the Mac's 0.25
        var bitmap = new Bitmap(size, size, PixelFormat.Format24bppRgb);
        var bits = bitmap.LockBits(new Rectangle(0, 0, size, size), ImageLockMode.WriteOnly, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[bits.Stride];
            for (int y = 0; y < size; y++)
            {
                for (int x = 0; x < size; x++)
                {
                    byte v = modules[y / scale, x / scale] ? dark : (byte)255;
                    row[x * 3] = v;
                    row[x * 3 + 1] = v;
                    row[x * 3 + 2] = v;
                }
                Marshal.Copy(row, 0, bits.Scan0 + y * bits.Stride, bits.Stride);
            }
        }
        finally { bitmap.UnlockBits(bits); }
        return bitmap;
    }
}

// At most one of a window: opening it again brings the one there is to the front.
sealed class SingleWindow<T> where T : class
{
    readonly Func<T> make;
    readonly Func<T, bool> alive;
    readonly Action<T> front;
    T? current;

    public SingleWindow(Func<T> make, Func<T, bool> alive, Action<T> front) =>
        (this.make, this.alive, this.front) = (make, alive, front);

    // The window that is open now, or null.
    public T? Current => current is { } c && alive(c) ? c : null;

    public T Show()
    {
        var w = Current ?? (current = make());
        front(w);
        return w;
    }
}
