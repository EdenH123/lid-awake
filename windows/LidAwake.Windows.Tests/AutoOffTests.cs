using LidAwake;
using Xunit;

public class AutoOffTests : IDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    DateTimeOffset clock = DateTimeOffset.FromUnixTimeSeconds(1_800_000_000);
    Settings? settings;

    Settings TheSettings => settings ??= new Settings(folder);

    AutoOff NewAutoOff() => new(TheSettings, () => clock);

    public void Dispose()
    {
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    [Fact]
    public void ChoicesAreTheMacMenuChoices()
    {
        Assert.Equal(new[] { 0, 15, 30, 60, 120, 240, 480 }, AutoOff.Choices.Select(c => c.Minutes));
        Assert.Equal(new[] { "Never", "15 minutes", "30 minutes", "1 hour", "2 hours", "4 hours", "8 hours" }, AutoOff.Choices.Select(c => c.Title));
        foreach (var minutes in new[] { 0, 15, 30, 60, 120, 240, 480 }) Assert.True(AutoOff.IsChoice(minutes));
        foreach (var minutes in new[] { -15, 1, 10, 45, 90, 481, 1440 }) Assert.False(AutoOff.IsChoice(minutes));
    }

    [Fact]
    public void StartsAtNeverWithNothingLeft()
    {
        var t = NewAutoOff();
        Assert.Equal(0, t.Minutes);
        t.Start();
        Assert.Null(t.Remaining());
        Assert.False(t.Expired());
    }

    [Fact]
    public void TheLengthIsKeptAcrossRestarts()
    {
        NewAutoOff().Set(120);
        // A second Settings on the same folder reads what the file holds, as after a restart.
        Assert.Equal(120, new AutoOff(new Settings(folder), () => clock).Minutes);
    }

    [Fact]
    public void ALengthThatIsNotAChoiceIsIgnoredWhenRead()
    {
        TheSettings.Set("timerMinutes", 45);
        Assert.Equal(0, NewAutoOff().Minutes);
    }

    [Fact]
    public void ALengthThatIsNotAChoiceIsRefused()
    {
        var t = NewAutoOff();
        Assert.Throws<ArgumentOutOfRangeException>(() => t.Set(45));
        Assert.Equal(0, t.Minutes);
    }

    [Fact]
    public void NothingIsLeftWhileOff()
    {
        var t = NewAutoOff();
        t.Set(30);
        Assert.Null(t.Remaining());
        Assert.False(t.Expired());
        clock += TimeSpan.FromHours(2);
        Assert.False(t.Expired());
    }

    [Fact]
    public void TurningOnStartsTheCountdown()
    {
        var t = NewAutoOff();
        t.Set(15);
        t.Start();
        Assert.Equal(900, t.Remaining()!.Value, 3);
        clock += TimeSpan.FromMinutes(5);
        Assert.Equal(600, t.Remaining()!.Value, 3);
        Assert.False(t.Expired());
    }

    [Fact]
    public void ItIsUpWhenTheTimeIsReached()
    {
        var t = NewAutoOff();
        t.Set(15);
        t.Start();
        clock += TimeSpan.FromMinutes(15) - TimeSpan.FromSeconds(1);
        Assert.False(t.Expired());
        Assert.NotNull(t.Remaining());
        clock += TimeSpan.FromSeconds(1);
        Assert.True(t.Expired());
        // "Only while in the future": nothing is left to report at the end or after it.
        Assert.Null(t.Remaining());
        clock += TimeSpan.FromMinutes(1);
        Assert.True(t.Expired());
        Assert.Null(t.Remaining());
    }

    [Fact]
    public void TurningOffEndsTheCountdown()
    {
        var t = NewAutoOff();
        t.Set(15);
        t.Start();
        t.Stop();
        Assert.Null(t.Remaining());
        clock += TimeSpan.FromHours(1);
        Assert.False(t.Expired());
        // Turning on again starts a fresh one.
        t.Start();
        Assert.Equal(900, t.Remaining()!.Value, 3);
    }

    [Fact]
    public void ChangingTheLengthWhileOnRestartsTheCountdownFromNow()
    {
        var t = NewAutoOff();
        t.Set(15);
        t.Start();
        clock += TimeSpan.FromMinutes(10);
        t.Set(60);
        Assert.Equal(3600, t.Remaining()!.Value, 3);
        // Choosing the same length again also gives more time.
        clock += TimeSpan.FromMinutes(30);
        t.Set(60);
        Assert.Equal(3600, t.Remaining()!.Value, 3);
    }

    [Fact]
    public void ChoosingNeverWhileOnRemovesTheCountdown()
    {
        var t = NewAutoOff();
        t.Set(15);
        t.Start();
        t.Set(0);
        Assert.Null(t.Remaining());
        clock += TimeSpan.FromHours(10);
        Assert.False(t.Expired());
    }

    [Fact]
    public void ChoosingALengthWhileOffDoesNotStartACountdown()
    {
        var t = NewAutoOff();
        t.Set(15);
        Assert.Null(t.Remaining());
        t.Start();
        Assert.Equal(900, t.Remaining()!.Value, 3);
    }

    [Fact]
    public async Task ReadingAndChangingFromManyThreadsIsSafe()
    {
        var t = NewAutoOff();
        t.Start();
        var lengths = AutoOff.Choices.Select(c => c.Minutes).ToArray();
        await Task.WhenAll(Enumerable.Range(0, 4).Select(w => Task.Run(() =>
        {
            for (int i = 0; i < 25; i++)
            {
                t.Set(lengths[(w + i) % lengths.Length]);
                _ = t.Remaining();
                _ = t.Expired();
                Assert.Contains(t.Minutes, lengths);
            }
        })));
        Assert.Contains(t.Minutes, lengths);
    }
}
