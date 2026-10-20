using System.Diagnostics;

namespace LidAwake;

// The picture quality presets the page asks for (docs/API.md 7.3), with the Mac's numbers
// (FrameStream.swift): JPEG width, quality and frames a second; video width, frames a second
// and highest bit rate.
public sealed record ScreenPreset(string Name, int MaxWidth, int Quality, int Fps, int VideoWidth, int VideoFps, int VideoMaxBitrate)
{
    public static readonly ScreenPreset Max = new("max", 4096, 70, 6, 4096, 15, 12_000_000);
    public static readonly ScreenPreset Sharp = new("sharp", 1600, 70, 12, 1600, 20, 6_000_000);
    public static readonly ScreenPreset Balanced = new("balanced", 1200, 50, 20, 1280, 30, 4_000_000);
    public static readonly ScreenPreset Fast = new("fast", 640, 35, 25, 1024, 30, 2_500_000);

    // An unknown name is null (the page's message is then ignored).
    public static ScreenPreset? Named(string? name) => name switch
    {
        "max" => Max,
        "sharp" => Sharp,
        "balanced" => Balanced,
        "fast" => Fast,
        _ => null,
    };
}

// What a video phone gets, in order: the decoder setup (before the first keyframe and whenever
// it changes) and the encoded pictures.
public abstract record VideoPacket
{
    public sealed record Setup(VideoConfig Config) : VideoPacket;
    public sealed record Picture(VideoChunk Chunk) : VideoPacket;
}

// A phone page that plays H.264 (FrameStream.VideoSink on the Mac). `Ready` says whether it has
// confirmed enough of what it got; the encoder skips screen pictures while any page is behind,
// so nothing queues up and no picture another one depends on is ever dropped.
public sealed class VideoSink
{
    public VideoSink(Action<VideoPacket> send, Func<bool> ready) => (Send, Ready) = (send, ready);

    public Action<VideoPacket> Send { get; }
    public Func<bool> Ready { get; }

    // ScreenFeed's own, under its lock: waiting for a keyframe, and the setup this page has.
    internal bool NeedsKey = true;
    internal VideoConfig? Config;
}

// Live screen pictures for the phone (FrameStream.swift). While someone watches, one background
// thread captures the screen (Desktop Duplication, or GDI) and makes what the watchers need:
// H.264 for video pages, JPEG for the others and for long polls (/screen.jpg). Capture stops a
// while (10 seconds) after the last watcher or poll. Nothing here throws to its callers, and a
// failure only falls back: video to JPEG, and JPEG to "no picture" with the page's message.
public sealed class ScreenFeed : IDisposable
{
    public enum Result { Frame, Unchanged, Unavailable }

    // A long poll's answer: a JPEG with its id, nothing newer, or no capture.
    public readonly record struct Poll(Result Kind, byte[]? Jpeg = null, int Id = 0);

    public static readonly TimeSpan DefaultIdleStop = TimeSpan.FromSeconds(10);
    public const int PollWaitMs = 2000;
    const int RetryOpenMs = 3000;
    const int StartBitrate = 1_000_000;

    readonly Func<int, IScreenGrabber?> openGrabber;
    readonly VideoEncoderFactory? openEncoder;
    readonly long idleStopMs;
    readonly Action<string>? note;
    readonly object gate = new();
    readonly object watchGate = new();
    readonly Stopwatch clock = Stopwatch.StartNew();

    // Under `gate`.
    readonly Dictionary<Guid, (Action<byte[]> Frame, Action Unavailable)> subscribers = new();
    readonly Dictionary<Guid, VideoSink> sinks = new();
    readonly List<(int After, TaskCompletionSource<Poll> Done)> waiters = new();
    ScreenPreset preset = ScreenPreset.Balanced;
    byte[]? latest;
    int latestId;
    long lastRequest = long.MinValue / 2;
    Thread? thread;
    bool stopping, disposed;
    bool unavailable;        // the last try to start capturing failed
    bool forceFrame;         // someone needs a picture now, even if the screen is still
    bool videoBroken;        // no encoder could be made: video pages get JPEG
    string? videoProblem;
    bool crispShown;         // fast preset: the clearer still picture is showing
    int bitrate = StartBitrate;
    bool bitrateChanged;
    long lastRateChange = long.MinValue / 2;
    string? grabberKind, encoderName;
    string? lastNote;
    bool reportedWatching;   // under watchGate

