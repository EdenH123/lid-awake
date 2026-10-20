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
// apart because they mean different things: the battery (its own sensor, or a laptop maker's
// sensor labelled for the battery; limit maxBatteryTemp, 40 °C, and the only one sent as
// batteryTemp), and the PC itself: the thermal zones or a maker's board sensor, where 40 to 60 °C is
// normal (ZoneLimit Limits.MaxPcTemp, 90 °C), or, only on a PC with no board-type sensor, a maker's
// processor die sensor (ZoneLimit Limits.MaxDieTemp, 100 °C).
// ZoneConfirmed: the zone may turn Lid Awake off or refuse a turn-on. TemperatureChain sets it only
// once the zone has been at or above ZoneLimit on HotReads reads in a row and has been seen at two
// different values in this run (a sensor that never moves is not trusted). A reading made directly
// (tests) counts as confirmed. Throttled: Windows has held the processor to half its speed or less
// (PercentPassiveLimit) on HotReads reads in a row, the Mac's thermal state "critical".
public readonly record struct TemperatureReading(double? Battery, double? Zone, int ZoneLimit = Limits.MaxPcTemp,
    bool ZoneConfirmed = true, bool Throttled = false)
{
    public static readonly TemperatureReading None = new(null, null);

    // Some source gave a temperature.
    public bool Any => Battery != null || Zone != null;
}

// Windows' own sources every round (as the Mac checks both the battery temperature and the thermal
// state), and the laptop makers' WMI sensors for what those leave out (TemperatureChain). Never
// throws; blocks for about 3 seconds per WMI query that answers slowly (the makers' together at most
// about 6), so never call it on the UI thread (HeatSensor calls it from the safety check's
// background timer).
public static class TemperatureSources
{
    // Told which source the temperature comes from, once per source (the tray sets this to the
    // activity log's Write): "Temperature from HP BIOS sensors".
    public static Action<string>? Log { get; set; }

    // The processor load between two rounds (GetSystemTimes), for the stuck-sensor check.
    static readonly SystemReadings cpu = new();
    static readonly CpuMeter meter = new();

    static readonly TemperatureChain chain = new(BatteryTemperature.Read, ZoneCounters.Read, VendorSensors.All, Wmi.Shared,
        load: () => cpu.Cpu() is { } times ? meter.Percent(times, Environment.TickCount64) : null, log: text => Log?.Invoke(text));

    public static TemperatureReading Read() => ReadRound().Reading;

