using System.Buffers.Binary;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LidAwake;
using Xunit;

// One request as the fake push service saw it (read inside the handler: the request is disposed after).
sealed record SeenPush(string Method, string Uri, string? Authorization, string? Ttl, string? Urgency,
                       string? ContentType, string? ContentEncoding, byte[] Body);

// A push service: records every request and answers what the test says (201 by default).
sealed class FakePushService : HttpMessageHandler
{
    readonly List<SeenPush> seen = new();
    public Func<string, HttpStatusCode> Answer { get; set; } = _ => HttpStatusCode.Created;
    public Exception? Throw { get; set; }
    // When set, every request waits for it (to show the caller does not).
    public TaskCompletionSource? Hold { get; set; }

    public List<SeenPush> Seen
    {
        get { lock (seen) return seen.ToList(); }
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        static string? One(System.Net.Http.Headers.HttpHeaders h, string name) =>
            h.TryGetValues(name, out var v) ? string.Join(", ", v) : null;
        var body = request.Content == null ? Array.Empty<byte>() : await request.Content.ReadAsByteArrayAsync(cancellationToken);
        var uri = request.RequestUri!.OriginalString;
        lock (seen)
            seen.Add(new SeenPush(request.Method.Method, uri, One(request.Headers, "Authorization"), One(request.Headers, "TTL"),
                One(request.Headers, "Urgency"), request.Content?.Headers.ContentType?.ToString(),
                request.Content == null ? null : One(request.Content.Headers, "Content-Encoding"), body));
        if (Hold != null) await Hold.Task.WaitAsync(cancellationToken);
        if (Throw != null) throw Throw;
        return new HttpResponseMessage(Answer(uri));
    }
}

// A phone: its key pair and auth secret, as a browser makes them.
sealed class FakePhone : IDisposable
{
    public readonly ECDiffieHellman Key = ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
    public readonly byte[] Auth = RandomNumberGenerator.GetBytes(16);

    public byte[] Public
    {
        get
        {
            var q = Key.ExportParameters(false).Q;
            return new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray();
        }
    }

    public string P256dh => Passkeys.Base64Url(Public);
    public string AuthText => Passkeys.Base64Url(Auth);

    public void Dispose() => Key.Dispose();
}

// An RFC 8291 receiver written from the RFC's steps, independent of WebPush.Encrypt: its own
// HKDF (RFC 5869 with HMAC-SHA-256) and its own parsing of the aes128gcm header and record.
static class PushReceiver
{
    static byte[] Hkdf(byte[] salt, byte[] ikm, byte[] info, int length)
    {
        var prk = HMACSHA256.HashData(salt, ikm);                          // Extract
        var t1 = HMACSHA256.HashData(prk, info.Concat(new byte[] { 1 }).ToArray());  // Expand, one block (length <= 32)
        return t1[..length];
    }

    public sealed record Message(uint RecordSize, int KeyLength, byte Delimiter, string Text);

    public static Message Decrypt(byte[] body, ECDiffieHellman phone, byte[] auth)
    {
        var salt = body[..16];
        var rs = BinaryPrimitives.ReadUInt32BigEndian(body.AsSpan(16, 4));
        int idlen = body[20];
        var serverPublic = body[21..(21 + idlen)];
        var record = body[(21 + idlen)..];
        using var server = ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = serverPublic[1..33], Y = serverPublic[33..65] },
        });
        using var serverKey = server.PublicKey;
        var secret = phone.DeriveRawSecretAgreement(serverKey);
        var q = phone.ExportParameters(false).Q;
        var phonePublic = new byte[] { 4 }.Concat(q.X!).Concat(q.Y!).ToArray();
        var info = Encoding.ASCII.GetBytes("WebPush: info\0").Concat(phonePublic).Concat(serverPublic).ToArray();
        var ikm = Hkdf(auth, secret, info, 32);
        var cek = Hkdf(salt, ikm, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"), 16);
        var nonce = Hkdf(salt, ikm, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"), 12);
        var plain = new byte[record.Length - 16];
        using (var aes = new AesGcm(cek, 16)) aes.Decrypt(nonce, record[..^16], record[^16..], plain);
        return new Message(rs, idlen, plain[^1], Encoding.UTF8.GetString(plain[..^1]));
    }
}

