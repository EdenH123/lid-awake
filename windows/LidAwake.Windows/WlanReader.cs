using System.Runtime.InteropServices;
using System.Text;

namespace LidAwake;

// The connected Wi-Fi: Windows' signal quality (0 to 100) and the network name (null when the
// name is hidden or isn't valid text).
public readonly record struct WifiReading(int Quality, string? Ssid);

// The Native Wifi API (wlanapi.dll): the signal and network name of the connected Wi-Fi. Every
// failure answers null and never throws: no wlanapi.dll (Windows Server without the wireless
// feature), the WLAN AutoConfig service not running (ERROR_SERVICE_NOT_ACTIVE, 1062), no Wi-Fi
// adapter, none connected, or (Windows 11) no Location consent for this app (ERROR_ACCESS_DENIED,
// 5; the first call shows a one-time system prompt). That last one is also noted once in the
// activity log. Nothing is kept between calls, so it is safe from any thread.
//
// Layouts, from wlanapi.h / windot11.h (all fields 4-byte aligned, natural packing):
//
//   WLAN_INTERFACE_INFO_LIST         dwNumberOfItems 0, dwIndex 4, InterfaceInfo[] at 8
//   WLAN_INTERFACE_INFO (532 bytes)  InterfaceGuid 0 (16), strInterfaceDescription 16 (WCHAR[256],
//                                    512 bytes), isState 528
//   WLAN_CONNECTION_ATTRIBUTES (604) isState 0, wlanConnectionMode 4, strProfileName 8 (WCHAR[256],
//                                    512 bytes), wlanAssociationAttributes 520, wlanSecurityAttributes 588
//   WLAN_ASSOCIATION_ATTRIBUTES (68, at 520)
//     dot11Ssid           520  DOT11_SSID, 36 bytes: uSSIDLength 520, ucSSID[32] 524
//     dot11BssType        556
//     dot11Bssid          560  6 bytes, then 2 bytes padding to 568
//     dot11PhyType        568
//     uDot11PhyIndex      572
//     wlanSignalQuality   576  0 to 100
//     ulRxRate            580
//     ulTxRate            584
//   WLAN_SECURITY_ATTRIBUTES (16, at 588)  bSecurityEnabled, bOneXEnabled, dot11AuthAlgorithm,
//                                          dot11CipherAlgorithm
//
// The tests check these sizes and offsets against the structs below (Marshal.SizeOf/OffsetOf),
// and parse hand-built blocks written at the offsets above, so CI checks the layout without Wi-Fi.
public static class WlanReader
{
    const uint ClientVersion = 2;               // Windows Vista and later
    const int StateConnected = 1;               // wlan_interface_state_connected
    const int OpcodeCurrentConnection = 7;      // wlan_intf_opcode_current_connection
    const int ListHeaderBytes = 8;              // dwNumberOfItems, dwIndex
    const uint ErrorAccessDenied = 5;           // Windows 11: no Location consent for this app

    // Told things worth a line in the activity log (the tray sets this to the log's Write).
    public static Action<string>? Log { get; set; }

    // What the activity log says the first time Windows refuses the signal for lack of Location.
    public const string LocationNote =
        "Wi-Fi signal needs Location to be allowed for Lid Awake (Settings -> Privacy & security -> Location)";

    // Once per run, whatever the thread.
    static int deniedNoted;

    // Every failing native call comes through here. Only "access denied" is told to the log, and
    // only the first time in this run. The reading itself stays null either way.
    internal static void NoteError(uint error)
    {
        if (error != ErrorAccessDenied) return;
        if (Interlocked.Exchange(ref deniedNoted, 1) != 0) return;
        try { Log?.Invoke(LocationNote); }
        catch (Exception) { }
    }