    // `openGrabber(level)` opens a screen capture, or null when there is none (level rises when
    // one keeps failing: ScreenGrabbers.Open). `openEncoder` makes an H.264 encoder or throws why
    // not (none: JPEG only). `note` gets one line whenever what is used changes (for the log).
    public ScreenFeed(Func<int, IScreenGrabber?> openGrabber, VideoEncoderFactory? openEncoder = null,
                      TimeSpan? idleStop = null, Action<string>? note = null)
    {
        this.openGrabber = openGrabber;
        this.openEncoder = openEncoder;
        idleStopMs = (long)(idleStop ?? DefaultIdleStop).TotalMilliseconds;
        this.note = note;
    }

    // True from when someone starts watching (a socket or a poll) until capture stops again.
    // For the display: keep it on while watched (the lid-screen task hooks this).
    public event Action<bool>? WatchingChanged;

    public bool IsCapturing { get { lock (gate) return thread != null; } }
    public string? GrabberKind { get { lock (gate) return grabberKind; } }
    public string? EncoderName { get { lock (gate) return encoderName; } }
    public string? VideoProblem { get { lock (gate) return videoProblem; } }
    public string PresetName { get { lock (gate) return preset.Name; } }
    public int Bitrate { get { lock (gate) return bitrate; } }

    long Now => clock.ElapsedMilliseconds;

    // ----- Watchers -----

    // Pushed pictures: `frame` gets every new JPEG (and the latest one right away), while this
    // subscriber has no working video. `unavailable` is called when capture can't start.
    public void Subscribe(Guid id, Action<byte[]> frame, Action unavailable)
    {
        byte[]? now;
        bool tell;
        lock (gate)
        {
            if (disposed) return;
            subscribers[id] = (frame, unavailable);
            lastRequest = Now;
            now = latest;
            tell = this.unavailable;
            if (now == null) forceFrame = true;
            EnsureRunning();
        }
        SyncWatching();
        if (now != null)
        {
            try { frame(now); }
            catch (Exception) { }
        }
        if (tell) Safe(unavailable);
    }

    public void Unsubscribe(Guid id)
    {
        lock (gate)
        {
            subscribers.Remove(id);
            RemoveSink(id);
            lastRequest = Now;
        }
    }

    // A subscriber switches to video (a sink) or back to JPEG (null).
    public void SetVideo(Guid id, VideoSink? sink)
    {
        lock (gate)
        {
            if (sink == null) { RemoveSink(id); return; }
            sinks[id] = sink;
            forceFrame = true;
        }
    }

    void RemoveSink(Guid id)
    {
        sinks.Remove(id);
        // With no video page left, the next one tries the encoder again.
        if (sinks.Count == 0) { videoBroken = false; videoProblem = null; }
    }

    // The page's decoder failed (or fell behind): the setup and a keyframe again.
    public void RestartVideo(Guid id)
    {
        lock (gate)
        {
            if (!sinks.TryGetValue(id, out var sink)) return;
            sink.NeedsKey = true;
            sink.Config = null;
            forceFrame = true;
        }
    }

    // One preset for every watcher; the latest wins. False for an unknown name.
    public bool SetPreset(string? name)
    {
        if (ScreenPreset.Named(name) is not { } p) return false;
        lock (gate)
        {
            if (p == preset) return true;
            preset = p;
            crispShown = false;
            forceFrame = true;
            bitrate = Math.Min(bitrate, p.VideoMaxBitrate);
            bitrateChanged = true;
        }
        return true;
    }

