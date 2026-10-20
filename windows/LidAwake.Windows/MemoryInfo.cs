using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LidAwake;

// The memory numbers Windows gives, in bytes. Free and Standby are null when the performance
// counters can't be read. Commit is what programs have been promised (RAM plus page files; the
// limit is how far that can go). Wired is the kernel's memory that never leaves RAM, Compressed
// the "Memory Compression" store (both null when unknown). LowMemory is Windows' own signal that
// RAM is running out (QueryMemoryResourceNotification).
public readonly record struct MemoryCounts(ulong Total, ulong Available, ulong? Free, ulong? Standby,
                                           ulong CommitTotal, ulong CommitLimit, ulong SwapUsed,
                                           ulong? Wired, bool LowMemory);

// Where MemoryCounts come from (see WindowsMemory). null: nothing could be read.
public interface IMemorySource
{
    MemoryCounts? Read();
}

// The phone's Memory window (docs/API.md 5.8), as on the Mac (MemoryInfo.swift): where the PC's
// memory goes right now, how hard Windows works to find room, and the programs using the most.
// Read fresh on every call (the process list is shared for a second, ProcessTable).
public sealed class MemoryInfo
{
    public const int TopCount = 5;

    readonly IMemorySource source;
    readonly IProcessTable processes;
    readonly string windowsFolder;
    readonly Func<string, string> nameOf;

    public MemoryInfo(ISystemReadings readings, IProcessTable processes) : this(new WindowsMemory(readings), processes) { }

    // `windowsFolder` decides what counts as Windows itself (ProcessTable.IsSystem); `nameOf`
    // gives a program's name from its exe (the tests pass their own).
    public MemoryInfo(IMemorySource source, IProcessTable processes, string? windowsFolder = null, Func<string, string>? nameOf = null)
    {
        this.source = source;
        this.processes = processes;
        this.windowsFolder = windowsFolder ?? ProcessTable.WindowsFolder();
        this.nameOf = nameOf ?? ProcessSource.NameOf;
    }

    // GET /memory: {total, used, app, wired?, compressed?, cached, free, swapUsed, pressure, level,
    // top: [{name, bytes, id}], otherPrograms}. Fields Windows didn't give are left out.
    public Dictionary<string, object> Snapshot()
    {
        ProcessSnapshot? snap;
        try { snap = processes.Snapshot(); }
        catch (Exception) { snap = null; }
        var list = snap?.Processes ?? Array.Empty<ProcessEntry>();
        var (top, others) = Top(list, TopCount, windowsFolder, nameOf);

        var answer = new Dictionary<string, object> { ["top"] = top, ["otherPrograms"] = others };
        MemoryCounts? read;
        try { read = source.Read(); }
        catch (Exception) { read = null; }
        if (read is not { } m)
        {
            answer["level"] = "normal";
            return answer;
        }
        ulong used = m.Total > m.Available ? m.Total - m.Available : 0;
        ulong apps = 0;
        foreach (var p in list) if (p.Pid != 0) apps += p.PrivateBytes;
        var (free, cached) = FreeAndCached(m);
        answer["total"] = m.Total;
        answer["used"] = used;
        answer["app"] = Math.Min(apps, used);
        answer["cached"] = cached;
        answer["free"] = free;
        answer["swapUsed"] = m.SwapUsed;
        if (m.Wired is { } wired) answer["wired"] = wired;
        if (Compressed(list) is { } compressed) answer["compressed"] = compressed;
        double commit = CommitRatio(m);
        answer["pressure"] = (int)Math.Round(Math.Clamp(commit, 0, 1) * 100);
        answer["level"] = Level(commit, m.Total > 0 ? (double)m.Available / m.Total : 1, m.LowMemory);
        return answer;
    }

    // Available memory is the standby list (files kept in spare memory, handed back the moment a
    // program needs it) plus the free and zeroed pages. With only one of the two counters, the
    // other is what is left; with neither, all of it counts as free.
    public static (ulong Free, ulong Cached) FreeAndCached(MemoryCounts m)
    {
        if (m.Free is { } f && m.Standby is { } s) return (f, s);
        if (m.Free is { } freeOnly) return (Math.Min(freeOnly, m.Available), m.Available - Math.Min(freeOnly, m.Available));
        if (m.Standby is { } standbyOnly) return (m.Available - Math.Min(standbyOnly, m.Available), Math.Min(standbyOnly, m.Available));
        return (m.Available, 0);
    }

    public static double CommitRatio(MemoryCounts m) => m.CommitLimit > 0 ? (double)m.CommitTotal / m.CommitLimit : 0;

