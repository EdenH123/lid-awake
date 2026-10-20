using LidAwake;
using Xunit;

// The lid and display notifications, the brightness behind a closed lid and the display hold,
// all with fakes: no WMI, no lid, no Windows power calls.
[Collection("ScreenWatch")]
public class LidScreenTests : IDisposable
{
    sealed class FakeBrightness : IBrightness
    {
        public int Level = 70;
        public int Lowest = 1;
        public bool NoPanel;
        public bool ReadThrows;
        public bool SetFails;
        public bool SetThrows;
        public bool SetTimesOut;  // the change is applied, but Set gives up waiting: a late finish
        public int Reads;
        public readonly List<int> SetCalls = new();

        public BrightnessReading? Read()
        {
            Reads++;
            if (ReadThrows) throw new InvalidOperationException("WMI is unavailable (fake)");
            return NoPanel ? null : new BrightnessReading(Level, Lowest);
        }

        public bool Set(int level)
        {
            SetCalls.Add(level);
            if (SetThrows) throw new InvalidOperationException("WMI is unavailable (fake)");
            if (SetTimesOut)
            {
                Level = level;
                throw new TimeoutException("Windows did not answer in time (fake)");
            }
            if (SetFails) return false;
            Level = level;
            return true;
        }
    }

    sealed class FakeHold : IDisplayHold
    {
        public bool Holding;
        public readonly List<bool> Calls = new();

        public void Hold(bool on)
        {
            Calls.Add(on);
            Holding = on;
        }
    }

    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    readonly Settings machine;
    readonly FakeBrightness panel = new();
    readonly FakeHold hold = new();
    readonly List<string> notes = new();
    readonly LidScreen screen;

    public LidScreenTests()
    {
        machine = new Settings(folder, Settings.MachineFileName);
        screen = Make(panel);
    }

    public void Dispose()
    {
        ScreenWatch.Connect(null);
        try { Directory.Delete(folder, true); }
        catch (Exception) { }
    }

    // Passes run on the spot, so every check sees the finished work.
    LidScreen Make(IBrightness b) => new(b, hold, machine, notes.Add, a => { a(); return Task.CompletedTask; }, FakeDelay);

    // Retries wait on these: a test finishes one when it wants that wait to be over.
    readonly List<(TimeSpan Wait, TaskCompletionSource Done)> delays = new();

    Task FakeDelay(TimeSpan wait)
    {
        var done = new TaskCompletionSource();
        delays.Add((wait, done));
        return done.Task;
    }

    // What machine.json holds on disk, not what the object remembers.
    int? SavedOnDisk() => new Settings(folder, Settings.MachineFileName).Get<int?>(LidScreen.SavedKey);

    static byte[] Dword(uint value) => BitConverter.GetBytes(value);

    // One boolean field of /status, built with this screen (none: no lid or display news).
    static bool Flag(LidScreen? s, string key) =>
        (bool)new StatusInfo(new FakeReadings()) { Screen = s }.Build(true)[key]!;

    // ---- /status: the lid and the display ----

    [Fact]
    public void StatusWithoutAScreenSaysOpenAndAwake()
    {
        Assert.False(Flag(null, "lidClosed"));
        Assert.False(Flag(null, "displayAsleep"));
        Assert.False(Flag(null, "screenOff"));
    }

    [Fact]
    public void LidNotificationsShowInStatus()
    {
        Assert.False(Flag(screen, "lidClosed"));
        screen.OnPowerSetting(LidScreen.LidSwitch, Dword(0));  // 0: closed
        Assert.True(Flag(screen, "lidClosed"));
        Assert.True(screen.LidClosed);
        screen.OnPowerSetting(LidScreen.LidSwitch, Dword(1));  // 1: open
        Assert.False(Flag(screen, "lidClosed"));
    }

    [Fact]
    public void DisplayNotificationsShowInStatus()
    {
        screen.OnPowerSetting(LidScreen.ConsoleDisplay, Dword(0));  // off
        Assert.True(Flag(screen, "displayAsleep"));
        screen.OnPowerSetting(LidScreen.ConsoleDisplay, Dword(2));  // dimmed is not asleep
        Assert.False(Flag(screen, "displayAsleep"));
        screen.OnPowerSetting(LidScreen.ConsoleDisplay, Dword(0));
        screen.OnPowerSetting(LidScreen.ConsoleDisplay, Dword(1));  // on
        Assert.False(Flag(screen, "displayAsleep"));
        // The lid and the display are separate answers.
        Assert.False(Flag(screen, "lidClosed"));
    }

