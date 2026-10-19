using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;

namespace LidAwake;

// The Win32 side of /status: reads the real numbers. Every method answers null (or 0) instead of
// throwing, so one failing call only leaves its field out. Safe to call from any thread.
public sealed class SystemReadings : ISystemReadings
{
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
            return GlobalMemoryStatusEx(ref m) ? new MemoryReading(m.ullTotalPhys, m.ullAvailPhys) : null;
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
                (ulong)Math.Max(0L, stats.BytesReceived), (ulong)Math.Max(0L, stats.BytesSent));
        }
        catch (Exception) { return null; }
    }

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

    public int ScreenWidth()
    {
        try { return Screen.PrimaryScreen?.Bounds.Width ?? 0; }
        catch (Exception) { return 0; }
    }

    public string HostName() => Environment.MachineName;

    public long Milliseconds() => Environment.TickCount64;
}
