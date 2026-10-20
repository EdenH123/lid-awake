using System.Globalization;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LidAwake;

// Face ID / fingerprint sign-in for the phone page (WebAuthn passkeys), the same rules as
// Passkeys.swift on the Mac (docs/API.md 5.3). The phone keeps a private key that only unlocks
// with the owner's face or finger; this PC stores just the public key and checks the signature on
// each sign-in. Adding one needs the password again, a new password removes them all, and they
// only work on the Tailscale HTTPS address (passkeys need HTTPS). ES256 (P-256) only.
public sealed class Passkeys
{
    public const string StoreKey = "passkeys";
    public const int MaxKeys = 20;
    public const int MaxChallenges = 32;
    public static readonly TimeSpan ChallengeLife = TimeSpan.FromMinutes(2);

    // Stored in settings, so its properties must be public. `PublicKey` is P-256 as 04 || x || y.
    public sealed record Stored(string Id, byte[] PublicKey, uint SignCount, long? Added = null, string? Device = null);

    public sealed record Info(string Id, DateTimeOffset? Added, string Device);

    sealed record Issued(byte[] Value, string Purpose, DateTimeOffset Expires, long Order);

    readonly Settings settings;
    readonly Func<DateTimeOffset> now;
    readonly object gate = new();
    readonly List<Issued> challenges = new();
    long issuedCount;