// Task 19: phone alerts (WebPush.swift on the Mac), docs/API.md 5.5.
public class WebPushTests : IDisposable
{
    const string Fcm = "https://fcm.googleapis.com/fcm/send/";
    const string Apple = "https://web.push.apple.com/";
    const string PageHost = "pc.example.ts.net";

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Settings machine;
    readonly FakePushService service = new();
    readonly List<IDisposable> owned = new();

    public WebPushTests() => machine = Settings.ForMachine(folder);

    public void Dispose()
    {
        foreach (var d in owned) d.Dispose();
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    WebPush Push(Func<DateTimeOffset>? now = null)
    {
        var push = new WebPush(machine, service, now);
        owned.Add(push);
        return push;
    }

    FakePhone Phone()
    {
        var phone = new FakePhone();
        owned.Add(phone);
        return phone;
    }

    static byte[] B(string base64url) => Passkeys.FromBase64Url(base64url)!;

    // ---- RFC 8291 ----

    // RFC 8291 section 5: the example message, byte for byte.
    const string RfcPlain = "When I grow up, I want to be a watermelon";
    const string RfcServerPrivate = "yfWPiYE-n46HLnH0KqZOF1fJJU3MYrct3AELtAQ-oRw";
    const string RfcServerPublic = "BP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A8";
    const string RfcPhonePrivate = "q1dXpw3UpT5VOmu_cf_v6ih07Aems3njxI-JWgLcM94";
    const string RfcPhonePublic = "BCVxsr7N_eNgVRqvHtD0zTZsEc6-VV-JvLexhqUzORcxaOzi6-AYWXvTBHm4bjyPjs7Vd8pZGH6SRpkNtoIAiw4";
    const string RfcSalt = "DGv6ra1nlYgDCS1FRnbzlw";
    const string RfcAuth = "BTBZMqHH6r4Tts7J_aSIgg";
    const string RfcBody = "DGv6ra1nlYgDCS1FRnbzlwAAEABBBP4z9KsN6nGRTbVYI_c7VJSPQTBtkgcy27mlmlMoZIIgDll6e3vCYLocInmYWAmS6TlzAC8wEqKK6PBru3jl7A_yl95bQpu6cVPTpK4Mqgkf1CXztLVBSt2Ks3oZwbuwXPXLWyouBWLVWGNWQexSgSxsj_Qulcy4a-fN";

    static ECDiffieHellman KeyFrom(string privateKey, string publicKey)
    {
        var pub = B(publicKey);
        return ECDiffieHellman.Create(new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            D = B(privateKey),
            Q = new ECPoint { X = pub[1..33], Y = pub[33..65] },
        });
    }

    [Fact]
    public void EncryptGivesTheRfc8291ExampleExactly()
    {
        using var server = KeyFrom(RfcServerPrivate, RfcServerPublic);
        var body = WebPush.Encrypt(Encoding.UTF8.GetBytes(RfcPlain), B(RfcPhonePublic), B(RfcAuth), server, B(RfcSalt));
        Assert.Equal(RfcBody, Passkeys.Base64Url(body));
    }

    [Fact]
    public void TheIndependentReceiverReadsTheRfc8291Example()
    {
        using var phone = KeyFrom(RfcPhonePrivate, RfcPhonePublic);
        var message = PushReceiver.Decrypt(B(RfcBody), phone, B(RfcAuth));
        Assert.Equal(RfcPlain, message.Text);
        Assert.Equal(4096u, message.RecordSize);
        Assert.Equal(65, message.KeyLength);
        Assert.Equal(2, message.Delimiter);
    }

