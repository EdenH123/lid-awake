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

// The battery's own temperature: IOCTL_BATTERY_QUERY_INFORMATION with BatteryTemperature, on each
// battery that SetupAPI lists (as Microsoft's "Enumerating Battery Devices" sample does). Many
// batteries don't report one (the IOCTL fails): then null. With several batteries, the hottest.
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
//                                               out: ULONG, tenths of a kelvin
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
    internal const int LevelBatteryTemperature = 2;
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

    // The hottest battery's temperature in °C, or null (no battery, no sensor, any failure).
    public static double? Read()
    {
        IntPtr devices = InvalidHandle;
        try
        {
            var guid = BatteryClass;
            devices = SetupDiGetClassDevs(ref guid, IntPtr.Zero, IntPtr.Zero, DigcfPresent | DigcfDeviceInterface);
            if (devices == InvalidHandle || devices == IntPtr.Zero) return null;
            double? hottest = null;
            for (uint i = 0; i < MaxBatteries; i++)
            {
                var data = new SpDeviceInterfaceData { CbSize = (uint)Marshal.SizeOf<SpDeviceInterfaceData>() };
                // False at the end of the list (ERROR_NO_MORE_ITEMS), or on any failure.
                if (!SetupDiEnumDeviceInterfaces(devices, IntPtr.Zero, ref guid, i, ref data)) break;
                if (DevicePath(devices, ref data) is not { } path) continue;
                if (ReadOne(path) is { } celsius && (hottest == null || celsius > hottest)) hottest = celsius;
            }
            return hottest;
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

    static double? ReadOne(string path)
    {
        using var device = Open(path);
        if (device == null) return null;
        uint wait = 0;  // don't wait for a battery to be inserted
        if (!DeviceIoControl(device, IoctlBatteryQueryTag, ref wait, sizeof(uint), out uint tag, sizeof(uint), out uint tagBytes, IntPtr.Zero)
            || tagBytes < sizeof(uint) || tag == 0)
            return null;
        var query = new BatteryQueryInformation { BatteryTag = tag, InformationLevel = LevelBatteryTemperature, AtRate = 0 };
        // Fails (ERROR_INVALID_FUNCTION or similar) when the battery has no temperature sensor.
        if (!DeviceIoControl(device, IoctlBatteryQueryInformation, ref query, (uint)Marshal.SizeOf<BatteryQueryInformation>(),
                out uint tenthsKelvin, sizeof(uint), out uint bytes, IntPtr.Zero)
            || bytes < sizeof(uint))
            return null;
        return TemperatureMath.FromTenthsOfKelvin(tenthsKelvin);
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
                    if (Tenths(zone) is { } tenths && TemperatureMath.FromTenthsOfKelvin(tenths) is { } celsius
                        && (hottest == null || celsius > hottest))
                        hottest = celsius;
                }
            }
            return hottest;
        }
        catch (Exception) { return null; }
    }

    // CurrentTemperature is a uint32; anything else (missing, another type) is no reading.
    static long? Tenths(ManagementBaseObject zone)
    {
        try
        {
            return zone["CurrentTemperature"] switch
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