    [Fact]
    public void NotificationsThatMeanNothingAreIgnored()
    {
        screen.OnPowerSetting(Guid.NewGuid(), Dword(0));                 // another setting
        screen.OnPowerSetting(LidScreen.LidSwitch, new byte[] { 0, 0 });  // too short
        screen.OnPowerSetting(LidScreen.ConsoleDisplay, Dword(7));       // not a display state
        Assert.False(screen.LidClosed);
        Assert.False(screen.DisplayAsleep);
    }

    [Fact]
    public void ThePowerBroadcastLayoutIsGuidLengthData()
    {
        var raw = new byte[24];
        LidScreen.LidSwitch.TryWriteBytes(raw);
        BitConverter.GetBytes(4).CopyTo(raw, 16);
        BitConverter.GetBytes(0u).CopyTo(raw, 20);
        var parsed = PowerSettingWindow.Parse(raw);
        Assert.NotNull(parsed);
        Assert.Equal(LidScreen.LidSwitch, parsed!.Value.Guid);
        Assert.Equal(Dword(0), parsed.Value.Data);

        Assert.Null(PowerSettingWindow.Parse(new byte[10]));            // no header
        BitConverter.GetBytes(40).CopyTo(raw, 16);
        Assert.Null(PowerSettingWindow.Parse(raw));                     // says more data than there is
    }

    // ---- brightness ----

    [Fact]
    public void LidClosedWhileOnSavesTheLevelThenLowersIt()
    {
        screen.SetOn(true);
        screen.SetLid(true);
        Assert.Equal(70, SavedOnDisk());
        Assert.Equal(new[] { 1 }, panel.SetCalls);
        Assert.Equal(1, panel.Level);
    }

    [Fact]
    public void TheLevelIsSavedBeforeItIsLowered()
    {
        // A crash right after the level is lowered must still leave the old level on disk.
        int? seenWhenLowering = null;
        var b = new SavingChecker(() => SavedOnDisk(), v => seenWhenLowering = v);
        var s = Make(b);
        s.SetOn(true);
        s.SetLid(true);
        Assert.Equal(70, seenWhenLowering);
    }

    sealed class SavingChecker : IBrightness
    {
        readonly Func<int?> saved;
        readonly Action<int?> seen;
        public SavingChecker(Func<int?> saved, Action<int?> seen) => (this.saved, this.seen) = (saved, seen);
        public BrightnessReading? Read() => new BrightnessReading(70, 1);
        public bool Set(int level) { seen(saved()); return true; }
    }

    [Fact]
    public void OpeningTheLidPutsTheLevelBack()
    {
        screen.SetOn(true);
        screen.SetLid(true);
        screen.SetLid(false);
        Assert.Equal(70, panel.Level);
        Assert.Null(SavedOnDisk());
    }

    [Fact]
    public void TurningOffWithTheLidClosedPutsTheLevelBack()
    {
        screen.SetOn(true);
        screen.SetLid(true);
        Assert.True(screen.SetOn(false).IsCompletedSuccessfully);
        Assert.Equal(70, panel.Level);
        Assert.Null(SavedOnDisk());
        // Closing it again while off does nothing to the screen.
        panel.SetCalls.Clear();
        screen.SetLid(false);
        screen.SetLid(true);
        Assert.Empty(panel.SetCalls);
        Assert.Equal(70, panel.Level);
    }

    [Fact]
    public void LidClosedWhileOffTouchesNothing()
    {
        screen.SetLid(true);
        screen.SetLid(false);
        Assert.Equal(0, panel.Reads);
        Assert.Empty(panel.SetCalls);
        Assert.Null(SavedOnDisk());
    }

    [Fact]
    public void TurningOnWithTheLidAlreadyClosedLowersIt()
    {
        screen.SetLid(true);
        screen.SetOn(true);
        Assert.Equal(70, SavedOnDisk());
        Assert.Equal(1, panel.Level);
    }

    [Fact]
    public void ClosingTheLidTwiceKeepsTheFirstSavedLevel()
    {
        screen.SetOn(true);
        screen.SetLid(true);
        screen.SetOn(true);   // asked again: the level now is the lowered one, not the real one
        screen.SetWatching(true);
        Assert.Equal(70, SavedOnDisk());
        Assert.Equal(new[] { 1 }, panel.SetCalls);
    }

