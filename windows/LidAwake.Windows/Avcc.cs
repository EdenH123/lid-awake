namespace LidAwake;

// H.264 as the phone's decoder wants it (docs/API.md 7.2). Media Foundation's encoder writes
// Annex B: NAL units separated by 00 00 01 or 00 00 00 01 start codes, with SPS and PPS in the
// stream. The page's WebCodecs decoder is set up with an avcC record (SPS and PPS, from the setup
// message) and then gets each picture in AVCC form: every NAL unit after a 4-byte big-endian
// length, without SPS, PPS or access unit delimiters. VideoEncoder.swift gets the same from
// VideoToolbox.
public static class Avcc
{
    public const int Idr = 5;
    public const int Sps = 7;
    public const int Pps = 8;
    public const int Aud = 9;

    // One encoded picture: AVCC data, whether it is a keyframe (has an IDR slice), and the SPS
    // and PPS it carried (null when it carried none).
    public sealed record Unit(byte[] Data, bool Key, byte[]? Sps, byte[]? Pps);

    public static int NalType(byte[] nal) => nal.Length == 0 ? -1 : nal[0] & 0x1F;

    // The NAL units between start codes. Zero bytes before a start code belong to it (a 4-byte
    // start code, or padding), so they are cut from the end of the unit before it. Data with no
    // start code at all has no units.
    public static List<byte[]> SplitAnnexB(ReadOnlySpan<byte> data)
    {
        var nals = new List<byte[]>();
        int start = -1;
        int i = 0;
        while (i + 2 < data.Length)
        {
            if (data[i] == 0 && data[i + 1] == 0 && data[i + 2] == 1)
            {
                if (start >= 0) Add(nals, data[start..i]);
                i += 3;
                start = i;
                continue;
            }
            i++;
        }
        if (start >= 0 && start < data.Length) Add(nals, data[start..]);
        return nals;
    }

    static void Add(List<byte[]> nals, ReadOnlySpan<byte> nal)
    {
        int end = nal.Length;
        while (end > 0 && nal[end - 1] == 0) end--;
        if (end > 0) nals.Add(nal[..end].ToArray());
    }

    // Each unit after its length, 4 bytes big-endian.
    public static byte[] ToAvcc(IEnumerable<byte[]> nals)
    {
        var list = nals.ToList();
        var output = new byte[list.Sum(n => 4 + n.Length)];
        int at = 0;
        foreach (var nal in list)
        {
            output[at] = (byte)(nal.Length >> 24);
            output[at + 1] = (byte)(nal.Length >> 16);
            output[at + 2] = (byte)(nal.Length >> 8);
            output[at + 3] = (byte)nal.Length;
            nal.CopyTo(output, at + 4);
            at += 4 + nal.Length;
        }
        return output;
    }

    // An encoder's Annex B output for one picture, as the phone gets it: SPS, PPS and access unit
    // delimiters are taken out (the SPS and PPS are returned, for the setup message); the rest
    // stays, in order.
    public static Unit FromAnnexB(ReadOnlySpan<byte> data)
    {
        byte[]? sps = null, pps = null;
        bool key = false;
        var kept = new List<byte[]>();
        foreach (var nal in SplitAnnexB(data))
        {
            switch (NalType(nal))
            {
                case Sps: sps = nal; break;
                case Pps: pps = nal; break;
                case Aud: break;
                case Idr: key = true; kept.Add(nal); break;
                default: kept.Add(nal); break;
            }
        }
        return new Unit(ToAvcc(kept), key, sps, pps);
    }

    // The avcC record (ISO/IEC 14496-15 5.2.4.1): version 1, profile, compatibility and level
    // from the SPS, 4-byte lengths (lengthSizeMinusOne 3), one SPS and one PPS. The High profiles
    // add the chroma format and bit depths: 4:2:0, 8 bits, which is what NV12 input gives.
    public static byte[] AvcC(byte[] sps, byte[] pps)
    {
        if (sps.Length < 4 || NalType(sps) != Sps) throw new ArgumentException("Not an SPS.", nameof(sps));
        if (pps.Length < 1 || NalType(pps) != Pps) throw new ArgumentException("Not a PPS.", nameof(pps));
        var box = new List<byte>
        {
            1, sps[1], sps[2], sps[3],
            0xFF,  // 6 reserved bits, then lengthSizeMinusOne = 3
            0xE1,  // 3 reserved bits, then one SPS
            (byte)(sps.Length >> 8), (byte)sps.Length,
        };
        box.AddRange(sps);
        box.Add(1);  // one PPS
        box.Add((byte)(pps.Length >> 8));
        box.Add((byte)pps.Length);
        box.AddRange(pps);
        if (sps[1] is 100 or 110 or 122 or 144)
        {
            box.Add(0xFC | 1);  // chroma_format_idc 1 (4:2:0)
            box.Add(0xF8);      // bit_depth_luma_minus8 0
            box.Add(0xF8);      // bit_depth_chroma_minus8 0
            box.Add(0);         // no SPS extensions
        }
        return box.ToArray();
    }

    // The WebCodecs codec string: "avc1." and the profile, compatibility and level bytes of the
    // SPS in hex, as VideoEncoder.swift builds it from the avcC record (for example avc1.4d0028).
    public static string Codec(byte[] sps)
    {
        if (sps.Length < 4) throw new ArgumentException("Not an SPS.", nameof(sps));
        return $"avc1.{sps[1]:x2}{sps[2]:x2}{sps[3]:x2}";
    }
}

// The decoder setup the phone gets before the first frame, and again whenever it changes
// (the `video` setup message, 7.2). Two setups are the same when every field is.
public sealed class VideoConfig : IEquatable<VideoConfig>
{
    public VideoConfig(string codec, byte[] description, int width, int height) =>
        (Codec, Description, Width, Height) = (codec, description, width, height);

    public string Codec { get; }
    public byte[] Description { get; }  // the avcC record
    public int Width { get; }
    public int Height { get; }

    public static VideoConfig From(byte[] sps, byte[] pps, int width, int height) =>
        new(Avcc.Codec(sps), Avcc.AvcC(sps, pps), width, height);

    public bool Equals(VideoConfig? other) =>
        other is not null && Codec == other.Codec && Width == other.Width && Height == other.Height
        && Description.AsSpan().SequenceEqual(other.Description);

    public override bool Equals(object? obj) => Equals(obj as VideoConfig);

    public override int GetHashCode() => HashCode.Combine(Codec, Width, Height, Description.Length);
}

// One encoded picture: AVCC data, keyframe or not, and its time in microseconds.
public sealed record VideoChunk(byte[] Data, bool Key, long Timestamp);
