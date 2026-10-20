using System.Drawing.Imaging;
using LidAwake;
using Xunit;

// The process table (one NtQuerySystemInformation call), and who used the processor between two
// snapshots (AppCpu), with fake snapshots where the numbers must be exact.
public sealed class ProcessTableTests
{
    const string Windows = @"C:\Windows";
    const string Chrome = @"C:\Program Files\Google\Chrome\Application\chrome.exe";
    const string Code = @"C:\Program Files\Microsoft VS Code\Code.exe";
    const long Second = 10_000_000;  // 100-nanosecond units
    const long Before = 133_000_000_000_000_000;  // a FILETIME

    static ProcessEntry P(int pid, string? path, long cpu, long created = 1, string? name = null)
        => new(pid, created, name ?? (path == null ? "x.exe" : Path.GetFileName(path)), path, 1 << 20, 1 << 20, cpu);

    static CpuApp? Who(ProcessEntry p) => AppCpu.Who(p, Windows, exe => Path.GetFileNameWithoutExtension(exe));

    // ---- the page files (a fake NtQuerySystemInformation) ----

    static void Fill(NativeBuffer buffer, int length, byte value) =>
        System.Runtime.InteropServices.Marshal.Copy(Enumerable.Repeat(value, length).ToArray(), 0, buffer.DangerousGetHandle(), length);

    // No page file: Windows answers success with a length of 0 and writes nothing. Whatever the
    // memory held must not be read as page-file use.
    [Fact]
    public void NoPageFileIsZero()
    {
        Assert.Equal(0UL, ProcessTable.PageFileInUse((int _, NativeBuffer buffer, int length, out int needed) =>
        {
            Fill(buffer, length, 0xFF);
            needed = 0;
            return 0;
        }));
    }

    // The buffer starts zeroed, and only the bytes Windows wrote are read.
    [Fact]
    public void OnlyTheEntriesWindowsWroteAreSummed()
    {
        int calls = 0;
        var used = ProcessTable.PageFileInUse((int infoClass, NativeBuffer buffer, int length, out int needed) =>
        {
            calls++;
            Assert.Equal(18, infoClass);
            if (calls == 1) { needed = 10_000; return 0xC0000004; }  // too small: grown and asked again
            Assert.True(length >= 10_000);
            var p = buffer.DangerousGetHandle();
            System.Runtime.InteropServices.Marshal.WriteInt32(p, 0, 0);     // the last (only) entry
            System.Runtime.InteropServices.Marshal.WriteInt32(p, 8, 10);    // 10 pages in use
            needed = 32;
            return 0;
        });
        Assert.Equal(2, calls);
        Assert.Equal(10UL * (ulong)Environment.SystemPageSize, used);
        Assert.Null(ProcessTable.PageFileInUse((int _, NativeBuffer _, int _, out int needed) => { needed = 0; return 0xC0000022; }));
    }

    [Fact]
    public void ABufferStartsZeroed()
    {
        using var buffer = NativeBuffer.Allocate(4096);
        var bytes = new byte[4096];
        System.Runtime.InteropServices.Marshal.Copy(buffer.DangerousGetHandle(), bytes, 0, bytes.Length);
        Assert.All(bytes, b => Assert.Equal(0, b));
    }

    // ---- who used the processor ----

    [Fact]
    public void TheDiffIsByExeBetweenTwoSnapshots()
    {
        var before = new ProcessSnapshot(new[]
        {
            P(0, null, 900 * Second, created: 0, name: "Idle"),
            P(4, null, 10 * Second, created: 0, name: "System"),
            P(100, Chrome, 5 * Second),
            P(101, Chrome, 1 * Second),
            P(200, Code, 2 * Second),
            P(300, @"C:\Old\gone.exe", 7 * Second),
        }, Before);
        var after = new ProcessSnapshot(new[]
        {
            P(0, null, 960 * Second, created: 0, name: "Idle"),               // idle time: never counted
            P(4, null, 11 * Second, created: 0, name: "System"),              // +1 s, Windows core
            P(100, Chrome, 8 * Second),                                       // +3 s
            P(101, Chrome, 2 * Second),                                       // +1 s
            P(102, Chrome, 2 * Second, created: Before + Second),             // new since: all 2 s
            P(103, Chrome, 9 * Second, created: Before - Second),             // older, but not seen before: left out
            P(200, Code, 2 * Second),                                         // used nothing
            P(201, Code, 1 * Second, created: 5),                             // older than the first snapshot, but missed by it: left out
            P(500, @"C:\Windows\System32\dwm.exe", 4 * Second, created: Before + 1),
            P(501, @"C:\Windows\System32\svchost.exe", Second / 2, created: Before + 1),
            P(502, null, Second, created: Before + 1, name: "MsMpEng.exe"),
        }, Before + 60 * Second);
        var used = AppCpu.Diff(before, after, Who);

        var chrome = used[PhoneApps.Id(Chrome)];
        Assert.Equal(6.0, chrome.Seconds, 6);
        Assert.Equal("app", chrome.App.Kind);
        Assert.Equal("chrome", chrome.App.Name);
        Assert.Equal(PhoneApps.Normalize(Chrome), chrome.App.Exe);
        Assert.False(used.ContainsKey(PhoneApps.Id(Code)));
        Assert.Equal(1.0, used["other.core"].Seconds, 6);
        Assert.Equal(4.0, used["other.screen"].Seconds, 6);
        Assert.Equal(0.5, used["other.windows"].Seconds, 6);
        Assert.Equal(1.0, used["other.msmpeng"].Seconds, 6);
        Assert.All(used.Values, u => Assert.Contains(u.App.Kind, new[] { "app", "other" }));
        Assert.Equal(5, used.Count);
    }