    [Fact]
    public void ACrashLeavesTheLevelSavedAndTheNextStartPutsItBack()
    {
        screen.SetOn(true);
        screen.SetLid(true);
        // The app is gone: nothing ran Off. A new run finds the saved level in machine.json.
        var after = new FakeBrightness { Level = 1 };
        var restarted = new LidScreen(after, hold, new Settings(folder, Settings.MachineFileName), notes.Add,
            a => { a(); return Task.CompletedTask; });
        Assert.True(restarted.Recover().IsCompletedSuccessfully);
        Assert.Equal(new[] { 70 }, after.SetCalls);
        Assert.Equal(70, after.Level);
        Assert.Null(SavedOnDisk());
        // Once is enough: another start has nothing to put back.
        after.SetCalls.Clear();
        Assert.True(restarted.Recover().IsCompletedSuccessfully);
        Assert.Empty(after.SetCalls);
    }

    [Fact]
    public void ALevelStillSavedFromACrashIsNotReplacedByTheLoweredOne()
    {
        // The next start could not put it back (Windows refused), and the lid is closed now.
        machine.Set(LidScreen.SavedKey, 55);
        var after = new FakeBrightness { Level = 1, SetFails = true };
        var restarted = Make(after);
        restarted.SetOn(true);  // the put-back fails: the level stays saved
        Assert.Equal(55, SavedOnDisk());
        after.SetFails = false;
        restarted.SetLid(true);  // the screen is at the lowered level, which must not be saved
        Assert.Equal(55, SavedOnDisk());
        restarted.SetLid(false);
        Assert.Equal(55, after.Level);
        Assert.Null(SavedOnDisk());
    }

    [Fact]
    public void ASavedLevelIsKeptWhenTheRestoreFailsAndTriedAgainLater()
    {
        screen.SetOn(true);
        screen.SetLid(true);
        panel.SetFails = true;
        screen.SetLid(false);
        Assert.Equal(70, SavedOnDisk());
        Assert.Single(notes);
        panel.SetFails = false;
        screen.SetOn(false);
        Assert.Equal(70, panel.Level);
        Assert.Null(SavedOnDisk());
    }

    // ---- WMI failures ----

    [Fact]
    public void AReadThatFailsChangesNothingAndLogsOnce()
    {
        panel.ReadThrows = true;
        screen.SetOn(true);
        screen.SetLid(true);
        screen.SetLid(false);
        screen.SetLid(true);
        screen.SetWatching(true);
        Assert.Empty(panel.SetCalls);
        Assert.Null(SavedOnDisk());
        Assert.Single(notes);
        Assert.Equal(70, panel.Level);
    }

    [Fact]
    public void ASetThatFailsChangesNothingAndLogsOnce()
    {
        panel.SetThrows = true;
        screen.SetOn(true);
        screen.SetLid(true);
        screen.SetLid(false);
        screen.SetLid(true);
        Assert.Null(SavedOnDisk());  // nothing was lowered, so nothing waits to be put back
        Assert.Single(notes);
        Assert.Equal(70, panel.Level);
    }

    [Fact]
    public void AnRefusedSetChangesNothingAndLogsOnce()
    {
        panel.SetFails = true;
        screen.SetOn(true);
        screen.SetLid(true);
        screen.SetLid(false);
        screen.SetLid(true);
        Assert.Null(SavedOnDisk());
        Assert.Single(notes);
        Assert.Equal(70, panel.Level);
    }

    [Fact]
    public void NoBuiltInScreenChangesNothingAndLogsOnce()
    {
        panel.NoPanel = true;
        screen.SetOn(true);
        screen.SetLid(true);
        screen.SetLid(false);
        screen.SetLid(true);
        Assert.Empty(panel.SetCalls);
        Assert.Null(SavedOnDisk());
        Assert.Single(notes);
    }

    [Fact]
    public void AFailureIsSaidAgainOnlyAfterSomethingWorkedInBetween()
    {
        panel.ReadThrows = true;
        screen.SetOn(true);
        screen.SetLid(true);
        Assert.Single(notes);
        panel.ReadThrows = false;
        screen.SetLid(false);
        screen.SetLid(true);  // works
        Assert.Equal(1, panel.Level);
        panel.SetThrows = true;
        screen.SetLid(false);  // the restore fails
        Assert.Equal(2, notes.Count);
    }

    [Fact]
    public void ALogThatThrowsBreaksNothing()
    {
        var s = new LidScreen(new FakeBrightness { ReadThrows = true }, hold, machine, _ => throw new IOException("disk full"),
            a => { a(); return Task.CompletedTask; });
        s.SetOn(true);
        s.SetLid(true);
        Assert.True(s.LidClosed);
    }

