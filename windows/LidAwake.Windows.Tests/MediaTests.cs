using System.Runtime.InteropServices;
using LidAwake;
using Xunit;
using Xunit.Abstractions;

// The real Media Foundation encoder and the real screen capture. A machine without them (a
// Windows Server runner may have no Media Foundation, no graphics card, no desktop) passes and
// says so in the test output, as "RUNNER: ...".
[Collection("Screen hardware")]
public class MediaTests
{
    readonly ITestOutputHelper output;

    public MediaTests(ITestOutputHelper output) => this.output = output;

    // A moving test picture, 320×180.
    sealed class Picture : IDisposable
    {
        public const int W = 320, H = 180;
        readonly IntPtr pixels = Marshal.AllocHGlobal(W * H * 4);
        readonly byte[] paint = new byte[W * H * 4];

        public ScreenImage Frame(int n)
        {
            for (int y = 0; y < H; y++)
                for (int x = 0; x < W; x++)
                {
                    int i = (y * W + x) * 4;
                    bool box = x >= (n * 7) % (W - 40) && x < (n * 7) % (W - 40) + 40 && y >= 60 && y < 120;
                    paint[i] = (byte)(box ? 30 : x * 255 / W);
                    paint[i + 1] = (byte)(box ? 200 : y * 255 / H);
                    paint[i + 2] = (byte)(box ? 90 : 128);
                    paint[i + 3] = 255;
                }
            Marshal.Copy(paint, 0, pixels, paint.Length);
            return new ScreenImage(pixels, W, H, W * 4);
        }

        public void Dispose() => Marshal.FreeHGlobal(pixels);
    }

    static bool HasOnlyPictureUnits(byte[] avcc)
    {
        int at = 0;
        while (at < avcc.Length)
        {
            if (at + 4 > avcc.Length) return false;
            int length = (avcc[at] << 24) | (avcc[at + 1] << 16) | (avcc[at + 2] << 8) | avcc[at + 3];
            if (length <= 0 || at + 4 + length > avcc.Length) return false;
            int type = avcc[at + 4] & 0x1F;
            if (type is Avcc.Sps or Avcc.Pps or Avcc.Aud) return false;
            at += 4 + length;
        }
        return at == avcc.Length;
    }

    MfH264Encoder? TryCreate(bool hardware)
    {
        try { return MfH264Encoder.Create(Picture.W, Picture.H, 30, 1_000_000, allowHardware: hardware); }
        catch (Exception e)
        {
            output.WriteLine($"RUNNER: no {(hardware ? "H.264" : "software H.264")} encoder: {e.Message}");
            return null;
        }
    }

    // Encodes until `count` pictures came out (an encoder may hold the first few back).
    static List<(VideoConfig Config, VideoChunk Chunk)> EncodeSome(IVideoEncoder encoder, Picture picture, ref int n, int count, bool forceFirst)
    {
        var got = new List<(VideoConfig Config, VideoChunk Chunk)>();
        for (int tries = 0; tries < count + 30 && got.Count < count; tries++, n++)
            got.AddRange(encoder.Encode(picture.Frame(n), n * 33_333L, forceFirst && tries == 0));
        return got;
    }

    [Fact]
    public void TheSoftwareEncoderStartsWithAKeyframeAndMakesOneOnRequest()
    {
        using var encoder = TryCreate(hardware: false);
        if (encoder == null) return;
        output.WriteLine($"RUNNER: software encoder {encoder.Name} (hardware {encoder.Hardware}, asynchronous {encoder.Asynchronous})");
        Assert.False(encoder.Hardware);
        using var picture = new Picture();
        int n = 0;
        var first = EncodeSome(encoder, picture, ref n, 6, forceFirst: true);
        Assert.True(first.Count >= 6, $"only {first.Count} pictures came out");
        // The first picture is a keyframe, and the setup has the SPS and PPS.
        Assert.True(first[0].Chunk.Key);
        var config = first[0].Config;
        Assert.Matches("^avc1\\.[0-9a-f]{6}$", config.Codec);
        Assert.Equal(1, config.Description[0]);
        Assert.Equal(0xFF, config.Description[4]);
        Assert.Equal(0xE1, config.Description[5]);
        Assert.Equal(Picture.W, config.Width);
        Assert.Equal(Picture.H, config.Height);
        Assert.All(first, f => Assert.True(HasOnlyPictureUnits(f.Chunk.Data)));
        // Then ordinary frames (keyframes only on request).
        int keys = first.Skip(1).Count(f => f.Chunk.Key);
        output.WriteLine($"RUNNER: {config.Codec}, {first.Count} pictures, {keys} more keyframes unasked, sizes {string.Join(",", first.Select(f => f.Chunk.Data.Length))}");
        Assert.Contains(first.Skip(1), f => !f.Chunk.Key);
        // A keyframe on request.
        var forced = EncodeSome(encoder, picture, ref n, 3, forceFirst: true);
        Assert.Contains(forced, f => f.Chunk.Key);
        // Timestamps rise.
        Assert.True(forced[^1].Chunk.Timestamp > first[0].Chunk.Timestamp);
    }

    [Fact]
    public void TheBestEncoderThisMachineHas()
    {
        using var encoder = TryCreate(hardware: true);
        if (encoder == null) return;
        output.WriteLine($"RUNNER: best encoder {encoder.Name} (hardware {encoder.Hardware}, asynchronous {encoder.Asynchronous})");
        using var picture = new Picture();
        int n = 0;
        List<(VideoConfig Config, VideoChunk Chunk)> got;
        try { got = EncodeSome(encoder, picture, ref n, 4, forceFirst: true); }
        catch (Exception e) when (encoder.Hardware)
        {
            // The feed falls back to software when this happens; say so, it is what the owner gets.
            output.WriteLine($"RUNNER: the hardware encoder failed, software is used instead: {e.Message}");
            Assert.True(MfH264Encoder.HardwareFailed);
            return;
        }
        Assert.NotEmpty(got);
        Assert.True(got[0].Chunk.Key);
    }

    [Fact]
    public void RealScreenCapture()
    {
        foreach (var level in new[] { 0, 1 })
        {
            var grabbers = new ScreenGrabbers { Note = text => output.WriteLine("RUNNER: " + text) };
            using var grabber = grabbers.Open(level);
            if (grabber == null)
            {
                output.WriteLine($"RUNNER: no screen capture at level {level}");
                continue;
            }
            ScreenImage? got;
            try { got = grabber.Grab(1280, 500, force: true); }
            catch (Exception e)
            {
                output.WriteLine($"RUNNER: {grabber.Kind} failed: {e.Message}");
                continue;
            }
            if (got is not { } image)
            {
                output.WriteLine($"RUNNER: {grabber.Kind} gave no picture");
                continue;
            }
            Assert.True(image.Width > 0 && image.Width <= 1280 && image.Width % 2 == 0);
            Assert.True(image.Height > 0 && image.Height % 2 == 0);
            var jpeg = JpegFrames.Encode(image, 50);
            Assert.Equal(0xFF, jpeg[0]);
            Assert.Equal(0xD8, jpeg[1]);
            output.WriteLine($"RUNNER: {grabber.Kind} captured {image.Width}x{image.Height}, JPEG {jpeg.Length} bytes");
        }
    }
}