    [Fact]
    public void AnAlertEncryptedForAPhoneIsReadByItAlone()
    {
        var phone = Phone();
        const string json = "{\"title\":\"Lid Awake\",\"body\":\"Battery is at 9%. Turned off so your PC can sleep.\"}";
        var body = WebPush.Encrypt(Encoding.UTF8.GetBytes(json), phone.Public, phone.Auth);
        // Header: salt (16) | rs (4) | idlen (1) | keyid (65), then plaintext + 0x02 + tag (16).
        Assert.Equal(16 + 4 + 1 + 65 + Encoding.UTF8.GetByteCount(json) + 1 + 16, body.Length);
        Assert.Equal(4, body[21]);
        var message = PushReceiver.Decrypt(body, phone.Key, phone.Auth);
        Assert.Equal(json, message.Text);
        Assert.Equal(4096u, message.RecordSize);
        Assert.Equal(2, message.Delimiter);

        // A new salt and server key every time.
        var again = WebPush.Encrypt(Encoding.UTF8.GetBytes(json), phone.Public, phone.Auth);
        Assert.NotEqual(body[..16], again[..16]);
        Assert.NotEqual(body[21..86], again[21..86]);

        // Another phone (or the wrong auth secret) can't read it.
        var other = Phone();
        Assert.ThrowsAny<CryptographicException>(() => PushReceiver.Decrypt(body, other.Key, phone.Auth));
        Assert.ThrowsAny<CryptographicException>(() => PushReceiver.Decrypt(body, phone.Key, other.Auth));
    }