    public Passkeys(Settings settings, Func<DateTimeOffset>? now = null)
    {
        this.settings = settings;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public int Count { get { lock (gate) return Load().Count; } }

    public IReadOnlyList<string> CredentialIds { get { lock (gate) return Load().Select(k => k.Id).ToList(); } }

    public IReadOnlyList<Info> List
    {
        get
        {
            lock (gate)
                return Load().Select(k => new Info(k.Id, k.Added is { } a ? DateTimeOffset.FromUnixTimeSeconds(a) : null, k.Device ?? "Phone")).ToList();
        }
    }

    public void RemoveAll()
    {
        lock (gate) settings.Set<List<Stored>>(StoreKey, null);
    }

    public void Remove(string id)
    {
        lock (gate)
        {
            var all = Load();
            if (all.RemoveAll(k => k.Id == id) > 0) Save(all);
        }
    }

    // A one-time challenge for "create" (adding a passkey), "get" (signing in) or "camera",
    // valid 2 minutes. Only a handful are ever needed: the oldest is dropped so requests can't
    // pile them up.
    public string Challenge(string purpose)
    {
        lock (gate)
        {
            var t = now();
            challenges.RemoveAll(c => c.Expires <= t);
            while (challenges.Count >= MaxChallenges)
                challenges.Remove(challenges.OrderBy(c => c.Expires).ThenBy(c => c.Order).First());
            var value = Base64Url(RandomNumberGenerator.GetBytes(32));
            challenges.Add(new Issued(Encoding.UTF8.GetBytes(value), purpose, t + ChallengeLife, issuedCount++));
            return value;
        }
    }

    // Adding a passkey: checks what the phone signed up with and keeps its public key.
    public bool Register(byte[] clientData, byte[] attestation, string host, string device = "Phone")
    {
        if (!host.EndsWith(".ts.net", StringComparison.Ordinal)) return false;
        if (!CheckClientData(clientData, "webauthn.create", "create", host)) return false;
        if (Cbor.TryDecode(attestation) is not CborMap att || att.Get("authData") is not byte[] authData) return false;
        var parsed = AuthData.Parse(authData);
        if (parsed is null || !SameHash(parsed.RpIdHash, host) || !parsed.UserVerified
            || parsed.CredentialId is not { } credentialId || parsed.PublicKey is not { } publicKey) return false;
        // A point that is not on the curve could never verify: refused now rather than stored.
        using (var check = ImportKey(publicKey))
            if (check is null) return false;
        var id = Base64Url(credentialId);
        lock (gate)
        {
            var all = Load();
            // A key with the same id is never replaced (that would let someone swap in their own).
            if (all.Any(k => k.Id == id) || all.Count >= MaxKeys) return false;
            all.Add(new Stored(id, publicKey, parsed.SignCount, now().ToUnixTimeSeconds(), device));
            Save(all);
            return true;
        }
    }

    // Signing in: the signature must come from a stored key, over this challenge, for this
    // address. `purpose` "camera": the same check, for the challenge handed out before the camera.
    public bool Verify(string credentialId, byte[] clientData, byte[] authenticatorData, byte[] signature, string host,
                       string purpose = "get")
    {
        if (!host.EndsWith(".ts.net", StringComparison.Ordinal)) return false;
        if (!CheckClientData(clientData, "webauthn.get", purpose, host)) return false;
        var parsed = AuthData.Parse(authenticatorData);
        if (parsed is null || !SameHash(parsed.RpIdHash, host) || !parsed.UserVerified) return false;
        lock (gate)
        {
            var all = Load();
            var index = all.FindIndex(k => k.Id == credentialId);
            if (index < 0) return false;
            using var key = ImportKey(all[index].PublicKey);
            if (key is null) return false;
            var signed = new byte[authenticatorData.Length + 32];
            authenticatorData.CopyTo(signed, 0);
            SHA256.HashData(clientData).CopyTo(signed, authenticatorData.Length);
            bool valid;
            try { valid = key.VerifyData(signed, signature, HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence); }
            catch (Exception e) when (e is CryptographicException or ArgumentException) { valid = false; }
            if (!valid) return false;
            // Counters only ever go up (synced passkeys always report 0, which is fine).
            var old = all[index].SignCount;
            if (old != 0 || parsed.SignCount != 0)
            {
                if (parsed.SignCount <= old) return false;
                all[index] = all[index] with { SignCount = parsed.SignCount };
                Save(all);
            }
            return true;
        }
    }

    // The browser's own summary: right kind of request, our one-time challenge, our address.
    // A challenge that is found is used up here, whatever is checked after it.
    bool CheckClientData(byte[] data, string type, string purpose, string host)
    {
        string? gotType = null, origin = null, challenge = null;
        try
        {
            using var doc = JsonDocument.Parse(data);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return false;
            foreach (var p in doc.RootElement.EnumerateObject())
            {
                if (p.Name is not ("type" or "origin" or "challenge")) continue;
                // One answer to each question: a name given twice, or not as text, is refused.
                if (p.Value.ValueKind != JsonValueKind.String) return false;
                var value = p.Value.GetString();
                switch (p.Name)
                {
                    case "type": if (gotType != null) return false; gotType = value; break;
                    case "origin": if (origin != null) return false; origin = value; break;
                    default: if (challenge != null) return false; challenge = value; break;
                }
            }
        }
        catch (JsonException) { return false; }
        catch (ArgumentException) { return false; }
        if (gotType != type || origin != "https://" + host || challenge is null) return false;
        var given = Encoding.UTF8.GetBytes(challenge);
        lock (gate)
        {
            // Compared with every challenge (each comparison in constant time), not looked up.
            Issued? found = null;
            foreach (var c in challenges)
                if (CryptographicOperations.FixedTimeEquals(c.Value, given)) found = c;
            if (found is null) return false;
            challenges.Remove(found);
            return found.Purpose == purpose && found.Expires > now();
        }
    }

    static bool SameHash(byte[] rpIdHash, string host) =>
        CryptographicOperations.FixedTimeEquals(rpIdHash, SHA256.HashData(Encoding.UTF8.GetBytes(host)));

    // A stored 04 || x || y key as an ECDsa, or null if it is not a P-256 point.
    static ECDsa? ImportKey(byte[] publicKey)
    {
        if (publicKey.Length != 65 || publicKey[0] != 4 || !OnCurve(publicKey[1..33], publicKey[33..65])) return null;
        var p = new ECParameters
        {
            Curve = ECCurve.NamedCurves.nistP256,
            Q = new ECPoint { X = publicKey[1..33], Y = publicKey[33..65] },
        };
        var key = ECDsa.Create();
        try
        {
            key.ImportParameters(p);
            return key;
        }
        catch (CryptographicException)
        {
            key.Dispose();
            return null;
        }
    }

    static readonly BigInteger P256Prime = BigInteger.Parse("0FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF", NumberStyles.HexNumber);
    static readonly BigInteger P256B = BigInteger.Parse("05AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B", NumberStyles.HexNumber);

    // y² = x³ − 3x + b (mod p), with x and y below p: checked here, whatever the platform's
    // import would do with a point that is not on the curve.
    internal static bool OnCurve(byte[] x, byte[] y)
    {
        var bx = new BigInteger(x, isUnsigned: true, isBigEndian: true);
        var by = new BigInteger(y, isUnsigned: true, isBigEndian: true);
        if (bx >= P256Prime || by >= P256Prime) return false;
        var left = BigInteger.ModPow(by, 2, P256Prime);
        var right = ((BigInteger.ModPow(bx, 3, P256Prime) - 3 * bx + P256B) % P256Prime + P256Prime) % P256Prime;
        return left == right;
    }

    // A damaged list counts as none (and is replaced by the next key added).
    List<Stored> Load()
    {
        try
        {
            return settings.Get<List<Stored?>>(StoreKey)?
                .Where(k => k is { Id: not null, PublicKey: not null })
                .Select(k => k!).ToList() ?? new();
        }
        catch (JsonException) { return new(); }
        catch (NotSupportedException) { return new(); }
    }

    void Save(List<Stored> all) => settings.Set(StoreKey, all);

    public static string Base64Url(byte[] data) =>
        Convert.ToBase64String(data).Replace('+', '-').Replace('/', '_').TrimEnd('=');

    // URL-safe base64, with or without padding; null if it is not that.
    public static byte[]? FromBase64Url(string? text)
    {
        if (text is null) return null;
        var s = text.TrimEnd('=');
        if (s.Length % 4 == 1) return null;
        foreach (var ch in s)
            if (!(ch is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '-' or '_')) return null;
        s = s.Replace('-', '+').Replace('_', '/');
        s += new string('=', (4 - s.Length % 4) % 4);
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}

// WebAuthn authenticator data: rpIdHash(32) flags(1) signCount(4) [aaguid(16) idLen(2) id key(CBOR)].
public sealed class AuthData
{
    public required byte[] RpIdHash { get; init; }
    public bool UserVerified { get; init; }
    public uint SignCount { get; init; }
    public byte[]? CredentialId { get; init; }
    public byte[]? PublicKey { get; init; }  // 04 || x || y

    public static AuthData? Parse(byte[] d)
    {
        if (d.Length < 37) return null;
        var flags = d[32];
        // User present (0x01) and verified with Face ID, fingerprint or the phone's code (0x04).
        bool verified = (flags & 0x01) != 0 && (flags & 0x04) != 0;
        uint count = (uint)d[33] << 24 | (uint)d[34] << 16 | (uint)d[35] << 8 | d[36];
        var rp = d[..32];
        // No attested credential (0x40): a sign-in.
        if ((flags & 0x40) == 0) return new AuthData { RpIdHash = rp, UserVerified = verified, SignCount = count };
        if (d.Length < 55) return null;
        int idLength = d[53] << 8 | d[54];
        // WebAuthn credential ids are 1 to 1023 bytes.
        if (idLength < 1 || idLength > 1023 || d.Length < 55 + idLength) return null;
        var id = d[55..(55 + idLength)];
        // COSE key: {1: 2 (EC2), 3: -7 (ES256), -1: 1 (P-256), -2: x, -3: y}. Extensions may follow.
        if (Cbor.TryDecode(d[(55 + idLength)..], allowTrailing: true) is not CborMap cose
            || !Is(cose.Get(1), 2) || !Is(cose.Get(3), -7) || !Is(cose.Get(-1), 1)
            || cose.Get(-2) is not byte[] { Length: 32 } x || cose.Get(-3) is not byte[] { Length: 32 } y) return null;
        var key = new byte[65];
        key[0] = 4;
        x.CopyTo(key, 1);
        y.CopyTo(key, 33);
        return new AuthData { RpIdHash = rp, UserVerified = verified, SignCount = count, CredentialId = id, PublicKey = key };
    }

    static bool Is(object? value, long expected) => value is long n && n == expected;
}

// A CBOR map: keys and values in the order they came. Integer and text keys can be looked up.
public sealed class CborMap
{
    public List<KeyValuePair<object, object>> Entries { get; } = new();

    public object? Get(long key) => Entries.FirstOrDefault(e => e.Key is long n && n == key).Value;
    public object? Get(string key) => Entries.FirstOrDefault(e => e.Key is string s && s == key).Value;
}

// A CBOR "simple" value below 24: false (20), true (21), null (22), undefined (23).
public sealed record CborSimple(byte Value);

// Just enough CBOR (RFC 8949) to read WebAuthn attestation objects and COSE keys, as in
// Passkeys.swift. Values are long, byte[], string, List<object>, CborMap or CborSimple. No
// indefinite lengths, floats or tags; at most 16 levels and 1023 items in an array or map. A map
// with the same integer or text key twice is refused.
public static class Cbor
{
    sealed class MalformedException : Exception { }

    static readonly UTF8Encoding StrictUtf8 = new(false, true);

    // Null if `data` is not one well-formed item (followed by nothing, unless `allowTrailing`).
    public static object? TryDecode(byte[] data, bool allowTrailing = false)
    {
        try
        {
            int offset = 0;
            var value = Item(data, ref offset, 0);
            if (!allowTrailing && offset != data.Length) return null;
            return value;
        }
        catch (MalformedException) { return null; }
    }

    static byte Byte(byte[] b, ref int offset)
    {
        if (offset >= b.Length) throw new MalformedException();
        return b[offset++];
    }

    static ulong Length(byte[] b, ref int offset, int info)
    {
        switch (info)
        {
            case < 24: return (ulong)info;
            case 24: return Byte(b, ref offset);
            case 25 or 26 or 27:
                ulong value = 0;
                for (int i = 0; i < 1 << (info - 24); i++) value = value << 8 | Byte(b, ref offset);
                return value;
            default: throw new MalformedException();
        }
    }

    static byte[] Take(byte[] b, ref int offset, ulong count)
    {
        if (count > (ulong)(b.Length - offset)) throw new MalformedException();
        var part = b[offset..(offset + (int)count)];
        offset += (int)count;
        return part;
    }

    static object Item(byte[] b, ref int offset, int depth)
    {
        if (depth >= 16) throw new MalformedException();
        var first = Byte(b, ref offset);
        int major = first >> 5, info = first & 0x1f;
        switch (major)
        {
            case 0:
            {
                var n = Length(b, ref offset, info);
                if (n > long.MaxValue) throw new MalformedException();
                return (long)n;
            }
            case 1:
            {
                var n = Length(b, ref offset, info);
                if (n >= long.MaxValue) throw new MalformedException();
                return -1 - (long)n;
            }
            case 2:
                return Take(b, ref offset, Length(b, ref offset, info));
            case 3:
            {
                var bytes = Take(b, ref offset, Length(b, ref offset, info));
                try { return StrictUtf8.GetString(bytes); }
                catch (DecoderFallbackException) { throw new MalformedException(); }
            }
            case 4:
            {
                var n = Length(b, ref offset, info);
                if (n >= 1024) throw new MalformedException();
                var list = new List<object>();
                for (ulong i = 0; i < n; i++) list.Add(Item(b, ref offset, depth + 1));
                return list;
            }
            case 5:
            {
                var n = Length(b, ref offset, info);
                if (n >= 1024) throw new MalformedException();
                var map = new CborMap();
                for (ulong i = 0; i < n; i++)
                {
                    var k = Item(b, ref offset, depth + 1);
                    var v = Item(b, ref offset, depth + 1);
                    if (k is long or string && map.Entries.Any(e => Equals(e.Key, k))) throw new MalformedException();
                    map.Entries.Add(new(k, v));
                }
                return map;
            }
            case 7:
                if (info >= 24) throw new MalformedException();  // true/false/null/undefined only
                return new CborSimple((byte)info);
            default:
                throw new MalformedException();  // tags
        }
    }
}