    // The Mac's three levels from Windows' numbers: critical when Windows itself says memory is
    // low, nine tenths of the commit limit is promised, or under 3% of RAM is available; warn from
    // three quarters of the commit limit, or under 10% available; otherwise normal.
    public static string Level(double commitRatio, double availableRatio, bool lowMemory)
    {
        if (lowMemory || commitRatio >= 0.90 || availableRatio < 0.03) return "critical";
        if (commitRatio >= 0.75 || availableRatio < 0.10) return "warn";
        return "normal";
    }

    // The programs using the most, each exe once (Chrome's many chrome.exe processes are one
    // Chrome), by private working set. Windows' own processes (ProcessTable.IsSystem) are left
    // out: the page shows them as "Windows & system". `others`: every other program's memory.
    // `id` is the exe's id from the Apps page (PhoneApps.Id), for its icon.
    public static (List<Dictionary<string, object>> Top, ulong Others) Top(IReadOnlyList<ProcessEntry> processes, int limit,
                                                                          string windowsFolder, Func<string, string> nameOf)
    {
        var sizes = new Dictionary<string, (string Path, ulong Bytes)>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in processes)
        {
            if (p.Pid == 0 || ProcessTable.IsSystem(p.Path, windowsFolder)) continue;
            var exe = PhoneApps.Normalize(p.Path!);
            sizes[exe] = sizes.TryGetValue(exe, out var had) ? (had.Path, had.Bytes + p.PrivateBytes) : (exe, p.PrivateBytes);
        }
        var ranked = sizes.Values.OrderByDescending(v => v.Bytes).ThenBy(v => v.Path, StringComparer.OrdinalIgnoreCase).ToList();
        var top = new List<Dictionary<string, object>>();
        foreach (var (path, bytes) in ranked.Take(limit))
        {
            string name;
            try { name = nameOf(path); }
            catch (Exception) { name = Path.GetFileNameWithoutExtension(path); }
            top.Add(new Dictionary<string, object> { ["name"] = name, ["bytes"] = bytes, ["id"] = PhoneApps.Id(path) });
        }
        ulong others = 0;
        foreach (var (_, bytes) in ranked.Skip(limit)) others += bytes;
        return (top, others);
    }

    // The "Memory Compression" process holds Windows' compressed memory (its working set).
    static ulong? Compressed(IReadOnlyList<ProcessEntry> processes)
    {
        foreach (var p in processes)
            if (p.Path == null && p.Name == "Memory Compression") return p.WorkingSet;
        return null;
    }
}

// The real numbers: GlobalMemoryStatusEx (through ISystemReadings), GetPerformanceInfo (commit,
// kernel), the page files' use (ProcessTable.PageFileInUse), the low-memory signal, and the
// performance counters "Free & Zero Page List Bytes" and "Standby Cache * Bytes" (PDH, by their
// English names so any Windows language works). A counter that can't be read leaves its number
// out, and PDH is not tried again for 10 minutes after it failed. Safe to call from any thread.
public sealed class WindowsMemory : IMemorySource
{
    [StructLayout(LayoutKind.Sequential)]
    struct PerformanceInformation
    {
        public uint cb;
        public UIntPtr CommitTotal, CommitLimit, CommitPeak, PhysicalTotal, PhysicalAvailable, SystemCache,
                       KernelTotal, KernelPaged, KernelNonpaged, PageSize;
        public uint HandleCount, ProcessCount, ThreadCount;
    }

