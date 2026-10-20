using System.Buffers.Binary;
using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LidAwake;

// The alert kinds (docs/API.md 5.5): the names in a phone's `off` list and the push `tag`.
public static class AlertKind
{
    public const string TurnedOff = "turnedOff";
    public const string LowBattery = "lowBattery";
    public const string ClaudeUsage = "claudeUsage";
    public const string ClaudeSession = "claudeSession";
    public const string Warm = "warm";
    // Always sent: a phone may list it in `off` (it is stored), but sending ignores that.
    public const string Security = "security";

    public static readonly string[] All = { TurnedOff, LowBattery, ClaudeUsage, ClaudeSession, Warm, Security };

    public static bool IsKind(string? name) => name != null && All.Contains(name);
}

// Phone alerts (standard Web Push), as WebPush.swift on the Mac. The phone subscribes once; the PC
// then sends short alerts through the phone's push service (Apple's for an iPhone, Google's for an
// Android phone), encrypted so only that phone can read them (RFC 8291, aes128gcm) and signed with
// this PC's key (VAPID, RFC 8292). Alerts reach the phone even when the page is closed.
//
// Everything lives in machine.json (Settings.ForMachine): the VAPID key is this PC's own, and a
// subscription only works with the key it was made for, so the phones must not roam either.
// Called from the web server's threads, the tray and the safety check's timer: every read and
// change of the list is under a lock. Sending never throws and never blocks the caller.
public sealed class WebPush : IDisposable
{
    public const int MaxPhones = 10;
    public const int MaxEndpointBytes = 1024;
    public const int RecordSize = 4096;
    // How long a push service may take to answer (as on the Mac).
    public static readonly TimeSpan SendTimeout = TimeSpan.FromSeconds(8);
    // The VAPID token's lifetime: 12 hours, as on the Mac (RFC 8292 allows at most 24).
    public static readonly TimeSpan TokenLifetime = TimeSpan.FromHours(12);
    // Only real push services: the PC never posts to an address a page made up.
    public static readonly string[] AllowedHosts = { "web.push.apple.com", "fcm.googleapis.com", "updates.push.services.mozilla.com" };
    // The VAPID contact before any phone has subscribed (the Mac's).
    public const string DefaultContact = "https://lid-awake.example";

    public const string KeyName = "pushKey";
    public const string ListName = "pushSubscriptions";
    public const string ContactName = "pushContact";

    // Stored in machine.json, so its properties must be public. Keys are base64url.
    public sealed record Subscription(string Endpoint, string P256dh, string Auth, List<string> Off);

    readonly Settings machine;
    readonly HttpClient http;
    readonly Func<DateTimeOffset> now;
    readonly object gate = new();
    readonly object keyGate = new();
    ECDsa? signingKey;

