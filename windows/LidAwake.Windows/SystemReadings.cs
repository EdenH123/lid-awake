using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LidAwake;

// The readings that take longer than a request may wait (WMI, the battery drivers). Each answer is
// the last one read in the background (BackgroundValue), null until the first read is done.
// SystemReadings has them; StatusInfo uses them only when its readings do (a test's fake may not).
public interface ISlowReadings
{
    ThermalZoneReading? Zones();    // the Thermal Zone Information counters (thermalState)
    BatteryCharge? Charge();        // the batteries' own drivers (minutesToFull, charged)
}

// A value that takes a while to read (a WMI query, the battery drivers, tailscale.exe), read in the
// background at most every `every`: Latest answers at once with the last value read (null until
// the first read is done) and starts a new read when that one is older. One read at a time; a read
// that throws or answers null keeps the last value, but only for MaxAgeTimes `every` after it was
// read: older than that it counts as unknown (null) until a read succeeds again, so a reading that
// keeps failing never shows a stale value for long. `start` runs the read (Task.Run; tests run it
// at once), `clock` is milliseconds that only go forward.
public sealed class BackgroundValue<T> where T : class
{
    public const int MaxAgeTimes = 3;
    readonly Func<T?> read;
    readonly long everyMs;
    readonly Func<long> clock;
    readonly Action<Action> start;
    readonly object gate = new();
    T? value;
    long valueAt;   // when `value` was read
    long? readAt;   // when the last read ended, good or not
    bool reading;

    public BackgroundValue(Func<T?> read, TimeSpan every, Func<long>? clock = null, Action<Action>? start = null)
    {
        this.read = read;
        everyMs = (long)every.TotalMilliseconds;
        this.clock = clock ?? (() => Environment.TickCount64);
        this.start = start ?? (work => Task.Run(work));
    }

    public T? Latest
    {
        get
        {
            bool go = false;
            lock (gate)
            {
                if (!reading && (readAt is not { } at || clock() - at >= everyMs)) reading = go = true;
            }
            if (go)
            {
                try { start(Read); }
                catch (Exception) { lock (gate) reading = false; }
            }
            lock (gate) return value != null && clock() - valueAt <= everyMs * MaxAgeTimes ? value : null;
        }
    }

    void Read()
    {
        T? now = null;
        try { now = read(); }
        catch (Exception) { }
        lock (gate)
        {
            readAt = clock();
            if (now != null) (value, valueAt) = (now, readAt.Value);
            reading = false;
        }
    }
}

// The primary display's scale: GetDpiForMonitor (effective DPI) over 96. Under Lid Awake's DPI
// awareness (system aware) this is the scale its own pixels are counted in, the same pixels as
// Screen.Bounds, GetSystemMetrics and GetCursorPos. 96 (100%) when Windows can't say.
public static class DisplayScale
{
    const uint MonitorDefaultToPrimary = 1;
    const int MdtEffectiveDpi = 0;

    [DllImport("user32.dll")]
    static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);

    [DllImport("shcore.dll")]
    static extern int GetDpiForMonitor(IntPtr monitor, int dpiType, out uint dpiX, out uint dpiY);

    public static uint PrimaryDpi()
    {
        try
        {
            // No window: the primary monitor.
            var monitor = MonitorFromWindow(IntPtr.Zero, MonitorDefaultToPrimary);
            if (monitor == IntPtr.Zero) return 96;
            return GetDpiForMonitor(monitor, MdtEffectiveDpi, out var x, out _) == 0 && x > 0 ? x : 96;
        }
        catch (Exception) { return 96; }
    }

    // Pixels per point (1.0 at 100%, 1.5 at 150%).
    public static double PixelsPerPoint() => PrimaryDpi() / 96.0;
}

// The Win32 side of /status: reads the real numbers. Every method answers null (or 0) instead of
// throwing, so one failing call only leaves its field out. Safe to call from any thread.
public sealed class SystemReadings : ISystemReadings, ISlowReadings
{
    readonly BackgroundValue<ThermalZoneReading> zones = new(ThermalZoneReading.Read, ThermalZoneReading.Every);
    readonly BackgroundValue<BatteryCharge> charge = new(BatteryCharge.Read, BatteryCharge.Every);

    public ThermalZoneReading? Zones() => zones.Latest;
    public BatteryCharge? Charge() => charge.Latest;

    // GetComputerNameEx with ComputerNamePhysicalDnsHostname: the name as the owner typed it (case
    // kept, up to 63 characters), not the NetBIOS name (upper case, at most 15).
    const int ComputerNamePhysicalDnsHostname = 5;