    [Fact]
    public void EncryptRefusesBadKeysAndAlertsTooLongForOneRecord()
    {
        var phone = Phone();
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt(new byte[1], phone.Public[..64], phone.Auth));
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt(new byte[1], phone.Public, phone.Auth[..15]));
        Assert.Throws<ArgumentException>(() => WebPush.Encrypt(new byte[4096 - 16], phone.Public, phone.Auth));
        Assert.NotEmpty(WebPush.Encrypt(new byte[4096 - 17], phone.Public, phone.Auth));
    }

    // ---- RFC 8292 ----

    static JsonElement Part(string part) => JsonDocument.Parse(B(part)).RootElement.Clone();

    static ECDsa Verifier(string publicKey)
    {
        var key = B(publicKey);
        Assert.Equal(65, key.Length);
        Assert.Equal(4, key[0]);
        return ECDsa.Create(new ECParameters { Curve = ECCurve.NamedCurves.nistP256, Q = new ECPoint { X = key[1..33], Y = key[33..65] } });
    }

    [Fact]
    public void TheVapidTokenIsAnEs256JwtSignedWithThePublishedKey()
    {
        var at = new DateTimeOffset(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
        var push = Push(() => at);
        // Before any phone: the Mac's placeholder contact (an https URL, as RFC 8292 asks).
        Assert.Equal(WebPush.DefaultContact, Part(push.VapidToken("https://fcm.googleapis.com").Split('.')[1]).GetProperty("sub").GetString());

        var phone = Phone();
        Assert.True(push.Subscribe(Fcm + "a", phone.P256dh, phone.AuthText, null, PageHost));
        var jwt = push.VapidToken("https://fcm.googleapis.com");
        var parts = jwt.Split('.');
        Assert.Equal(3, parts.Length);
        var header = Part(parts[0]);
        Assert.Equal("ES256", header.GetProperty("alg").GetString());
        Assert.Equal("JWT", header.GetProperty("typ").GetString());
        var claims = Part(parts[1]);
        Assert.Equal("https://fcm.googleapis.com", claims.GetProperty("aud").GetString());
        var exp = claims.GetProperty("exp").GetInt64();
        Assert.Equal(at.AddHours(12).ToUnixTimeSeconds(), exp);
        Assert.InRange(exp - at.ToUnixTimeSeconds(), 1, 24 * 3600);
        Assert.Equal("https://" + PageHost, claims.GetProperty("sub").GetString());

        // The signature is raw r || s (64 bytes) and verifies with GET /push/key's key.
        var signature = B(parts[2]);
        Assert.Equal(64, signature.Length);
        using var verifier = Verifier(push.PublicKey);
        var signed = Encoding.ASCII.GetBytes(parts[0] + "." + parts[1]);
        Assert.True(verifier.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
        var forged = Encoding.ASCII.GetBytes(parts[0] + "." + Passkeys.Base64Url(Encoding.UTF8.GetBytes("{\"aud\":\"https://evil.example\"}")));
        Assert.False(verifier.VerifyData(forged, signature, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation));
    }

    [Fact]
    public void TheKeyIsMadeOnceAndKeptInMachineJson()
    {
        var first = Push().PublicKey;
        Assert.Equal(65, B(first).Length);
        Assert.NotNull(machine.Get<string>(WebPush.KeyName));
        Assert.True(File.Exists(Path.Combine(folder, Settings.MachineFileName)));
        Assert.Equal(first, new WebPush(machine, service).PublicKey);
        // A damaged key is replaced by a new one.
        machine.Set(WebPush.KeyName, "not a key");
        using var fresh = new WebPush(machine, service);
        Assert.NotEqual(first, fresh.PublicKey);
        Assert.Equal(fresh.PublicKey, new WebPush(machine, service).PublicKey);
    }

    // ---- Which addresses ----

    [Theory]
    [InlineData("https://fcm.googleapis.com/fcm/send/abc:APA91b")]
    [InlineData("https://web.push.apple.com/QGxxYz")]
    [InlineData("https://updates.push.services.mozilla.com/wpush/v2/gAAAA")]
    [InlineData("https://fcm.googleapis.com/fcm/send/x?y=1")]
    public void TheMacsPushServicesAreAllowed(string endpoint) => Assert.NotNull(WebPush.PushUri(endpoint));

    [Theory]
    [InlineData("")]
    [InlineData("fcm.googleapis.com/fcm/send/x")]
    [InlineData("http://fcm.googleapis.com/fcm/send/x")]
    [InlineData("wss://fcm.googleapis.com/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com:443/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com:8443/fcm/send/x")]
    [InlineData("https://user@fcm.googleapis.com/fcm/send/x")]
    [InlineData("https://user:pw@fcm.googleapis.com/fcm/send/x")]
    [InlineData("https://fcm.googleapis.com@evil.example/x")]
    [InlineData("https://fcm.googleapis.com\\@evil.example/x")]
    [InlineData("https://fcm.googleapis.com#@evil.example/x")]
    [InlineData("https://fcm.googleapis.com.evil.example/x")]
    [InlineData("https://evil.fcm.googleapis.com/x")]
    [InlineData("https://googleapis.com/x")]
    [InlineData("https://FCM.googleapis.com/x")]
    [InlineData("https://push.example.com/x")]
    [InlineData("https://127.0.0.1/x")]
    [InlineData(" https://fcm.googleapis.com/x")]
    [InlineData("https://fcm.googleapis.com/x y")]
    [InlineData("https://fcm.googleapis.com/café")]
    public void AnyOtherAddressIsRefused(string endpoint)
    {
        Assert.Null(WebPush.PushUri(endpoint));
        var phone = Phone();
        Assert.False(Push().Subscribe(endpoint, phone.P256dh, phone.AuthText, null, PageHost));
    }

    [Fact]
    public void TheEndpointMustBeUnder1024Bytes()
    {
        var phone = Phone();
        var push = Push();
        var longest = Fcm + new string('a', 1023 - Fcm.Length);
        Assert.True(push.Subscribe(longest, phone.P256dh, phone.AuthText, null, PageHost));
        Assert.False(push.Subscribe(longest + "a", phone.P256dh, phone.AuthText, null, PageHost));
    }

    [Fact]
    public void ThePhonesKeysAreChecked()
    {
        var phone = Phone();
        var push = Push();
        var key = phone.Public;
        var compressedStart = (byte[])key.Clone();
        compressedStart[0] = 3;
        Assert.False(push.Subscribe(Fcm + "a", Passkeys.Base64Url(key[..64]), phone.AuthText, null, PageHost));
        Assert.False(push.Subscribe(Fcm + "a", Passkeys.Base64Url(compressedStart), phone.AuthText, null, PageHost));
        Assert.False(push.Subscribe(Fcm + "a", "not base64!", phone.AuthText, null, PageHost));
        Assert.False(push.Subscribe(Fcm + "a", null, phone.AuthText, null, PageHost));
        Assert.False(push.Subscribe(Fcm + "a", phone.P256dh, Passkeys.Base64Url(phone.Auth[..15]), null, PageHost));
        Assert.False(push.Subscribe(Fcm + "a", phone.P256dh, Passkeys.Base64Url(new byte[17]), null, PageHost));
        Assert.False(push.Subscribe(Fcm + "a", phone.P256dh, null, null, PageHost));
        Assert.Empty(push.Phones);
        // Padded base64url is fine too.
        Assert.True(push.Subscribe(Fcm + "a", phone.P256dh + "=", phone.AuthText + "==", null, PageHost));
        Assert.Single(push.Phones);
    }

    // ---- The list ----

    [Fact]
    public void AtMostTenPhonesTheOldestGoesFirstAndTheSameEndpointIsReplaced()
    {
        var phone = Phone();
        var push = Push();
        for (int i = 0; i < 12; i++) Assert.True(push.Subscribe(Fcm + i, phone.P256dh, phone.AuthText, null, PageHost));
        Assert.Equal(Enumerable.Range(2, 10).Select(i => Fcm + i), push.Phones.Select(p => p.Endpoint));
        Assert.True(push.Subscribe(Fcm + 5, phone.P256dh, phone.AuthText, new[] { "warm" }, PageHost));
        var phones = push.Phones;
        Assert.Equal(10, phones.Count);
        Assert.Equal(Fcm + 5, phones[^1].Endpoint);
        Assert.Equal(new[] { "warm" }, phones[^1].Off);
        // Kept in machine.json: a new instance (a restart) has the same list.
        Assert.Equal(phones.Select(p => p.Endpoint), new WebPush(machine, service).Phones.Select(p => p.Endpoint));
    }

    [Fact]
    public void UnknownNamesInOffAreDroppedAndSecurityIsKept()
    {
        var phone = Phone();
        var push = Push();
        Assert.True(push.Subscribe(Fcm + "a", phone.P256dh, phone.AuthText, new[] { "warm", "bogus", "security", "warm" }, PageHost));
        Assert.Equal(new[] { "warm", "security" }, Assert.Single(push.Phones).Off);
        push.SetOff(Fcm + "a", new[] { "lowBattery", "Warm", "claudeUsage" });
        Assert.Equal(new[] { "lowBattery", "claudeUsage" }, Assert.Single(push.Phones).Off);
        push.SetOff(Fcm + "a", null);
        Assert.Empty(Assert.Single(push.Phones).Off);
        push.SetOff(Fcm + "unknown", new[] { "warm" });
        Assert.Empty(Assert.Single(push.Phones).Off);
        push.Unsubscribe(Fcm + "unknown");
        Assert.Single(push.Phones);
        push.Unsubscribe(Fcm + "a");
        Assert.Empty(push.Phones);
    }

    [Fact]
    public void ANewPasswordForgetsEveryPhone()
    {
        var phone = Phone();
        var push = Push();
        var auth = new Auth(new Settings(Path.Combine(folder, "roaming")), iterations: 1000);
        auth.PasswordSet += push.RemoveAll;
        var key = push.PublicKey;
        Assert.True(push.Subscribe(Fcm + "a", phone.P256dh, phone.AuthText, null, PageHost));
        Assert.True(push.Subscribe(Apple + "b", phone.P256dh, phone.AuthText, null, PageHost));
        auth.SetPassword("correct horse battery");
        Assert.Empty(push.Phones);
        // The key stays: phones subscribe again with the same one.
        Assert.Equal(key, new WebPush(machine, service).PublicKey);
    }

    // ---- Sending ----

    [Fact]
    public async Task AnAlertIsPostedEncryptedAndSignedWithTheMacsHeaders()
    {
        var phone = Phone();
        var push = Push();
        Assert.True(push.Subscribe(Apple + "phone", phone.P256dh, phone.AuthText, null, PageHost));
        await push.Send(AlertKind.LowBattery, "PC battery at 20%", "Plug in to keep it running.");
        var seen = Assert.Single(service.Seen);
        Assert.Equal("POST", seen.Method);
        Assert.Equal(Apple + "phone", seen.Uri);
        Assert.Equal("86400", seen.Ttl);
        Assert.Equal("high", seen.Urgency);
        Assert.Equal("aes128gcm", seen.ContentEncoding);
        Assert.Equal("application/octet-stream", seen.ContentType);

        // Authorization: vapid t=<jwt>, k=<the published key>; the token is for Apple's origin.
        Assert.StartsWith("vapid t=", seen.Authorization);
        var fields = seen.Authorization!["vapid ".Length..].Split(", ");
        Assert.Equal(2, fields.Length);
        Assert.Equal("k=" + push.PublicKey, fields[1]);
        var jwt = fields[0]["t=".Length..].Split('.');
        Assert.Equal("https://web.push.apple.com", Part(jwt[1]).GetProperty("aud").GetString());
        using var verifier = Verifier(push.PublicKey);
        Assert.True(verifier.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), B(jwt[2]), HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation));

        // The phone reads {"title","body","tag"}.
        var payload = JsonDocument.Parse(PushReceiver.Decrypt(seen.Body, phone.Key, phone.Auth).Text).RootElement;
        Assert.Equal("PC battery at 20%", payload.GetProperty("title").GetString());
        Assert.Equal("Plug in to keep it running.", payload.GetProperty("body").GetString());
        Assert.Equal("lowBattery", payload.GetProperty("tag").GetString());
    }

    [Fact]
    public async Task APhoneThatTurnedAKindOffDoesNotGetItButAlwaysGetsSecurity()
    {
        var quiet = Phone();
        var loud = Phone();
        var push = Push();
        Assert.True(push.Subscribe(Fcm + "quiet", quiet.P256dh, quiet.AuthText, new[] { "warm", "security" }, PageHost));
        Assert.True(push.Subscribe(Fcm + "loud", loud.P256dh, loud.AuthText, null, PageHost));

        await push.Send(AlertKind.Warm, "PC is getting warm", "Battery at 37.0°C.");
        Assert.Equal(Fcm + "loud", Assert.Single(service.Seen).Uri);

        await push.Send(AlertKind.TurnedOff, "Lid Awake turned off", "Timer finished after 1 h. Your PC can sleep now.");
        Assert.Equal(3, service.Seen.Count);

        await push.SendSecurity("New sign-in to Lid Awake", "With the password, on an iPhone.");
        var security = service.Seen.Skip(3).ToList();
        Assert.Equal(new[] { Fcm + "loud", Fcm + "quiet" }, security.Select(s => s.Uri).OrderBy(u => u, StringComparer.Ordinal));
        var mine = security.Single(s => s.Uri == Fcm + "quiet");
        var text = JsonDocument.Parse(PushReceiver.Decrypt(mine.Body, quiet.Key, quiet.Auth).Text).RootElement;
        Assert.Equal("security", text.GetProperty("tag").GetString());
        Assert.Equal("New sign-in to Lid Awake", text.GetProperty("title").GetString());
    }

    [Fact]
    public async Task PhonesThePushServiceSaysAreGoneAreRemoved()
    {
        var phone = Phone();
        var push = Push();
        foreach (var name in new[] { "gone404", "gone410", "ok201", "busy500", "limit429" })
            Assert.True(push.Subscribe(Fcm + name, phone.P256dh, phone.AuthText, null, PageHost));
        service.Answer = uri => uri.EndsWith("404", StringComparison.Ordinal) ? HttpStatusCode.NotFound
            : uri.EndsWith("410", StringComparison.Ordinal) ? HttpStatusCode.Gone
            : uri.EndsWith("500", StringComparison.Ordinal) ? HttpStatusCode.InternalServerError
            : uri.EndsWith("429", StringComparison.Ordinal) ? HttpStatusCode.TooManyRequests
            : HttpStatusCode.Created;
        await push.Send(AlertKind.TurnedOff, "Lid Awake turned off", "Timer finished after 1 h. Your PC can sleep now.");
        Assert.Equal(5, service.Seen.Count);
        Assert.Equal(new[] { Fcm + "ok201", Fcm + "busy500", Fcm + "limit429" }, push.Phones.Select(p => p.Endpoint));
    }

    [Fact]
    public async Task SendingNeverThrowsAndKeepsThePhoneWhenTheNetworkFails()
    {
        var phone = Phone();
        var push = Push();
        Assert.True(push.Subscribe(Fcm + "a", phone.P256dh, phone.AuthText, null, PageHost));
        service.Throw = new HttpRequestException("no network");
        await push.Send(AlertKind.Warm, "PC is getting warm", "Battery at 37.0°C.");
        service.Throw = new TaskCanceledException("timed out");
        await push.SendSecurity("New sign-in to Lid Awake", "With the password, on a browser.");
        Assert.Equal(2, service.Seen.Count);
        Assert.Single(push.Phones);
    }

    [Fact]
    public async Task SendReturnsAtOnceWhileThePushServiceIsSlow()
    {
        var phone = Phone();
        var push = Push();
        Assert.True(push.Subscribe(Fcm + "a", phone.P256dh, phone.AuthText, null, PageHost));
        service.Hold = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var sending = push.Send(AlertKind.Warm, "PC is getting warm", "Battery at 37.0°C.");
        Assert.False(sending.IsCompleted);
        service.Hold.SetResult();
        await sending.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Single(service.Seen);
    }

    [Fact]
    public async Task WithNoPhonesNothingIsSent()
    {
        var push = Push();
        var sending = push.Send(AlertKind.Warm, "PC is getting warm", "Battery at 37.0°C.");
        Assert.True(sending.IsCompleted);
        await sending;
        Assert.Empty(service.Seen);
    }

    [Fact]
    public void TheSendTimeoutIsTheMacs()
    {
        Assert.Equal(TimeSpan.FromSeconds(8), WebPush.SendTimeout);
        Assert.Equal(TimeSpan.FromHours(12), WebPush.TokenLifetime);
        Assert.Equal(new[] { "web.push.apple.com", "fcm.googleapis.com", "updates.push.services.mozilla.com" }, WebPush.AllowedHosts);
    }
}

