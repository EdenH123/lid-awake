using System.Security.Cryptography;
using System.Text;
using LidAwake;
using Xunit;

// Passkeys.cs against a software phone (SoftwarePhone). The first group is
// tests/passkeys/main.swift, the Mac's own vectors, in the same order.
public class PasskeysTests : IDisposable
{
    const string Host = "your-mac.your-tailnet.ts.net";

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Settings settings;
    readonly Passkeys passkeys;
    readonly SoftwarePhone phone = new(Host);
    DateTimeOffset clock = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    public PasskeysTests()
    {
        settings = new Settings(folder);
        passkeys = new Passkeys(settings, () => clock);
    }

    public void Dispose()
    {
        phone.Dispose();
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    bool Register(SoftwarePhone p, string challenge, byte flags = SoftwarePhone.UP | SoftwarePhone.UV | SoftwarePhone.AT,
                  uint count = 0, string? origin = null, string? rp = null, byte[]? cose = null, string host = Host, Passkeys? store = null)
    {
        var (cd, att) = p.Register(challenge, flags, count, origin, rp, cose);
        return (store ?? passkeys).Register(cd, att, host);
    }

    bool Register(string? challenge = null) => Register(phone, challenge ?? passkeys.Challenge("create"));

    bool SignIn(string challenge, string? origin = null, byte flags = SoftwarePhone.UP | SoftwarePhone.UV, uint count = 0,
                ECDsa? signer = null, string? rp = null, string type = "webauthn.get", SoftwarePhone? p = null, string purpose = "get")
    {
        var who = p ?? phone;
        var (cd, ad, sig) = who.SignIn(challenge, flags, count, origin, rp, type, signer);
        return passkeys.Verify(who.Id, cd, ad, sig, Host, purpose);
    }

    bool SignIn(uint count = 0) => SignIn(passkeys.Challenge("get"), count: count);

    // ---- tests/passkeys/main.swift ----

    [Fact]
    public void MacVectors()
    {
        var c1 = passkeys.Challenge("create");
        Assert.True(Register(phone, c1), "register");
        Assert.False(Register(phone, c1), "register reuse of challenge refused");
        Assert.False(Register(phone, passkeys.Challenge("create"), flags: SoftwarePhone.UP | SoftwarePhone.AT), "register without Face ID (no UV) refused");
        Assert.Equal(1, passkeys.Count);
        Assert.False(Register(phone, passkeys.Challenge("create")), "same key id can't be registered again (no swapping keys)");
        Assert.Equal(1, passkeys.Count);

        var g1 = passkeys.Challenge("get");
        Assert.True(SignIn(g1), "sign in");
        Assert.False(SignIn(g1), "same challenge twice refused");
        Assert.False(SignIn("abc"), "made-up challenge refused");
        Assert.False(SignIn(passkeys.Challenge("get"), origin: "https://evil.example"), "other website origin refused");
        Assert.False(SignIn(passkeys.Challenge("get"), rp: "evil.example"), "other rpId refused");
        using (var other = ECDsa.Create(ECCurve.NamedCurves.nistP256))
            Assert.False(SignIn(passkeys.Challenge("get"), signer: other), "wrong key refused");
        Assert.False(SignIn(passkeys.Challenge("get"), flags: SoftwarePhone.UP), "no Face ID (UV) refused");
        Assert.False(SignIn(passkeys.Challenge("create")), "create-challenge used to sign in refused");
        Assert.False(passkeys.Verify(phone.Id, Array.Empty<byte>(), Array.Empty<byte>(), Array.Empty<byte>(), "127.0.0.1"), "http host refused");
        Assert.True(SignIn(count: 5), "counter going up ok");
        Assert.False(SignIn(count: 3), "counter going back refused");
        passkeys.RemoveAll();
        Assert.Equal(0, passkeys.Count);
    }

    // ---- what is stored ----

    [Fact]
    public void StoresTheKeyAs04XYWithTheCounterTimeAndDevice()
    {
        var (cd, att) = phone.Register(passkeys.Challenge("create"), count: 7);
        Assert.True(passkeys.Register(cd, att, Host, "iPhone"));
        var stored = Assert.Single(settings.Get<List<Passkeys.Stored>>(Passkeys.StoreKey)!);
        Assert.Equal(phone.Id, stored.Id);
        Assert.Equal(65, stored.PublicKey.Length);
        Assert.Equal(phone.PublicKeyX963, stored.PublicKey);
        Assert.Equal(7u, stored.SignCount);
        Assert.Equal(clock.ToUnixTimeSeconds(), stored.Added);
        Assert.Equal("iPhone", stored.Device);
        Assert.Equal(new[] { phone.Id }, passkeys.CredentialIds);
        var info = Assert.Single(passkeys.List);
        Assert.Equal("iPhone", info.Device);
        Assert.Equal(clock, info.Added);
    }

    [Fact]
    public void KeysSurviveARestart()
    {
        Assert.True(Register());
        var again = new Passkeys(new Settings(folder), () => clock);
        Assert.Equal(1, again.Count);
        var (cd, ad, sig) = phone.SignIn(again.Challenge("get"));
        Assert.True(again.Verify(phone.Id, cd, ad, sig, Host));
    }

    [Fact]
    public void ANewPasswordRemovesEveryPasskey()
    {
        var auth = new Auth(settings, iterations: 1000, now: () => clock);
        auth.SetPassword("correct horse battery");
        var (cd, att) = phone.Register(auth.Passkeys.Challenge("create"));
        Assert.True(auth.Passkeys.Register(cd, att, Host));
        Assert.Equal(1, auth.Passkeys.Count);
        auth.SetPassword("another good password");
        Assert.Equal(0, auth.Passkeys.Count);
        var (gcd, ad, sig) = phone.SignIn(auth.Passkeys.Challenge("get"));
        Assert.False(auth.Passkeys.Verify(phone.Id, gcd, ad, sig, Host));
    }

    [Fact]
    public void RemoveForgetsOnlyThatKey()
    {
        using var second = new SoftwarePhone(Host);
        Assert.True(Register());
        Assert.True(Register(second, passkeys.Challenge("create")));
        passkeys.Remove(phone.Id);
        Assert.Equal(new[] { second.Id }, passkeys.CredentialIds);
        Assert.False(SignIn());
        Assert.True(SignIn(passkeys.Challenge("get"), p: second));
    }

    // ---- caps and never replacing ----

    [Fact]
    public void AtMost20Keys()
    {
        var phones = Enumerable.Range(0, 21).Select(_ => new SoftwarePhone(Host)).ToList();
        try
        {
            for (int i = 0; i < 20; i++) Assert.True(Register(phones[i], passkeys.Challenge("create")), $"key {i + 1}");
            Assert.Equal(20, passkeys.Count);
            Assert.False(Register(phones[20], passkeys.Challenge("create")), "the 21st is refused");
            Assert.Equal(20, passkeys.Count);
            passkeys.Remove(phones[0].Id);
            Assert.True(Register(phones[20], passkeys.Challenge("create")), "room again after one is removed");
        }
        finally { foreach (var p in phones) p.Dispose(); }
    }

    [Fact]
    public void AnExistingIdIsNeverReplacedByAnotherKey()
    {
        Assert.True(Register());
        using var attacker = new SoftwarePhone(Host, phone.CredentialId);
        Assert.False(Register(attacker, passkeys.Challenge("create")));
        Assert.Equal(phone.PublicKeyX963, Assert.Single(settings.Get<List<Passkeys.Stored>>(Passkeys.StoreKey)!).PublicKey);
        Assert.False(SignIn(passkeys.Challenge("get"), p: attacker));
        Assert.True(SignIn());
    }

    // ---- challenges ----

    [Fact]
    public void ChallengesAre32RandomBytesInBase64Url()
    {
        var a = passkeys.Challenge("get");
        var b = passkeys.Challenge("get");
        Assert.Matches("^[A-Za-z0-9_-]{43}$", a);
        Assert.NotEqual(a, b);
        Assert.Equal(32, Passkeys.FromBase64Url(a)!.Length);
    }

    [Fact]
    public void AChallengeLastsTwoMinutes()
    {
        Assert.True(Register());
        var c = passkeys.Challenge("get");
        clock += TimeSpan.FromSeconds(119);
        Assert.True(SignIn(c), "still good just before 2 minutes");
        var late = passkeys.Challenge("get");
        clock += TimeSpan.FromSeconds(120);
        Assert.False(SignIn(late), "expired at 2 minutes");
        var create = passkeys.Challenge("create");
        clock += TimeSpan.FromSeconds(121);
        using var second = new SoftwarePhone(Host);
        Assert.False(Register(second, create), "an expired create challenge is refused");
    }

    [Fact]
    public void AtMost32ChallengesAreKeptAndTheOldestGoes()
    {
        Assert.True(Register());
        var issued = new List<string>();
        for (int i = 0; i < 33; i++)
        {
            issued.Add(passkeys.Challenge("get"));
            clock += TimeSpan.FromMilliseconds(10);
        }
        Assert.False(SignIn(issued[0]), "the oldest was dropped");
        Assert.True(SignIn(issued[1]));
        Assert.True(SignIn(issued[32]));
    }

    [Fact]
    public void AChallengeIsBoundToItsPurposeAndUsedUpEvenWhenRefused()
    {
        Assert.True(Register());
        var camera = passkeys.Challenge("camera");
        Assert.False(SignIn(camera), "a camera challenge does not sign in");
        var get = passkeys.Challenge("get");
        Assert.False(SignIn(get, purpose: "camera"), "a get challenge does not open the camera");
        Assert.False(SignIn(get), "and it was used up");
        Assert.True(SignIn(passkeys.Challenge("camera"), purpose: "camera"));
        // A challenge that matched is gone even when a later check fails.
        var once = passkeys.Challenge("get");
        Assert.False(SignIn(once, flags: SoftwarePhone.UP));
        Assert.False(SignIn(once));
    }

    // ---- clientData tampered ----

    [Fact]
    public void ClientDataMustBeTheRightTypeOriginAndShape()
    {
        Assert.True(Register());
        Assert.False(SignIn(passkeys.Challenge("get"), type: "webauthn.create"), "create type to sign in");
        Assert.False(SignIn(passkeys.Challenge("get"), origin: "http://" + Host), "http origin");
        Assert.False(SignIn(passkeys.Challenge("get"), origin: "https://" + Host + ":8443"), "other port");
        Assert.False(SignIn(passkeys.Challenge("get"), origin: "https://evil." + Host), "a subdomain");

        bool Raw(string json)
        {
            var cd = Encoding.UTF8.GetBytes(json);
            var ad = phone.AuthData(SoftwarePhone.UP | SoftwarePhone.UV, 0, false);
            return passkeys.Verify(phone.Id, cd, ad, SoftwarePhone.Sign(ad, cd, phone.Key), Host);
        }
        var c = passkeys.Challenge("get");
        Assert.False(Raw("not json"));
        Assert.False(Raw("[]"));
        Assert.False(Raw($"{{\"type\":\"webauthn.get\",\"origin\":\"https://{Host}\"}}"), "no challenge");
        Assert.False(Raw($"{{\"type\":\"webauthn.get\",\"challenge\":\"{c}\",\"origin\":\"https://{Host}\",\"type\":\"webauthn.get\"}}"), "a name twice");
        Assert.False(Raw($"{{\"type\":\"webauthn.get\",\"challenge\":1,\"origin\":\"https://{Host}\"}}"), "challenge not text");
        // The browser may add fields of its own.
        Assert.True(Raw($"{{\"type\":\"webauthn.get\",\"challenge\":\"{c}\",\"origin\":\"https://{Host}\",\"crossOrigin\":false}}"));
    }

    [Fact]
    public void ClientDataChangedAfterSigningIsRefused()
    {
        Assert.True(Register());
        var c = passkeys.Challenge("get");
        var (cd, ad, sig) = phone.SignIn(c);
        var changed = SoftwarePhone.ClientData("webauthn.get", c, "https://" + Host).Concat(new byte[] { (byte)' ' }).ToArray();
        Assert.False(passkeys.Verify(phone.Id, changed, ad, sig, Host));
        Assert.True(cd.Length > 0);
    }

    // ---- authenticatorData tampered ----

    [Fact]
    public void AuthenticatorDataIsCheckedAndSigned()
    {
        Assert.True(Register());
        Assert.False(SignIn(passkeys.Challenge("get"), flags: SoftwarePhone.UV), "no user present");
        Assert.False(SignIn(passkeys.Challenge("get"), flags: 0), "no flags");
        Assert.True(SignIn(passkeys.Challenge("get"), flags: SoftwarePhone.UP | SoftwarePhone.UV | 0x18), "backup flags are fine");

        // Flags or counter changed after signing: the signature no longer matches.
        var (cd, ad, sig) = phone.SignIn(passkeys.Challenge("get"), count: 0);
        ad[36] = 9;
        Assert.False(passkeys.Verify(phone.Id, cd, ad, sig, Host), "counter changed");
        var (cd2, ad2, sig2) = phone.SignIn(passkeys.Challenge("get"), flags: SoftwarePhone.UP);
        ad2[32] |= SoftwarePhone.UV;
        Assert.False(passkeys.Verify(phone.Id, cd2, ad2, sig2, Host), "UV flag added");
        var (cd3, ad3, sig3) = phone.SignIn(passkeys.Challenge("get"));
        Assert.False(passkeys.Verify(phone.Id, cd3, ad3[..36], sig3, Host), "too short");
        var (cd4, ad4, sig4) = phone.SignIn(passkeys.Challenge("get"));
        ad4[0] ^= 1;
        Assert.False(passkeys.Verify(phone.Id, cd4, ad4, sig4, Host), "rp hash changed");
    }

    [Fact]
    public void TheRpIdMustBeThisHost()
    {
        Assert.True(Register());
        // Signed correctly, but for another site.
        Assert.False(SignIn(passkeys.Challenge("get"), rp: "evil.ts.net"));
        Assert.False(SignIn(passkeys.Challenge("get"), rp: Host.ToUpperInvariant()));
    }

    // ---- signature tampered ----

    [Fact]
    public void ABadSignatureIsRefused()
    {
        Assert.True(Register());
        foreach (var damage in new Func<byte[], byte[]>[]
                 {
                     s => { var t = (byte[])s.Clone(); t[^1] ^= 1; return t; },
                     s => { var t = (byte[])s.Clone(); t[10] ^= 0x80; return t; },
                     s => s[..^1],
                     s => s.Concat(new byte[] { 0 }).ToArray(),
                     _ => Array.Empty<byte>(),
                     _ => new byte[64],
                     _ => new byte[] { 0x30, 0x00 },
                 })
        {
            var (cd, ad, sig) = phone.SignIn(passkeys.Challenge("get"));
            Assert.False(passkeys.Verify(phone.Id, cd, ad, damage(sig), Host));
        }
        // The raw r || s form (what WebCrypto makes) is not the DER the phone sends.
        var c = passkeys.Challenge("get");
        var cdRaw = SoftwarePhone.ClientData("webauthn.get", c, "https://" + Host);
        var adRaw = phone.AuthData(SoftwarePhone.UP | SoftwarePhone.UV, 0, false);
        var p1363 = phone.Key.SignData(adRaw.Concat(SHA256.HashData(cdRaw)).ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        Assert.False(passkeys.Verify(phone.Id, cdRaw, adRaw, p1363, Host));
        Assert.True(SignIn());
    }

    [Fact]
    public void AnUnknownCredentialIdIsRefused()
    {
        Assert.True(Register());
        var (cd, ad, sig) = phone.SignIn(passkeys.Challenge("get"));
        Assert.False(passkeys.Verify("bm90LWEta2V5", cd, ad, sig, Host));
    }

    // ---- counter ----

    [Fact]
    public void ZeroAndZeroIsAlwaysAccepted()
    {
        Assert.True(Register());
        for (int i = 0; i < 3; i++) Assert.True(SignIn(count: 0), $"sign-in {i + 1} with counter 0");
    }

    [Fact]
    public void ACounterOnlyGoesUp()
    {
        Assert.True(Register());
        Assert.True(SignIn(count: 1));
        Assert.False(SignIn(count: 1), "the same counter again");
        Assert.False(SignIn(count: 0), "back to 0 once it has counted");
        Assert.True(SignIn(count: 10));
        Assert.Equal(10u, Assert.Single(settings.Get<List<Passkeys.Stored>>(Passkeys.StoreKey)!).SignCount);
        Assert.True(SignIn(count: uint.MaxValue));
        Assert.False(SignIn(count: uint.MaxValue));
    }

    [Fact]
    public void ACounterStartedAtRegistrationIsKept()
    {
        Assert.True(Register(phone, passkeys.Challenge("create"), count: 7));
        Assert.False(SignIn(count: 0));
        Assert.False(SignIn(count: 7));
        Assert.True(SignIn(count: 8));
    }

    [Fact]
    public void ARefusedSignatureDoesNotMoveTheCounter()
    {
        Assert.True(Register());
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        Assert.False(SignIn(passkeys.Challenge("get"), count: 100, signer: other));
        Assert.Equal(0u, Assert.Single(settings.Get<List<Passkeys.Stored>>(Passkeys.StoreKey)!).SignCount);
        Assert.True(SignIn(count: 1));
    }

    // ---- registering ----

    [Fact]
    public void RegisterChecksTheHostOriginRpIdAndFlags()
    {
        Assert.False(Register(phone, passkeys.Challenge("create"), host: "127.0.0.1"), "not a ts.net host");
        Assert.False(Register(phone, passkeys.Challenge("create"), origin: "https://evil.example"), "other origin");
        Assert.False(Register(phone, passkeys.Challenge("create"), rp: "evil.example"), "other rpId");
        Assert.False(Register(phone, passkeys.Challenge("create"), flags: SoftwarePhone.UV | SoftwarePhone.AT), "no UP");
        Assert.False(Register(phone, passkeys.Challenge("create"), flags: SoftwarePhone.UP | SoftwarePhone.UV), "no attested credential");
        Assert.False(Register(phone, passkeys.Challenge("get")), "a get challenge");
        Assert.False(Register(phone, "made-up"), "a made-up challenge");
        var (cd, att) = phone.Register(passkeys.Challenge("create"));
        var getType = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(cd).Replace("webauthn.create", "webauthn.get"));
        Assert.False(passkeys.Register(getType, att, Host), "a get type");
        Assert.Equal(0, passkeys.Count);
        Assert.True(Register());
    }

    [Fact]
    public void OnlyAnEs256P256CoseKeyIsTaken()
    {
        var (x, y) = phone.Point;
        byte[] Bad(int kty = 2, int alg = -7, int crv = 1) => SoftwarePhone.Cose(x, y, kty, alg, crv);
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: Bad(kty: 1)), "OKP");
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: Bad(kty: 3)), "RSA");
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: Bad(alg: -8)), "EdDSA");
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: Bad(alg: -257)), "RS256");
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: Bad(crv: 2)), "P-384");
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: SoftwarePhone.Cose(x[..31], y)), "31-byte x");
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: SoftwarePhone.Cose(x, y.Concat(new byte[] { 0 }).ToArray())), "33-byte y");
        // Without y: a map of four.
        var noY = new byte[] { 0xA4, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21 }.Concat(SoftwarePhone.CborBytes(x)).ToArray();
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: noY), "no y");
        // x as text, not bytes.
        var textX = new byte[] { 0xA5, 0x01, 0x02, 0x03, 0x26, 0x20, 0x01, 0x21 }.Concat(SoftwarePhone.CborText(new string('a', 32)))
            .Concat(new byte[] { 0x22 }).Concat(SoftwarePhone.CborBytes(y)).ToArray();
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: textX), "x as text");
        // A point that is not on the curve.
        var offCurve = (byte[])y.Clone();
        offCurve[31] ^= 1;
        Assert.False(Register(phone, passkeys.Challenge("create"), cose: SoftwarePhone.Cose(x, offCurve)), "not on the curve");
        Assert.Equal(0, passkeys.Count);
        // Extensions after the key are fine.
        var withExtensions = phone.Cose().Concat(new byte[] { 0xA0 }).ToArray();
        Assert.True(Register(phone, passkeys.Challenge("create"), cose: withExtensions));
    }

    [Fact]
    public void TheAttestationObjectMustBeCborWithAuthData()
    {
        var ad = phone.AuthData(SoftwarePhone.UP | SoftwarePhone.UV | SoftwarePhone.AT, 0, true);
        bool Try(byte[] att) => passkeys.Register(SoftwarePhone.ClientData("webauthn.create", passkeys.Challenge("create"), "https://" + Host), att, Host);
        Assert.False(Try(Array.Empty<byte>()), "empty");
        Assert.False(Try(SoftwarePhone.CborBytes(ad)), "not a map");
        Assert.False(Try(SoftwarePhone.Attestation(ad).Concat(new byte[] { 0 }).ToArray()), "trailing bytes");
        Assert.False(Try(SoftwarePhone.Attestation(ad)[..^1]), "cut short");
        var asText = new byte[] { 0xA1 }.Concat(SoftwarePhone.CborText("authData")).Concat(SoftwarePhone.CborText("x")).ToArray();
        Assert.False(Try(asText), "authData as text");
        var other = new byte[] { 0xA1 }.Concat(SoftwarePhone.CborText("authdata")).Concat(SoftwarePhone.CborBytes(ad)).ToArray();
        Assert.False(Try(other), "no authData");
        var twice = new byte[] { 0xA2 }.Concat(SoftwarePhone.CborText("authData")).Concat(SoftwarePhone.CborBytes(ad))
            .Concat(SoftwarePhone.CborText("authData")).Concat(SoftwarePhone.CborBytes(ad)).ToArray();
        Assert.False(Try(twice), "authData twice");
        var minimal = new byte[] { 0xA1 }.Concat(SoftwarePhone.CborText("authData")).Concat(SoftwarePhone.CborBytes(ad)).ToArray();
        Assert.True(Try(minimal), "only authData");
    }

    [Fact]
    public void ACredentialIdLengthBeyondTheDataIsRefused()
    {
        var ad = phone.AuthData(SoftwarePhone.UP | SoftwarePhone.UV | SoftwarePhone.AT, 0, true);
        ad[53] = 0x10;  // 4096 + 16 bytes
        Assert.Null(AuthData.Parse(ad));
        ad[53] = 0;
        ad[54] = 0;  // no id at all
        Assert.Null(AuthData.Parse(ad));
        Assert.Null(AuthData.Parse(ad[..54]));
    }

    // ---- CBOR ----

    static object? D(params byte[] bytes) => Cbor.TryDecode(bytes);

    [Fact]
    public void CborReadsWhatWebAuthnUses()
    {
        Assert.Equal<object?>(0L, D(0x00));
        Assert.Equal<object?>(23L, D(0x17));
        Assert.Equal<object?>(24L, D(0x18, 0x18));
        Assert.Equal<object?>(1000L, D(0x19, 0x03, 0xE8));
        Assert.Equal<object?>(1000000L, D(0x1A, 0x00, 0x0F, 0x42, 0x40));
        Assert.Equal<object?>(long.MaxValue, D(0x1B, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF));
        Assert.Equal<object?>(-1L, D(0x20));
        Assert.Equal<object?>(-7L, D(0x26));
        Assert.Equal<object?>(-257L, D(0x39, 0x01, 0x00));
        Assert.Equal(new byte[] { 1, 2 }, Assert.IsType<byte[]>(D(0x42, 1, 2)));
        Assert.Equal<object?>("fmt", D(0x63, (byte)'f', (byte)'m', (byte)'t'));
        Assert.Equal(new object[] { 1L, 2L }, Assert.IsType<List<object>>(D(0x82, 0x01, 0x02)).ToArray());
        Assert.Equal<object?>(new CborSimple(21), D(0xF5));
        var map = Assert.IsType<CborMap>(Cbor.TryDecode(new byte[] { 0xA2, 0x01, 0x02, 0x61, (byte)'a', 0x20 }));
        Assert.Equal(2L, map.Get(1));
        Assert.Equal(-1L, map.Get("a"));
        Assert.Null(map.Get(2));
    }

    [Fact]
    public void CborRefusesWhatItDoesNotKnowOrWhatIsBroken()
    {
        Assert.Null(Cbor.TryDecode(Array.Empty<byte>()));
        Assert.Null(Cbor.TryDecode(new byte[] { 0x00, 0x00 }));  // trailing
        Assert.NotNull(Cbor.TryDecode(new byte[] { 0x00, 0x00 }, allowTrailing: true));
        Assert.Null(Cbor.TryDecode(new byte[] { 0x1B, 0x80, 0, 0, 0, 0, 0, 0, 0 }));  // above long
        Assert.Null(Cbor.TryDecode(new byte[] { 0x3B, 0x7F, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }));  // below long
        Assert.Null(Cbor.TryDecode(new byte[] { 0x1C }));  // reserved
        Assert.Null(Cbor.TryDecode(new byte[] { 0x5F, 0x41, 0x00, 0xFF }));  // indefinite bytes
        Assert.Null(Cbor.TryDecode(new byte[] { 0xBF, 0x01, 0x02, 0xFF }));  // indefinite map
        Assert.Null(Cbor.TryDecode(new byte[] { 0x45, 1, 2 }));  // shorter than it says
        Assert.Null(Cbor.TryDecode(new byte[] { 0x5B, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF }));  // huge
        Assert.Null(Cbor.TryDecode(new byte[] { 0x62, 0xC3, 0x28 }));  // not UTF-8
        Assert.Null(Cbor.TryDecode(new byte[] { 0xC1, 0x00 }));  // a tag
        Assert.Null(Cbor.TryDecode(new byte[] { 0xF9, 0x3C, 0x00 }));  // a float
        Assert.Null(Cbor.TryDecode(new byte[] { 0x99, 0x04, 0x00 }));  // 1024 items
        Assert.Null(Cbor.TryDecode(new byte[] { 0xA2, 0x01, 0x02, 0x01, 0x03 }));  // a key twice
        Assert.Null(Cbor.TryDecode(Enumerable.Repeat((byte)0x81, 16).Append((byte)0x00).ToArray()));  // 17 levels
        Assert.NotNull(Cbor.TryDecode(Enumerable.Repeat((byte)0x81, 15).Append((byte)0x00).ToArray()));  // 16 levels
    }

    // ---- base64url ----

    [Fact]
    public void Base64UrlBothWays()
    {
        var data = new byte[] { 0xFB, 0xFF, 0xBF, 0x00 };
        var text = Passkeys.Base64Url(data);
        Assert.Equal("-_-_AA", text);
        Assert.Equal(data, Passkeys.FromBase64Url(text));
        Assert.Equal(data, Passkeys.FromBase64Url("-_-_AA=="));
        Assert.Equal(Array.Empty<byte>(), Passkeys.FromBase64Url(""));
        Assert.Null(Passkeys.FromBase64Url(null));
        Assert.Null(Passkeys.FromBase64Url("+/+/AA"));
        Assert.Null(Passkeys.FromBase64Url("abcde"));
        Assert.Null(Passkeys.FromBase64Url("ab cd"));
    }

    [Fact]
    public void TheP256CurveCheck()
    {
        var (x, y) = phone.Point;
        Assert.True(Passkeys.OnCurve(x, y));
        var bad = (byte[])y.Clone();
        bad[0] ^= 0x40;
        Assert.False(Passkeys.OnCurve(x, bad));
        Assert.False(Passkeys.OnCurve(Enumerable.Repeat((byte)0xFF, 32).ToArray(), y));
    }
}
