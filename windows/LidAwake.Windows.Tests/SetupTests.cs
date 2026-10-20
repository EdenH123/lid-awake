using LidAwake;
using Xunit;

// "Set up Lid Awake": the model, the QR code and the one-window rule, apart from the Form (as
// the Mac tests SetupModel). The Form itself is not built here.
public class SetupTests : IDisposable
{
    const string Host = "desk-pc.tail1234.ts.net";
    const string Good = "correct horse battery";

    const string RunningJson =
        "{\"BackendState\":\"Running\",\"CertDomains\":[\"desk-pc.tail1234.ts.net\"]," +
        "\"Self\":{\"DNSName\":\"Desk-PC.tail1234.ts.net.\",\"UserID\":4242}," +
        "\"User\":{\"4242\":{\"LoginName\":\"Owner@Example.com\"}}}";

    readonly List<string> folders = new();

    Settings NewSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        folders.Add(folder);
        return new Settings(folder);
    }

    public void Dispose()
    {
        foreach (var folder in folders)
        {
            try { Directory.Delete(folder, true); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    Auth NewAuth() => new(NewSettings(), iterations: 1000);

    static SetupModel Model(Auth auth, Tailnet.SetupState state, string? host = Host, List<string>? log = null,
                            bool awake = false) =>
        new(auth, log == null ? null : new Action<string>(log.Add), () => state, () => host, () => awake, () => false);

    static SetupModel.Checked Check(SetupModel model)
    {
        var task = model.StartCheck();
        Assert.NotNull(task);
        var result = task!.Result;
        model.Apply(result);
        return result;
    }

    // ---- The QR code ----

    // The picture, read back pixel by pixel with ZXing, holds exactly the page's address.
    static string Decode(Bitmap bitmap)
    {
        var rgb = new byte[bitmap.Width * bitmap.Height * 3];
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                int i = (y * bitmap.Width + x) * 3;
                rgb[i] = c.R;
                rgb[i + 1] = c.G;
                rgb[i + 2] = c.B;
            }
        var source = new ZXing.RGBLuminanceSource(rgb, bitmap.Width, bitmap.Height, ZXing.RGBLuminanceSource.BitmapFormat.RGB24);
        var result = new ZXing.QrCode.QRCodeReader().decode(new ZXing.BinaryBitmap(new ZXing.Common.HybridBinarizer(source)));
        Assert.NotNull(result);
        return result.Text;
    }

    [Fact]
    public void QrCodeHoldsThePageAddress()
    {
        var model = Model(NewAuth(), new(true, true, true));
        Check(model);
        Assert.Equal("https://" + Host + "/", model.PageUrl);
        using var bitmap = QrImage.Render(model.PageUrl, QrImage.TargetSize);
        Assert.NotNull(bitmap);
        Assert.Equal(model.PageUrl, Decode(bitmap!));
    }

    [Fact]
    public void QrCodeHasSharpWholePixelModulesOnWhite()
    {
        var url = "https://" + Host + "/";
        var modules = QrImage.Modules(url)!;
        int n = modules.GetLength(0);
        using var bitmap = QrImage.Render(url, QrImage.TargetSize)!;
        int scale = QrImage.Scale(n, QrImage.TargetSize);
        Assert.Equal(n * scale, bitmap.Width);
        Assert.Equal(bitmap.Width, bitmap.Height);
        Assert.InRange(bitmap.Width, QrImage.TargetSize * 3 / 4, QrImage.TargetSize * 5 / 4);
        // The 4-module quiet zone is white all round; every pixel is pure black or pure white.
        for (int i = 0; i < 4; i++)
            for (int j = 0; j < n; j++)
                Assert.False(modules[i, j] || modules[n - 1 - i, j] || modules[j, i] || modules[j, n - 1 - i]);
        for (int y = 0; y < bitmap.Height; y++)
            for (int x = 0; x < bitmap.Width; x++)
            {
                var c = bitmap.GetPixel(x, y);
                Assert.Equal(modules[y / scale, x / scale] ? 0 : 255, (int)c.R);
                Assert.True(c.R == c.G && c.G == c.B);
            }
    }

    // Faded (setup not finished): the same modules, light grey on white.
    [Fact]
    public void FadedCodeIsTheSameCodeInGrey()
    {
        var url = "https://" + Host + "/";
        var modules = QrImage.Modules(url)!;
        int scale = QrImage.Scale(modules.GetLength(0), QrImage.TargetSize);
        using var bitmap = QrImage.Render(url, QrImage.TargetSize, faded: true)!;
        for (int y = 0; y < bitmap.Height; y += scale)
            for (int x = 0; x < bitmap.Width; x += scale)
                Assert.Equal(modules[y / scale, x / scale] ? 191 : 255, (int)bitmap.GetPixel(x, y).R);
    }

    [Fact]
    public void NoAddressGivesNoCode()
    {
        Assert.Null(QrImage.Render(null, QrImage.TargetSize));
        Assert.Null(QrImage.Render("", QrImage.TargetSize));
        Assert.Null(SetupModel.PageAddress(null));
        Assert.Null(SetupModel.PageAddress(" "));

        // Not signed in: no address, even when a name is remembered from before.
        var model = Model(NewAuth(), new(true, false, false));
        Check(model);
        Assert.Null(model.Host);
        Assert.Null(model.PageUrl);
        Assert.False(model.Ready);

        // Signed in, but the name isn't known yet.
        var unknown = Model(NewAuth(), new(true, true, true), host: null);
        Check(unknown);
        Assert.Null(unknown.PageUrl);
    }

    // ---- The Tailscale rows, from the one status parser (Tailnet) ----

    [Fact]
    public void TailscaleCheckReadsSignedInAndHttps()
    {
        var settings = NewSettings();
        var t = new Tailnet(settings, _ => RunningJson);
        Assert.Equal(new Tailnet.SetupState(true, true, true), t.CheckSetup());
        // It learns the name as Refresh does, the same name the QR code shows.
        Assert.Equal(Host, t.Host);

        var noCerts = new Tailnet(NewSettings(), _ => RunningJson.Replace("[\"desk-pc.tail1234.ts.net\"]", "[]"));
        Assert.Equal(new Tailnet.SetupState(true, true, false), noCerts.CheckSetup());
        var signedOut = new Tailnet(NewSettings(), _ => "{\"BackendState\":\"NeedsLogin\"}");
        Assert.Equal(new Tailnet.SetupState(true, false, false), signedOut.CheckSetup());
        var broken = new Tailnet(NewSettings(), _ => "not json");
        Assert.Equal(new Tailnet.SetupState(true, false, false), broken.CheckSetup());
        var missing = Tailnet.WithCli(NewSettings(), _ => new Tailnet.CliResult(false, "", Missing: true));
        Assert.Equal(new Tailnet.SetupState(false, false, false), missing.CheckSetup());
    }

    [Fact]
    public async Task OneTailscaleCheckAtATime()
    {
        var gate = new ManualResetEventSlim();
        int runs = 0;
        var model = new SetupModel(NewAuth(), null, () => { Interlocked.Increment(ref runs); gate.Wait(5000); return new(true, true, true); },
            () => Host, () => false, () => false);
        var first = model.StartCheck();
        Assert.NotNull(first);
        Assert.Null(model.StartCheck());  // still running
        gate.Set();
        model.Apply(await first!);
        Assert.Equal(Host, model.Host);
        var second = model.StartCheck();  // allowed again once applied
        Assert.NotNull(second);
        model.Apply(await second!);
        Assert.Equal(2, runs);
    }

    // ---- The phone password row: the same setter as "Set phone password…" ----

    [Fact]
    public void PasswordRowUsesTheSharedSetter()
    {
        var auth = NewAuth();
        var log = new List<string>();
        var model = Model(auth, new(true, true, true), log: log);
        Assert.False(model.HasPassword);
        Assert.Contains($"At least {Auth.MinPasswordLength} characters", model.PasswordDetail);

        var shortOne = new string('x', Auth.MinPasswordLength - 1);
        Assert.Equal($"Use at least {Auth.MinPasswordLength} characters.", model.SavePassword(shortOne, shortOne));
        Assert.Equal(PhonePassword.Save(auth, shortOne, shortOne, null), model.SavePassword(shortOne, shortOne));
        Assert.Equal("The two passwords do not match.", model.SavePassword(Good, Good + "!"));
        Assert.False(auth.HasPassword);
        Assert.Empty(log);

        // A first password, then a phone signs in; a new password from the setup row signs it out.
        Assert.Null(model.SavePassword(Good, Good));
        Assert.True(model.HasPassword);
        var signedIn = auth.Login(Good, "iPhone");
        Assert.True(auth.IsValid(signedIn.Token));
        int pushRemoved = 0;
        auth.PasswordSet += () => pushRemoved++;
        var exactlyMin = new string('y', Auth.MinPasswordLength);
        Assert.Null(model.SavePassword(exactlyMin, exactlyMin));
        Assert.False(auth.IsValid(signedIn.Token));
        Assert.Equal(1, pushRemoved);  // the alert phones are forgotten too (WebPush.RemoveAll)
        Assert.Equal(new[] { PhonePassword.LogLine, PhonePassword.LogLine }, log);
        Assert.Equal("Phone password changed on the PC", PhonePassword.LogLine);
        Assert.Equal(SetupModel.PasswordSet, model.PasswordDetail);
        Assert.Equal("Set. Change it from the tray menu.", model.PasswordDetail);
    }

    // ---- Opening ----

    [Fact]
    public void OpensByItselfOnlyWithoutAPassword()
    {
        Assert.True(SetupModel.OpensAtStart(hasPassword: false));
        Assert.False(SetupModel.OpensAtStart(hasPassword: true));
        var auth = NewAuth();
        Assert.True(SetupModel.OpensAtStart(auth.HasPassword));
        auth.SetPassword(Good);
        Assert.False(SetupModel.OpensAtStart(auth.HasPassword));
    }

    sealed class FakeWindow
    {
        public bool Closed;
        public int Fronted;
    }

    [Fact]
    public void OnlyOneWindowEverExists()
    {
        int made = 0;
        var windows = new SingleWindow<FakeWindow>(() => { made++; return new FakeWindow(); }, w => !w.Closed, w => w.Fronted++);
        Assert.Null(windows.Current);
        var first = windows.Show();
        var again = windows.Show();
        Assert.Same(first, again);
        Assert.Equal(1, made);
        Assert.Equal(2, first.Fronted);  // opening it again brings it to the front
        Assert.Same(first, windows.Current);

        // Closed: the next open makes a new one, and there is still only one.
        first.Closed = true;
        Assert.Null(windows.Current);
        var second = windows.Show();
        Assert.NotSame(first, second);
        Assert.Same(second, windows.Show());
        Assert.Equal(2, made);
    }

    // ---- What the steps say ----

    [Fact]
    public void TurnOnButtonOnlyWhileOffAndReady()
    {
        var auth = NewAuth();
        auth.SetPassword(Good);
        var off = Model(auth, new(true, true, true));
        Check(off);
        Assert.True(off.Ready);
        Assert.True(off.ShowsTurnOn);
        Assert.Equal("Turn Lid Awake on: the page only opens while it's on.", off.AwakeStep);

        var on = Model(auth, new(true, true, true), awake: true);
        Check(on);
        Assert.False(on.ShowsTurnOn);
        Assert.Equal("Lid Awake is on, so the page is open.", on.AwakeStep);

        var noHttps = Model(auth, new(true, true, false));
        Check(noHttps);
        Assert.False(noHttps.ShowsTurnOn);
        Assert.StartsWith("In Tailscale's settings, turn on MagicDNS and HTTPS Certificates", noHttps.AddressDetail);
    }
}