    // For tests: forget that the denial was already told.
    internal static void ResetDeniedNote() => Interlocked.Exchange(ref deniedNoted, 0);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanInterfaceInfo
    {
        public Guid InterfaceGuid;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string Description;
        public int State;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct Dot11Ssid
    {
        public uint Length;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)] public byte[] Bytes;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanAssociationAttributes
    {
        public Dot11Ssid Ssid;
        public int BssType;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)] public byte[] Bssid;
        public int PhyType;
        public uint PhyIndex;
        public uint SignalQuality;
        public uint RxRate;
        public uint TxRate;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct WlanSecurityAttributes
    {
        public int SecurityEnabled;
        public int OneXEnabled;
        public int AuthAlgorithm;
        public int CipherAlgorithm;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WlanConnectionAttributes
    {
        public int State;
        public int Mode;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string ProfileName;
        public WlanAssociationAttributes Association;
        public WlanSecurityAttributes Security;
    }

    [DllImport("wlanapi.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint WlanOpenHandle(uint dwClientVersion, IntPtr pReserved, out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint WlanEnumInterfaces(IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    // pInterfaceGuid is `const GUID *`; pWlanOpcodeValueType is optional, so it is passed as NULL.
    [DllImport("wlanapi.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern uint WlanQueryInterface(IntPtr hClientHandle, ref Guid pInterfaceGuid, int opCode, IntPtr pReserved,
        out int pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    [DllImport("wlanapi.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    static extern void WlanFreeMemory(IntPtr pMemory);

    // The connected Wi-Fi, preferring the adapter `adapterId` (NetworkInterface.Id, the GUID in
    // braces) when it is one of the connected ones; otherwise the first connected one. Null on
    // any failure.
    public static WifiReading? Read(string? adapterId)
    {
        IntPtr handle = IntPtr.Zero, list = IntPtr.Zero;
        try
        {
            var error = WlanOpenHandle(ClientVersion, IntPtr.Zero, out _, out handle);
            if (error != 0) { handle = IntPtr.Zero; NoteError(error); return null; }
            error = WlanEnumInterfaces(handle, IntPtr.Zero, out list);
            if (error != 0) { list = IntPtr.Zero; NoteError(error); return null; }
            Guid? preferred = Guid.TryParse(adapterId, out var g) ? g : null;
            foreach (var guid in ConnectedInterfaces(list, preferred))
                if (Query(handle, guid) is { } reading) return reading;
            return null;
        }
        catch (Exception) { return null; }
        finally
        {
            try { if (list != IntPtr.Zero) WlanFreeMemory(list); }
            catch (Exception) { }
            try { if (handle != IntPtr.Zero) WlanCloseHandle(handle, IntPtr.Zero); }
            catch (Exception) { }
        }
    }

    static WifiReading? Query(IntPtr handle, Guid guid)
    {
        IntPtr data = IntPtr.Zero;
        try
        {
            var error = WlanQueryInterface(handle, ref guid, OpcodeCurrentConnection, IntPtr.Zero, out int size, out data, IntPtr.Zero);
            if (error != 0) { data = IntPtr.Zero; NoteError(error); return null; }
            return FromConnection(data, size);
        }
        catch (Exception) { return null; }
        finally
        {
            try { if (data != IntPtr.Zero) WlanFreeMemory(data); }
            catch (Exception) { }
        }
    }

    // The connected interfaces in a WLAN_INTERFACE_INFO_LIST, `preferred` first when it is one.
    internal static List<Guid> ConnectedInterfaces(IntPtr list, Guid? preferred)
    {
        var connected = new List<Guid>();
        if (list == IntPtr.Zero) return connected;
        int count = Marshal.ReadInt32(list, 0);
        int stride = Marshal.SizeOf<WlanInterfaceInfo>();
        for (int i = 0; i < count && i < 64; i++)
        {
            var info = Marshal.PtrToStructure<WlanInterfaceInfo>(list + ListHeaderBytes + i * stride);
            if (info.State == StateConnected) connected.Add(info.InterfaceGuid);
        }
        if (preferred is { } p && connected.Remove(p)) connected.Insert(0, p);
        return connected;
    }

    // The reading in a WLAN_CONNECTION_ATTRIBUTES block of `size` bytes, or null.
    internal static WifiReading? FromConnection(IntPtr data, int size)
    {
        if (data == IntPtr.Zero || size < Marshal.SizeOf<WlanConnectionAttributes>()) return null;
        var c = Marshal.PtrToStructure<WlanConnectionAttributes>(data);
        if (c.State != StateConnected) return null;
        uint quality = c.Association.SignalQuality;
        if (quality > 100) return null;
        return new WifiReading((int)quality, SsidText(c.Association.Ssid));
    }

    static readonly UTF8Encoding StrictUtf8 = new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    // The name as UTF-8 text, or null when hidden (length 0), too long, or not valid text.
    static string? SsidText(Dot11Ssid ssid)
    {
        if (ssid.Bytes == null || ssid.Length == 0 || ssid.Length > 32) return null;
        try
        {
            var text = StrictUtf8.GetString(ssid.Bytes, 0, (int)ssid.Length);
            return text.Any(char.IsControl) ? null : text;
        }
        catch (ArgumentException) { return null; }   // DecoderFallbackException: not UTF-8
    }
}
