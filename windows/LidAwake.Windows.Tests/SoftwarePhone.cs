using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using LidAwake;

// Plays a phone's part with a software P-256 key, as tests/passkeys/main.swift does on the Mac:
// builds what the browser and the authenticator would send to register and to sign in.
public sealed class SoftwarePhone : IDisposable
{
    public const byte UP = 0x01, UV = 0x04, AT = 0x40;

    public ECDsa Key { get; }
    public byte[] CredentialId { get; }
    public string Id => Passkeys.Base64Url(CredentialId);
    public string Host { get; }

    public SoftwarePhone(string host, byte[]? credentialId = null, ECDsa? key = null)
    {
        Host = host;
        Key = key ?? ECDsa.Create(ECCurve.NamedCurves.nistP256);
        CredentialId = credentialId ?? RandomNumberGenerator.GetBytes(16);
    }

    public void Dispose() => Key.Dispose();

    public (byte[] X, byte[] Y) Point
    {
        get
        {
            var q = Key.ExportParameters(false).Q;
            return (q.X!, q.Y!);
        }
    }

    // 04 || x || y, as the PC stores it.
    public byte[] PublicKeyX963
    {
        get
        {
            var (x, y) = Point;
            return new byte[] { 4 }.Concat(x).Concat(y).ToArray();
        }
    }

    public string Origin => "https://" + Host;

    public static byte[] ClientData(string type, string challenge, string origin) =>
        JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object> { ["type"] = type, ["challenge"] = challenge, ["origin"] = origin });

    // ---- CBOR, only what an attestation object and a COSE key need ----

    public static byte[] Head(int major, int length) => length switch
    {
        < 24 => new[] { (byte)(major << 5 | length) },
        < 256 => new[] { (byte)(major << 5 | 24), (byte)length },
        _ => new[] { (byte)(major << 5 | 25), (byte)(length >> 8), (byte)length },
    };

    public static byte[] CborBytes(byte[] d) => Head(2, d.Length).Concat(d).ToArray();
    public static byte[] CborText(string t) => Head(3, Encoding.UTF8.GetByteCount(t)).Concat(Encoding.UTF8.GetBytes(t)).ToArray();

    // {1: kty, 3: alg, -1: crv, -2: x, -3: y}
    public static byte[] Cose(byte[] x, byte[] y, int kty = 2, int alg = -7, int crv = 1) =>
        new byte[] { 0xA5, 0x01 }.Concat(Int(kty)).Concat(new byte[] { 0x03 }).Concat(Int(alg))
            .Concat(new byte[] { 0x20 }).Concat(Int(crv))
            .Concat(new byte[] { 0x21 }).Concat(CborBytes(x))
            .Concat(new byte[] { 0x22 }).Concat(CborBytes(y)).ToArray();

    static byte[] Int(int n) => n >= 0 ? Head(0, n) : Head(1, -1 - n);

    public byte[] Cose() { var (x, y) = Point; return Cose(x, y); }

    public byte[] AuthData(byte flags, uint count, bool attested, string? rp = null, byte[]? cose = null)
    {
        var d = new List<byte>(SHA256.HashData(Encoding.UTF8.GetBytes(rp ?? Host)));
        d.Add(flags);
        d.AddRange(new[] { (byte)(count >> 24), (byte)(count >> 16), (byte)(count >> 8), (byte)count });
        if (attested)
        {
            d.AddRange(new byte[16]);
            d.Add((byte)(CredentialId.Length >> 8));
            d.Add((byte)CredentialId.Length);
            d.AddRange(CredentialId);
            d.AddRange(cose ?? Cose());
        }
        return d.ToArray();
    }

    // {"fmt": "none", "attStmt": {}, "authData": <bytes>}
    public static byte[] Attestation(byte[] authData) =>
        new byte[] { 0xA3 }.Concat(CborText("fmt")).Concat(CborText("none")).Concat(CborText("attStmt"))
            .Concat(new byte[] { 0xA0 }).Concat(CborText("authData")).Concat(CborBytes(authData)).ToArray();

    // What navigator.credentials.create gives: clientDataJSON and attestationObject.
    public (byte[] ClientData, byte[] Attestation) Register(string challenge, byte flags = UP | UV | AT, uint count = 0,
                                                            string? origin = null, string? rp = null, byte[]? cose = null) =>
        (ClientData("webauthn.create", challenge, origin ?? Origin), Attestation(AuthData(flags, count, true, rp, cose)));

    // What navigator.credentials.get gives: clientDataJSON, authenticatorData and a DER signature.
    public (byte[] ClientData, byte[] AuthData, byte[] Signature) SignIn(string challenge, byte flags = UP | UV, uint count = 0,
                                                                         string? origin = null, string? rp = null,
                                                                         string type = "webauthn.get", ECDsa? signer = null)
    {
        var cd = ClientData(type, challenge, origin ?? Origin);
        var ad = AuthData(flags, count, false, rp);
        var sig = Sign(ad, cd, signer ?? Key);
        return (cd, ad, sig);
    }

    public static byte[] Sign(byte[] authData, byte[] clientData, ECDsa key) =>
        key.SignData(authData.Concat(SHA256.HashData(clientData)).ToArray(), HashAlgorithmName.SHA256, DSASignatureFormat.Rfc3279DerSequence);
}