    [DllImport("kernel32.dll", EntryPoint = "K32GetPerformanceInfo", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetPerformanceInfo(out PerformanceInformation info, uint size);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeWaitHandle CreateMemoryResourceNotification(int type);

    [DllImport("kernel32.dll", SetLastError = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool QueryMemoryResourceNotification(SafeWaitHandle handle, [MarshalAs(UnmanagedType.Bool)] out bool state);

    const int LowMemoryResourceNotification = 0;
    static readonly TimeSpan RetryPdh = TimeSpan.FromMinutes(10);

    readonly ISystemReadings readings;
    readonly object gate = new();
    SafeWaitHandle? lowMemory;
    bool triedLowMemory;
    PdhMemoryCounters? counters;
    DateTime pdhFailedAt = DateTime.MinValue;

    public WindowsMemory(ISystemReadings readings) => this.readings = readings;

    public MemoryCounts? Read()
    {
        if (readings.Memory() is not { } m) return null;
        ulong commitTotal = 0, commitLimit = 0;
        ulong? wired = null;
        try
        {
            if (GetPerformanceInfo(out var info, (uint)Marshal.SizeOf<PerformanceInformation>()))
            {
                ulong page = (ulong)info.PageSize;
                commitTotal = (ulong)info.CommitTotal * page;
                commitLimit = (ulong)info.CommitLimit * page;
                wired = (ulong)info.KernelNonpaged * page;
            }
        }
        catch (Exception) { }
        ulong? free, standby;
        bool low;
        lock (gate)
        {
            (free, standby) = ReadCounters();
            low = IsLow();
        }
        return new MemoryCounts(m.Total, m.Available, free, standby, commitTotal, commitLimit,
                                ProcessTable.PageFileInUse() ?? 0, wired, low);
    }

    // Under `gate`.
    (ulong? Free, ulong? Standby) ReadCounters()
    {
        if (counters == null)
        {
            if (DateTime.UtcNow - pdhFailedAt < RetryPdh) return (null, null);
            counters = PdhMemoryCounters.Open();
            if (counters == null) { pdhFailedAt = DateTime.UtcNow; return (null, null); }
        }
        var (free, standby) = counters.Read();
        if (free == null && standby == null)
        {
            counters.Dispose();
            counters = null;
            pdhFailedAt = DateTime.UtcNow;
        }
        return (free, standby);
    }

    // Under `gate`.
    bool IsLow()
    {
        try
        {
            if (!triedLowMemory)
            {
                triedLowMemory = true;
                var handle = CreateMemoryResourceNotification(LowMemoryResourceNotification);
                if (handle.IsInvalid) handle.Dispose();
                else lowMemory = handle;
            }
            return lowMemory != null && QueryMemoryResourceNotification(lowMemory, out var state) && state;
        }
        catch (Exception) { return false; }
    }
}

// The PDH query for the four memory counters, kept open between calls (raw counters: one
// collection gives their value).
sealed class PdhMemoryCounters : IDisposable
{
    [StructLayout(LayoutKind.Explicit)]
    struct PdhFmtCounterValue
    {
        [FieldOffset(0)] public uint CStatus;
        [FieldOffset(8)] public long LargeValue;
    }

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhOpenQueryW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint PdhOpenQuery(string? dataSource, IntPtr userData, out PdhQueryHandle query);

    [DllImport("pdh.dll", CharSet = CharSet.Unicode, EntryPoint = "PdhAddEnglishCounterW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint PdhAddEnglishCounter(PdhQueryHandle query, string path, IntPtr userData, out IntPtr counter);

    [DllImport("pdh.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint PdhCollectQueryData(PdhQueryHandle query);

    [DllImport("pdh.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, out uint type, out PdhFmtCounterValue value);

    const uint PdhFmtLarge = 0x00000400;
    const string FreeCounter = @"\Memory\Free & Zero Page List Bytes";
    static readonly string[] StandbyCounters =
    {
        @"\Memory\Standby Cache Reserve Bytes", @"\Memory\Standby Cache Normal Priority Bytes", @"\Memory\Standby Cache Core Bytes",
    };

    readonly PdhQueryHandle query;
    readonly IntPtr free;
    readonly IntPtr[] standby;

    PdhMemoryCounters(PdhQueryHandle query, IntPtr free, IntPtr[] standby) => (this.query, this.free, this.standby) = (query, free, standby);

    // null: PDH or the Memory counters aren't there.
    public static PdhMemoryCounters? Open()
    {
        try
        {
            if (PdhOpenQuery(null, IntPtr.Zero, out var query) != 0 || query.IsInvalid) { query.Dispose(); return null; }
            if (PdhAddEnglishCounter(query, FreeCounter, IntPtr.Zero, out var free) != 0) free = IntPtr.Zero;
            var standby = new List<IntPtr>();
            foreach (var path in StandbyCounters)
            {
                if (PdhAddEnglishCounter(query, path, IntPtr.Zero, out var c) != 0) { standby.Clear(); break; }
                standby.Add(c);
            }
            if (free == IntPtr.Zero && standby.Count == 0) { query.Dispose(); return null; }
            return new PdhMemoryCounters(query, free, standby.ToArray());
        }
        catch (Exception) { return null; }
    }

    public (ulong? Free, ulong? Standby) Read()
    {
        try
        {
            if (PdhCollectQueryData(query) != 0) return (null, null);
            ulong? freeBytes = free == IntPtr.Zero ? null : Value(free);
            ulong? standbyBytes = standby.Length == 0 ? null : 0;
            foreach (var c in standby)
            {
                if (Value(c) is not { } v) { standbyBytes = null; break; }
                standbyBytes += v;
            }
            return (freeBytes, standbyBytes);
        }
        catch (Exception) { return (null, null); }
    }

    static ulong? Value(IntPtr counter)
    {
        if (PdhGetFormattedCounterValue(counter, PdhFmtLarge, out _, out var v) != 0 || v.CStatus > 1 || v.LargeValue < 0) return null;
        return (ulong)v.LargeValue;
    }

    public void Dispose() => query.Dispose();
}

// A PDH query, closed with PdhCloseQuery.
sealed class PdhQueryHandle : SafeHandleZeroOrMinusOneIsInvalid
{
    [DllImport("pdh.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint PdhCloseQuery(IntPtr query);

    public PdhQueryHandle() : base(true) { }

    protected override bool ReleaseHandle() => PdhCloseQuery(handle) == 0;
}