// The alerts the tray sends (main.swift stopWithAlert and checkAlerts), through fakes.
public class PhoneAlertsTests
{
    readonly List<(string Kind, string Title, string Body)> sent = new();
    DateTimeOffset now = new(2026, 10, 20, 9, 0, 0, TimeSpan.Zero);
    PowerReading? power;
    double? batteryTemp;
    bool heatGuard = true;

    PhoneAlerts Alerts() => new((kind, title, body) =>
    {
        sent.Add((kind, title, body));
        return Task.CompletedTask;
    }, () => power, () => batteryTemp, () => heatGuard, () => now);

    static PowerReading OnBattery(byte percent, long lifeSeconds = -1) => new(0, 1, percent, lifeSeconds);
    static PowerReading PluggedIn(byte percent) => new(1, 8, percent, -1);

    [Fact]
    public async Task TurnedOffSaysWhyAndHowLongItWasOn()
    {
        var alerts = Alerts();
        alerts.TurnedOn();
        now = now.AddMinutes(125).AddSeconds(30);
        await alerts.TurnedOff("Timer finished");
        Assert.Equal((AlertKind.TurnedOff, "Lid Awake turned off", "Timer finished after 2 h 5 min. Your PC can sleep now."), Assert.Single(sent));

        alerts.TurnedOn();
        now = now.AddSeconds(59);
        var why = Safety.Decide(true, true, OnBattery(9), TemperatureReading.None)!;
        await alerts.TurnedOff(why.AlertReason);
        Assert.Equal("Battery reached 9% after under a minute. Your PC can sleep now.", sent[^1].Body);
    }

