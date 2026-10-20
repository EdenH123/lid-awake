using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LidAwake;

// One process in a snapshot. CreateTime and CpuTime are in 100-nanosecond units (CreateTime as a
// FILETIME, 0 for the system's own); a process is known by Pid and CreateTime together, since a
// pid is used again once its process ends. Path is the full path of its exe, or null when Windows
// won't say (the system's own processes, protected ones, and some of other users). PrivateBytes
// is its private working set (Task Manager's "Memory" column), WorkingSet the whole one.
public readonly record struct ProcessEntry(int Pid, long CreateTime, string Name, string? Path,
                                           ulong PrivateBytes, ulong WorkingSet, long CpuTime);

// Every process at one moment. FileTime is when the snapshot was asked for (UTC FILETIME), so a
// process created after it is known to be newer than the snapshot.
public sealed record ProcessSnapshot(IReadOnlyList<ProcessEntry> Processes, long FileTime);

// The processes now (see ProcessTable). null: Windows didn't answer.
public interface IProcessTable
{
    ProcessSnapshot? Snapshot();
}

// All processes, from one NtQuerySystemInformation(SystemProcessInformation) call: it needs no
// administrator and sees every process, other users' and the system's included, with its private
// working set and processor time. The exe paths come from QueryFullProcessImageName (through
// Win32Apps.ExePath, PROCESS_QUERY_LIMITED_INFORMATION) and are kept per process (pid and create
// time), so each process is opened once in its life. Shared by the memory window (MemoryInfo) and
// the processor history (HistorySampler). Safe to call from any thread.
public sealed class ProcessTable : IProcessTable
{
    // One table for the whole app: it keeps the paths it found.
    public static readonly ProcessTable Shared = new();

    // Several phones with the memory window open, plus the history sampler: one snapshot serves
    // all who ask within this time.
    public const long ReuseMilliseconds = 1000;

    const int SystemProcessInformation = 5;
    const int SystemPageFileInformation = 18;
    const uint StatusInfoLengthMismatch = 0xC0000004;
    const int MaxBuffer = 64 << 20;
    // SYSTEM_PROCESS_INFORMATION on 64-bit Windows (the fields read below end before this).
    const int EntrySize = 152;

