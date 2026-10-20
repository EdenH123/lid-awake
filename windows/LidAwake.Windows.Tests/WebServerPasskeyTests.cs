using System.Text.Json;
using LidAwake;
using Xunit;

// The four passkey routes end to end (docs/API.md 5.3), with SoftwarePhone acting as the phone:
// it builds the attestation object and signs the sign-ins with a software P-256 key.
public partial class WebServerTests
{
    const string PasskeyLoginFailed = "Face ID sign-in didn't work. Use your password.";
    const string PasskeyRegisterFailed = "Couldn't turn on Face ID. Try again.";

    async Task<(string Challenge, string RpId, string[] Ids)> RegisterOptions(string cookie, string password = Good)
    {
        var r = await Post("/passkey/register-options", cookie, $"{{\"password\":\"{password}\"}}");
        Assert.Equal(200, r.Code);
        var j = Json(r);
        return (j.GetProperty("challenge").GetString()!, j.GetProperty("rpId").GetString()!,
                j.GetProperty("ids").EnumerateArray().Select(i => i.GetString()!).ToArray());
    }

    static string RegisterBody(byte[] clientData, byte[] attestation) =>
        JsonSerializer.Serialize(new { clientData = Passkeys.Base64Url(clientData), attestation = Passkeys.Base64Url(attestation) });

    static string LoginBody(string id, byte[] clientData, byte[] authData, byte[] signature) =>
        JsonSerializer.Serialize(new
        {
            id,
            clientData = Passkeys.Base64Url(clientData),
            authenticatorData = Passkeys.Base64Url(authData),
            signature = Passkeys.Base64Url(signature),
        });

    // Adds the phone's key through the routes, signed in with `cookie`.
    async Task AddPasskey(string cookie, SoftwarePhone phone, string? ua = IPhone)
    {
        var (challenge, _, _) = await RegisterOptions(cookie);
        var (cd, att) = phone.Register(challenge);
        var r = await Post("/passkey/register", cookie, RegisterBody(cd, att), ua);
        Assert.Equal(204, r.Code);
    }

    async Task<string> LoginChallenge()
    {
        var r = await Send("GET", "/passkey/login-options");
        Assert.Equal(200, r.Code);
        return Json(r).GetProperty("challenge").GetString()!;
    }

    // A passkey sign-in with no session, as from the lock screen.
    async Task<Reply> PasskeyLogin(SoftwarePhone phone, uint count = 0, string? ua = IPhone)
    {
        var (cd, ad, sig) = phone.SignIn(await LoginChallenge(), count: count);
        return await Send("POST", "/passkey/login", lid: true, json: LoginBody(phone.Id, cd, ad, sig), ua: ua);
    }

    static string CookieOf(Reply r) => r.Response.Headers.GetValues("Set-Cookie").Single().Split(';')[0];

    [Fact]
    public async Task PasskeyLoginOptionsArePublicAndForThisHost()
    {
        var r = await Send("GET", "/passkey/login-options");
        Assert.Equal(200, r.Code);
        Assert.Equal("application/json", r.Header("Content-Type"));
        var j = Json(r);
        Assert.Equal(new[] { "challenge", "rpId" }, j.EnumerateObject().Select(p => p.Name));
        Assert.Equal(TailHost, j.GetProperty("rpId").GetString());
        Assert.Matches("^[A-Za-z0-9_-]{43}$", j.GetProperty("challenge").GetString()!);
        Assert.NotEqual(j.GetProperty("challenge").GetString(), Json(await Send("GET", "/passkey/login-options")).GetProperty("challenge").GetString());
        // While off it is closed like everything else.
        on = false;
        Assert.Equal(423, (await Send("GET", "/passkey/login-options")).Code);
    }

    [Fact]
    public async Task PasskeyRegisterOptionsNeedASessionAndThePasswordAgain()
    {
        auth.SetPassword(Good);
        var locked = await Send("POST", "/passkey/register-options", lid: true, json: $"{{\"password\":\"{Good}\"}}");
        Assert.Equal(401, locked.Code);
        Assert.Equal("Locked", locked.Body);
        Assert.Equal(401, (await Send("POST", "/passkey/register", lid: true, json: "{}")).Code);

        var cookie = await SignIn();
        var wrong = await Post("/passkey/register-options", cookie, "{\"password\":\"not it at all\"}");
        Assert.Equal(401, wrong.Code);
        Assert.Equal("Wrong password.", wrong.Body);
        Assert.Equal(401, (await Post("/passkey/register-options", cookie, "{}")).Code);
        Assert.Equal(403, (await Send("POST", "/passkey/register-options", cookie: cookie, json: $"{{\"password\":\"{Good}\"}}")).Code);

        var ok = await Post("/passkey/register-options", cookie, $"{{\"password\":\"{Good}\"}}");
        Assert.Equal(200, ok.Code);
        Assert.Equal("application/json", ok.Header("Content-Type"));
        var j = Json(ok);
        Assert.Equal(new[] { "challenge", "rpId", "ids" }, j.EnumerateObject().Select(p => p.Name));
        Assert.Equal(TailHost, j.GetProperty("rpId").GetString());
        Assert.Equal(0, j.GetProperty("ids").GetArrayLength());
    }