    [Fact]
    public void TheSafetyReasonsAreWordedForThePhone()
    {
        Assert.Equal("Battery reached 9%", Safety.Decide(true, true, OnBattery(9), TemperatureReading.None)!.AlertReason);
        Assert.Equal("Battery got too hot (41.2°C)", Safety.Decide(true, true, PluggedIn(80), new TemperatureReading(41.2, null))!.AlertReason);
        Assert.Equal("Your PC got too hot (92.0°C)", Safety.Decide(true, true, PluggedIn(80), new TemperatureReading(null, 92.0))!.AlertReason);
        // A reason made without the phone's wording falls back to the tray's sentence.
        Assert.Equal("Something", new SafetyReason("Something.", "something").AlertReason);
    }

    [Fact]
    public void LowBatteryOnceAt20PercentAgainAfterPluggingInOrAbove25()
    {
        var alerts = Alerts();
        power = OnBattery(21, 6000);
        alerts.Check();
        Assert.Empty(sent);

        power = OnBattery(20, 5400);
        alerts.Check();
        Assert.Equal((AlertKind.LowBattery, "PC battery at 20%", "About 1 h 30 min left. Plug in to keep it running."), Assert.Single(sent));
        power = OnBattery(15, 1000);
        alerts.Check();
        Assert.Single(sent);

        // 25% does not re-arm; 26% does.
        power = OnBattery(25);
        alerts.Check();
        power = OnBattery(19);
        alerts.Check();
        Assert.Single(sent);
        power = OnBattery(26);
        alerts.Check();
        power = OnBattery(19);
        alerts.Check();
        Assert.Equal(2, sent.Count);
        // Windows doesn't know the time left: only the advice.
        Assert.Equal("Plug in to keep it running.", sent[^1].Body);

        // Plugged in re-arms.
        power = PluggedIn(18);
        alerts.Check();
        Assert.Equal(2, sent.Count);
        power = OnBattery(18, 30);
        alerts.Check();
        Assert.Equal(3, sent.Count);
        Assert.Equal("PC battery at 18%", sent[^1].Title);
        Assert.Equal("Plug in to keep it running.", sent[^1].Body);
    }