    // ---- the display hold ----

    [Fact]
    public void TheDisplayIsHeldOnlyWhileWatchedAndOn()
    {
        screen.SetWatching(true);
        Assert.False(hold.Holding);          // off: no hold
        screen.SetOn(true);
        Assert.True(hold.Holding);           // on and watched
        screen.SetWatching(false);
        Assert.False(hold.Holding);          // nobody watches
        screen.SetWatching(true);
        Assert.True(hold.Holding);
        screen.SetOn(false);
        Assert.False(hold.Holding);          // off again
        Assert.Equal(new[] { true, false, true, false }, hold.Calls);
    }

    [Fact]
    public void TheDisplayHoldIsNotAskedForTwice()
    {
        screen.SetOn(true);
        screen.SetWatching(true);
        screen.SetWatching(true);
        screen.SetLid(true);
        screen.SetLid(false);
        Assert.Equal(new[] { true }, hold.Calls);
    }

    [Fact]
    public void WatchingWithTheLidOpenOnlyHoldsTheDisplay()
    {
        screen.SetOn(true);
        screen.SetWatching(true);
        Assert.Empty(panel.SetCalls);
        Assert.Equal(0, panel.Reads);
    }

    [Fact]
    public void TheScreenWatchHookReachesTheScreen()
    {
        ScreenWatch.SetWatching(true);  // nobody listening yet: nothing happens
        ScreenWatch.Connect(screen.SetWatching);
        screen.SetOn(true);
        ScreenWatch.SetWatching(true);
        Assert.True(hold.Holding);
        ScreenWatch.SetViewers(0);
        Assert.False(hold.Holding);
        ScreenWatch.SetViewers(2);
        Assert.True(hold.Holding);
    }

    [Fact]
    public void AHoldThatThrowsBreaksNothing()
    {
        var s = new LidScreen(panel, new ThrowingHold(), machine, notes.Add, a => { a(); return Task.CompletedTask; });
        s.SetOn(true);
        s.SetWatching(true);
        s.SetLid(true);
        Assert.Equal(1, panel.Level);  // the brightness part still ran
    }

    sealed class ThrowingHold : IDisplayHold
    {
        public void Hold(bool on) => throw new InvalidOperationException("no hold (fake)");
    }

    // ---- a change that timed out ----

    [Fact]
    public void ALowerThatTimesOutKeepsTheSavedLevelSoItCanBePutBack()
    {
        panel.SetTimesOut = true;  // Windows applied it anyway, but too late for the wait
        screen.SetOn(true);
        screen.SetLid(true);
        Assert.Equal(1, panel.Level);
        Assert.Equal(70, SavedOnDisk());  // not forgotten
        Assert.Single(notes);
        panel.SetTimesOut = false;
        screen.SetLid(false);
        Assert.Equal(70, panel.Level);
        Assert.Null(SavedOnDisk());
    }

    [Fact]
    public void ALowerThatTimedOutIsPutBackAtTheNextStart()
    {
        panel.SetTimesOut = true;
        screen.SetOn(true);
        screen.SetLid(true);
        var after = new FakeBrightness { Level = 1 };
        var restarted = new LidScreen(after, hold, new Settings(folder, Settings.MachineFileName), notes.Add,
            a => { a(); return Task.CompletedTask; }, FakeDelay);
        Assert.True(restarted.Recover().IsCompletedSuccessfully);
        Assert.Equal(70, after.Level);
        Assert.Null(SavedOnDisk());
    }

    [Fact]
    public void ACallThatFinishesLateStillFinishesBeforeTheNextOneStarts()
    {
        var calls = new OrderedCalls();
        var order = new List<string>();
        using var release = new ManualResetEventSlim(false);
        using var secondRan = new ManualResetEventSlim(false);
        Assert.Throws<TimeoutException>(() => calls.Run(() =>
        {
            release.Wait();
            lock (order) order.Add("late");
            return 1;
        }, TimeSpan.FromMilliseconds(100)));
        _ = Task.Run(() => calls.Run(() =>
        {
            lock (order) order.Add("second");
            secondRan.Set();
            return 2;
        }, TimeSpan.FromSeconds(30)));
        Assert.False(secondRan.Wait(300));  // still waiting behind the late one
        release.Set();
        Assert.True(secondRan.Wait(30_000));
        lock (order) Assert.Equal(new[] { "late", "second" }, order);
    }

