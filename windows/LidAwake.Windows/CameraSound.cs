using System.Buffers.Binary;

namespace LidAwake;

// G.711 μ-law, one byte a sample, as the Mac (CameraStream.mulaw) and the page (MULAW, mulawByte).
public static class Mulaw
{
    // 16-bit sample → one byte (the Mac's code, line for line).
    public static byte Encode(short value)
    {
        int sample = value;
        int sign = sample < 0 ? 0x80 : 0;
        if (sign != 0) sample = -sample;
        sample = Math.Min(sample, 32635) + 0x84;
        int exponent = 7, mask = 0x4000;
        while (exponent > 0 && (sample & mask) == 0) { exponent--; mask >>= 1; }
        int mantissa = (sample >> (exponent + 3)) & 0x0F;
        return (byte)(~(sign | (exponent << 4) | mantissa) & 0xFF);
    }

    // -1..1 → one byte, as the Mac turns a float sample into 16 bits: clamped, times 32767, cut toward 0.
    public static byte Encode(float value) => Encode(ToInt16(value));

    public static short ToInt16(float value)
    {
        if (!float.IsFinite(value)) return 0;
        return (short)(Math.Clamp(value, -1f, 1f) * 32767f);
    }

    // One byte → the 16-bit sample (the page's formula: u = ~byte; t = ((u & 15) << 3 + 0x84) << exponent).
    public static short Decode(byte b)
    {
        int u = ~b & 0xFF;
        int t = (((u & 0x0F) << 3) + 0x84) << ((u >> 4) & 7);
        return (short)((u & 0x80) != 0 ? 0x84 - t : t - 0x84);
    }

    // Every byte's sample as -1..1 (the page divides by 32768).
    public static readonly float[] Samples = Enumerable.Range(0, 256).Select(i => Decode((byte)i) / 32768f).ToArray();

    public static float[] DecodeAll(ReadOnlySpan<byte> bytes)
    {
        var samples = new float[bytes.Length];
        for (int i = 0; i < bytes.Length; i++) samples[i] = Samples[bytes[i]];
        return samples;
    }
}

// How a microphone delivers its samples.
public enum SampleKind { Float32, Int16, Int24, Int32 }

// The microphone's sound for the phone, as the Mac's CameraStream.addSound: the first channel
// only, as μ-law bytes, about 0.1 s (`rate / 10` samples) per message. The Mac asks the system for
// 16 kHz; Windows gives the microphone's own rate (usually 44.1 or 48 kHz), so it is brought down
// to 16 kHz here: every output sample is the average of the input samples it covers (a moving-
// average low-pass, then decimation, the same as the page does with the phone's microphone).
// A microphone at 16 kHz or less is sent at its own rate, as the Mac does (the rate is in each
// message). Called on the microphone's thread; not thread-safe on its own (CameraService locks).
public sealed class SoundChunker
{
    public const int TargetRate = 16000;

    int inRate;
    int outRate = TargetRate;
    double step = 1;        // input samples per output sample
    double windowEnd;       // where the current output sample's input ends, counted from `seen`
    double sum;
    int count;
    readonly List<byte> pending = new();

    public int Rate => outRate;

    public void Reset()
    {
        inRate = 0;
        outRate = TargetRate;
        step = 1;
        windowEnd = 0;
        sum = 0;
        count = 0;
        pending.Clear();
    }

    // Adds the first channel's samples (-1..1) at `rate` Hz. Answers the messages that are ready:
    // each is every byte collected so far once there are at least `rate / 10` (as the Mac sends).
    public List<(int Rate, byte[] Samples)> Add(ReadOnlySpan<float> mono, int rate)
    {
        var ready = new List<(int, byte[])>();
        if (rate <= 0) return ready;
        if (rate != inRate)
        {
            // A new rate starts over, as on the Mac.
            Reset();
            inRate = rate;
            outRate = Math.Min(rate, TargetRate);
            step = (double)rate / outRate;
            windowEnd = step;
        }
        if (step == 1)
        {
            foreach (var s in mono) pending.Add(Mulaw.Encode(s));
        }
        else
        {
            for (int i = 0; i < mono.Length; i++)
            {
                sum += float.IsFinite(mono[i]) ? mono[i] : 0f;
                count++;
                // This input sample ends the current window: one output sample.
                if (i + 1 >= windowEnd)
                {
                    pending.Add(Mulaw.Encode((float)(sum / count)));
                    sum = 0;
                    count = 0;
                    windowEnd += step;
                }
            }
            windowEnd -= mono.Length;  // counted from the next call's first sample
        }
        if (pending.Count >= outRate / 10)
        {
            ready.Add((outRate, pending.ToArray()));
            pending.Clear();
        }
        return ready;
    }

    // The first channel of interleaved samples, as -1..1. A partial frame at the end is left out.
    public static float[] FirstChannel(ReadOnlySpan<byte> data, SampleKind kind, int channels)
    {
        channels = Math.Max(1, channels);
        int bytes = kind switch { SampleKind.Int16 => 2, SampleKind.Int24 => 3, _ => 4 };
        int frame = bytes * channels;
        int frames = data.Length / frame;
        var mono = new float[frames];
        for (int i = 0; i < frames; i++)
        {
            var s = data.Slice(i * frame, bytes);
            mono[i] = kind switch
            {
                SampleKind.Float32 => BinaryPrimitives.ReadSingleLittleEndian(s),
                SampleKind.Int16 => BinaryPrimitives.ReadInt16LittleEndian(s) / 32768f,
                SampleKind.Int24 => ((s[2] << 24 | s[1] << 16 | s[0] << 8) >> 8) / 8388608f,
                _ => BinaryPrimitives.ReadInt32LittleEndian(s) / 2147483648f,
            };
        }
        return mono;
    }

