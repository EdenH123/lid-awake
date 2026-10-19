using LidAwake;
using Xunit;

public class AuthTests : IDisposable
{
    const string Good = "correct horse battery";

    readonly List<string> folders = new();
    DateTimeOffset clock = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);

    Settings NewSettings()
    {
        var folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        folders.Add(folder);
        return new Settings(folder);
    }

    // The clock only moves when a test moves it.
    Auth NewAuth(Settings? settings = null) =>
        new(settings ?? NewSettings(), iterations: 1000, now: () => clock);

    public void Dispose()
    {
        foreach (var folder in folders)
        {
            try
            {
                Directory.Delete(folder, true);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    [Fact]
    public void NoPasswordYet() => Assert.Equal(LoginResult.Kind.NoPassword, NewAuth().Login("anything", "Phone").Result);

    [Fact]
    public void RightPasswordGivesValidSession()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        var r = a.Login(Good, "Phone");
        Assert.Equal(LoginResult.Kind.Ok, r.Result);
        Assert.True(a.IsValid(r.Token));
        Assert.False(a.IsValid("not-a-token"));
        Assert.False(a.IsValid(null));
    }

    [Fact]
    public void WrongPasswordRefusedThenLocked()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries - 1; i++) Assert.Equal(LoginResult.Kind.Wrong, a.Login("nope", "Phone").Result);
        var fifth = a.Login("nope", "Phone");
        Assert.Equal(LoginResult.Kind.Locked, fifth.Result);
        Assert.Equal(60, fifth.Seconds);
    }

    [Fact]
    public void CorrectPasswordDuringLockoutIsLocked()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries; i++) a.Login("nope", "Phone");
        clock += TimeSpan.FromSeconds(30);
        var r = a.Login(Good, "Phone");
        Assert.Equal(LoginResult.Kind.Locked, r.Result);
        Assert.Equal(30, r.Seconds);
        Assert.Null(r.Token);
    }

    [Fact]
    public void RemainingSecondsRoundUp()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries; i++) a.Login("nope", "Phone");
        clock += TimeSpan.FromMilliseconds(30_500);
        Assert.Equal(30, a.Login(Good, "Phone").Seconds);
    }

    [Fact]
    public void CorrectPasswordWorksAgainWhenTheLockExpires()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries; i++) a.Login("nope", "Phone");
        clock += TimeSpan.FromSeconds(61);
        Assert.Equal(LoginResult.Kind.Ok, a.Login(Good, "Phone").Result);
    }

    [Fact]
    public void SixthWrongTryAfterTheLockExpiresLocksForTwoMinutes()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries; i++) a.Login("nope", "Phone");
        clock += TimeSpan.FromSeconds(61);
        var sixth = a.Login("nope", "Phone");
        Assert.Equal(LoginResult.Kind.Locked, sixth.Result);
        Assert.Equal(120, sixth.Seconds);
    }

    [Fact]
    public void LockoutGrowsToAnHourAndStopsThere()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries - 1; i++) a.Login("nope", "Phone");
        LoginResult last = a.Login("nope", "Phone");
        for (int i = 0; i < 15; i++)
        {
            clock += TimeSpan.FromSeconds(4000);
            last = a.Login("nope", "Phone");
        }
        Assert.Equal(LoginResult.Kind.Locked, last.Result);
        Assert.Equal(3600, last.Seconds);
    }

    [Fact]
    public void CorrectPasswordResetsTheCount()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries - 1; i++) a.Login("nope", "Phone");
        Assert.Equal(LoginResult.Kind.Ok, a.Login(Good, "Phone").Result);
        for (int i = 0; i < Auth.MaxTries - 1; i++) Assert.Equal(LoginResult.Kind.Wrong, a.Login("nope", "Phone").Result);
    }

    [Fact]
    public void CountAndLockSurviveANewAuthOnTheSameSettings()
    {
        var settings = NewSettings();
        var a = NewAuth(settings);
        a.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries - 1; i++) a.Login("nope", "Phone");

        var b = NewAuth(settings);
        var fifth = b.Login("nope", "Phone");
        Assert.Equal(LoginResult.Kind.Locked, fifth.Result);

        var c = NewAuth(settings);
        Assert.Equal(LoginResult.Kind.Locked, c.Login(Good, "Phone").Result);
    }

    [Fact]
    public void SetPasswordClearsTheLock()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        for (int i = 0; i < Auth.MaxTries; i++) a.Login("nope", "Phone");
        Assert.Equal(LoginResult.Kind.Locked, a.Login(Good, "Phone").Result);
        a.SetPassword("another long password");
        Assert.Equal(LoginResult.Kind.Ok, a.Login("another long password", "Phone").Result);
        // The count started again too: four wrong tries are still only Wrong.
        for (int i = 0; i < Auth.MaxTries - 1; i++) Assert.Equal(LoginResult.Kind.Wrong, a.Login("nope", "Phone").Result);
    }

    [Fact]
    public void LogoutEndsSession()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        var t = a.Login(Good, "Phone").Token!;
        a.Logout(t);
        Assert.False(a.IsValid(t));
    }

    [Fact]
    public void MadeUpTokenSignsNobodyOut()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        var t = a.Login(Good, "Phone").Token!;
        a.Logout("not-a-token");
        a.Logout(null);
        Assert.True(a.IsValid(t));
    }

    [Fact]
    public void NewPasswordSignsEveryoneOut()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        var t = a.Login(Good, "Phone").Token!;
        a.SetPassword("another long password");
        Assert.False(a.IsValid(t));
    }

    [Fact]
    public void SessionLastsSevenDays()
    {
        var a = NewAuth();
        a.SetPassword(Good);
        var t = a.Login(Good, "Phone").Token!;
        clock += TimeSpan.FromDays(6.9);
        Assert.True(a.IsValid(t));
        clock += TimeSpan.FromDays(0.2);
        Assert.False(a.IsValid(t));
    }

    [Fact]
    public void SessionKeepsItsDeviceAndOnlyAHashOfTheToken()
    {
        var settings = NewSettings();
        var a = NewAuth(settings);
        a.SetPassword(Good);
        var t = a.Login(Good, "iPhone").Token!;
        var stored = Assert.Single(settings.Get<List<Auth.Session>>("sessions")!);
        Assert.Equal("iPhone", stored.Device);
        Assert.NotEqual(t, stored.Hash);
        Assert.Equal(64, stored.Hash.Length);
    }

    [Fact]
    public void ShortPasswordIsRefused()
    {
        var a = NewAuth();
        Assert.Throws<ArgumentException>(() => a.SetPassword(new string('x', Auth.MinPasswordLength - 1)));
        Assert.False(a.HasPassword);
        a.SetPassword(new string('x', Auth.MinPasswordLength));
        Assert.True(a.HasPassword);
    }

    [Theory]
    [InlineData("Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X) AppleWebKit/605.1.15", "iPhone")]
    [InlineData("Mozilla/5.0 (Linux; Android 14; Pixel 8) AppleWebKit/537.36 Chrome/120 Mobile Safari/537.36", "Android phone")]
    [InlineData("Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 Chrome/120 Safari/537.36", "Windows PC")]
    [InlineData("Mozilla/5.0 (Macintosh; Intel Mac OS X 10_15_7) AppleWebKit/605.1.15", "Mac")]
    [InlineData("curl/8.4.0", "Browser")]
    [InlineData(null, "Browser")]
    public void DeviceNameFromUserAgent(string? userAgent, string expected) =>
        Assert.Equal(expected, Auth.DeviceName(userAgent));
}