    // The same read, with the source of each value (the self-test and the battery check).
    public static TemperatureRound ReadRound()
    {
        try { return chain.ReadRound(); }
        catch (Exception) { return TemperatureRound.None; }
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

// ACPI thermal zones through WMI (root\WMI MSAcpi_ThermalZoneTemperature, tenths of a kelvin), each
// by its InstanceName. Most PCs refuse this without admin rights (access denied), and many have no
// zones: TemperatureChain then leaves it alone for 10 minutes. Through Wmi: 3 seconds per query.
public static class ThermalZones
{
    public static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(3);
    public const string Scope = @"root\WMI";
    public const string Query = "SELECT InstanceName, CurrentTemperature FROM MSAcpi_ThermalZoneTemperature";

    // The hottest zone in °C, or null (none, refused, failed).
    public static double? Read()
    {
        try { return NamedZone.Hottest(Each(Wmi.Shared.Query(Scope, Query).Rows)); }
        catch (Exception) { return null; }
    }

    // Every zone with a CurrentTemperature (a uint32); Celsius null for garbage.
    public static List<NamedZone> Each(IEnumerable<IReadOnlyDictionary<string, object?>> rows)
    {
        var zones = new List<NamedZone>();
        foreach (var row in rows)
        {
            if (!row.TryGetValue("CurrentTemperature", out var v) || WmiValues.Whole(v) is not { } tenths) continue;
            string name = row.TryGetValue("InstanceName", out var n) && n is string s && s.Length > 0 ? s : "zone " + zones.Count;
            zones.Add(new NamedZone(name, TemperatureMath.FromTenthsOfKelvin(tenths)));
        }
        return zones;
    }
}

// One thermal zone's temperature, by its name; Celsius null when it gave garbage.
public readonly record struct NamedZone(string Name, double? Celsius)
{
    public static double? Hottest(IEnumerable<NamedZone> zones)
    {
        double? hottest = null;
        foreach (var z in zones)
            if (z.Celsius is { } c && TemperatureMath.IsSane(c) && (hottest == null || c > hottest)) hottest = c;
        return hottest;
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

    public const string Scope = @"root\cimv2";
    public const string Query =
        "SELECT Name, HighPrecisionTemperature, Temperature, PercentPassiveLimit FROM Win32_PerfFormattedData_Counters_ThermalZoneInformation";

    // Each zone by its counter instance name ("\_TZ.TZ00"), for TemperatureChain's stuck-sensor check.
    public IReadOnlyList<NamedZone> Zones { get; init; } = Array.Empty<NamedZone>();

    public bool Equals(ThermalZoneReading? other) => other is not null && Hottest == other.Hottest
        && PassiveLimit == other.PassiveLimit && Zones.SequenceEqual(other.Zones);

    public override int GetHashCode() => HashCode.Combine(Hottest, PassiveLimit, Zones.Count);

    // Read again in the background at most this often (SystemReadings).
    public static readonly TimeSpan Every = TimeSpan.FromSeconds(15);

    // Each zone's temperature in tenths of a kelvin (HighPrecisionTemperature, else Temperature in
    // whole kelvins times 10) and its PercentPassiveLimit, as the counters give them (null: none).
    // A limit of 0 or over 100 is no reading: some zones have no passive cooling at all.
    public static ThermalZoneReading From(IEnumerable<(long? Tenths, long? PassiveLimit)> zones) =>
        From(zones.Select((z, i) => ("zone " + i, z.Tenths, z.PassiveLimit)));

    // The same, each zone with its name (Zones lists the ones with a temperature).
    public static ThermalZoneReading From(IEnumerable<(string Name, long? Tenths, long? PassiveLimit)> zones)
    {
        int? limit = null;
        var named = new List<NamedZone>();
        foreach (var (name, tenths, passive) in zones)
        {
            if (tenths is { } t) named.Add(new NamedZone(name, TemperatureMath.FromTenthsOfKelvin(t)));
            if (passive is >= 1 and <= 100 && (limit == null || passive < limit)) limit = (int)passive.Value;
        }
        return new ThermalZoneReading(NamedZone.Hottest(named), limit)
        {
            Zones = named.Count == 0 ? Array.Empty<NamedZone>() : named,
        };
    }

    // From the counters' WMI objects.
    public static ThermalZoneReading FromRows(IEnumerable<IReadOnlyDictionary<string, object?>> rows) =>
        From(rows.Select((row, i) =>
        {
            long? precise = Whole(row, "HighPrecisionTemperature"), kelvin = Whole(row, "Temperature");
            long? tenths = precise > 0 ? precise : kelvin > 0 ? kelvin * 10 : null;
            string name = row.TryGetValue("Name", out var n) && n is string s && s.Length > 0 ? s : "zone " + i;
            return (name, tenths, Whole(row, "PercentPassiveLimit"));
        }).ToList());

    static long? Whole(IReadOnlyDictionary<string, object?> row, string name) =>
        row.TryGetValue(name, out var v) ? WmiValues.Whole(v) : null;

    // Through Wmi (3 seconds, one query at a time): only in the background. Null when the query
    // failed or took too long (then the last reading is kept); None when this PC has no zones or
    // may not read them. ZoneCounters shares one reading between its two users.
    public static ThermalZoneReading? Read()
    {
        try
        {
            var result = Wmi.Shared.Query(Scope, Query);
            return result.Outcome switch
            {
                WmiOutcome.Rows => FromRows(result.Rows),
                WmiOutcome.Missing => None,
                _ => null,
            };
        }
        catch (Exception) { return null; }
    }
}

// The Thermal Zone Information counters, read at most every Fresh (10 seconds) for both their users:
// the temperature chain on the safety check's thread and thermalState (SystemReadings), so one query
// serves both. A read that fails is not kept, and the next caller tries again.
public static class ZoneCounters
{
    public static readonly TimeSpan Fresh = TimeSpan.FromSeconds(10);
    static readonly object gate = new();
    static ThermalZoneReading? last;
    static long lastAt;

    public static ThermalZoneReading? Read()
    {
        lock (gate)
        {
            if (last != null && Environment.TickCount64 - lastAt < (long)Fresh.TotalMilliseconds) return last;
        }
        var read = ThermalZoneReading.Read();
        if (read != null)
        {
            lock (gate) (last, lastAt) = (read, Environment.TickCount64);
        }
        return read;
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
