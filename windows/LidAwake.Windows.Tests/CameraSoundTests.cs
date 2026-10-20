using System.Buffers.Binary;
using LidAwake;
using Xunit;

public class MulawTests
{
    // Entries of the ITU-T G.711 μ-law decoding table (16-bit linear values), at the segment edges.
    [Theory]
    [InlineData(0x00, -32124)]
    [InlineData(0x01, -31100)]
    [InlineData(0x0F, -16764)]
    [InlineData(0x10, -15996)]
    [InlineData(0x6F, -132)]
    [InlineData(0x70, -120)]
    [InlineData(0x7E, -8)]
    [InlineData(0x7F, 0)]
    [InlineData(0x80, 32124)]
    [InlineData(0x8F, 16764)]
    [InlineData(0x90, 15996)]
    [InlineData(0xF0, 120)]
    [InlineData(0xFE, 8)]
    [InlineData(0xFF, 0)]
    public void DecodeMatchesTheG711Table(int b, int linear)
    {
        Assert.Equal(linear, Mulaw.Decode((byte)b));
        Assert.Equal(linear / 32768f, Mulaw.Samples[b]);
    }

    [Theory]
    [InlineData(0, 0xFF)]
    [InlineData(-1, 0x7F)]
    [InlineData(8, 0xFE)]
    [InlineData(-8, 0x7E)]
    [InlineData(32767, 0x80)]
    [InlineData(-32768, 0x00)]
    [InlineData(32124, 0x80)]
    [InlineData(-32124, 0x00)]
    public void EncodeKnownValues(int linear, int b) => Assert.Equal((byte)b, Mulaw.Encode((short)linear));

    [Fact]
    public void EncodeUndoesDecodeForEveryByteButNegativeZero()
    {
        for (int b = 0; b < 256; b++)
        {
            var back = Mulaw.Encode(Mulaw.Decode((byte)b));
            Assert.Equal(b == 0x7F ? 0xFF : b, back);
        }
    }

    [Fact]
    public void EncodeNeverMovesAwayFromTheNearestStep()
    {
        // Every 16-bit value lands on a step no further than the segment's step size.
        for (int x = -32768; x <= 32767; x += 7)
        {
            int decoded = Mulaw.Decode(Mulaw.Encode((short)x));
            int clamped = Math.Clamp(x, -32124, 32124);
            int step = Math.Abs(clamped) < 256 ? 16 : Math.Abs(clamped) / 8 + 132;
            Assert.True(Math.Abs(decoded - clamped) <= step, $"{x} -> {decoded}");
        }
    }

    [Fact]
    public void FloatsAreClampedAndCutTowardZeroLikeTheMac()
    {
        Assert.Equal(32767, Mulaw.ToInt16(1f));
        Assert.Equal(32767, Mulaw.ToInt16(5f));
        Assert.Equal(-32767, Mulaw.ToInt16(-1f));
        Assert.Equal(0, Mulaw.ToInt16(float.NaN));
        Assert.Equal(0, Mulaw.ToInt16(float.PositiveInfinity));
        Assert.Equal(16383, Mulaw.ToInt16(0.5f));  // 16383.5 cut toward zero
        Assert.Equal(Mulaw.Encode((short)16383), Mulaw.Encode(0.5f));
    }

    [Fact]
    public void DecodeAllUsesTheTable()
    {
        Assert.Equal(new[] { 0f, 32124 / 32768f, -32124 / 32768f }, Mulaw.DecodeAll(new byte[] { 0xFF, 0x80, 0x00 }));
    }
}

public class SoundChunkerTests
{
    static float[] Constant(int n, float v) => Enumerable.Repeat(v, n).ToArray();

    [Fact]
    public void FortyEightKilohertzIsSentAt16kInTenthOfASecondChunks()
    {
        var c = new SoundChunker();
        for (int i = 0; i < 9; i++) Assert.Empty(c.Add(Constant(480, 0.5f), 48000));  // 9 × 10 ms
        var ready = c.Add(Constant(480, 0.5f), 48000);                              // 100 ms
        var (rate, bytes) = Assert.Single(ready);
        Assert.Equal(16000, rate);
        Assert.Equal(1600, bytes.Length);  // rate / 10
        Assert.All(bytes, b => Assert.Equal(Mulaw.Encode(0.5f), b));
        Assert.Empty(c.Add(Constant(480, 0.5f), 48000));
    }

    [Fact]
    public void FortyFourPointOneKilohertzKeepsTheRateOverASecond()
    {
        var c = new SoundChunker();
        var chunks = new List<byte[]>();
        for (int i = 0; i < 100; i++) chunks.AddRange(c.Add(Constant(441, 0.25f), 44100).Select(x => x.Samples));
        Assert.InRange(chunks.Count, 9, 10);
        // Each chunk is at least rate / 10, and less than one more call's worth (441 / 2.75625 = 160).
        Assert.All(chunks, ch => Assert.InRange(ch.Length, 1600, 1760));
        Assert.InRange(chunks.Sum(ch => ch.Length), 16000 - 1760, 16001);
    }