    [Fact]
    public async Task PasskeyRegisterOptionsShareTheLockoutWithLogin()
    {
        var cookie = await SignIn();
        for (int i = 0; i < 4; i++)
            Assert.Equal(401, (await Send("POST", "/login", lid: true, json: "{\"password\":\"wrong password\"}")).Code);
        var fifth = await Post("/passkey/register-options", cookie, "{\"password\":\"wrong password\"}");
        Assert.Equal(429, fifth.Code);
        Assert.Equal("Too many wrong tries. Try again in 60 seconds.", fifth.Body);
        // Locked for both, even with the right password.
        Assert.Equal(429, (await Send("POST", "/login", lid: true, json: $"{{\"password\":\"{Good}\"}}")).Code);
        var again = await Post("/passkey/register-options", cookie, $"{{\"password\":\"{Good}\"}}");
        Assert.Equal(429, again.Code);
        Assert.StartsWith("Too many wrong tries. Try again in ", again.Body);
    }

    [Fact]
    public async Task APasskeyIsAddedAndSignsInEndToEnd()
    {
        var password = await SignIn(IPhone);
        announced.Clear();
        Assert.False(Json(await Send("GET", "/auth")).GetProperty("faceID").GetBoolean());

        using var phone = new SoftwarePhone(TailHost);
        var (challenge, rpId, ids) = await RegisterOptions(password);
        Assert.Equal(TailHost, rpId);
        Assert.Empty(ids);
        var (cd, att) = phone.Register(challenge);
        var added = await Post("/passkey/register", password, RegisterBody(cd, att), IPhone);
        Assert.Equal(204, added.Code);
        Assert.Equal("", added.Body);

        // Announced as a security event, like a sign-in, and written to the activity log.
        var note = Assert.Single(announced);
        Assert.Equal("Face ID or fingerprint added to Lid Awake", note.Title);
        Assert.Equal("On an iPhone. If this wasn't you, remove it from the Lid Awake menu on your PC.", note.Body);
        Assert.Contains(Lines(await Send("GET", "/activity", cookie: password)), l => l.Contains("Face ID or fingerprint added to Lid Awake"));

        Assert.True(Json(await Send("GET", "/auth")).GetProperty("faceID").GetBoolean());
        Assert.Equal(new[] { phone.Id }, (await RegisterOptions(password)).Ids);
        var stored = Assert.Single(auth.Passkeys.List);
        Assert.Equal("iPhone", stored.Device);

        // Signing in from the lock screen, with no session.
        announced.Clear();
        var r = await PasskeyLogin(phone);
        Assert.Equal(204, r.Code);
        Assert.Equal("", r.Body);
        var setCookie = r.Response.Headers.GetValues("Set-Cookie").Single();
        Assert.Matches("^__Host-la_session=[0-9a-f]{64}; Path=/; Max-Age=604800; HttpOnly; SameSite=Strict; Secure$", setCookie);
        // Passkey sign-ins are not announced (as on the Mac).
        Assert.Empty(announced);

        var faceCookie = setCookie.Split(';')[0];
        Assert.Equal(200, (await Send("GET", "/status", cookie: faceCookie)).Code);
        Assert.True(Json(await Send("GET", "/auth", cookie: faceCookie)).GetProperty("loggedIn").GetBoolean());
        var list = SessionList(await Send("GET", "/sessions", cookie: faceCookie));
        Assert.Equal(2, list.Length);
        var mine = list.Single(x => x.GetProperty("current").GetBoolean());
        Assert.True(mine.GetProperty("faceID").GetBoolean());
        Assert.Equal("iPhone", mine.GetProperty("device").GetString());
        Assert.False(list.Single(x => !x.GetProperty("current").GetBoolean()).GetProperty("faceID").GetBoolean());

        // Counters: a synced passkey (0) again is fine; a counting one only goes up.
        Assert.Equal(204, (await PasskeyLogin(phone)).Code);
        Assert.Equal(204, (await PasskeyLogin(phone, count: 3)).Code);
        Assert.Equal(401, (await PasskeyLogin(phone, count: 3)).Code);
    }