    [Fact]
    public void NoLowBatteryWithoutABatteryOrWhenWindowsCantTell()
    {
        var alerts = Alerts();
        foreach (var p in new PowerReading?[] { null, new PowerReading(0, 128, 255, -1), new PowerReading(255, 1, 10, -1), new PowerReading(0, 255, 10, -1) })
        {
            power = p;
            alerts.Check();
        }
        Assert.Empty(sent);
    }

    [Fact]
    public void WarmOnceWithin3DegreesAgainBelow5Under()
    {
        var alerts = Alerts();
        batteryTemp = 36.9;
        alerts.Check();
        Assert.Empty(sent);

        batteryTemp = 37.04;
        alerts.Check();
        Assert.Equal((AlertKind.Warm, "PC is getting warm", "Battery at 37.0°C. Lid Awake turns off at 40°C."), Assert.Single(sent));
        batteryTemp = 39.0;
        alerts.Check();
        batteryTemp = 35.0;
        alerts.Check();
        batteryTemp = 38.0;
        alerts.Check();
        Assert.Single(sent);

        batteryTemp = 34.9;
        alerts.Check();
        heatGuard = false;
        batteryTemp = 38.25;
        alerts.Check();
        Assert.Equal(2, sent.Count);
        Assert.Equal("Battery at 38.3°C.", sent[^1].Body);
    }