    [Fact]
    public void SixteenKilohertzAndLessIsSentAsItIs()
    {
        var c = new SoundChunker();
        var samples = Enumerable.Range(0, 1600).Select(i => (float)Math.Sin(i / 10.0) * 0.8f).ToArray();
        var (rate, bytes) = Assert.Single(c.Add(samples, 16000));
        Assert.Equal(16000, rate);
        Assert.Equal(samples.Select(x => Mulaw.Encode(x)).ToArray(), bytes);

        var low = new SoundChunker();
        Assert.Empty(low.Add(Constant(799, 0.1f), 8000));
        var (lowRate, lowBytes) = Assert.Single(low.Add(Constant(1, 0.1f), 8000));
        Assert.Equal(8000, lowRate);
        Assert.Equal(800, lowBytes.Length);
    }

    [Fact]
    public void ANewRateStartsOver()
    {
        var c = new SoundChunker();
        Assert.Empty(c.Add(Constant(4000, 0f), 48000));  // 1333 samples waiting at 16 kHz
        Assert.Empty(c.Add(Constant(1000, 0f), 16000));  // dropped, starts again
        Assert.Single(c.Add(Constant(600, 0f), 16000));
    }

    [Fact]
    public void AToneAboveWhatSixteenKilohertzCarriesIsDamped()
    {
        // 24 kHz (the highest a 48 kHz signal holds): +1, -1, +1, ... Each output averages three.
        var c = new SoundChunker();
        var tone = Enumerable.Range(0, 4800).Select(i => i % 2 == 0 ? 1f : -1f).ToArray();
        var (_, bytes) = Assert.Single(c.Add(tone, 48000));
        Assert.All(bytes, b => Assert.InRange(Math.Abs(Mulaw.Samples[b]), 0f, 0.35f));
    }

    [Fact]
    public void FirstChannelOfEachFormat()
    {
        var int16 = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(int16.AsSpan(0), 16384);
        BinaryPrimitives.WriteInt16LittleEndian(int16.AsSpan(2), -1);
        BinaryPrimitives.WriteInt16LittleEndian(int16.AsSpan(4), -16384);
        BinaryPrimitives.WriteInt16LittleEndian(int16.AsSpan(6), 7);
        Assert.Equal(new[] { 0.5f, -0.5f }, SoundChunker.FirstChannel(int16, SampleKind.Int16, 2));

        var f32 = new byte[3 * 4 * 2 + 5];  // three stereo frames and a partial one
        BinaryPrimitives.WriteSingleLittleEndian(f32.AsSpan(0), 0.25f);
        BinaryPrimitives.WriteSingleLittleEndian(f32.AsSpan(8), -0.75f);
        BinaryPrimitives.WriteSingleLittleEndian(f32.AsSpan(16), 1f);
        Assert.Equal(new[] { 0.25f, -0.75f, 1f }, SoundChunker.FirstChannel(f32, SampleKind.Float32, 2));

        var i24 = new byte[] { 0x00, 0x00, 0x40, 0xFF, 0xFF, 0xFF, 0x00, 0x00, 0xC0 };  // mono: 0.5, tiny, -0.5
        var mono24 = SoundChunker.FirstChannel(i24, SampleKind.Int24, 1);
        Assert.Equal(0.5f, mono24[0]);
        Assert.Equal(-1 / 8388608f, mono24[1]);
        Assert.Equal(-0.5f, mono24[2]);

        var i32 = new byte[8];
        BinaryPrimitives.WriteInt32LittleEndian(i32.AsSpan(0), int.MinValue);
        BinaryPrimitives.WriteInt32LittleEndian(i32.AsSpan(4), 1 << 30);
        Assert.Equal(new[] { -1f, 0.5f }, SoundChunker.FirstChannel(i32, SampleKind.Int32, 1));
    }

    [Fact]
    public void FormatKinds()
    {
        Assert.Equal(SampleKind.Float32, SoundChunker.KindOf(true, 32));
        Assert.Equal(SampleKind.Int16, SoundChunker.KindOf(false, 16));
        Assert.Equal(SampleKind.Int24, SoundChunker.KindOf(false, 24));
        Assert.Equal(SampleKind.Int32, SoundChunker.KindOf(false, 32));
        Assert.Null(SoundChunker.KindOf(true, 64));
        Assert.Null(SoundChunker.KindOf(false, 8));
    }
}