    [Fact]
    public async Task APasskeySignInResetsTheWrongPasswordCount()
    {
        var cookie = await SignIn();
        using var phone = new SoftwarePhone(TailHost);
        await AddPasskey(cookie, phone);
        for (int i = 0; i < 4; i++)
            Assert.Equal(401, (await Send("POST", "/login", lid: true, json: "{\"password\":\"wrong password\"}")).Code);
        Assert.Equal(204, (await PasskeyLogin(phone)).Code);
        // Counting starts again: the next wrong try is the first, not the fifth.
        Assert.Equal(401, (await Send("POST", "/login", lid: true, json: "{\"password\":\"wrong password\"}")).Code);
    }

    [Fact]
    public async Task EveryFailedPasskeySignInIs401AndGivesNoSession()
    {
        var cookie = await SignIn();
        using var phone = new SoftwarePhone(TailHost);
        await AddPasskey(cookie, phone);
        announced.Clear();

        async Task Refused(string json)
        {
            var r = await Send("POST", "/passkey/login", lid: true, json: json);
            Assert.Equal(401, r.Code);
            Assert.Equal(PasskeyLoginFailed, r.Body);
            Assert.Equal("text/plain; charset=utf-8", r.Header("Content-Type"));
            Assert.Null(r.Header("Set-Cookie"));
        }

        await Refused("{}");
        await Refused("not json");
        var (cd, ad, sig) = phone.SignIn(await LoginChallenge());
        await Refused(LoginBody(phone.Id, cd, ad, sig).Replace("\"signature\":\"", "\"signature\":\"*"));  // not base64url
        await Refused(JsonSerializer.Serialize(new { id = 5, clientData = Passkeys.Base64Url(cd), authenticatorData = Passkeys.Base64Url(ad), signature = Passkeys.Base64Url(sig) }));
        // That challenge was not used by the refusals above (they stopped before the checks).
        var tampered = (byte[])sig.Clone();
        tampered[^1] ^= 1;
        await Refused(LoginBody(phone.Id, cd, ad, tampered));
        // Used up by the try above, so even the real signature fails now.
        await Refused(LoginBody(phone.Id, cd, ad, sig));

        var (cd2, ad2, sig2) = phone.SignIn(await LoginChallenge());
        await Refused(LoginBody("bm8tc3VjaC1rZXk", cd2, ad2, sig2));  // an unknown id
        var (cd3, ad3, sig3) = phone.SignIn(await LoginChallenge(), origin: "https://evil.example");
        await Refused(LoginBody(phone.Id, cd3, ad3, sig3));
        var (cd4, ad4, sig4) = phone.SignIn(await LoginChallenge(), rp: "evil.ts.net");
        await Refused(LoginBody(phone.Id, cd4, ad4, sig4));
        var (cd5, ad5, sig5) = phone.SignIn(await LoginChallenge(), flags: SoftwarePhone.UP);
        await Refused(LoginBody(phone.Id, cd5, ad5, sig5));
        var (cd6, ad6, sig6) = phone.SignIn((await RegisterOptions(cookie)).Challenge);  // a create challenge
        await Refused(LoginBody(phone.Id, cd6, ad6, sig6));

        // A plain web form cannot post it (step 8 after the public check).
        var (cd7, ad7, sig7) = phone.SignIn(await LoginChallenge());
        Assert.Equal(403, (await Send("POST", "/passkey/login", json: LoginBody(phone.Id, cd7, ad7, sig7))).Code);
        Assert.Empty(announced);
        Assert.Single(SessionList(await Send("GET", "/sessions", cookie: cookie)));
    }

    [Fact]
    public async Task APasskeySignInNeedsAPasswordToExist()
    {
        var cookie = await SignIn();
        using var phone = new SoftwarePhone(TailHost);
        await AddPasskey(cookie, phone);
        settings.Set<string>("pwHash", null);
        var r = await PasskeyLogin(phone);
        Assert.Equal(401, r.Code);
        Assert.Equal(PasskeyLoginFailed, r.Body);
    }

