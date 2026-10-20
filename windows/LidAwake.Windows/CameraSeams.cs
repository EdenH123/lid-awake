namespace LidAwake;

// The camera's three seams: the passkeys, the H.264 encoder and the live socket. The camera only
// talks to them through these small interfaces; the tests give fakes, and the real ones are
// CameraWiring.cs (PasskeyCamera, MfCameraEncoders) and LiveServer (a WireCameraSocket per phone).

// Task 18's passkeys, as the two camera routes need them (docs/API.md 5.4).
public interface ICameraPasskeys
{
    // The stored credential ids (base64url), for the phone to choose from. Empty: no passkey yet.
    IReadOnlyList<string> CredentialIds();

    // A new one-time challenge (base64url) with the purpose "camera" (5.3: 2 minutes, used once).
    string CameraChallenge();

    // The same checks as POST /passkey/login (5.3), but against a "camera" challenge, which is
    // used up. `host` is the request's Host (the relying party id), already lowercased.
    bool VerifyCamera(string credentialId, byte[] clientData, byte[] authenticatorData, byte[] signature, string host);
}

// Task 22's H.264 encoder.
public interface IVideoEncoderFactory
{
    // An encoder for NV12 frames of this size at about this many bits a second. null: this PC
    // has no H.264 encoder.
    IH264Encoder? Create(int width, int height, int bitsPerSecond);

    // One access unit as the encoder gave it (Annex B start codes) → the same NAL units in AVCC
    // form (each after a 4-byte big-endian length), without SPS and PPS, and the SPS (type 7) and
    // PPS (type 8) it carried (null when it had none).
    (byte[] Avcc, byte[]? Sps, byte[]? Pps) ToAvcc(byte[] annexB);

    // The avcC record (the setup message's `description`) for this SPS and PPS.
    byte[] AvcC(byte[] sps, byte[] pps);

    // The WebCodecs codec string for this SPS: "avc1." and the profile, constraint and level bytes in hex.
    string CodecString(byte[] sps);
}

public interface IH264Encoder : IDisposable
{
    // One NV12 frame (Y plane, then interleaved U and V, rows exactly `width` bytes) with its time
    // in microseconds. `keyframe`: this one must be a keyframe. Gives back what the encoder has
    // ready, oldest first: zero, one or more access units.
    IReadOnlyList<H264Unit> Encode(byte[] nv12, long micros, bool keyframe);
}

// One encoded access unit, Annex B, with whether it is a keyframe and its time in microseconds.
public sealed record H264Unit(byte[] AnnexB, bool Key, long Micros);

// The camera setup message's fields (docs/API.md 7.2): codec string, avcC record, size in pixels.
public sealed record CameraSetup(string Codec, byte[] Description, int Width, int Height)
{
    public bool SameAs(CameraSetup? other) =>
        other != null && other.Codec == Codec && other.Width == Width && other.Height == Height
        && other.Description.AsSpan().SequenceEqual(Description);
}

// Task 22's live socket, one per phone, as the camera sees it. What the phone sends goes the other
// way: the socket hands every JSON message to CameraService.Receive first (it answers true for
// `camera`, `cameraKey` and `talk`), and calls CameraService.Closed when it closes. While
// CameraService.IsOpen(socket) is true, the socket sends this phone no screen frames.
//
// The methods are called on the camera's threads (and under its lock, to keep the order): they
// must queue and return, and must not call back into CameraService. WireCameraSocket is a ready
// implementation over two "send text" and "send binary" functions.
public interface ICameraSocket
{
    // The session cookie the socket was opened with: tickets are only good for the session that asked.
    string? SessionToken { get; }

    // The phone's device name (Auth.DeviceName), for the activity log.
    string Device { get; }

    void SendSetup(CameraSetup setup);                       // {"camera": {...}}
    void SendVideo(bool keyframe, long micros, byte[] avcc); // [3|4][Int64 µs, big-endian][AVCC]
    void SendSound(int rate, byte[] mulaw);                  // [5][UInt32 rate, big-endian][μ-law]
    void SendCameraError(string text);                       // {"cameraError": "..."}, "" clears it
    void SendCameraStopped(string text);                     // {"cameraStopped": "..."}
}

// Whether "camera" may be offered in /status features: only with all three seams wired. A build
// without the real passkeys, encoder or socket never offers it (and its routes answer 404).
public static class CameraFeature
{
    public static bool Ready(ICameraPasskeys? passkeys, IVideoEncoderFactory? encoders, bool socketWired) =>
        passkeys != null && encoders != null && socketWired;
}
