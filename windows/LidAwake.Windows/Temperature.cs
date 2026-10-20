using System.Management;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LidAwake;

// Both Windows sources give the temperature in tenths of a kelvin. Only obvious garbage counts as
// no reading: a raw 0, 0xFFFF or 0xFFFFFFFF (a sensor that isn't really there), or a value outside
// -40 to 150 °C. A real 101 to 150 °C is kept, so real overheating is never hidden.
public static class TemperatureMath
{
    public const double Lowest = -40.0;
    public const double Highest = 150.0;

    public static double? FromTenthsOfKelvin(long tenths)
    {
        if (tenths is 0 or 0xFFFF or 0xFFFFFFFF) return null;
        double celsius = tenths / 10.0 - 273.15;
        return IsSane(celsius) ? celsius : null;
    }

    public static bool IsSane(double celsius) => !double.IsNaN(celsius) && celsius >= Lowest && celsius <= Highest;

    // One decimal, rounded the way the Mac rounds ((t * 10).rounded() / 10: halves away from zero).
    public static double OneDecimal(double celsius) => Math.Round(celsius * 10, MidpointRounding.AwayFromZero) / 10;
}

// One round of temperature readings, in °C, each null when its source has none. They are kept
// apart because they mean different things: the battery's own sensor (limit maxBatteryTemp, 40 °C,
// and the only one sent as batteryTemp), and the ACPI thermal zones, which measure the processor
// or the board, where 40 to 60 °C is normal (limit MaxPcTemp, 90 °C).
public readonly record struct TemperatureReading(double? Battery, double? Zone)
{
    public static readonly TemperatureReading None = new(null, null);

    // Some source gave a temperature.
    public bool Any => Battery != null || Zone != null;
}

// Both sources, every round (as the Mac checks both the battery temperature and the thermal state).
// Never throws; blocks for up to about 3 seconds (WMI), so never call it on the UI thread
// (HeatSensor calls it from the safety check's background timer).
public static class TemperatureSources
{
    public static TemperatureReading Read()
    {
        double? battery = null, zone = null;
        try { battery = BatteryTemperature.Read(); }
        catch (Exception) { }
        try { zone = ThermalZones.Read(); }
        catch (Exception) { }
        return new TemperatureReading(battery, zone);
    }
}