    [Fact]
    public void NoWarmWithoutABatteryTemperature()
    {
        var alerts = Alerts();
        batteryTemp = null;
        power = PluggedIn(100);
        alerts.Check();
        batteryTemp = double.NaN;
        alerts.Check();
        Assert.Empty(sent);
    }

    [Fact]
    public void AFailingReadingOrSendIsOnlyNoAlert()
    {
        var alerts = new PhoneAlerts((_, _, _) => throw new InvalidOperationException("send"),
            () => throw new InvalidOperationException("power"), () => 39.0, () => throw new InvalidOperationException("guard"));
        alerts.Check();
        Assert.True(alerts.TurnedOff("Timer finished").IsCompleted);
    }

    [Theory]
    [InlineData(0, "under a minute")]
    [InlineData(59, "under a minute")]
    [InlineData(60, "1 min")]
    [InlineData(45 * 60 + 59, "45 min")]
    [InlineData(3600, "1 h")]
    [InlineData(3 * 3600 + 12 * 60, "3 h 12 min")]
    [InlineData(-30, "under a minute")]
    public void DurationsAreSpokenLikeTheMac(int seconds, string spoken) =>
        Assert.Equal(spoken, PhoneAlerts.Spoken(TimeSpan.FromSeconds(seconds)));
}