    // The kind of a wave format: `floatFormat` is true for IEEE float (plain, or the extensible
    // format's float sub-format). null: a format Lid Awake can't read (then there's no sound).
    public static SampleKind? KindOf(bool floatFormat, int bitsPerSample) => (floatFormat, bitsPerSample) switch
    {
        (true, 32) => SampleKind.Float32,
        (false, 16) => SampleKind.Int16,
        (false, 24) => SampleKind.Int24,
        (false, 32) => SampleKind.Int32,
        _ => null,
    };
}

// Plays the phone's voice (7.5 talk back) on the PC's speakers.
public interface ITalkOutput : IDisposable { }

// Talk back: 16 kHz mono μ-law from the phone, played back to back on the default speakers, as
// the Mac's TalkPlayer. Never more than about a second behind: a message that arrives while a
// second or more is still waiting is dropped. The speakers are let go 4 seconds after the last
// word (Idle, from CameraService's 2-second check), and at once when the camera closes (Stop).
// The output pulls the samples with Read on its own thread.
public sealed class TalkPlayer : IDisposable
{
    public const int Rate = 16000;
    public static readonly TimeSpan MaxBehind = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan ReleaseAfter = TimeSpan.FromSeconds(4);
    // No speakers (or they couldn't be opened): don't try again for every message.
    public static readonly TimeSpan RetryAfter = TimeSpan.FromSeconds(10);

    readonly Func<TalkPlayer, ITalkOutput?> open;
    readonly Func<DateTimeOffset> now;
    readonly object gate = new();
    readonly Queue<float[]> chunks = new();
    int offset;        // samples of the first chunk already played
    int queued;        // samples waiting
    ITalkOutput? output;
    bool opening;
    DateTimeOffset lastVoice;
    DateTimeOffset? failedAt;

    // `open` starts playing on the speakers, pulling from this player's Read (null or a throw: no
    // speakers).
    public TalkPlayer(Func<TalkPlayer, ITalkOutput?> open, Func<DateTimeOffset>? now = null)
    {
        this.open = open;
        this.now = now ?? (() => DateTimeOffset.UtcNow);
    }

    public double SecondsAhead { get { lock (gate) return queued / (double)Rate; } }
    public bool Playing { get { lock (gate) return output != null; } }

    // Queues one message. False when it was dropped (empty, too far behind, or no speakers).
    public bool Play(ReadOnlySpan<byte> mulaw)
    {
        if (mulaw.IsEmpty) return false;
        var samples = Mulaw.DecodeAll(mulaw);
        bool start;
        lock (gate)
        {
            if (queued >= Rate * MaxBehind.TotalSeconds) return false;
            if (output == null && !opening && failedAt is { } at && now() - at < RetryAfter) return false;
            chunks.Enqueue(samples);
            queued += samples.Length;
            lastVoice = now();
            start = output == null && !opening;
            if (start) opening = true;
        }
        if (!start) return true;
        ITalkOutput? started = null;
        try { started = open(this); }
        catch (Exception) { }
        lock (gate)
        {
            opening = false;
            if (started != null) { output = started; failedAt = null; return true; }
            failedAt = now();
            Clear();
            return false;
        }
    }

    // The next samples to play (-1..1, 16 kHz), silence once nothing is waiting. Always fills `dest`.
    public int Read(Span<float> dest)
    {
        lock (gate)
        {
            int filled = 0;
            while (filled < dest.Length && chunks.Count > 0)
            {
                var chunk = chunks.Peek();
                int n = Math.Min(dest.Length - filled, chunk.Length - offset);
                chunk.AsSpan(offset, n).CopyTo(dest[filled..]);
                filled += n;
                offset += n;
                queued -= n;
                if (offset == chunk.Length) { chunks.Dequeue(); offset = 0; }
            }
            dest[filled..].Clear();
            return dest.Length;
        }
    }

    // Lets go of the speakers once nobody has talked for 4 seconds.
    public void Idle()
    {
        ITalkOutput? done = null;
        lock (gate)
        {
            if (output != null && now() - lastVoice >= ReleaseAfter) { done = output; output = null; Clear(); }
        }
        Release(done);
    }

    // Stops now (the camera closed).
    public void Stop()
    {
        ITalkOutput? done;
        lock (gate) { done = output; output = null; Clear(); }
        Release(done);
    }

    public void Dispose() => Stop();

    void Clear()
    {
        chunks.Clear();
        offset = 0;
        queued = 0;
    }

    // Outside the lock: stopping the output waits for its thread, which may be in Read.
    static void Release(ITalkOutput? done)
    {
        try { done?.Dispose(); }
        catch (Exception) { }
    }
}

// 16 kHz mono → the speakers' own rate, by straight-line interpolation (enough for a voice).
public sealed class TalkResampler
{
    readonly double step;   // input samples per output sample
    double pos = 1;         // where between `a` and `b` the next output sample is
    float a, b;
    readonly float[] input = new float[256];
    int used = 256;

    public TalkResampler(int outputRate) => step = TalkPlayer.Rate / (double)Math.Max(1, outputRate);

    public void Fill(Span<float> dest, Func<float[], int> pull)
    {
        for (int i = 0; i < dest.Length; i++)
        {
            while (pos >= 1)
            {
                a = b;
                if (used >= input.Length) { pull(input); used = 0; }
                b = input[used++];
                pos -= 1;
            }
            dest[i] = a + (b - a) * (float)pos;
            pos += step;
        }
    }
}