// The batteries that SetupAPI lists (as Microsoft's "Enumerating Battery Devices" sample does), and
// what each one says. Read: the battery's own temperature, IOCTL_BATTERY_QUERY_INFORMATION with
// BatteryTemperature. Many batteries don't report one (the IOCTL fails): then null. With several
// batteries, the hottest. Cells: each battery's charge (BatteryCharge), from BATTERY_INFORMATION and
// IOCTL_BATTERY_QUERY_STATUS.
//
// Layouts (setupapi.h packs its structs with pshpack8 on 64-bit and pshpack1 on 32-bit;
// batclass.h uses natural alignment):
//
//   SP_DEVICE_INTERFACE_DATA            x64: 32 bytes   x86: 28 bytes
//     DWORD cbSize                      0  (4)
//     GUID InterfaceClassGuid           4  (16)
//     DWORD Flags                       20 (4)
//     ULONG_PTR Reserved                24 (8 on x64, 4 on x86)
//   SP_DEVICE_INTERFACE_DETAIL_DATA_W   not marshalled as a struct: a buffer of RequiredSize bytes
//     DWORD cbSize                      0  (4). The value written is the size of the fixed part:
//                                          8 on x64 (4 + one WCHAR, padded to 8), 6 on x86 (pack 1)
//     WCHAR DevicePath[]                4  NUL-terminated
//   BATTERY_QUERY_INFORMATION           12 bytes on both
//     ULONG BatteryTag                  0  (4)
//     BATTERY_QUERY_INFORMATION_LEVEL   4  (4, an enum; BatteryTemperature = 2)
//     LONG AtRate                       8  (4)
//   IOCTL_BATTERY_QUERY_TAG (0x294040)          in: ULONG wait timeout (ms, 0 = don't wait)
//                                               out: ULONG tag (0 = BATTERY_TAG_INVALID, no battery)
//   IOCTL_BATTERY_QUERY_INFORMATION (0x294044)  in: BATTERY_QUERY_INFORMATION
//                                               out: ULONG, tenths of a kelvin (BatteryTemperature),
//                                                    or BATTERY_INFORMATION (BatteryInformation = 0)
//   BATTERY_INFORMATION                 36 bytes on both
//     ULONG Capabilities                0  (4; BATTERY_CAPACITY_RELATIVE = 0x40000000)
//     UCHAR Technology, Reserved[3]     4  (4)
//     UCHAR Chemistry[4]                8  (4)
//     ULONG DesignedCapacity            12 (4)
//     ULONG FullChargedCapacity         16 (4, mWh)
//     ULONG DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount   20, 24, 28, 32
//   IOCTL_BATTERY_QUERY_STATUS (0x29404C)       in: BATTERY_WAIT_STATUS, 20 bytes: ULONG BatteryTag,
//                                                   Timeout (0 = answer now), PowerState, LowCapacity, HighCapacity
//                                               out: BATTERY_STATUS, 16 bytes: ULONG PowerState 0,
//                                                    ULONG Capacity 4 (mWh), ULONG Voltage 8, LONG Rate 12 (mW, + charging)
//
// The tests check the sizes and offsets above against the structs below.
public static class BatteryTemperature
{
    // GUID_DEVCLASS_BATTERY, which is also the battery device interface class (GUID_DEVICE_BATTERY).
    static readonly Guid BatteryClass = new("72631E54-78A4-11D0-BCF7-00AA00B7B32A");
    const uint DigcfPresent = 0x00000002;
    const uint DigcfDeviceInterface = 0x00000010;
    const uint GenericRead = 0x80000000;
    const uint GenericWrite = 0x40000000;
    const uint FileShareRead = 0x00000001;
    const uint FileShareWrite = 0x00000002;
    const uint OpenExisting = 3;
    const uint FileAttributeNormal = 0x00000080;
    const int ErrorAccessDenied = 5;
    const int ErrorInsufficientBuffer = 122;
    internal const uint IoctlBatteryQueryTag = 0x294040;          // CTL_CODE(0x29, 0x10, METHOD_BUFFERED, FILE_READ_ACCESS)
    internal const uint IoctlBatteryQueryInformation = 0x294044;  // CTL_CODE(0x29, 0x11, METHOD_BUFFERED, FILE_READ_ACCESS)
    internal const uint IoctlBatteryQueryStatus = 0x29404C;       // CTL_CODE(0x29, 0x13, METHOD_BUFFERED, FILE_READ_ACCESS)
    internal const int LevelBatteryInformation = 0;
    internal const int LevelBatteryTemperature = 2;
    internal const uint CapacityRelative = 0x40000000;
    const uint MaxBatteries = 8;
    const uint MaxDetailBytes = 32 * 1024;
    static readonly IntPtr InvalidHandle = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    internal struct SpDeviceInterfaceData
    {
        public uint CbSize;
        public Guid InterfaceClassGuid;
        public uint Flags;
        public UIntPtr Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryQueryInformation
    {
        public uint BatteryTag;
        public int InformationLevel;
        public int AtRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryInformation
    {
        public uint Capabilities;
        public byte Technology;
        public byte Reserved0, Reserved1, Reserved2;
        public uint Chemistry;  // four characters
        public uint DesignedCapacity;
        public uint FullChargedCapacity;
        public uint DefaultAlert1, DefaultAlert2, CriticalBias, CycleCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryWaitStatus
    {
        public uint BatteryTag;
        public uint Timeout;
        public uint PowerState;
        public uint LowCapacity;
        public uint HighCapacity;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BatteryStatus
    {
        public uint PowerState;
        public uint Capacity;
        public uint Voltage;
        public int Rate;
    }

    // cbSize of SP_DEVICE_INTERFACE_DETAIL_DATA_W (see the layout above).
    internal static int DetailDataCbSize => IntPtr.Size == 8 ? 8 : 6;

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetClassDevsW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid,
        uint memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

    [DllImport("setupapi.dll", EntryPoint = "SetupDiGetDeviceInterfaceDetailW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SpDeviceInterfaceData deviceInterfaceData,
        IntPtr deviceInterfaceDetailData, uint deviceInterfaceDetailDataSize, out uint requiredSize, IntPtr deviceInfoData);

    [DllImport("setupapi.dll", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern SafeFileHandle CreateFile(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes,
        uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

    // IOCTL_BATTERY_QUERY_TAG: a ULONG in, a ULONG out.
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref uint inBuffer, uint inBufferSize,
        out uint outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    // IOCTL_BATTERY_QUERY_INFORMATION: a BATTERY_QUERY_INFORMATION in, a ULONG out (BatteryTemperature).
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref BatteryQueryInformation inBuffer, uint inBufferSize,
        out uint outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    // IOCTL_BATTERY_QUERY_INFORMATION: a BATTERY_QUERY_INFORMATION in, a BATTERY_INFORMATION out.
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref BatteryQueryInformation inBuffer, uint inBufferSize,
        out BatteryInformation outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    // IOCTL_BATTERY_QUERY_STATUS: a BATTERY_WAIT_STATUS in, a BATTERY_STATUS out.
    [DllImport("kernel32.dll", EntryPoint = "DeviceIoControl", SetLastError = true, ExactSpelling = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool DeviceIoControl(SafeFileHandle device, uint ioControlCode, ref BatteryWaitStatus inBuffer, uint inBufferSize,
        out BatteryStatus outBuffer, uint outBufferSize, out uint bytesReturned, IntPtr overlapped);

    // The hottest battery's temperature in °C, or null (no battery, no sensor, any failure).
    public static double? Read()
    {
        var all = EachBattery(ReadTemperature);
        return all is { Count: > 0 } ? all.Max() : null;
    }

    // Every battery's charge; empty with no battery; null when the list could not be read.
    public static List<BatteryCell>? Cells() => EachBattery(ReadCell);

    // `readOne` on each battery that is there (its tag is not 0), in the list's order; a battery
    // it answers null for is left out. Null when the list itself failed.
    static List<T>? EachBattery<T>(Func<SafeFileHandle, uint, T?> readOne) where T : struct
    {
        IntPtr devices = InvalidHandle;
        try
        {
            var guid = BatteryClass;
            devices = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (devices == InvalidHandle || devices == IntPtr.Zero) return null;
            var found = new List<T>();
            for (uint i = 0; i < MaxBatteries; i++)
            {
                var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                // False at the end of the list (ERROR_NO_MORE_ITEMS), or on any failure.
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, i, ref data)) break;
                if (DevicePath(devices, ref data) is not { } path) continue;
                using var device = Open(path);
                if (device == null || Tag(device) is not { } tag) continue;
                if (readOne(device, tag) is { } value) found.Add(value);
            }
            return found;
        }
        catch (Exception) { return null; }
        finally
        {
            if (devices != InvalidHandle && devices != IntPtr.Zero)
            {
                try { SetupDiDestroyDeviceInfoList(devices); }
                catch (Exception) { }
            }
        }
    }

    static string? DevicePath(IntPtr devices, ref SpDeviceInterfaceData data)
    {
        // First call: no buffer, to learn the size. It must fail with ERROR_INSUFFICIENT_BUFFER.
        if (SetupDiGetDeviceInterfaceDetail(devices, ref data, IntPtr.Zero, 0, out uint required, IntPtr.Zero)) return null;
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer) return null;
        if (required < DetailDataCbSize || required > MaxDetailBytes) return null;
        IntPtr buffer = IntPtr.Zero;
        try
        {
            buffer = Marshal.AllocHGlobal((int)required);
            // Zeroed, so the path below always ends in a NUL inside the buffer.
            for (int b = 0; b < required; b++) Marshal.WriteByte(buffer, b, 0);
            Marshal.WriteInt32(buffer, 0, DetailDataCbSize);
            if (!SetupDiGetDeviceInterfaceDetail(devices, ref data, buffer, required, out _, IntPtr.Zero)) return null;
            int chars = (int)((required - 4) / 2);
            if (chars <= 0) return null;
            var path = Marshal.PtrToStringUni(buffer + 4, chars);
            int end = path.IndexOf('\0');
            if (end >= 0) path = path[..end];
            return path.Length == 0 ? null : path;
        }
        finally
        {
            if (buffer != IntPtr.Zero) Marshal.FreeHGlobal(buffer);
        }
    }

    // The battery's tag, or null when there is no battery in this slot (BATTERY_TAG_INVALID, 0).
    static uint? Tag(SafeFileHandle device)
    {
        uint wait = 0;  // don't wait for a battery to be inserted
        if (!DeviceIoControl(device, IoctlBatteryQueryTag, ref wait, sizeof(uint), out uint tag, sizeof(uint), out uint tagBytes, IntPtr.Zero)
            || tagBytes < sizeof(uint) || tag == 0)
            return null;
        return tag;
    }

    static double? ReadTemperature(SafeFileHandle device, uint tag)
    {
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = LevelBatteryTemperature, AtRate = 0 };
        // Fails (ERROR_INVALID_FUNCTION or similar) when the battery has no temperature sensor.
        if (!DeviceIoControl(device, IoctlBatteryQueryInformation, ref query, (uint)Marshal.SizeOf<BatteryQueryInformation>(),
                out uint tenthsKelvin, sizeof(uint), out uint bytes, IntPtr.Zero)
            || bytes < sizeof(uint))
            return null;
        return TemperatureMath.FromTenthsOfKelvin(tenthsKelvin);
    }

    // The full charge capacity (BATTERY_INFORMATION) and the state now (BATTERY_STATUS), or null.
    static BatteryCell? ReadCell(SafeFileHandle device, uint tag)
    {
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = LevelBatteryInformation, AtRate = 0 };
        uint infoSize = (uint)Marshal.SizeOf<BatteryInformation>();
        if (!DeviceIoControl(device, IoctlBatteryQueryInformation, ref query, (uint)Marshal.SizeOf<BatteryQueryInformation>(),
                out BatteryInformation info, infoSize, out uint infoBytes, IntPtr.Zero)
            || infoBytes < infoSize)
            return null;
        var wait = new BatteryWaitStatus { BatteryTag = tag };  // Timeout 0: the state now
        uint statusSize = (uint)Marshal.SizeOf<BatteryStatus>();
        if (!DeviceIoControl(device, IoctlBatteryQueryStatus, ref wait, (uint)Marshal.SizeOf<BatteryWaitStatus>(),
                out BatteryStatus status, statusSize, out uint statusBytes, IntPtr.Zero)
            || statusBytes < statusSize)
            return null;
        return new BatteryCell(status.PowerState, status.Capacity, status.Rate, info.FullChargedCapacity,
            (info.Capabilities & CapacityRelative) != 0);
    }

    // Read and write access, as the sample opens it; read only if that is refused (both IOCTLs
    // only need read access).
    static SafeFileHandle? Open(string path)
    {
        var handle = CreateFile(path, GenericRead | GenericWrite, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error != ErrorAccessDenied) return null;
        handle = CreateFile(path, GenericRead, FileShareRead | FileShareWrite, IntPtr.Zero, OpenExisting, FileAttributeNormal, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        handle.Dispose();
        return null;
    }
}

// ACPI thermal zones through WMI (root\WMI MSAcpi_ThermalZoneTemperature, tenths of a kelvin): the
// hottest zone in °C, or null. Most PCs refuse this without admin rights (access denied), and many
// have no zones: both are null. The query gets 3 seconds; a query still stuck from an earlier call
// is left to finish and no second one is started.
public static class ThermalZones
{
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);
    static readonly object gate = new();
    static Task<double?>? pending;

    public static double? Read()
    {
        try
        {
            Task<double?> query;
            lock (gate)
            {
                if (pending is { IsCompleted: false }) return null;
                query = pending = Task.Run(Query);
            }
            return query.Wait(QueryTimeout) ? query.Result : null;
        }
        catch (Exception) { return null; }
    }

    static double? Query()
    {
        try
        {
            // System.Management's options, not System.IO's (the implicit usings bring in both names).
            var options = new System.Management.EnumerationOptions { Timeout = QueryTimeout, ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\WMI"),
                new ObjectQuery("SELECT CurrentTemperature FROM MSAcpi_ThermalZoneTemperature"), options);
            using var zones = searcher.Get();
            double? hottest = null;
            foreach (ManagementBaseObject zone in zones)
            {
                using (zone)
                {
                    if (Whole(zone, "CurrentTemperature") is { } tenths && TemperatureMath.FromTenthsOfKelvin(tenths) is { } celsius
                        && (hottest == null || celsius > hottest))
                        hottest = celsius;
                }
            }
            return hottest;
        }
        catch (Exception) { return null; }
    }

    // A whole-number property (CurrentTemperature, a uint32, and the counters of
    // ThermalZoneInformation); anything else (missing, another type) is no reading.
    internal static long? Whole(ManagementBaseObject zone, string name)
    {
        try
        {
            return zone[name] switch
            {
                uint u => u,
                int i => i,
                long l => l,
                ulong ul when ul <= long.MaxValue => (long)ul,
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }
}

// One battery, as its driver reports it: PowerState (BATTERY_POWER_ON_LINE 1, BATTERY_DISCHARGING 2,
// BATTERY_CHARGING 4), Capacity now and FullCapacity (mWh; 0xFFFFFFFF unknown), Rate (mW, positive
// while charging; int.MinValue unknown), and Relative: the driver counts in its own units, not mWh.
public readonly record struct BatteryCell(uint PowerState, uint Capacity, int Rate, uint FullCapacity, bool Relative = false);

// What /status needs from the batteries' own drivers (6.4), over all batteries together.
public sealed record BatteryCharge(IReadOnlyList<BatteryCell> Cells)
{
    public const uint OnLine = 1, Discharging = 2, Charging = 4;
    public const uint UnknownCapacity = 0xFFFFFFFF;
    public const int UnknownRate = int.MinValue;  // BATTERY_UNKNOWN_RATE, 0x80000000
    // Longer than a day is not an estimate anyone can use (a trickle charge): unknown.
    public const int MaxMinutes = 24 * 60;
    // The share of the full charge capacity that counts as full, for `charged`.
    public const int FullPercent = 98;
    // Read again in the background at most this often (SystemReadings).
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(10);

    // Slow (it opens each battery): never on a request's thread. Null when the list failed.
    public static BatteryCharge? Read() => BatteryTemperature.Cells() is { } cells ? new BatteryCharge(cells) : null;

    // minutesToFull: what is missing to full, over all batteries, at the rate the charging ones
    // charge at now, in whole minutes. -1 when no battery is charging, or a capacity or rate is
    // unknown, or the batteries count in units of their own.
    public int MinutesToFull()
    {
        long missing = 0, rate = 0;
        bool charging = false;
        foreach (var c in Cells)
        {
            if (c.Relative || c.Capacity == UnknownCapacity || c.FullCapacity is 0 or UnknownCapacity) return -1;
            missing += Math.Max(0L, (long)c.FullCapacity - c.Capacity);
            if ((c.PowerState & Charging) == 0) continue;
            charging = true;
            if (c.Rate == UnknownRate || c.Rate <= 0) return -1;
            rate += c.Rate;
        }
        if (!charging || rate <= 0) return -1;
        double minutes = Math.Round(missing * 60.0 / rate);
        return minutes > MaxMinutes ? -1 : (int)minutes;
    }

    // charged: every battery is on the power line, neither charging nor discharging, and together
    // they hold at least FullPercent of their full charge. False as soon as one charges, discharges
    // or is off the line. Null when it can't be told (no battery, a capacity unknown or relative):
    // then StatusInfo keeps "plugged in at 100%".
    public bool? Charged()
    {
        if (Cells.Count == 0) return null;
        foreach (var c in Cells)
            if ((c.PowerState & OnLine) == 0 || (c.PowerState & (Charging | Discharging)) != 0) return false;
        long held = 0, full = 0;
        foreach (var c in Cells)
        {
            if (c.Relative || c.Capacity == UnknownCapacity || c.FullCapacity is 0 or UnknownCapacity) return null;
            held += c.Capacity;
            full += c.FullCapacity;
        }
        return held * 100 >= full * FullPercent;
    }
}

// The Thermal Zone Information performance counters (root\cimv2
// Win32_PerfFormattedData_Counters_ThermalZoneInformation), which Windows lets any user read, unlike
// MSAcpi_ThermalZoneTemperature. Hottest: the hottest zone in °C (null: none sane). PassiveLimit: the
// lowest PercentPassiveLimit from 1 to 100 (null: none): below 100, Windows is slowing the processor
// down to cool the zone (passive cooling, "throttling").
public sealed record ThermalZoneReading(double? Hottest, int? PassiveLimit)
{
    public static readonly ThermalZoneReading None = new(null, null);

    // Read again in the background at most this often (SystemReadings).
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    // Each zone's temperature in tenths of a kelvin (HighPrecisionTemperature, else Temperature in
    // whole kelvins times 10) and its PercentPassiveLimit, as the counters give them (null: none).
    // A limit of 0 or over 100 is no reading: some zones have no passive cooling at all.
    public static ThermalZoneReading From(IEnumerable<(long? Tenths, long? PassiveLimit)> zones)
    {
        double? hottest = null;
        int? limit = null;
        foreach (var (tenths, passive) in zones)
        {
            if (tenths is { } t && TemperatureMath.FromTenthsOfKelvin(t) is { } celsius && (hottest == null || celsius > hottest))
                hottest = celsius;
            if (passive is >= 1 and <= 100 && (limit == null || passive < limit)) limit = (int)passive.Value;
        }
        return new ThermalZoneReading(hottest, limit);
    }

    // Blocks for up to about 3 seconds (WMI): only in the background. Null when the query failed
    // (then the last reading is kept); None when this PC has no zones.
    public static ThermalZoneReading? Read()
    {
        try
        {
            var options = new System.Management.EnumerationOptions { Timeout = ThermalZones.QueryTimeout, ReturnImmediately = true, Rewindable = false };
            using var searcher = new ManagementObjectSearcher(new ManagementScope(@"root\cimv2"),
                new ObjectQuery("SELECT HighPrecisionTemperature, Temperature, PercentPassiveLimit FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation"),
                options);
            using var zones = searcher.Get();
            var read = new List<(long?, long?)>();
            foreach (ManagementBaseObject zone in zones)
            {
                using (zone)
                {
                    long? precise = ThermalZones.Whole(zone, "HighPrecisionTemperature"), kelvin = ThermalZones.Whole(zone, "Temperature");
                    long? tenths = precise > 0 ? precise : kelvin > 0 ? kelvin * 10 : null;
                    read.Add((tenths, ThermalZones.Whole(zone, "PercentPassiveLimit")));
                }
            }
            return From(read);
        }
        catch (ManagementException e) when (e.ErrorCode == ManagementStatus.InvalidClass) { return None; }
        catch (Exception) { return null; }
    }
}

// thermalState (6.1) from the thermal zones, in the Mac's four words. The hottest zone is measured
// against the 90 °C limit at which Lid Awake turns off (Limits.MaxPcTemp), and Windows slowing the
// processor down to cool it (PercentPassiveLimit under SeriousPassiveLimit, so a moment's light
// throttling doesn't flicker) counts as the Mac's "serious".
public static class ThermalState
{
    public const string Unknown = "unknown";
    // Within this much of the limit: "serious", and within FairBelow: "fair".
    public const int SeriousBelow = 5;
    public const int FairBelow = 15;
    // The processor held to half its speed or less: "critical", as at the limit itself; under 90%: "serious".
    public const int CriticalPassiveLimit = 50;
    public const int SeriousPassiveLimit = 90;

    // `zone` is MSAcpi_ThermalZoneTemperature's hottest (HeatSensor), `zones` the performance
    // counters; the hotter of the two counts. "unknown" when neither has anything (the page shows a dash).
    public static string Name(double? zone, ThermalZoneReading? zones)
    {
        double? hottest = Sane(zone);
        if (Sane(zones?.Hottest) is { } other && (hottest == null || other > hottest)) hottest = other;
        int? limit = zones?.PassiveLimit;
        if (hottest == null && limit == null) return Unknown;
        double t = hottest ?? double.NegativeInfinity;
        if (t >= Limits.MaxPcTemp || limit <= CriticalPassiveLimit) return "critical";
        if (limit < SeriousPassiveLimit || t >= Limits.MaxPcTemp - SeriousBelow) return "serious";
        if (t >= Limits.MaxPcTemp - FairBelow) return "fair";
        return "nominal";
    }

    static double? Sane(double? celsius) => celsius is { } c && TemperatureMath.IsSane(c) ? c : null;
}