    [Fact]
    public async Task EveryFailedRegistrationIs400AndNotAnnounced()
    {
        var cookie = await SignIn();
        announced.Clear();
        using var phone = new SoftwarePhone(TailHost);

        async Task Refused(string json)
        {
            var r = await Post("/passkey/register", cookie, json);
            Assert.Equal(400, r.Code);
            Assert.Equal(PasskeyRegisterFailed, r.Body);
        }

        await Refused("{}");
        await Refused("{\"clientData\":\"***\",\"attestation\":\"AA\"}");
        var (cd, att) = phone.Register("made-up-challenge");
        await Refused(RegisterBody(cd, att));
        var (cd1, att1) = phone.Register(await LoginChallenge());  // a get challenge
        await Refused(RegisterBody(cd1, att1));
        var (cd2, att2) = phone.Register((await RegisterOptions(cookie)).Challenge, flags: SoftwarePhone.UP | SoftwarePhone.AT);
        await Refused(RegisterBody(cd2, att2));
        var (cd3, att3) = phone.Register((await RegisterOptions(cookie)).Challenge, rp: "evil.ts.net");
        await Refused(RegisterBody(cd3, att3));
        var (x, y) = phone.Point;
        var (cd4, att4) = phone.Register((await RegisterOptions(cookie)).Challenge, cose: SoftwarePhone.Cose(x, y, alg: -8));
        await Refused(RegisterBody(cd4, att4));

        Assert.Empty(announced);
        Assert.False(Json(await Send("GET", "/auth")).GetProperty("faceID").GetBoolean());

        // The same key twice: the second is refused, the first stays.
        await AddPasskey(cookie, phone);
        announced.Clear();
        var (cd5, att5) = phone.Register((await RegisterOptions(cookie)).Challenge);
        await Refused(RegisterBody(cd5, att5));
        Assert.Empty(announced);
        Assert.Equal(1, auth.Passkeys.Count);
    }

    [Fact]
    public async Task ARegistrationFromABrowserSaysABrowser()
    {
        var cookie = await SignIn();
        announced.Clear();
        using var phone = new SoftwarePhone(TailHost);
        await AddPasskey(cookie, phone, ua: null);
        var note = Assert.Single(announced);
        Assert.Equal("On a browser. If this wasn't you, remove it from the Lid Awake menu on your PC.", note.Body);
        Assert.Equal("Browser", Assert.Single(auth.Passkeys.List).Device);
    }

    [Fact]
    public async Task ANewPasswordOnThePcRemovesEveryPasskey()
    {
        var cookie = await SignIn();
        using var phone = new SoftwarePhone(TailHost);
        await AddPasskey(cookie, phone);
        var face = CookieOf(await PasskeyLogin(phone));

        auth.SetPassword("another good password");
        Assert.False(Json(await Send("GET", "/auth")).GetProperty("faceID").GetBoolean());
        Assert.Equal(401, (await Send("GET", "/status", cookie: face)).Code);
        var r = await PasskeyLogin(phone);
        Assert.Equal(401, r.Code);
        Assert.Equal(PasskeyLoginFailed, r.Body);
    }

    // What the tray's "Face ID and fingerprint" item does when a key is clicked.
    [Fact]
    public async Task RemovingAKeySignsOutOnlyTheDevicesThatUsedIt()
    {
        var password = await SignIn();
        using var phone = new SoftwarePhone(TailHost);
        using var other = new SoftwarePhone(TailHost);
        await AddPasskey(password, phone);
        await AddPasskey(password, other);
        var face = CookieOf(await PasskeyLogin(phone));
        var otherFace = CookieOf(await PasskeyLogin(other));

        auth.Passkeys.Remove(phone.Id);
        Assert.Equal(1, auth.SignOutPasskey(phone.Id));
        Assert.Equal(401, (await Send("GET", "/status", cookie: face)).Code);
        Assert.Equal(200, (await Send("GET", "/status", cookie: otherFace)).Code);
        Assert.Equal(200, (await Send("GET", "/status", cookie: password)).Code);
        Assert.Equal(401, (await PasskeyLogin(phone)).Code);
        Assert.Equal(204, (await PasskeyLogin(other)).Code);
        Assert.True(Json(await Send("GET", "/auth")).GetProperty("faceID").GetBoolean());
    }

    [Fact]
    public async Task StatusListsPasskeys()
    {
        var cookie = await SignIn();
        statusAnswer = () => new StatusInfo(new FakeReadings()).Build(true);
        var features = Json(await Send("GET", "/status", cookie: cookie)).GetProperty("features").EnumerateArray().Select(f => f.GetString());
        Assert.Contains("passkeys", features);
    }
}