    // `handler` is how requests go out (the tests pass a fake). By default: no redirects, no cookies.
    public WebPush(Settings machine, HttpMessageHandler? handler = null, Func<DateTimeOffset>? now = null)
    {
        this.machine = machine;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        // One client for every alert, with the Mac's 8-second limit. A handler passed in stays the caller's.
        http = handler == null
            ? new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, ConnectTimeout = SendTimeout })
            : new HttpClient(handler, disposeHandler: false);
        http.Timeout = SendTimeout;
    }

    public void Dispose()
    {
        http.Dispose();
        lock (keyGate) signingKey?.Dispose();
    }

    // ---- The key ----

    // This PC's VAPID key, made once (under the lock, so two alerts can't make two keys) and kept
    // in machine.json as PKCS#8. A damaged stored key is replaced (the phones then subscribe again).
    ECDsa SigningKey
    {
        get
        {
            lock (keyGate)
            {
                if (signingKey != null) return signingKey;
                if (Loaded(StoredKey()) is { } kept) return signingKey = kept;
                var made = ECDsa.Create(ECCurve.NamedCurves.nistP256);
                machine.Set(KeyName, Convert.ToBase64String(made.ExportPkcs8PrivateKey()));
                return signingKey = made;
            }
        }
    }

    string? StoredKey()
    {
        try { return machine.Get<string>(KeyName); }
        catch (Exception e) when (e is JsonException or InvalidOperationException) { return null; }
    }

    static ECDsa? Loaded(string? stored)
    {
        if (string.IsNullOrEmpty(stored)) return null;
        var key = ECDsa.Create();
        try
        {
            key.ImportPkcs8PrivateKey(Convert.FromBase64String(stored), out _);
            if (key.KeySize == 256) return key;
        }
        catch (Exception e) when (e is CryptographicException or FormatException) { }
        key.Dispose();
        return null;
    }

    // GET /push/key: the public key, 65 bytes uncompressed (04 || x || y), base64url.
    public string PublicKey
    {
        get
        {
            ECParameters key;
            lock (keyGate) key = SigningKey.ExportParameters(false);
            return Passkeys.Base64Url(Uncompressed(key.Q));
        }
    }

    // ---- The phones ----

    public int Count
    {
        get { lock (gate) return Stored().Count; }
    }

    // A copy of the stored list, oldest first.
    public List<Subscription> Phones
    {
        get { lock (gate) return Stored(); }
    }

    // POST /push/subscribe. False when any check fails (the route answers 400). An entry with the
    // same endpoint is replaced. `pageHost` is the page's own address, remembered as the VAPID
    // contact (https://<host>).
    public bool Subscribe(string? endpoint, string? p256dh, string? auth, IEnumerable<string>? off, string pageHost)
    {
        if (endpoint == null || PushUri(endpoint) == null || Encoding.UTF8.GetByteCount(endpoint) >= MaxEndpointBytes) return false;
        if (Passkeys.FromBase64Url(p256dh) is not { Length: 65 } key || key[0] != 4 || !IsPoint(key)) return false;
        if (Passkeys.FromBase64Url(auth) is not { Length: 16 } secret) return false;
        lock (gate)
        {
            if (IsContactHost(pageHost)) machine.Set(ContactName, "https://" + pageHost);
            var all = Stored().Where(s => s.Endpoint != endpoint).ToList();
            all.Add(new Subscription(endpoint, Passkeys.Base64Url(key), Passkeys.Base64Url(secret), Kinds(off)));
            // A few phones at most; the oldest goes first.
            if (all.Count > MaxPhones) all = all.Skip(all.Count - MaxPhones).ToList();
            machine.Set(ListName, all);
        }
        return true;
    }

    // POST /push/settings with an `off` list (none: every alert back on). An unknown endpoint is ignored.
    public void SetOff(string endpoint, IEnumerable<string>? off)
    {
        lock (gate)
        {
            var all = Stored();
            var i = all.FindIndex(s => s.Endpoint == endpoint);
            if (i < 0) return;
            all[i] = all[i] with { Off = Kinds(off) };
            machine.Set(ListName, all);
        }
    }

    // POST /push/settings with `enabled: false`, and a phone its push service says is gone.
    public void Unsubscribe(string endpoint)
    {
        lock (gate)
        {
            var all = Stored();
            if (all.RemoveAll(s => s.Endpoint == endpoint) > 0) machine.Set(ListName, all);
        }
    }

    // A new password forgets every phone (they can turn alerts on again after signing in).
    public void RemoveAll()
    {
        lock (gate) machine.Set<List<Subscription>>(ListName, null);
    }

    // Names that are not alert kinds are dropped.
    static List<string> Kinds(IEnumerable<string>? off) =>
        (off ?? Enumerable.Empty<string>()).Where(AlertKind.IsKind).Distinct().ToList();

    // A damaged list counts as no phones.
    List<Subscription> Stored()
    {
        try
        {
            return (machine.Get<List<Subscription>>(ListName) ?? new())
                .Where(s => s is { Endpoint: not null, P256dh: not null, Auth: not null })
                .Select(s => s.Off is null ? s with { Off = new() } : s)
                .ToList();
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or NotSupportedException) { return new(); }
    }

    // The page's host as the VAPID contact: a plain DNS name (the Tailscale name, lowercased by the server).
    static bool IsContactHost(string? host) =>
        host is { Length: > 0 and < 254 } && host.All(ch => ch is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '-' or '.');

    // ---- Sending ----

    // A security alert (a new sign-in, a passkey added): sent to every phone, whatever its `off` list.
    public Task SendSecurity(string title, string body) => Send(AlertKind.Security, title, body);

    // Sends to every phone that wants this kind, off the caller's thread. The task finishes when
    // every push service has answered or the 8-second limit has passed, and never fails. A 404 or
    // 410 removes that phone (it unsubscribed, or the app was removed).
    public Task Send(string kind, string title, string body)
    {
        try
        {
            List<Subscription> targets;
            lock (gate) targets = Stored().Where(s => kind == AlertKind.Security || !s.Off.Contains(kind)).ToList();
            if (targets.Count == 0) return Task.CompletedTask;
            var payload = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, string> { ["title"] = title, ["body"] = body, ["tag"] = kind });
            return Task.Run(async () =>
            {
                try { await Task.WhenAll(targets.Select(t => SendOne(t, payload))); }
                catch (Exception) { }
            });
        }
        catch (Exception) { return Task.CompletedTask; }
    }

    async Task SendOne(Subscription target, byte[] payload)
    {
        try
        {
            using var request = Request(target, payload);
            if (request == null) return;
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            if ((int)response.StatusCode is 404 or 410) Unsubscribe(target.Endpoint);
        }
        catch (Exception) { }  // a timeout, no network, a refused connection: this alert is lost
    }

    // The POST for one phone, or null when its stored keys don't work (the phone is kept).
    internal HttpRequestMessage? Request(Subscription target, byte[] payload)
    {
        if (PushUri(target.Endpoint) is not { } uri) return null;
        if (Passkeys.FromBase64Url(target.P256dh) is not { Length: 65 } key || Passkeys.FromBase64Url(target.Auth) is not { Length: 16 } secret) return null;
        byte[] body;
        string jwt;
        try
        {
            body = Encrypt(payload, key, secret);
            jwt = VapidToken("https://" + uri.Host);
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException) { return null; }
        var request = new HttpRequestMessage(HttpMethod.Post, uri) { Content = new ByteArrayContent(body) };
        request.Headers.TryAddWithoutValidation("Authorization", $"vapid t={jwt}, k={PublicKey}");
        request.Headers.TryAddWithoutValidation("TTL", "86400");
        request.Headers.TryAddWithoutValidation("Urgency", "high");
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        request.Content.Headers.ContentEncoding.Add("aes128gcm");
        return request;
    }

    // The push service's address, exactly as given: https, one of the known hosts (exactly, in
    // lower case), no port, no user or password, printable ASCII only. Null when it isn't one.
    public static Uri? PushUri(string? endpoint)
    {
        if (string.IsNullOrEmpty(endpoint) || endpoint.Any(ch => ch <= ' ' || ch > '~')) return null;
        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps) return null;
        if (uri.UserInfo.Length > 0 || !uri.IsDefaultPort) return null;
        foreach (var host in AllowedHosts)
        {
            // The host must come right after "https://" and end there: this rules out a port
            // (":443" too), a login part, and another host that only starts the same way.
            var start = "https://" + host;
            if (!endpoint.StartsWith(start, StringComparison.Ordinal)) continue;
            if (endpoint.Length != start.Length && endpoint[start.Length] is not ('/' or '?')) continue;
            return uri.Host == host ? uri : null;
        }
        return null;
    }

    // ---- RFC 8291 (aes128gcm) ----

    // Encrypts one alert for a phone: `phoneKey` is its p256dh (65 bytes), `authSecret` its auth
    // (16 bytes). `serverKey` (the ephemeral key) and `salt` are new for every message unless given.
    public static byte[] Encrypt(byte[] plaintext, byte[] phoneKey, byte[] authSecret, ECDiffieHellman? serverKey = null, byte[]? salt = null)
    {
        if (phoneKey.Length != 65 || phoneKey[0] != 4) throw new ArgumentException("The phone's key must be 65 bytes, uncompressed.", nameof(phoneKey));
        if (authSecret.Length != 16) throw new ArgumentException("The auth secret must be 16 bytes.", nameof(authSecret));
        // One record: the plaintext, the 0x02 delimiter (last record) and the 16-byte tag must fit.
        if (plaintext.Length + 1 + 16 > RecordSize) throw new ArgumentException("The alert is too long.", nameof(plaintext));
        salt ??= RandomNumberGenerator.GetBytes(16);
        if (salt.Length != 16) throw new ArgumentException("The salt must be 16 bytes.", nameof(salt));
        var ephemeral = serverKey ?? ECDiffieHellman.Create(ECCurve.NamedCurves.nistP256);
        try
        {
            using var phone = ECDiffieHellman.Create(PublicParameters(phoneKey));
            using var phonePublic = phone.PublicKey;
            var serverPublic = Uncompressed(ephemeral.ExportParameters(false).Q);
            var ecdh = ephemeral.DeriveRawSecretAgreement(phonePublic);
            // IKM = HKDF(salt = auth, ikm = ECDH secret, info = "WebPush: info" 0 ua_public as_public, 32)
            var ikm = HKDF.DeriveKey(HashAlgorithmName.SHA256, ecdh, 32, authSecret,
                Concat(Encoding.ASCII.GetBytes("WebPush: info\0"), phoneKey, serverPublic));
            var prk = HKDF.Extract(HashAlgorithmName.SHA256, ikm, salt);
            var cek = HKDF.Expand(HashAlgorithmName.SHA256, prk, 16, Encoding.ASCII.GetBytes("Content-Encoding: aes128gcm\0"));
            var nonce = HKDF.Expand(HashAlgorithmName.SHA256, prk, 12, Encoding.ASCII.GetBytes("Content-Encoding: nonce\0"));
            var record = Concat(plaintext, new byte[] { 2 });
            var cipher = new byte[record.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(cek, 16)) aes.Encrypt(nonce, record, cipher, tag);
            // Header: salt (16) | record size (4, big-endian 4096) | key length (1) | the server's public key.
            var rs = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(rs, RecordSize);
            return Concat(salt, rs, new[] { (byte)serverPublic.Length }, serverPublic, cipher, tag);
        }
        finally
        {
            if (serverKey == null) ephemeral.Dispose();
        }
    }

    // ---- RFC 8292 (VAPID) ----

    // An ES256 JWT for one push service: {aud: its origin, exp: in 12 hours, sub: the contact},
    // signed with this PC's key, the signature as raw r || s (64 bytes).
    public string VapidToken(string audience)
    {
        var header = Passkeys.Base64Url(Encoding.UTF8.GetBytes("{\"typ\":\"JWT\",\"alg\":\"ES256\"}"));
        var claims = new Dictionary<string, object>
        {
            ["aud"] = audience,
            ["exp"] = (now() + TokenLifetime).ToUnixTimeSeconds(),
            ["sub"] = Contact,
        };
        var body = Passkeys.Base64Url(JsonSerializer.SerializeToUtf8Bytes(claims));
        var signed = Encoding.ASCII.GetBytes(header + "." + body);
        // One use of the key at a time: alerts are sent from several threads at once.
        byte[] signature;
        lock (keyGate) signature = SigningKey.SignData(signed, HashAlgorithmName.SHA256, DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        return header + "." + body + "." + Passkeys.Base64Url(signature);
    }

    // The remembered https://<page host>, or the Mac's placeholder.
    public string Contact
    {
        get
        {
            try { return machine.Get<string>(ContactName) is { Length: > 0 } c ? c : DefaultContact; }
            catch (Exception e) when (e is JsonException or InvalidOperationException) { return DefaultContact; }
        }
    }

    // ---- Helpers ----

    static ECParameters PublicParameters(byte[] uncompressed) => new()
    {
        Curve = ECCurve.NamedCurves.nistP256,
        Q = new ECPoint { X = uncompressed[1..33], Y = uncompressed[33..65] },
    };

    // Whether 65 bytes are a point on P-256 (importing checks it).
    static bool IsPoint(byte[] uncompressed)
    {
        try
        {
            using var key = ECDiffieHellman.Create(PublicParameters(uncompressed));
            return true;
        }
        catch (CryptographicException) { return false; }
    }

    static byte[] Uncompressed(ECPoint q) => Concat(new byte[] { 4 }, q.X!, q.Y!);

    static byte[] Concat(params byte[][] parts)
    {
        var all = new byte[parts.Sum(p => p.Length)];
        int at = 0;
        foreach (var p in parts) { p.CopyTo(all, at); at += p.Length; }
        return all;
    }
}