    [Fact]
    public void AnOrderedCallGivesBackItsAnswerAndItsException()
    {
        var calls = new OrderedCalls();
        Assert.Equal(7, calls.Run(() => 7, TimeSpan.FromSeconds(30)));
        Assert.Throws<InvalidOperationException>(() => calls.Run<int>(() => throw new InvalidOperationException("no"), TimeSpan.FromSeconds(30)));
        Assert.Equal(8, calls.Run(() => 8, TimeSpan.FromSeconds(30)));  // a failure does not block the next
    }

    // ---- a put-back that fails is tried again ----

    void LowerThenFailToRestore()
    {
        screen.SetOn(true);
        screen.SetLid(true);
        panel.SetFails = true;
        screen.SetLid(false);
        Assert.Equal(70, SavedOnDisk());
    }

    [Fact]
    public void AFailedPutBackIsTriedAgainAfterAShortWait()
    {
        LowerThenFailToRestore();
        Assert.Equal(new[] { TimeSpan.FromSeconds(3) }, delays.Select(d => d.Wait));
        panel.SetFails = false;
        delays[0].Done.SetResult();
        Assert.Equal(70, panel.Level);
        Assert.Null(SavedOnDisk());
        Assert.Single(delays);  // it worked: no more retries
    }

    [Fact]
    public void ARetryThatFailsTriesOnceMoreThenStops()
    {
        LowerThenFailToRestore();
        delays[0].Done.SetResult();                    // fails again
        Assert.Equal(new[] { TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(10) }, delays.Select(d => d.Wait));
        panel.SetFails = false;
        delays[1].Done.SetResult();                    // works
        Assert.Equal(70, panel.Level);
        Assert.Null(SavedOnDisk());
        Assert.Equal(2, delays.Count);
    }

    [Fact]
    public void RetriesAreNotEndless()
    {
        LowerThenFailToRestore();
        delays[0].Done.SetResult();
        delays[1].Done.SetResult();
        Assert.Equal(2, delays.Count);
        Assert.Equal(70, SavedOnDisk());  // still saved: the next event or start tries again
        Assert.Single(notes);             // and it was said once
    }

    [Fact]
    public void ARetryMadeStaleByANewerEventDoesNothing()
    {
        LowerThenFailToRestore();
        panel.SetFails = false;
        screen.SetWatching(true);          // a newer pass puts it back
        Assert.Equal(70, panel.Level);
        int calls = panel.SetCalls.Count;
        delays[0].Done.SetResult();        // the old wait ends: nothing to do
        Assert.Equal(calls, panel.SetCalls.Count);
    }

    [Fact]
    public void TheDisplayComingBackOnPutsBackALevelThatCouldNotBePutBack()
    {
        screen.SetOn(true);
        screen.SetLid(true);
        screen.OnPowerSetting(LidScreen.ConsoleDisplay, Dword(0));   // the panel goes off with the lid
        panel.SetFails = true;
        screen.SetLid(false);                                        // WMI is not ready yet
        Assert.Equal(70, SavedOnDisk());
        panel.SetFails = false;
        screen.OnPowerSetting(LidScreen.ConsoleDisplay, Dword(1));   // the panel is back on
        Assert.Equal(70, panel.Level);
        Assert.Null(SavedOnDisk());
    }

    [Fact]
    public void TheDisplayStateOtherThanOnDoesNotStartAPass()
    {
        screen.SetOn(true);
        screen.SetLid(true);
        panel.SetCalls.Clear();
        screen.OnPowerSetting(LidScreen.ConsoleDisplay, Dword(0));
        screen.OnPowerSetting(LidScreen.ConsoleDisplay, Dword(2));
        Assert.Empty(panel.SetCalls);
        Assert.Equal(1, panel.Level);
    }

    // ---- the phone turned the display off ----

    [Fact]
    public void TheDisplayIsNotHeldWhileTheScreenIsOff()
    {
        screen.SetOn(true);
        screen.SetWatching(true);
        Assert.True(hold.Holding);
        ScreenWatch.Connect(screen.SetWatching, screen.SetScreenOff);
        ScreenWatch.SetScreenOff(true);
        Assert.True(screen.ScreenOff);
        Assert.False(hold.Holding);
        screen.SetWatching(false);
        screen.SetWatching(true);
        Assert.False(hold.Holding);        // watched, but the phone turned it off
        ScreenWatch.SetScreenOff(false);
        Assert.True(hold.Holding);         // woken, and still watched
    }
}