    [DllImport("kernel32.dll", EntryPoint = "GetComputerNameExW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetComputerNameEx(int format, [Out] char[]? buffer, ref uint size);

    [StructLayout(LayoutKind.Sequential)]
    struct SystemPowerStatus
    {
        public byte ACLineStatus, BatteryFlag, BatteryLifePercent, SystemStatusFlag;
        public uint BatteryLifeTime, BatteryFullLifeTime;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemPowerStatus(out SystemPowerStatus status);

    [StructLayout(LayoutKind.Sequential)]
    struct MemoryStatusEx
    {
        public uint dwLength, dwMemoryLoad;
        public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
    }

    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GlobalMemoryStatusEx(ref MemoryStatusEx status);

    // The three FILETIME values are 64-bit counts of 100-nanosecond units.
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetSystemTimes(out ulong idle, out ulong kernel, out ulong user);

    // The interface Windows would use to reach an address (IPv4 in network byte order).
    [DllImport("iphlpapi.dll")]
    static extern uint GetBestInterface(uint destinationAddress, out uint bestIfIndex);

    public PowerReading? Power()
    {
        try
        {
            if (!GetSystemPowerStatus(out var p)) return null;
            long life = p.BatteryLifeTime == uint.MaxValue ? -1 : p.BatteryLifeTime;
            return new PowerReading(p.ACLineStatus, p.BatteryFlag, p.BatteryLifePercent, life);
        }
        catch (Exception) { return null; }
    }

    public MemoryReading? Memory()
    {
        try
        {
            var m = new MemoryStatusEx { dwLength = (uint)Marshal.SizeOf<MemoryStatusEx>() };
            return GlobalMemoryStatusEx(ref m) ? new MemoryReading(m.ullTotalPhys, m.ullAvailPhys, m.ullTotalPageFile, m.ullAvailPageFile) : null;
        }
        catch (Exception) { return null; }
    }

    public CpuTimes? Cpu()
    {
        try
        {
            return GetSystemTimes(out var idle, out var kernel, out var user) ? new CpuTimes(idle, kernel, user) : null;
        }
        catch (Exception) { return null; }
    }

    public DiskReading? Disk()
    {
        try
        {
            var root = Path.GetPathRoot(Environment.SystemDirectory);
            if (string.IsNullOrEmpty(root)) return null;
            var drive = new DriveInfo(root);
            return new DiskReading(drive.AvailableFreeSpace, drive.TotalSize);
        }
        catch (Exception) { return null; }
    }

    public NetCounters? Network()
    {
        try
        {
            var up = new List<NetworkInterface>();
            foreach (var ni in NetworkInterface.GetAllNetworkInterfaces())
                if (ni.OperationalStatus == OperationalStatus.Up
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Loopback
                    && ni.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                    up.Add(ni);

            NetworkInterface? pick = null;
            if (BestInterfaceIndex() is { } best)
                pick = up.FirstOrDefault(ni => Ipv4Index(ni) == best);
            // Otherwise the first one with an IPv4 gateway: that is one that reaches the internet.
            pick ??= up.FirstOrDefault(HasIpv4Gateway);
            if (pick == null) return null;

            var stats = pick.GetIPStatistics();
            return new NetCounters(pick.Name, pick.NetworkInterfaceType != NetworkInterfaceType.Wireless80211,
                (ulong)Math.Max(0L, stats.BytesReceived), (ulong)Math.Max(0L, stats.BytesSent), pick.Id);
        }
        catch (Exception) { return null; }
    }

    // Native Wifi (WlanReader): null when there is no connected Wi-Fi or the API isn't available.
    public WifiReading? Wifi(string? adapterId) => WlanReader.Read(adapterId);

    static int? BestInterfaceIndex()
    {
        try
        {
            // 8.8.8.8: any public address, to ask which interface leads to the internet.
            return GetBestInterface(0x08080808u, out var index) == 0 ? (int)index : null;
        }
        catch (Exception) { return null; }
    }

    static int? Ipv4Index(NetworkInterface ni)
    {
        try { return ni.GetIPProperties().GetIPv4Properties().Index; }
        catch (Exception) { return null; }  // no IPv4 on this interface
    }

    static bool HasIpv4Gateway(NetworkInterface ni)
    {
        try
        {
            return ni.GetIPProperties().GatewayAddresses.Any(g =>
                g.Address.AddressFamily == AddressFamily.InterNetwork && !g.Address.Equals(IPAddress.Any));
        }
        catch (Exception) { return false; }
    }

    public long UptimeSeconds() => Environment.TickCount64 / 1000;

    // In points, as the Mac sends it: the page divides it by the picture's width to turn a drag on
    // the Screen tab into the PC's points for `scroll`.
    public int ScreenWidth()
    {
        try { return Points(Screen.PrimaryScreen?.Bounds.Width ?? 0, DisplayScale.PrimaryDpi()); }
        catch (Exception) { return 0; }
    }

    // Pixels at `dpi` in points (96 per inch); unchanged when the DPI is unknown (0).
    public static int Points(int pixels, uint dpi) => dpi == 0 ? pixels : (int)Math.Round(pixels * 96.0 / dpi);

    public string HostName() => PickHost(DnsHostName(), Environment.MachineName);

    // The DNS host name when there is one, else the NetBIOS name (Environment.MachineName).
    public static string PickHost(string? dnsName, string machineName) =>
        string.IsNullOrWhiteSpace(dnsName) ? machineName : dnsName.Trim();

    static string? DnsHostName()
    {
        try
        {
            uint size = 0;
            // The first call only says how long the name is (with its NUL).
            GetComputerNameEx(ComputerNamePhysicalDnsHostname, null, ref size);
            if (size == 0 || size > 1024) return null;
            var buffer = new char[size];
            return GetComputerNameEx(ComputerNamePhysicalDnsHostname, buffer, ref size) ? new string(buffer, 0, (int)Math.Min(size, (uint)buffer.Length)) : null;
        }
        catch (Exception) { return null; }
    }

    public long Milliseconds() => Environment.TickCount64;
}