    // Send → confirm time beyond the link's usual, for a video picture. Rising delay means
    // pictures queue somewhere: lower the bit rate quickly, otherwise raise it slowly (the Mac's
    // videoFeedback).
    public void VideoFeedback(double extraDelay)
    {
        lock (gate)
        {
            long now = Now;
            if (extraDelay > 0.12)
            {
                if (now - lastRateChange <= 300) return;
                bitrate = Math.Max(250_000, (int)(bitrate * 0.8));
            }
            else
            {
                if (now - lastRateChange <= 1000) return;
                bitrate = Math.Min(preset.VideoMaxBitrate, (int)(bitrate * 1.1));
            }
            lastRateChange = now;
            bitrateChanged = true;
        }
    }

    // Long poll: a picture other than the phone's `after` (newer, or from before a restart of
    // Lid Awake), waiting up to 2 seconds for one.
    public Task<Poll> FrameAfter(int after)
    {
        Poll? ready = null;
        TaskCompletionSource<Poll>? done = null;
        lock (gate)
        {
            if (disposed) return Task.FromResult(new Poll(Result.Unavailable));
            lastRequest = Now;
            EnsureRunning();
            if (latest != null && latestId != after) ready = new Poll(Result.Frame, latest, latestId);
            else
            {
                done = new TaskCompletionSource<Poll>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.Add((after, done));
                if (latest == null) forceFrame = true;
            }
        }
        SyncWatching();
        if (ready is { } r) return Task.FromResult(r);
        _ = ExpireLater(done!);
        return done!.Task;
    }

    async Task ExpireLater(TaskCompletionSource<Poll> done)
    {
        await Task.Delay(PollWaitMs).ConfigureAwait(false);
        bool none;
        lock (gate)
        {
            waiters.RemoveAll(w => w.Done == done);
            none = unavailable;
        }
        done.TrySetResult(new Poll(none ? Result.Unavailable : Result.Unchanged));
    }

    void EnsureRunning()
    {
        if (thread != null || disposed) return;
        stopping = false;
        var t = new Thread(Run) { IsBackground = true, Name = "Lid Awake screen" };
        t.SetApartmentState(ApartmentState.MTA);
        thread = t;
        t.Start();
    }