    [DllImport("ntdll.dll", ExactSpelling = true), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint NtQuerySystemInformation(int infoClass, NativeBuffer buffer, int length, out int returnLength);

    readonly object gate = new();
    readonly Dictionary<(int Pid, long Created), string?> paths = new();
    readonly Func<int, string?> exePath;
    readonly Func<long> clock;
    ProcessSnapshot? last;
    long lastAt;
    int processBuffer = 512 * 1024;

    // `exePath(pid)` finds a process's exe (tests count the calls); `clock` is milliseconds.
    public ProcessTable(Func<int, string?>? exePath = null, Func<long>? clock = null)
    {
        this.exePath = exePath ?? (pid => Win32Apps.ExePath((uint)pid));
        this.clock = clock ?? (() => Environment.TickCount64);
    }

    public ProcessSnapshot? Snapshot()
    {
        lock (gate)
        {
            long now = clock();
            if (last != null && now - lastAt >= 0 && now - lastAt < ReuseMilliseconds) return last;
            long fileTime = DateTime.UtcNow.ToFileTimeUtc();
            List<ProcessEntry>? found;
            try { found = Read(); }
            catch (Exception) { found = null; }
            if (found == null) return null;
            last = new ProcessSnapshot(found, fileTime);
            lastAt = now;
            return last;
        }
    }

    // Under `gate`. null: not 64-bit Windows, or the call failed.
    List<ProcessEntry>? Read()
    {
        if (IntPtr.Size != 8) return null;
        using var buffer = Query(SystemProcessInformation, ref processBuffer);
        if (buffer == null) return null;
        var list = new List<ProcessEntry>();
        var alive = new HashSet<(int, long)>();
        bool added = false;
        try
        {
            buffer.DangerousAddRef(ref added);
            IntPtr start = buffer.DangerousGetHandle();
            long end = start + (long)buffer.Used;
            long offset = 0;
            while (offset + EntrySize <= buffer.Used)
            {
                IntPtr e = start + (nint)offset;
                int next = Marshal.ReadInt32(e, 0);
                long privateWs = Marshal.ReadInt64(e, 8);
                long created = Marshal.ReadInt64(e, 32);
                long user = Marshal.ReadInt64(e, 40);
                long kernel = Marshal.ReadInt64(e, 48);
                int nameBytes = (ushort)Marshal.ReadInt16(e, 56);
                IntPtr namePtr = Marshal.ReadIntPtr(e, 64);
                int pid = (int)Marshal.ReadInt64(e, 80);
                long ws = Marshal.ReadInt64(e, 144);

                string name = "";
                if (pid == 0) name = "Idle";
                else if (namePtr != IntPtr.Zero && nameBytes > 0 && (long)namePtr >= start && (long)namePtr + nameBytes <= end)
                    name = Marshal.PtrToStringUni(namePtr, nameBytes / 2);

                var key = (pid, created);
                alive.Add(key);
                if (!paths.TryGetValue(key, out var path))
                {
                    // The idle and system processes have no exe.
                    path = pid is 0 or 4 ? null : Safe(() => exePath(pid));
                    paths[key] = path;
                }
                list.Add(new ProcessEntry(pid, created, name, path, (ulong)Math.Max(0, privateWs), (ulong)Math.Max(0, ws),
                                          Math.Max(0, user) + Math.Max(0, kernel)));
                if (next <= 0) break;
                offset += next;
            }
        }
        finally
        {
            if (added) buffer.DangerousRelease();
        }
        // Forget the processes that ended.
        foreach (var gone in paths.Keys.Where(k => !alive.Contains(k)).ToList()) paths.Remove(gone);
        return list;
    }

    static string? Safe(Func<string?> find)
    {
        try { return find(); }
        catch (Exception) { return null; }
    }

    // Bytes of the page files in use (the memory moved to disk), or null when Windows didn't say.
    public static ulong? PageFileInUse()
    {
        try
        {
            if (IntPtr.Size != 8) return null;
            int size = 4096;
            using var buffer = Query(SystemPageFileInformation, ref size);
            if (buffer == null) return null;
            ulong pages = 0;
            bool added = false;
            try
            {
                buffer.DangerousAddRef(ref added);
                IntPtr start = buffer.DangerousGetHandle();
                long offset = 0;
                // SYSTEM_PAGEFILE_INFORMATION: NextEntryOffset, TotalSize, TotalInUse, PeakUsage (pages), name.
                while (offset + 32 <= buffer.Used)
                {
                    IntPtr e = start + (nint)offset;
                    int next = Marshal.ReadInt32(e, 0);
                    pages += (uint)Marshal.ReadInt32(e, 8);
                    if (next <= 0) break;
                    offset += next;
                }
            }
            finally
            {
                if (added) buffer.DangerousRelease();
            }
            return pages * (ulong)Environment.SystemPageSize;
        }
        catch (Exception) { return null; }
    }

    // One NtQuerySystemInformation call, the buffer grown while Windows says it is too small (the
    // process list changes between two calls, so a little more is added). `size` keeps the size
    // that worked, for the next call. null: any other failure, or more than 64 MB.
    static NativeBuffer? Query(int infoClass, ref int size)
    {
        for (int attempt = 0; attempt < 8 && size > 0 && size <= MaxBuffer; attempt++)
        {
            var buffer = NativeBuffer.Allocate(size);
            uint status = NtQuerySystemInformation(infoClass, buffer, size, out int needed);
            if (status == 0)
            {
                buffer.Used = needed > 0 && needed <= size ? needed : size;
                return buffer;
            }
            buffer.Dispose();
            if (status != StatusInfoLengthMismatch) return null;
            long grown = Math.Max((long)needed + 64 * 1024, (long)size * 2);
            if (grown > MaxBuffer) return null;
            size = (int)grown;
        }
        return null;
    }

    // ---- what a process is ----

    // A process of Windows itself: one whose exe Windows won't name, or one inside the Windows
    // folder (C:\Windows). The memory window counts these under "Windows & system"; the processor
    // history lists them as "other", never as apps.
    public static bool IsSystem(string? path, string windowsFolder)
    {
        if (path == null) return true;
        if (string.IsNullOrEmpty(windowsFolder)) return false;
        var folder = windowsFolder.TrimEnd('\\') + "\\";
        return path.StartsWith(folder, StringComparison.OrdinalIgnoreCase);
    }

    public static string WindowsFolder()
    {
        try { return Environment.GetFolderPath(Environment.SpecialFolder.Windows); }
        catch (Exception) { return ""; }
    }
}

// Memory from Marshal.AllocHGlobal, freed by the SafeHandle (also if a caller forgets).
sealed class NativeBuffer : SafeHandleZeroOrMinusOneIsInvalid
{
    public NativeBuffer() : base(true) { }

    // Bytes Windows filled in.
    public int Used { get; set; }

    public static NativeBuffer Allocate(int size)
    {
        var buffer = new NativeBuffer();
        buffer.SetHandle(Marshal.AllocHGlobal(size));
        return buffer;
    }

    protected override bool ReleaseHandle()
    {
        Marshal.FreeHGlobal(handle);
        return true;
    }
}
