using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace LidAwake;

// Every sentence the camera says, with the Mac's wording for a PC (docs/API.md 5.4, 7.5). The page
// shows them as they are, after `localize` (which turns "Face ID" into the phone's own word).
public static class CameraTexts
{
    public const string Off = "The camera is off. On your PC, tick “Allow camera & microphone from phone” in the Lid Awake menu.";
    public const string NeedsPasskeySetUp = "The camera needs Face ID. Turn it on first in Settings → Sign-in.";
    public const string PasskeyFailed = "Face ID didn't work, so the camera stays closed.";
    public const string NeedsPasskey = "Face ID is needed to open the camera. Try again.";
    public const string SwitchedOff = "The camera was turned off on the PC.";
    public static readonly string TenMinutes = $"The camera closed by itself after {CameraService.Minutes} minutes. Open it again to keep watching.";
    public const string NoCamera = "This PC has no camera.";
    public const string LidClosed = "The PC's camera is off while the lid is closed. Open the lid, or plug in a USB camera.";
    public const string Busy = "The PC's camera is busy. Try again.";
    public const string NotAllowed = "Lid Awake isn't allowed to use the camera. On your PC: Settings → Privacy & security → Camera → turn on “Let desktop apps access your camera”.";
    public const string Stalled = "The camera stopped sending. If the PC's lid is closed, open it or plug in a USB camera.";
    public const string NoMediaFoundation = "This PC can't use its camera: Windows is missing its media features (Media Feature Pack).";
    public const string NoEncoder = "This PC can't send the camera picture: it has no video encoder.";

    // Why no camera could be opened. `error` is the first failure's HRESULT (0: none was tried).
    public static string OpenFailed(bool anyCamera, bool lidClosed, int error)
    {
        if (error == unchecked((int)0x80070005)) return NotAllowed;  // E_ACCESSDENIED: Windows' camera privacy switch
        if (lidClosed) return LidClosed;
        return anyCamera ? Busy : NoCamera;
    }
}

// The owner's "Allow camera & microphone from phone" switch in the tray, off by default, stored as
// `cameraFromPhone` (the Mac's key). It only counts with the screen switch on (`remoteScreen`, the
// screen task's key), as on the Mac.
public sealed class CameraSwitch
{
    public const string Key = "cameraFromPhone";
    public const string ScreenKey = "remoteScreen";

    readonly Settings settings;

    public CameraSwitch(Settings settings) => this.settings = settings;

    public bool RemoteScreen => Read(ScreenKey);
    public bool FromPhone => Read(Key);

    // /status cameraAllowed, and what the routes and the socket check.
    public bool Allowed => RemoteScreen && FromPhone;

    public void Set(bool on) => settings.Set(Key, on);

    // A value that isn't a boolean counts as off.
    bool Read(string key)
    {
        try { return settings.Get<bool>(key); }
        catch (Exception) { return false; }
    }
}

// The one-time tickets the live socket redeems (5.4, as the Mac's CameraTickets): 32 random bytes,
// base64url, good for 60 seconds, once, and only for the session that asked. At most 16 are kept;
// with 16 already there the answer still has a ticket, but it won't be accepted.
public sealed class CameraTickets
{
    public const int Max = 16;
    public static readonly TimeSpan Life = TimeSpan.FromSeconds(60);

    readonly Func<DateTimeOffset> now;
    readonly object gate = new();
    // ticket → the SHA-256 of the session token it belongs to, and when it expires.
    readonly Dictionary<string, (string Session, DateTimeOffset Expires)> tickets = new(StringComparer.Ordinal);

    public CameraTickets(Func<DateTimeOffset>? now = null) => this.now = now ?? (() => DateTimeOffset.UtcNow);

    public int Count { get { lock (gate) return tickets.Count; } }

    public string Issue(string sessionToken)
    {
        lock (gate)
        {
            var t = now();
            foreach (var old in tickets.Where(kv => kv.Value.Expires <= t).Select(kv => kv.Key).ToList()) tickets.Remove(old);
            var ticket = CameraAccess.Base64Url(RandomNumberGenerator.GetBytes(32));
            if (tickets.Count < Max) tickets[ticket] = (Hash(sessionToken), t + Life);
            return ticket;
        }
    }

    // Used up whatever the answer, like the Mac's.
    public bool Redeem(string? ticket, string? sessionToken)
    {
        if (ticket == null || sessionToken == null) return false;
        lock (gate)
        {
            if (!tickets.Remove(ticket, out var issued)) return false;
            return issued.Session == Hash(sessionToken) && issued.Expires > now();
        }
    }

    static string Hash(string token) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));
}

// POST /camera/options and POST /camera/unlock (5.4), through Task 18's passkeys. WebServer answers
// them only when the camera is wired (WebServer.Camera); otherwise they are 404, as for a feature
// this PC doesn't have. Neither route opens the camera: only a ticket on the live socket does.
public sealed class CameraAccess
{
    readonly ICameraPasskeys passkeys;
    readonly CameraTickets tickets;
    readonly Func<bool> allowed;

    // `allowed` is the owner's switches (CameraSwitch.Allowed: screen and camera).
    public CameraAccess(ICameraPasskeys passkeys, CameraTickets tickets, Func<bool> allowed)
    {
        this.passkeys = passkeys;
        this.tickets = tickets;
        this.allowed = allowed;
    }

    // An answer: the status code, and JSON or a plain sentence.
    public sealed record Answer(int Code, string? Json, string? Text);

    public Answer Options(string host)
    {
        if (!allowed()) return new(403, null, CameraTexts.Off);
        var ids = passkeys.CredentialIds();
        if (ids.Count == 0 || !host.EndsWith(".ts.net", StringComparison.Ordinal)) return new(409, null, CameraTexts.NeedsPasskeySetUp);
        return new(200, JsonSerializer.Serialize(new { challenge = passkeys.CameraChallenge(), rpId = host, ids }), null);
    }

    // `sessionToken` is the caller's valid session; the ticket is bound to it.
    public Answer Unlock(string host, string? sessionToken, IReadOnlyDictionary<string, JsonElement> body)
    {
        var failed = new Answer(401, null, CameraTexts.PasskeyFailed);
        if (!allowed() || string.IsNullOrEmpty(sessionToken)) return failed;
        string? Field(string name) => body.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        if (Field("id") is not { } id
            || FromBase64Url(Field("clientData")) is not { } clientData
            || FromBase64Url(Field("authenticatorData")) is not { } authData
            || FromBase64Url(Field("signature")) is not { } signature)
            return failed;
        bool ok;
        try { ok = passkeys.VerifyCamera(id, clientData, authData, signature, host); }
        catch (Exception) { ok = false; }
        if (!ok) return failed;
        return new(200, JsonSerializer.Serialize(new { ticket = tickets.Issue(sessionToken) }), null);
    }

    // URL-safe base64 without padding (5.3).
    public static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    // URL-safe base64, padding optional. null: not base64url.
    public static byte[]? FromBase64Url(string? text)
    {
        if (text == null) return null;
        var s = text.TrimEnd('=');
        if (s.Any(ch => !(char.IsAsciiLetterOrDigit(ch) || ch == '-' || ch == '_'))) return null;
        s = s.Replace('-', '+').Replace('_', '/');
        s += (s.Length % 4) switch { 2 => "==", 3 => "=", 0 => "", _ => "!" };
        try { return Convert.FromBase64String(s); }
        catch (FormatException) { return null; }
    }
}
