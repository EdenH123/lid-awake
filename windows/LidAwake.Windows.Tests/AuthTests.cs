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

    // Three devices signed in one minute apart: the oldest first, so the list must reverse them.
    (Auth A, string[] Tokens) ThreeSessions(Settings? settings = null)
    {
        var a = NewAuth(settings);
        a.SetPassword(Good);
        var tokens = new string[3];
        var devices = new[] { "iPhone", "Mac", "Android phone" };
        for (int i = 0; i < 3; i++)
        {
            clock += TimeSpan.FromMinutes(1);
            tokens[i] = a.Login(Good, devices[i]).Token!;
        }
        return (a, tokens);
    }

    [Fact]
    public void SessionsAreListedNewestFirstWithTheirShortIdAndCurrentFlag()
    {
        var settings = NewSettings();
        var (a, t) = ThreeSessions(settings);
        var stored = settings.Get<List<Auth.Session>>("sessions")!;
        var list = a.Sessions(new[] { t[1] });

        Assert.Equal(new[] { "Android phone", "Mac", "iPhone" }, list.Select(s => s.Device));
        Assert.Equal(new[] { false, true, false }, list.Select(s => s.Current));
        Assert.True(list[0].Created > list[1].Created && list[1].Created > list[2].Created);
        Assert.Equal(clock.ToUnixTimeSeconds(), list[0].Created);
        foreach (var s in list)
        {
            Assert.Equal(12, s.Id.Length);
            Assert.Matches("^[0-9a-f]{12}$", s.Id);
            Assert.Contains(stored, x => x.Hash.StartsWith(s.Id, StringComparison.Ordinal));
        }
        Assert.Equal(3, list.Select(s => s.Id).Distinct().Count());
    }

    [Fact]
    public void NoSessionIsCurrentForAnUnknownTokenOrNone()
    {
        var (a, _) = ThreeSessions();
        Assert.All(a.Sessions(new[] { "made-up" }), s => Assert.False(s.Current));
        Assert.All(a.Sessions(Array.Empty<string>()), s => Assert.False(s.Current));
        Assert.All(a.Sessions(new string[] { "" }), s => Assert.False(s.Current));
    }

    [Fact]
    public void ExpiredSessionsAreNotListed()
    {
        var (a, t) = ThreeSessions();
        // After this the iPhone one (signed in 2 minutes before the last one) is just past 7 days, the Mac one is not.
        clock += TimeSpan.FromDays(Auth.SessionDays) - TimeSpan.FromSeconds(90);
        Assert.Equal(new[] { "Android phone", "Mac" }, a.Sessions(new[] { t[2] }).Select(s => s.Device));
    }

    [Fact]
    public void SignOutOneByIdRemovesOnlyThatSession()
    {
        var (a, t) = ThreeSessions();
        var mac = a.Sessions(new[] { t[2] }).Single(s => s.Device == "Mac");
        Assert.Equal(1, a.SignOut(mac.Id, new[] { t[2] }));
        Assert.True(a.IsValid(t[0]));
        Assert.False(a.IsValid(t[1]));
        Assert.True(a.IsValid(t[2]));
        Assert.Equal(2, a.Sessions(new[] { t[2] }).Count);
    }

    [Fact]
    public void SignOutNeverEndsTheCallersOwnSession()
    {
        var (a, t) = ThreeSessions();
        var own = a.Sessions(new[] { t[2] }).Single(s => s.Current);
        Assert.Equal(0, a.SignOut(own.Id, new[] { t[2] }));
        Assert.True(a.IsValid(t[2]));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("0123456789abc")]  // 13 characters
    [InlineData("000000000000")]   // 12 characters, matches nothing
    public void AnIdThatIsNotTwelveCharactersOrMatchesNothingChangesNothing(string? id)
    {
        var (a, t) = ThreeSessions();
        Assert.Equal(0, a.SignOut(id, new[] { t[2] }));
        Assert.All(t, token => Assert.True(a.IsValid(token)));
    }

    [Fact]
    public void ALongerIdThatStartsLikeARealOneDoesNotMatch()
    {
        var (a, t) = ThreeSessions();
        var other = a.Sessions(new[] { t[2] }).First(s => !s.Current);
        Assert.Equal(0, a.SignOut(other.Id + "0", new[] { t[2] }));
        Assert.Equal(0, a.SignOut(other.Id[..11], new[] { t[2] }));
        Assert.Equal(0, a.SignOut(other.Id.ToUpperInvariant() == other.Id ? other.Id + "x" : other.Id.ToUpperInvariant(), new[] { t[2] }));
        Assert.Equal(3, a.Sessions(new[] { t[2] }).Count);
    }

    [Fact]
    public void SignOutOthersKeepsTheCallersSession()
    {
        var (a, t) = ThreeSessions();
        Assert.Equal(2, a.SignOutOthers(new[] { t[1] }));
        Assert.False(a.IsValid(t[0]));
        Assert.True(a.IsValid(t[1]));
        Assert.False(a.IsValid(t[2]));
        var only = Assert.Single(a.Sessions(new[] { t[1] }));
        Assert.True(only.Current);
        Assert.Equal(0, a.SignOutOthers(new[] { t[1] }));
    }

    [Fact]
    public void SignOutOthersKeepsEveryCookieTheRequestCarried()
    {
        var (a, t) = ThreeSessions();
        Assert.Equal(1, a.SignOutOthers(new[] { t[0], t[1] }));
        Assert.True(a.IsValid(t[0]));
        Assert.True(a.IsValid(t[1]));
        Assert.False(a.IsValid(t[2]));
    }

    [Fact]
    public void SignOutsAreKeptOnDisk()
    {
        var settings = NewSettings();
        var (a, t) = ThreeSessions(settings);
        a.SignOutOthers(new[] { t[0] });
        var again = NewAuth(settings);
        Assert.True(again.IsValid(t[0]));
        Assert.False(again.IsValid(t[1]));
        Assert.False(again.IsValid(t[2]));
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
