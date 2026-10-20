using System.Text.Json;
using Microsoft.AspNetCore.Http;

namespace LidAwake;

// The passkey routes (docs/API.md 5.3), as in Server.swift. Signing in: a one-time challenge,
// then the phone's signature. Adding one: signed in, and the password typed again before the
// phone is even asked (the challenge is only handed out after the password checks out).
public sealed partial class WebServer
{
    const string PasskeyLoginFailed = "Face ID sign-in didn't work. Use your password.";
    const string PasskeyRegisterFailed = "Couldn't turn on Face ID. Try again.";

    // POST /passkey/login (public): a session marked as a passkey one.
    async Task PasskeyLogin(HttpContext c, string host)
    {
        var body = await ReadJson(c.Request);
        var device = Auth.DeviceName(c.Request.Headers.UserAgent.ToString());
        if (!auth.HasPassword
            || Text(body, "id") is not { } id
            || Passkeys.FromBase64Url(Text(body, "clientData")) is not { } clientData
            || Passkeys.FromBase64Url(Text(body, "authenticatorData")) is not { } authData
            || Passkeys.FromBase64Url(Text(body, "signature")) is not { } signature
            || !auth.Passkeys.Verify(id, clientData, authData, signature, host))
        {
            await SendText(c, 401, PasskeyLoginFailed);
            return;
        }
        var token = auth.LoginWithPasskey(id, device);
        c.Response.Headers.Append("Set-Cookie",
            $"{Auth.CookieName}={token}; Path=/; Max-Age={(int)(Auth.SessionDays * 86400)}; HttpOnly; SameSite=Strict; Secure");
        // Not announced, as on the Mac: only password sign-ins and added passkeys are.
        Empty(c);
    }

    // POST /passkey/register-options: the password again, with the same lockout as /login.
    async Task PasskeyRegisterOptions(HttpContext c, string host)
    {
        var body = await ReadJson(c.Request);
        var r = auth.Confirm(Text(body, "password") ?? "");
        switch (r.Result)
        {
            case LoginResult.Kind.Ok:
                await SendJson(c, new { challenge = auth.Passkeys.Challenge("create"), rpId = host, ids = auth.Passkeys.CredentialIds });
                return;
            case LoginResult.Kind.Locked:
                await SendText(c, 429, $"Too many wrong tries. Try again in {r.Seconds} seconds.");
                return;
            default:
                await SendText(c, 401, "Wrong password.");
                return;
        }
    }

    // POST /passkey/register: keeps the phone's public key, and tells the owner.
    async Task PasskeyRegister(HttpContext c, string host)
    {
        var body = await ReadJson(c.Request);
        var device = Auth.DeviceName(c.Request.Headers.UserAgent.ToString());
        if (Passkeys.FromBase64Url(Text(body, "clientData")) is not { } clientData
            || Passkeys.FromBase64Url(Text(body, "attestation")) is not { } attestation
            || !auth.Passkeys.Register(clientData, attestation, host, device))
        {
            await SendText(c, 400, PasskeyRegisterFailed);
            return;
        }
        Empty(c);
        var where = device == "Browser" ? "On a browser." : $"On an {device}.";
        var announcement = where + " If this wasn't you, remove it from the Lid Awake menu on your PC.";
        LogQuietly("Face ID or fingerprint added to Lid Awake · " + announcement);
        try { announce("Face ID or fingerprint added to Lid Awake", announcement); }
        catch (Exception) { }
    }

    // A text field of a JSON body, or null.
    static string? Text(Dictionary<string, JsonElement> body, string name) =>
        body.TryGetValue(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