    // A pid used again by a new process is a different process.
    [Fact]
    public void APidUsedAgainIsANewProcess()
    {
        var before = new ProcessSnapshot(new[] { P(100, Chrome, 50 * Second, created: Before - 100) }, Before);
        var after = new ProcessSnapshot(new[] { P(100, Code, 3 * Second, created: Before + 10) }, Before + 60 * Second);
        var used = AppCpu.Diff(before, after, Who);
        Assert.Equal(3.0, Assert.Single(used).Value.Seconds, 6);
        Assert.True(used.ContainsKey(PhoneApps.Id(Code)));
    }

    [Fact]
    public void WindowsOwnProcessesAreOtherNeverMacos()
    {
        Assert.Null(Who(P(0, null, 0, name: "Idle")));
        Assert.Equal("other.core", Who(P(4, null, 0, name: "System"))!.Key);
        Assert.Equal("other.core", Who(P(88, null, 0, name: "Memory Compression"))!.Key);
        Assert.Equal("other.core", Who(P(89, null, 0, name: "Registry"))!.Key);
        Assert.Equal(("other.screen", "Screen & windows"), (Who(P(90, @"C:\WINDOWS\system32\dwm.exe", 0))!.Key, Who(P(90, @"C:\WINDOWS\system32\dwm.exe", 0))!.Name));
        Assert.Equal("other.windows", Who(P(91, @"C:\Windows\explorer.exe", 0))!.Key);
        Assert.Equal(("other.csrss", "csrss"), (Who(P(92, null, 0, name: "csrss.exe"))!.Key, Who(P(92, null, 0, name: "csrss.exe"))!.Name));
        Assert.Equal("other.unnamed", Who(P(93, null, 0, name: ""))!.Key);
        foreach (var p in new[] { P(4, null, 0, name: "System"), P(91, @"C:\Windows\explorer.exe", 0), P(92, null, 0, name: "csrss.exe") })
        {
            var who = Who(p)!;
            Assert.Equal("other", who.Kind);
            Assert.Null(who.Exe);
        }
        var app = Who(P(94, Code, 0))!;
        Assert.Equal(("app", PhoneApps.Id(Code)), (app.Kind, app.Key));
    }

    // The Mac's rule: listed from 0.2 s or 0.5% of everyone's time, the busiest first.
    [Fact]
    public void SmallUsersAreFoldedIntoOther()
    {
        var secs = new Dictionary<string, double> { ["a"] = 30, ["b"] = 0.19, ["c"] = 0.2, ["d"] = 0.001, ["e"] = 0, ["f"] = 12.346 };
        var kept = AppCpu.Fold(secs);
        Assert.Equal(new[] { "a", "f", "c" }, kept.Keys.ToArray());
        Assert.Equal(12.35, kept["f"]);
        // 0.5% of a small total lists a small user too.
        Assert.Equal(new[] { "x", "y" }, AppCpu.Fold(new Dictionary<string, double> { ["x"] = 0.15, ["y"] = 0.1 }).Keys.ToArray());
        // At most KeepPerRow.
        var many = Enumerable.Range(0, 20).ToDictionary(i => "k" + i, i => 1.0 + i);
        var top = AppCpu.Fold(many);
        Assert.Equal(AppCpu.KeepPerRow, top.Count);
        Assert.Equal("k19", top.Keys.First());
    }

    [Fact]
    public void TheIconColourIsItsBrightHue()
    {
        Assert.Equal("#ff0000", AppCpu.IconColor(Png(System.Drawing.Color.Red)));
        Assert.Null(AppCpu.IconColor(Png(System.Drawing.Color.Gray)));
        Assert.Null(AppCpu.IconColor(new byte[] { 1, 2, 3 }));
    }

    static byte[] Png(System.Drawing.Color color)
    {
        using var bitmap = new System.Drawing.Bitmap(32, 32);
        using (var g = System.Drawing.Graphics.FromImage(bitmap)) g.Clear(color);
        using var stream = new MemoryStream();
        bitmap.Save(stream, ImageFormat.Png);
        return stream.ToArray();
    }
}
