using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using LidAwake;
using Xunit;

// The Native Wifi reader. CI has no Wi-Fi, so the layout is checked two ways that need none: the
// structs' marshaled sizes and offsets against the Windows SDK's, and hand-built native blocks
// (written byte by byte at the SDK offsets) parsed by the same code the real calls use.
public class WlanReaderTests
{
    // ---- struct layout, against wlanapi.h / windot11.h ----

    [Fact]
    public void StructSizesMatchTheWindowsSdk()
    {
        Assert.Equal(532, Marshal.SizeOf<WlanReader.WlanInterfaceInfo>());
        Assert.Equal(36, Marshal.SizeOf<WlanReader.Dot11Ssid>());
        Assert.Equal(68, Marshal.SizeOf<WlanReader.WlanAssociationAttributes>());
        Assert.Equal(16, Marshal.SizeOf<WlanReader.WlanSecurityAttributes>());
        Assert.Equal(604, Marshal.SizeOf<WlanReader.WlanConnectionAttributes>());
    }

    [Fact]
    public void FieldOffsetsMatchTheWindowsSdk()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<WlanReader.WlanInterfaceInfo>("InterfaceGuid"));
        Assert.Equal(16, (int)Marshal.OffsetOf<WlanReader.WlanInterfaceInfo>("Description"));
        Assert.Equal(528, (int)Marshal.OffsetOf<WlanReader.WlanInterfaceInfo>("State"));

        Assert.Equal(0, (int)Marshal.OffsetOf<WlanReader.WlanConnectionAttributes>("State"));
        Assert.Equal(4, (int)Marshal.OffsetOf<WlanReader.WlanConnectionAttributes>("Mode"));
        Assert.Equal(8, (int)Marshal.OffsetOf<WlanReader.WlanConnectionAttributes>("ProfileName"));
        Assert.Equal(520, (int)Marshal.OffsetOf<WlanReader.WlanConnectionAttributes>("Association"));
        Assert.Equal(588, (int)Marshal.OffsetOf<WlanReader.WlanConnectionAttributes>("Security"));

        // Inside WLAN_ASSOCIATION_ATTRIBUTES (add 520 for the place in the whole block).
        Assert.Equal(0, (int)Marshal.OffsetOf<WlanReader.WlanAssociationAttributes>("Ssid"));
        Assert.Equal(36, (int)Marshal.OffsetOf<WlanReader.WlanAssociationAttributes>("BssType"));
        Assert.Equal(40, (int)Marshal.OffsetOf<WlanReader.WlanAssociationAttributes>("Bssid"));
        Assert.Equal(48, (int)Marshal.OffsetOf<WlanReader.WlanAssociationAttributes>("PhyType"));
        Assert.Equal(52, (int)Marshal.OffsetOf<WlanReader.WlanAssociationAttributes>("PhyIndex"));
        Assert.Equal(56, (int)Marshal.OffsetOf<WlanReader.WlanAssociationAttributes>("SignalQuality"));
        Assert.Equal(60, (int)Marshal.OffsetOf<WlanReader.WlanAssociationAttributes>("RxRate"));
        Assert.Equal(64, (int)Marshal.OffsetOf<WlanReader.WlanAssociationAttributes>("TxRate"));

        Assert.Equal(0, (int)Marshal.OffsetOf<WlanReader.Dot11Ssid>("Length"));
        Assert.Equal(4, (int)Marshal.OffsetOf<WlanReader.Dot11Ssid>("Bytes"));
    }

    // ---- hand-built WLAN_CONNECTION_ATTRIBUTES ----

    // A zeroed native block of `size` bytes, freed on dispose.
    sealed class Native : IDisposable
    {
        public readonly IntPtr Ptr;
        public readonly int Size;
        public Native(int size)
        {
            Size = size;
            Ptr = Marshal.AllocHGlobal(size);
            Marshal.Copy(new byte[size], 0, Ptr, size);
        }
        public void Bytes(int offset, byte[] bytes) => Marshal.Copy(bytes, 0, Ptr + offset, bytes.Length);
        public void Dispose() => Marshal.FreeHGlobal(Ptr);
    }

    // Written at the SDK offsets: isState 0, profile 8, SSID length 520, SSID bytes 524,
    // BSS type 556, BSSID 560, PHY type 568, signal quality 576, rates 580 and 584.
    static Native Connection(int state, uint quality, byte[] ssid, uint? ssidLength = null)
    {
        var n = new Native(604);
        Marshal.WriteInt32(n.Ptr, 0, state);
        Marshal.WriteInt32(n.Ptr, 4, 0);                                   // wlan_connection_mode_profile
        n.Bytes(8, Encoding.Unicode.GetBytes("Profile name"));
        Marshal.WriteInt32(n.Ptr, 520, (int)(ssidLength ?? (uint)ssid.Length));
        n.Bytes(524, ssid);
        Marshal.WriteInt32(n.Ptr, 556, 1);                                 // dot11_BSS_type_infrastructure
        n.Bytes(560, new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF });
        Marshal.WriteInt32(n.Ptr, 568, 7);                                 // a PHY type
        Marshal.WriteInt32(n.Ptr, 572, 0);
        Marshal.WriteInt32(n.Ptr, 576, (int)quality);
        Marshal.WriteInt32(n.Ptr, 580, 866_700);
        Marshal.WriteInt32(n.Ptr, 584, 866_700);
        Marshal.WriteInt32(n.Ptr, 588, 1);                                 // security enabled
        return n;
    }

    [Fact]
    public void AConnectedBlockGivesTheQualityAndName()
    {
        using var n = Connection(1, 60, Encoding.UTF8.GetBytes("Home"));
        Assert.Equal(new WifiReading(60, "Home"), WlanReader.FromConnection(n.Ptr, n.Size));
    }

    [Fact]
    public void TheNameIsDecodedAsUtf8UpToItsLength()
    {
        // 32 bytes, the longest a name can be, and not followed by a zero.
        var name = Encoding.UTF8.GetBytes("Café ☕ upstairs network 5G_ok");
        Assert.Equal(32, name.Length);
        using var n = Connection(1, 99, name);
        Assert.Equal(new WifiReading(99, "Café ☕ upstairs network 5G_ok"), WlanReader.FromConnection(n.Ptr, n.Size));
        // A length shorter than the bytes: only that many are used.
        using var shorter = Connection(1, 99, name, ssidLength: 3);
        Assert.Equal("Caf", WlanReader.FromConnection(shorter.Ptr, shorter.Size)!.Value.Ssid);
        // Cut through the middle of "é": no longer valid UTF-8, so no name.
        using var cut = Connection(1, 99, name, ssidLength: 4);
        Assert.Null(WlanReader.FromConnection(cut.Ptr, cut.Size)!.Value.Ssid);
    }

    [Fact]
    public void ANameThatIsNotTextIsLeftOutButTheSignalStays()
    {
        foreach (var (bytes, length) in new (byte[], uint?)[]
        {
            (new byte[] { 0xFF, 0xFE, 0x41 }, null),      // not UTF-8
            (new byte[] { 0x41, 0x00, 0x42 }, null),      // a control character
            (Array.Empty<byte>(), null),                  // hidden network: length 0
            (new byte[] { 0x41 }, 33u),                   // longer than 32: nonsense
        })
        {
            using var n = Connection(1, 40, bytes, length);
            var reading = WlanReader.FromConnection(n.Ptr, n.Size);
            Assert.NotNull(reading);
            Assert.Equal(40, reading!.Value.Quality);
            Assert.Null(reading.Value.Ssid);
        }
    }

    [Fact]
    public void NotConnectedTooSmallOrOutOfRangeGivesNothing()
    {
        using (var n = Connection(4, 60, Encoding.UTF8.GetBytes("Home")))   // wlan_interface_state_disconnected
            Assert.Null(WlanReader.FromConnection(n.Ptr, n.Size));
        using (var n = Connection(1, 101, Encoding.UTF8.GetBytes("Home")))
            Assert.Null(WlanReader.FromConnection(n.Ptr, n.Size));
        using (var n = Connection(1, 60, Encoding.UTF8.GetBytes("Home")))
            Assert.Null(WlanReader.FromConnection(n.Ptr, 580));
        Assert.Null(WlanReader.FromConnection(IntPtr.Zero, 604));
    }

    // ---- hand-built WLAN_INTERFACE_INFO_LIST ----

    // dwNumberOfItems 0, dwIndex 4, then 532-byte entries from 8: GUID 0, description 16, isState 528.
    static Native InterfaceList(params (Guid Id, int State)[] items)
    {
        var n = new Native(8 + 532 * Math.Max(1, items.Length));
        Marshal.WriteInt32(n.Ptr, 0, items.Length);
        Marshal.WriteInt32(n.Ptr, 4, 0);
        for (int i = 0; i < items.Length; i++)
        {
            int at = 8 + i * 532;
            n.Bytes(at, items[i].Id.ToByteArray());
            n.Bytes(at + 16, Encoding.Unicode.GetBytes("Wireless adapter " + i));
            Marshal.WriteInt32(n.Ptr, at + 528, items[i].State);
        }
        return n;
    }

    static readonly Guid A = Guid.Parse("11111111-2222-3333-4444-555555555555");
    static readonly Guid B = Guid.Parse("6B29FC40-CA47-1067-B31D-00DD010662DA");
    static readonly Guid C = Guid.Parse("AAAAAAAA-BBBB-CCCC-DDDD-EEEEEEEEEEEE");

    [Fact]
    public void OnlyConnectedInterfacesAreTried()
    {
        using var n = InterfaceList((A, 4), (B, 1), (C, 1));
        Assert.Equal(new[] { B, C }, WlanReader.ConnectedInterfaces(n.Ptr, null));
    }

    [Fact]
    public void TheAdapterInUseIsTriedFirst()
    {
        using var n = InterfaceList((A, 1), (B, 1), (C, 4));
        Assert.Equal(new[] { B, A }, WlanReader.ConnectedInterfaces(n.Ptr, B));
        // Not connected (or not in the list): the connected ones, in order.
        Assert.Equal(new[] { A, B }, WlanReader.ConnectedInterfaces(n.Ptr, C));
        Assert.Equal(new[] { A, B }, WlanReader.ConnectedInterfaces(n.Ptr, Guid.NewGuid()));
    }

    [Fact]
    public void AnEmptyListHasNoInterfaces()
    {
        using var n = InterfaceList();
        Assert.Empty(WlanReader.ConnectedInterfaces(n.Ptr, null));
        Assert.Empty(WlanReader.ConnectedInterfaces(IntPtr.Zero, A));
    }

    // ---- the real Native Wifi API on this machine ----

    [Fact]
    public void TheRealReaderNeverThrowsAndAnswersNothingOrAReadingInRange()
    {
        // CI has no Wi-Fi (and maybe no wlanapi.dll or WLAN service): null. A laptop on Wi-Fi: a reading.
        foreach (var id in new[] { null, "", "not a guid", "{" + Guid.NewGuid() + "}" })
        {
            var reading = WlanReader.Read(id);
            if (reading is { } r)
            {
                Assert.InRange(r.Quality, 0, 100);
                Assert.True(r.Ssid == null || r.Ssid.Length > 0);
            }
        }
        var viaReadings = new SystemReadings().Wifi(null);
        if (viaReadings is { } v) Assert.InRange(v.Quality, 0, 100);
    }

    [Fact]
    public void TheRealStatusSendsRssiOnlyInRangeAndNeverWhenWired()
    {
        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(new StatusInfo(new SystemReadings()).Build(true)));
        var net = doc.RootElement.GetProperty("net");
        if (net.TryGetProperty("rssi", out var rssi))
        {
            Assert.InRange(rssi.GetInt32(), -100, -50);
            Assert.False(net.GetProperty("wired").GetBoolean());
        }
        if (net.TryGetProperty("wifi", out var name)) Assert.False(string.IsNullOrEmpty(name.GetString()));
    }
}