// The alerts the tray sends about Lid Awake itself, with the Mac's thresholds and wording, "PC"
// for "Mac" (main.swift stopWithAlert and checkAlerts). Windows sends no Claude alerts.
// `send(kind, title, body)` is WebPush.Send; `power` is GetSystemPowerStatus, `batteryTemp` the
// battery's own sensor (HeatSensor.Latest.Battery, never a thermal zone), `heatGuard` the switch.
public sealed class PhoneAlerts
{
    public const int LowBatteryAt = 20;
    public const int LowBatteryRearmAbove = 25;
    public const int WarmWithin = 3;
    public const int WarmRearmUnder = 5;

    readonly Func<string, string, string, Task> send;
    readonly Func<PowerReading?> power;
    readonly Func<double?> batteryTemp;
    readonly Func<bool> heatGuard;
    readonly Func<DateTimeOffset> now;
    readonly object gate = new();
    DateTimeOffset onSince;
    bool lowBatteryAlerted, warmAlerted;

    public PhoneAlerts(Func<string, string, string, Task> send, Func<PowerReading?> power, Func<double?> batteryTemp,
                       Func<bool> heatGuard, Func<DateTimeOffset>? now = null)
    {
        (this.send, this.power, this.batteryTemp, this.heatGuard) = (send, power, batteryTemp, heatGuard);
        this.now = now ?? (() => DateTimeOffset.UtcNow);
        onSince = this.now();
    }

