using LidAwake;
using Xunit;

// Annex B (what Media Foundation writes) → AVCC (what the page decodes), the avcC record and the
// codec string, with fixed SPS/PPS bytes (docs/API.md 7.2, VideoEncoder.swift).
public class AvccTests
{
    // Main profile (0x4D), no constraint flags, level 4.0 (0x28).
    static readonly byte[] Sps = { 0x67, 0x4D, 0x00, 0x28, 0x95, 0xA0, 0x1E, 0x00, 0x89, 0xF9, 0x70 };
    static readonly byte[] Pps = { 0x68, 0xEE, 0x3C, 0x80 };
    static readonly byte[] Idr = { 0x65, 0x88, 0x84, 0x00, 0x33, 0xFF };
    static readonly byte[] Slice = { 0x41, 0x9A, 0x02, 0x04 };
    static readonly byte[] Aud = { 0x09, 0xF0 };
    static readonly byte[] Sei = { 0x06, 0x05, 0x01, 0x80 };

    static byte[] Join(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static readonly byte[] Four = { 0, 0, 0, 1 };
    static readonly byte[] Three = { 0, 0, 1 };

    [Fact]
    public void SplitsOnThreeAndFourByteStartCodes()
    {
        var data = Join(Four, Aud, Four, Sps, Three, Pps, Three, Idr);
        var nals = Avcc.SplitAnnexB(data);
        Assert.Equal(4, nals.Count);
        Assert.Equal(Aud, nals[0]);
        Assert.Equal(Sps, nals[1]);
        Assert.Equal(Pps, nals[2]);
        Assert.Equal(Idr, nals[3]);
    }

    [Fact]
    public void DataWithoutAStartCodeHasNoUnits()
    {
        Assert.Empty(Avcc.SplitAnnexB(new byte[] { 0x65, 1, 2, 3 }));
        Assert.Empty(Avcc.SplitAnnexB(Array.Empty<byte>()));
    }

    [Fact]
    public void AKeyframeLosesAudSpsAndPpsAndGetsFourByteLengths()
    {
        var unit = Avcc.FromAnnexB(Join(Four, Aud, Four, Sps, Four, Pps, Four, Sei, Four, Idr));
        Assert.True(unit.Key);
        Assert.Equal(Sps, unit.Sps);
        Assert.Equal(Pps, unit.Pps);
        // SEI and the IDR slice stay, in order, each after its length.
        var expected = Join(new byte[] { 0, 0, 0, 4 }, Sei, new byte[] { 0, 0, 0, 6 }, Idr);
        Assert.Equal(expected, unit.Data);
    }

    [Fact]
    public void AnOrdinaryFrameIsNotAKeyframeAndCarriesNoSetup()
    {
        var unit = Avcc.FromAnnexB(Join(Four, Aud, Four, Slice));
        Assert.False(unit.Key);
        Assert.Null(unit.Sps);
        Assert.Null(unit.Pps);
        Assert.Equal(Join(new byte[] { 0, 0, 0, 4 }, Slice), unit.Data);
    }

    [Fact]
    public void AvcCRecordForMainProfile()
    {
        var expected = Join(
            new byte[] { 1, 0x4D, 0x00, 0x28, 0xFF, 0xE1, 0x00, (byte)Sps.Length }, Sps,
            new byte[] { 1, 0x00, (byte)Pps.Length }, Pps);
        Assert.Equal(expected, Avcc.AvcC(Sps, Pps));
    }

    [Fact]
    public void AvcCRecordForHighProfileAddsTheChromaFormatAndBitDepths()
    {
        var high = new byte[] { 0x67, 0x64, 0x00, 0x1F, 0xAC, 0xD9 };
        var box = Avcc.AvcC(high, Pps);
        Assert.Equal(new byte[] { 1, 0x64, 0x00, 0x1F, 0xFF, 0xE1 }, box[..6]);
        Assert.Equal(new byte[] { 0xFD, 0xF8, 0xF8, 0x00 }, box[^4..]);
        Assert.Equal(6 + 2 + high.Length + 1 + 2 + Pps.Length + 4, box.Length);
    }

    [Fact]
    public void CodecStringIsProfileCompatibilityAndLevelInHex()
    {
        Assert.Equal("avc1.4d0028", Avcc.Codec(Sps));
        Assert.Equal("avc1.64001f", Avcc.Codec(new byte[] { 0x67, 0x64, 0x00, 0x1F }));
        Assert.Equal("avc1.42e01e", Avcc.Codec(new byte[] { 0x67, 0x42, 0xE0, 0x1E }));
    }

    [Fact]
    public void SpsAndPpsAreChecked()
    {
        Assert.Throws<ArgumentException>(() => Avcc.AvcC(Pps, Pps));
        Assert.Throws<ArgumentException>(() => Avcc.AvcC(Sps, Sps));
    }

    [Fact]
    public void SetupsAreEqualWhenEveryFieldIs()
    {
        var a = VideoConfig.From(Sps, Pps, 1280, 720);
        var b = VideoConfig.From(Sps.ToArray(), Pps.ToArray(), 1280, 720);
        Assert.Equal(a, b);
        Assert.NotEqual(a, VideoConfig.From(Sps, Pps, 1024, 576));
        Assert.Equal("avc1.4d0028", a.Codec);
        Assert.Equal(Avcc.AvcC(Sps, Pps), a.Description);
    }

    // The encoder's input: BGRA → NV12, video range.
    [Fact]
    public unsafe void Nv12OfGreyIsMidGrey()
    {
        const int w = 4, h = 2;
        var bgra = Enumerable.Repeat((byte)128, w * h * 4).ToArray();
        var nv12 = new byte[Nv12.Size(w, h)];
        fixed (byte* src = bgra)
        fixed (byte* dst = nv12)
            Nv12.FromBgra(new ScreenImage((IntPtr)src, w, h, w * 4), dst);
        // Y of 128 in video range is 16 + 219 × 128 / 255 ≈ 126; grey has no colour (128).
        Assert.All(nv12[..(w * h)], y => Assert.InRange(y, 125, 127));
        Assert.All(nv12[(w * h)..], c => Assert.InRange(c, 127, 129));
    }
}