public class TalkPlayerTests
{
    sealed class FakeOutput : ITalkOutput
    {
        public bool Disposed;
        public void Dispose() => Disposed = true;
    }

    DateTimeOffset now = new(2026, 10, 20, 12, 0, 0, TimeSpan.Zero);
    readonly List<FakeOutput> outputs = new();
    int opens;
    bool noSpeakers;

    TalkPlayer Make() => new(_ =>
    {
        opens++;
        if (noSpeakers) return null;
        var o = new FakeOutput();
        outputs.Add(o);
        return o;
    }, () => now);

    static byte[] Voice(int n, byte b = 0x80) => Enumerable.Repeat(b, n).ToArray();

    [Fact]
    public void PlaysMessagesBackToBackThenSilence()
    {
        var p = Make();
        Assert.True(p.Play(new byte[] { 0x80, 0x00 }));
        Assert.True(p.Play(new byte[] { 0xFE }));
        Assert.Equal(1, opens);
        var out5 = new float[5];
        p.Read(out5);
        Assert.Equal(new[] { 32124 / 32768f, -32124 / 32768f, 8 / 32768f, 0f, 0f }, out5);
        Assert.Equal(0, p.SecondsAhead);
    }

    [Fact]
    public void DropsWhenASecondBehind()
    {
        var p = Make();
        for (int i = 0; i < 4; i++) Assert.True(p.Play(Voice(4000)));  // 4 × 0.25 s
        Assert.Equal(1.0, p.SecondsAhead);
        Assert.False(p.Play(Voice(4000)));
        Assert.Equal(1.0, p.SecondsAhead);
        p.Read(new float[8000]);
        Assert.Equal(0.5, p.SecondsAhead);
        Assert.True(p.Play(Voice(10)));
    }

    [Fact]
    public void EmptyMessagesAreIgnored()
    {
        var p = Make();
        Assert.False(p.Play(ReadOnlySpan<byte>.Empty));
        Assert.Equal(0, opens);
        Assert.False(p.Playing);
    }

    [Fact]
    public void LetsGoOfTheSpeakersFourSecondsAfterTheLastWord()
    {
        var p = Make();
        p.Play(Voice(640));
        now += TimeSpan.FromSeconds(3.9);
        p.Idle();
        Assert.True(p.Playing);
        Assert.False(outputs[0].Disposed);
        now += TimeSpan.FromSeconds(0.1);
        p.Idle();
        Assert.False(p.Playing);
        Assert.True(outputs[0].Disposed);
        Assert.Equal(0, p.SecondsAhead);
        p.Play(Voice(640));
        Assert.Equal(2, opens);
    }

    [Fact]
    public void StopLetsGoAtOnce()
    {
        var p = Make();
        p.Play(Voice(640));
        p.Stop();
        Assert.True(outputs[0].Disposed);
        Assert.False(p.Playing);
        Assert.Equal(0, p.SecondsAhead);
    }

    [Fact]
    public void WithoutSpeakersItTriesAgainOnlyAfterTenSeconds()
    {
        noSpeakers = true;
        var p = Make();
        Assert.False(p.Play(Voice(640)));
        Assert.False(p.Play(Voice(640)));
        Assert.Equal(1, opens);
        Assert.Equal(0, p.SecondsAhead);
        now += TimeSpan.FromSeconds(10);
        noSpeakers = false;
        Assert.True(p.Play(Voice(640)));
        Assert.Equal(2, opens);
    }

    [Fact]
    public void AStopWhileTheSpeakersAreStartingWins()
    {
        TalkPlayer? p = null;
        var late = new FakeOutput();
        // The camera closes (Stop) while the speakers are still being opened.
        p = new TalkPlayer(_ => { p!.Stop(); return late; }, () => now);
        Assert.False(p.Play(Voice(640)));
        Assert.True(late.Disposed);
        Assert.False(p.Playing);
        Assert.Equal(0, p.SecondsAhead);
    }

    [Fact]
    public void AnOutputThatThrowsCountsAsNoSpeakers()
    {
        var p = new TalkPlayer(_ => throw new InvalidOperationException("no device"), () => now);
        Assert.False(p.Play(Voice(10)));
        Assert.False(p.Playing);
    }

    [Fact]
    public void TheResamplerHoldsALevelAtTheSpeakersRate()
    {
        var r = new TalkResampler(48000);
        var out48 = new float[3000];
        int pulled = 0;
        r.Fill(out48, b => { pulled += b.Length; Array.Fill(b, 0.5f); return b.Length; });
        Assert.All(out48.Skip(4), v => Assert.Equal(0.5f, v, 3));
        // 3000 outputs at 48 kHz need 1000 inputs at 16 kHz: pulled in blocks of 256.
        Assert.InRange(pulled, 1000, 1024);
    }
}