    // Tells WatchingChanged whatever is true now; every change of `thread` is followed by a call,
    // so the last thing told is always the truth.
    void SyncWatching()
    {
        lock (watchGate)
        {
            bool now;
            lock (gate) now = thread != null;
            if (now == reportedWatching) return;
            reportedWatching = now;
            try { WatchingChanged?.Invoke(now); }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        Thread? t;
        List<TaskCompletionSource<Poll>> left;
        lock (gate)
        {
            disposed = true;
            stopping = true;
            t = thread;
            left = waiters.Select(w => w.Done).ToList();
            waiters.Clear();
        }
        foreach (var w in left) w.TrySetResult(new Poll(Result.Unavailable));
        try { t?.Join(3000); }
        catch (Exception) { }
    }

    // ----- The capture thread -----

    void Run()
    {
        try { Loop(); }
        catch (Exception e)
        {
            // Never take the app down: note it, and let the next watcher start afresh.
            Note("Screen capture stopped: " + e.Message);
            lock (gate) { thread = null; ResetSession(); }
        }
        SyncWatching();
    }

    // Under gate: what a stopped capture forgets.
    void ResetSession()
    {
        latest = null;
        unavailable = false;
        grabberKind = null;
        encoderName = null;
    }

    bool Idle() => subscribers.Count == 0 && waiters.Count == 0 && Now - lastRequest > idleStopMs;

    void Loop()
    {
        Gdi.UseRealPixels();
        SyncWatching();
        IScreenGrabber? grabber = null;
        IVideoEncoder? encoder = null;
        int level = 0, grabFailures = 0, encodeFailures = 0;
        long nextOpen = 0, sharpenDue = -1, lastSharpen = long.MinValue / 2;
        var last = new LastPicture();
        try
        {
            while (true)
            {
                ScreenPreset p;
                bool video, jpegWanted, force, needsKey, rateChanged;
                int rate;
                List<VideoSink> sinkList;
                bool stop;
                lock (gate)
                {
                    stop = stopping || Idle();
                    p = preset;
                    video = sinks.Count > 0 && !videoBroken;
                    jpegWanted = waiters.Count > 0 || subscribers.Keys.Any(id => !(video && sinks.ContainsKey(id)));
                    sinkList = video ? sinks.Values.ToList() : new List<VideoSink>();
                    needsKey = sinkList.Any(s => s.NeedsKey);
                    force = forceFrame || needsKey;
                    rate = bitrate;
                    rateChanged = bitrateChanged;
                    bitrateChanged = false;
                }
                if (stop)
                {
                    // Let go of the screen and the encoder first, so a capture started right after
                    // can have them.
                    Dispose(ref grabber);
                    Dispose(ref encoder);
                    lock (gate)
                    {
                        if (stopping || Idle())
                        {
                            thread = null;
                            ResetSession();
                            return;
                        }
                    }
                    continue;  // someone came back meanwhile
                }

                if (!video && encoder != null) { Dispose(ref encoder); lock (gate) encoderName = null; }
                if (rateChanged && encoder != null)
                {
                    try { encoder.SetBitrate(rate); }
                    catch (Exception) { }
                }

                if (grabber == null)
                {
                    if (Now < nextOpen) { Thread.Sleep(50); continue; }
                    grabber = OpenGrabber(level);
                    if (grabber == null)
                    {
                        nextOpen = Now + RetryOpenMs;
                        TellUnavailable();
                        continue;
                    }
                    lock (gate) { unavailable = false; grabberKind = grabber.Kind; }
                    Note("Screen capture: " + grabber.Kind);
                    last.Clear();
                    force = true;
                }

                int fps = Math.Max(1, video ? p.VideoFps : p.Fps);
                int interval = 1000 / fps;
                long started = Now;
                ScreenImage? got;
                try
                {
                    got = grabber.Grab(video ? p.VideoWidth : p.MaxWidth, Math.Min(interval, 100), force);
                    grabFailures = 0;
                }
                catch (Exception e)
                {
                    Note($"Screen capture ({grabber.Kind}) failed: {e.Message}");
                    Dispose(ref grabber);
                    // Desktop Duplication that keeps failing: GDI from now on, until capture stops.
                    // GDI that keeps failing too: no picture, tried again in a few seconds.
                    if (++grabFailures >= 3)
                    {
                        grabFailures = 0;
                        if (level == 0) level = 1;
                        else
                        {
                            nextOpen = Now + RetryOpenMs;
                            TellUnavailable();
                        }
                    }
                    Thread.Sleep(50);
                    continue;
                }

                if (got is { } image)
                {
                    double change = last.Compare(image);
                    if (change > 0) last.Remember(image);
                    if (change > 0 || force)
                    {
                        lock (gate) forceFrame = false;
                        if (video) encoder = EncodeVideo(image, encoder, sinkList, needsKey, p, rate, ref encodeFailures);
                        if (jpegWanted || VideoJustBroke(video))
                        {
                            if (p == ScreenPreset.Fast && !video)
                            {
                                bool motion = change > 0.03;
                                bool crisp;
                                lock (gate) crisp = crispShown;
                                if (motion || !crisp || force)
                                {
                                    lock (gate) crispShown = false;
                                    DeliverJpeg(image, p.Quality);
                                    if (motion || sharpenDue < 0) sharpenDue = Now + 700;
                                }
                                // A small change (a blinking cursor, a clock) while the clearer
                                // picture shows: keep it, and refresh it soon instead.
                                else if (sharpenDue < 0) sharpenDue = Now + 700;
                            }
                            else DeliverJpeg(image, p.Quality);
                        }
                    }
                }

                // Fast: once the screen has been still for a moment, one clearer picture (at most
                // every 1.5 s), like the Mac.
                if (p == ScreenPreset.Fast && !video && sharpenDue >= 0 && Now >= sharpenDue && Now - lastSharpen >= 1500)
                {
                    sharpenDue = -1;
                    lastSharpen = Now;
                    try
                    {
                        if (grabber.Grab(1280, 0, true) is { } clear)
                        {
                            DeliverJpeg(clear, 60);
                            lock (gate) crispShown = true;
                        }
                    }
                    catch (Exception e)
                    {
                        Note($"Screen capture ({grabber.Kind}) failed: {e.Message}");
                        Dispose(ref grabber);
                        continue;
                    }
                }

                long left = interval - (Now - started);
                if (left > 0) Thread.Sleep((int)left);
            }
        }
        finally
        {
            Dispose(ref grabber);
            Dispose(ref encoder);
        }
    }

    // True once, right after video broke on this picture: the video pages need a JPEG now.
    bool VideoJustBroke(bool wasVideo)
    {
        if (!wasVideo) return false;
        lock (gate) return videoBroken;
    }

    IScreenGrabber? OpenGrabber(int level)
    {
        try { return openGrabber(level); }
        catch (Exception e)
        {
            Note("Screen capture couldn't start: " + e.Message);
            return null;
        }
    }

    // No capture: every waiting poll answers 503 and every watcher hears why.
    void TellUnavailable()
    {
        List<TaskCompletionSource<Poll>> polls;
        List<Action> tell;
        lock (gate)
        {
            unavailable = true;
            polls = waiters.Select(w => w.Done).ToList();
            waiters.Clear();
            tell = subscribers.Values.Select(s => s.Unavailable).ToList();
        }
        Note("Screen capture couldn't start.");
        foreach (var p in polls) p.TrySetResult(new Poll(Result.Unavailable));
        foreach (var t in tell) Safe(t);
    }

    IVideoEncoder? EncodeVideo(ScreenImage image, IVideoEncoder? encoder, List<VideoSink> sinkList, bool needsKey,
                               ScreenPreset p, int rate, ref int failures)
    {
        // Only while every video page has caught up (VideoSink).
        foreach (var sink in sinkList)
        {
            bool ready;
            try { ready = sink.Ready(); }
            catch (Exception) { ready = true; }
            if (!ready) return encoder;
        }
        bool fresh = false;
        if (encoder == null || encoder.Width != image.Width || encoder.Height != image.Height || encoder.Fps != p.VideoFps)
        {
            Dispose(ref encoder);
            try
            {
                if (openEncoder == null) throw new InvalidOperationException("No H.264 encoder.");
                encoder = openEncoder(image.Width, image.Height, p.VideoFps, rate);
            }
            catch (Exception e)
            {
                BreakVideo(e.Message);
                return null;
            }
            lock (gate) encoderName = encoder.Name;
            Note($"Screen video: {encoder.Name}, {image.Width}x{image.Height}");
            fresh = true;
        }
        IReadOnlyList<(VideoConfig Config, VideoChunk Chunk)> output;
        try
        {
            output = encoder.Encode(image, clock.Elapsed.Ticks / 10, needsKey || fresh);
            failures = 0;
        }
        catch (Exception e)
        {
            Note($"Screen video ({encoder.Name}) failed: {e.Message}");
            Dispose(ref encoder);
            lock (gate) encoderName = null;
            if (++failures >= 3) BreakVideo(e.Message);
            return null;
        }
        Distribute(output);
        return encoder;
    }

    void BreakVideo(string why)
    {
        lock (gate)
        {
            videoBroken = true;
            videoProblem = why;
            encoderName = null;
            forceFrame = true;
        }
        Note("Screen video unavailable, sending pictures instead: " + why);
    }

    void Distribute(IReadOnlyList<(VideoConfig Config, VideoChunk Chunk)> output)
    {
        if (output.Count == 0) return;
        var sends = new List<(VideoSink Sink, VideoPacket Packet)>();
        lock (gate)
        {
            foreach (var (config, chunk) in output)
                foreach (var sink in sinks.Values)
                {
                    if (sink.NeedsKey)
                    {
                        if (!chunk.Key) continue;
                        sink.NeedsKey = false;
                    }
                    if (!config.Equals(sink.Config))
                    {
                        sink.Config = config;
                        sends.Add((sink, new VideoPacket.Setup(config)));
                    }
                    sends.Add((sink, new VideoPacket.Picture(chunk)));
                }
        }
        foreach (var (sink, packet) in sends) Safe(() => sink.Send(packet));
    }

    void DeliverJpeg(ScreenImage image, int quality)
    {
        byte[] jpeg;
        try { jpeg = JpegFrames.Encode(image, quality); }
        catch (Exception e)
        {
            Note("JPEG encoding failed: " + e.Message);
            return;
        }
        int id;
        List<TaskCompletionSource<Poll>> polls;
        List<Action<byte[]>> pushes;
        lock (gate)
        {
            latest = jpeg;
            id = ++latestId;
            polls = waiters.Select(w => w.Done).ToList();
            waiters.Clear();
            bool video = sinks.Count > 0 && !videoBroken;
            pushes = subscribers.Where(s => !(video && sinks.ContainsKey(s.Key))).Select(s => s.Value.Frame).ToList();
        }
        foreach (var p in polls) p.TrySetResult(new Poll(Result.Frame, jpeg, id));
        foreach (var push in pushes) Safe(() => push(jpeg));
    }

    void Note(string text)
    {
        lock (gate)
        {
            if (text == lastNote) return;
            lastNote = text;
        }
        try { note?.Invoke(text); }
        catch (Exception) { }
    }

    static void Safe(Action a)
    {
        try { a(); }
        catch (Exception) { }
    }

    static void Dispose<T>(ref T? thing) where T : class, IDisposable
    {
        var t = thing;
        thing = null;
        try { t?.Dispose(); }
        catch (Exception) { }
    }

    // The last picture sent, to tell whether the screen changed, and how much (the Mac measures
    // the changed share on a 96×60 thumbnail; here 96×60 sampled points).
    sealed unsafe class LastPicture
    {
        byte[] bytes = Array.Empty<byte>();
        int width, height, stride;

        public void Clear() { bytes = Array.Empty<byte>(); width = height = stride = 0; }

        // 0: the same; 1: new or another size; else the share of sampled points that changed
        // visibly (at least a tiny bit when any byte changed).
        public double Compare(ScreenImage image)
        {
            if (image.Width != width || image.Height != height || image.Stride != stride || bytes.Length == 0) return 1;
            var now = new ReadOnlySpan<byte>((void*)image.Pixels, image.Stride * image.Height);
            if (now.SequenceEqual(bytes)) return 0;
            const int w = 96, h = 60;
            int changed = 0;
            for (int y = 0; y < h; y++)
            {
                int row = (int)((y + 0.5) * image.Height / h) * image.Stride;
                for (int x = 0; x < w; x++)
                {
                    int i = row + (int)((x + 0.5) * image.Width / w) * 4;
                    int d = Math.Abs(now[i] - bytes[i]) + Math.Abs(now[i + 1] - bytes[i + 1]) + Math.Abs(now[i + 2] - bytes[i + 2]);
                    if (d > 24) changed++;
                }
            }
            return Math.Max(changed / (double)(w * h), 0.0001);
        }

        public void Remember(ScreenImage image)
        {
            int length = image.Stride * image.Height;
            if (bytes.Length != length) bytes = new byte[length];
            new ReadOnlySpan<byte>((void*)image.Pixels, length).CopyTo(bytes);
            width = image.Width;
            height = image.Height;
            stride = image.Stride;
        }
    }
}