    // Lid Awake went from off to on: "how long it was on" counts from here.
    public void TurnedOn()
    {
        lock (gate) onSince = now();
    }

    // Before turning off for the timer or a safety limit: tell the phones why, and how long it was
    // on ("Timer finished after 2 h 5 min. Your PC can sleep now."). The task finishes when they
    // have answered (at most 8 seconds) and never fails.
    public Task TurnedOff(string reason)
    {
        TimeSpan on;
        lock (gate) on = now() - onSince;
        return SendQuietly(AlertKind.TurnedOff, "Lid Awake turned off", $"{reason} after {Spoken(on)}. Your PC can sleep now.");
    }

    // The early warnings, checked while on, each sent once until things recover: on battery at 20%
    // or below (again after it is plugged in or above 25%), and the battery within 3 degrees of
    // the heat limit (again once it is 5 degrees under). No battery temperature: no `warm`.
    public void Check()
    {
        PowerReading? p;
        double? t;
        bool guard;
        try { p = power(); }
        catch (Exception) { p = null; }
        try { t = batteryTemp(); }
        catch (Exception) { t = null; }
        try { guard = heatGuard(); }
        catch (Exception) { guard = true; }
        lock (gate)
        {
            // Windows can't always tell whether it is plugged in (255): then nothing changes.
            if (p is { } b && StatusInfo.HasBattery(b) && b.AcLineStatus != 255)
            {
                bool onBattery = b.AcLineStatus == 0;
                int percent = b.BatteryLifePercent;
                if (onBattery && percent <= LowBatteryAt && !lowBatteryAlerted)
                {
                    lowBatteryAlerted = true;
                    long minutes = b.LifeSeconds > 0 ? b.LifeSeconds / 60 : -1;
                    var left = minutes > 0 ? $"About {Spoken(TimeSpan.FromMinutes(minutes))} left. " : "";
                    _ = SendQuietly(AlertKind.LowBattery, $"PC battery at {percent}%", left + "Plug in to keep it running.");
                }
                else if (!onBattery || percent > LowBatteryRearmAbove) lowBatteryAlerted = false;
            }
            if (t is { } celsius && TemperatureMath.IsSane(celsius))
            {
                if (celsius >= Limits.MaxBatteryTemp - WarmWithin && !warmAlerted)
                {
                    warmAlerted = true;
                    var shown = TemperatureMath.OneDecimal(celsius).ToString("0.0", CultureInfo.InvariantCulture);
                    _ = SendQuietly(AlertKind.Warm, "PC is getting warm",
                        $"Battery at {shown}°C." + (guard ? $" Lid Awake turns off at {Limits.MaxBatteryTemp}°C." : ""));
                }
                else if (celsius < Limits.MaxBatteryTemp - WarmRearmUnder) warmAlerted = false;
            }
        }
    }

    Task SendQuietly(string kind, string title, string body)
    {
        try { return send(kind, title, body) ?? Task.CompletedTask; }
        catch (Exception) { return Task.CompletedTask; }
    }

    // "3 h 12 min", "45 min", "under a minute" (main.swift spokenDuration).
    public static string Spoken(TimeSpan span)
    {
        var minutes = (long)Math.Floor(span.TotalMinutes);
        if (minutes < 1) return "under a minute";
        long h = minutes / 60, m = minutes % 60;
        return h == 0 ? $"{m} min" : m == 0 ? $"{h} h" : $"{h} h {m} min";
    }
}
