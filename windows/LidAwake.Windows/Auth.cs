using System.Security.Cryptography;
using System.Text;

namespace LidAwake;

public record LoginResult(LoginResult.Kind Result, string? Token = null, int Seconds = 0)
{
    public enum Kind { Ok, Wrong, Locked, NoPassword }
}

// Password lock for the phone page, same rules as Auth.swift on the Mac: only a salted
// PBKDF2-SHA256 hash of the password is kept, sessions are SHA-256 hashes of random tokens
// (7 days) with the device that signed in, and wrong tries are counted across restarts and
// lock sign-in for 1 minute, then 2, 4, ... up to 1 hour.
public sealed class Auth
{
    public const string CookieName = "__Host-la_session";
    public const double SessionDays = 7;
    public const int MinPasswordLength = 10;
    public const int MaxTries = 5;

    // Stored in settings, so its properties must be public.
    public sealed record Session(string Hash, string Device, long Created);

    readonly Settings settings;
    readonly int iterations;
    readonly Func<DateTimeOffset> now;
    readonly object gate = new();

    public Auth(Settings settings, int iterations = 600_000, Func<DateTimeOffset>? now = null)
    {
        this.settings = settings;
        this.iterations = iterations;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public bool HasPassword => settings.Get<string>("pwHash") != null;

    public void SetPassword(string password)
    {
        if (password is null || password.Length < MinPasswordLength)
            throw new ArgumentException($"The password needs at least {MinPasswordLength} characters.", nameof(password));
        lock (gate)
        {
            var salt = RandomNumberGenerator.GetBytes(16);
            settings.Set("pwSalt", Convert.ToBase64String(salt));
            settings.Set("pwHash", Convert.ToBase64String(Derive(password, salt, iterations)));
            settings.Set("pwIterations", iterations);
            settings.Set("sessions", new List<Session>());  // a new password signs everyone out
            Failures = 0;
            LockedUntil = 0;
        }
    }

    // Wrong tries in a row (kept across restarts), and when the lockout ends in unix seconds.
    int Failures
    {
        get => settings.Get<int?>("loginFailures") ?? 0;
        set => settings.Set("loginFailures", value);
    }

    double LockedUntil
    {
        get => settings.Get<double?>("loginLockedUntil") ?? 0;
        set => settings.Set("loginLockedUntil", value);
    }

    double NowSeconds => now().ToUnixTimeMilliseconds() / 1000.0;

    public LoginResult Login(string password, string device)
    {
        lock (gate)
        {
            var saltText = settings.Get<string>("pwSalt");
            var hashText = settings.Get<string>("pwHash");
            if (saltText == null || hashText == null) return new(LoginResult.Kind.NoPassword);
            // Even the right password is refused while locked.
            var wait = LockedUntil - NowSeconds;
            if (wait > 0) return new(LoginResult.Kind.Locked, Seconds: (int)Math.Ceiling(wait));
            var rounds = settings.Get<int?>("pwIterations") ?? iterations;
            var ok = CryptographicOperations.FixedTimeEquals(
                Derive(password, Convert.FromBase64String(saltText), rounds), Convert.FromBase64String(hashText));
            if (!ok)
            {
                var failures = Failures + 1;
                Failures = failures;
                if (failures >= MaxTries)
                {
                    var seconds = (int)Math.Min(3600, 60 * Math.Pow(2, Math.Min(failures - MaxTries, 6)));
                    LockedUntil = NowSeconds + seconds;
                    return new(LoginResult.Kind.Locked, Seconds: seconds);
                }
                return new(LoginResult.Kind.Wrong);
            }
            Failures = 0;
            var token = Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
            var sessions = Live();
            sessions.Add(new Session(HashToken(token), device, now().ToUnixTimeSeconds()));
            settings.Set("sessions", sessions);
            return new(LoginResult.Kind.Ok, token);
        }
    }

    public bool IsValid(string? token)
    {
        if (string.IsNullOrEmpty(token)) return false;
        var hash = HashToken(token);
        lock (gate) return Live().Any(s => s.Hash == hash);
    }

    public void Logout(string? token)
    {
        if (string.IsNullOrEmpty(token)) return;
        var hash = HashToken(token);
        lock (gate)
        {
            var sessions = Live();
            // A made-up token signs nothing out.
            if (sessions.RemoveAll(s => s.Hash == hash) > 0) settings.Set("sessions", sessions);
        }
    }

    // "iPhone", "iPad", "Mac"... from the browser's description of itself (as on the Mac).
    public static string DeviceName(string? userAgent)
    {
        var ua = userAgent ?? "";
        foreach (var (needle, name) in new[] { ("iPhone", "iPhone"), ("iPad", "iPad"), ("Android", "Android phone"), ("Macintosh", "Mac"), ("Windows", "Windows PC") })
            if (ua.Contains(needle, StringComparison.Ordinal)) return name;
        return "Browser";
    }

    // Sessions that have not expired.
    List<Session> Live()
    {
        var oldest = now().ToUnixTimeSeconds() - (long)(SessionDays * 86400);
        return (settings.Get<List<Session>>("sessions") ?? new()).Where(s => s.Created > oldest).ToList();
    }

    static byte[] Derive(string password, byte[] salt, int rounds) =>
        Rfc2898DeriveBytes.Pbkdf2(Encoding.UTF8.GetBytes(password), salt, rounds, HashAlgorithmName.SHA256, 32);

    static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token))).ToLowerInvariant();
}
