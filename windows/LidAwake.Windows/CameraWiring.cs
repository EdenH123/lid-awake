namespace LidAwake;

// The camera's seams (CameraSeams.cs) over the real pieces: Task 18's passkeys and Task 22's
// H.264 encoder. The third seam, the live socket, is LiveServer's (WireCameraSocket per phone).

// The camera routes' passkeys (docs/API.md 5.4): the stored keys, and challenges with the purpose
// "camera". A camera challenge never signs in, and a sign-in challenge never opens the camera:
// Passkeys checks the purpose of the challenge it finds (and uses it up either way).
public sealed class PasskeyCamera : ICameraPasskeys
{
    public const string Purpose = "camera";

    readonly Passkeys passkeys;

    public PasskeyCamera(Passkeys passkeys) => this.passkeys = passkeys;

    public IReadOnlyList<string> CredentialIds() => passkeys.CredentialIds;

    public string CameraChallenge() => passkeys.Challenge(Purpose);

    public bool VerifyCamera(string credentialId, byte[] clientData, byte[] authenticatorData, byte[] signature, string host) =>
        passkeys.Verify(credentialId, clientData, authenticatorData, signature, host, Purpose);
}

// The camera's H.264 encoders: Media Foundation's (MfH264Encoder, the screen's encoder, fed NV12
// as it comes from the camera), hardware first unless `allowHardware` is false. Create answers null
// when this PC has none, as the seam asks.
public sealed class MfCameraEncoders : IVideoEncoderFactory
{
    // The camera's pace (CameraService.MinFrameMicros): about 24 pictures a second.
    public const int Fps = 24;

    readonly bool allowHardware;
    readonly Func<int, int, int, int, MfH264Encoder> make;
    readonly Action<string>? note;

    // `make` is for the tests (none: MfH264Encoder.Create). `note` hears why there is no encoder.
    public MfCameraEncoders(bool allowHardware = true, Func<int, int, int, int, MfH264Encoder>? make = null, Action<string>? note = null)
    {
        this.allowHardware = allowHardware;
        this.make = make ?? ((w, h, fps, bps) => MfH264Encoder.Create(w, h, fps, bps, this.allowHardware));
        this.note = note;
    }

    public IH264Encoder? Create(int width, int height, int bitsPerSecond)
    {
        try { return new MfCameraEncoder(make(width, height, Fps, bitsPerSecond)); }
        catch (Exception e)
        {
            try { note?.Invoke("Camera: no H.264 encoder (" + e.Message + ")"); }
            catch (Exception) { }
            return null;
        }
    }

    public (byte[] Avcc, byte[]? Sps, byte[]? Pps) ToAvcc(byte[] annexB)
    {
        var unit = Avcc.FromAnnexB(annexB);
        return (unit.Data, unit.Sps, unit.Pps);
    }

    public byte[] AvcC(byte[] sps, byte[] pps) => Avcc.AvcC(sps, pps);

    public string CodecString(byte[] sps) => Avcc.Codec(sps);
}

// One MfH264Encoder as the camera's IH264Encoder. MfH264Encoder already gives AVCC with the SPS and
// PPS kept apart; the camera's seam wants Annex B, so each picture goes back to start codes, with
// the SPS and PPS in front of every keyframe (the service sends a new setup only when they change).
public sealed class MfCameraEncoder : IH264Encoder
{
    readonly MfH264Encoder encoder;

    public MfCameraEncoder(MfH264Encoder encoder) => this.encoder = encoder;

    public IReadOnlyList<H264Unit> Encode(byte[] nv12, long micros, bool keyframe)
    {
        var units = new List<H264Unit>();
        foreach (var (_, chunk) in encoder.EncodeNv12(nv12, micros, keyframe))
        {
            byte[]? sps = null, pps = null;
            if (chunk.Key) (sps, pps) = encoder.ParameterSets;
            units.Add(new H264Unit(AnnexB.FromAvcc(chunk.Data, sps, pps), chunk.Key, chunk.Timestamp));
        }
        return units;
    }

    public void Dispose() => encoder.Dispose();
}

public static class AnnexB
{
    static readonly byte[] StartCode = { 0, 0, 0, 1 };

    // AVCC (each NAL unit after a 4-byte big-endian length) → Annex B (each after a start code),
    // with the SPS and PPS first when given. A length that runs past the end stops there.
    public static byte[] FromAvcc(byte[] avcc, byte[]? sps = null, byte[]? pps = null)
    {
        var output = new List<byte>(avcc.Length + 64);
        if (sps != null && pps != null)
        {
            output.AddRange(StartCode);
            output.AddRange(sps);
            output.AddRange(StartCode);
            output.AddRange(pps);
        }
        int at = 0;
        while (at + 4 <= avcc.Length)
        {
            int length = (avcc[at] << 24) | (avcc[at + 1] << 16) | (avcc[at + 2] << 8) | avcc[at + 3];
            at += 4;
            if (length <= 0 || length > avcc.Length - at) break;
            output.AddRange(StartCode);
            output.AddRange(new ArraySegment<byte>(avcc, at, length));
            at += length;
        }
        return output.ToArray();
    }
}
