using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Channels;
using LidAwake;

// Stand-ins for the screen and the encoder, and a phone's socket written by hand, so the live
// socket's rules can be checked on any machine (the real capture and encoder are tried in
// MediaTests, and only reported when the runner lacks them).

// A screen of 320×180 that changes on every Grab while `Changing` (else it behaves like Desktop
// Duplication: nothing new, unless forced).
sealed class FakeGrabber : IScreenGrabber
{
    public const int W = 320, H = 180;
    readonly IntPtr pixels = Marshal.AllocHGlobal(W * H * 4);
    readonly byte[] paint = new byte[W * H * 4];
    readonly Func<bool> changing;
    int frame;
    bool disposed;

    public FakeGrabber(Func<bool> changing) => this.changing = changing;

    public string Kind => "fake screen";
    public int Grabs;
    public bool Disposed => disposed;

    public ScreenImage? Grab(int maxWidth, int waitMs, bool force)
    {
        if (disposed) throw new ObjectDisposedException(nameof(FakeGrabber));
        Interlocked.Increment(ref Grabs);
        bool change = changing() || frame == 0;
        if (!change && !force)
        {
            Thread.Sleep(Math.Min(Math.Max(waitMs, 1), 20));
            return null;
        }
        if (change)
        {
            frame++;
            for (int i = 0; i < paint.Length; i += 4)
            {
                paint[i] = (byte)(frame * 37 + i / 1280);
                paint[i + 1] = (byte)(frame * 11 + i);
                paint[i + 2] = (byte)(i / 4 % W);
                paint[i + 3] = 255;
            }
            Marshal.Copy(paint, 0, pixels, paint.Length);
        }
        return new ScreenImage(pixels, W, H, W * 4);
    }

    public void Dispose()
    {
        if (disposed) return;
        disposed = true;
        Marshal.FreeHGlobal(pixels);
    }
}

// Every grabber a feed opened, for the tests to look at.
sealed class FakeScreens
{
    readonly List<FakeGrabber> opened = new();
    public volatile bool Changing = true;
    public volatile bool Broken;  // no capture at all
    public readonly List<int> Levels = new();

    public IScreenGrabber? Open(int level)
    {
        lock (opened)
        {
            Levels.Add(level);
            if (Broken) return null;
            var g = new FakeGrabber(() => Changing);
            opened.Add(g);
            return g;
        }
    }

    public List<FakeGrabber> Opened { get { lock (opened) return opened.ToList(); } }
}

// An H.264 encoder stand-in: a fixed SPS/PPS, a keyframe first and whenever forced.
sealed class FakeEncoder : IVideoEncoder
{
    public static readonly byte[] Sps = { 0x67, 0x4D, 0x00, 0x28, 0x95, 0xA0, 0x1E, 0x00, 0x89, 0xF9, 0x70 };
    public static readonly byte[] Pps = { 0x68, 0xEE, 0x3C, 0x80 };

    public FakeEncoder(int width, int height, int fps) => (Width, Height, Fps) = (width, height, fps);

    public string Name => "fake H.264";
    public int Width { get; }
    public int Height { get; }
    public int Fps { get; }
    public int Encoded, ForcedKeys, Bitrate;
    public bool Disposed;

    public IReadOnlyList<(VideoConfig Config, VideoChunk Chunk)> Encode(ScreenImage image, long timestamp, bool forceKey)
    {
        Encoded++;
        if (forceKey) ForcedKeys++;
        bool key = forceKey || Encoded == 1;
        var nal = new byte[] { (byte)(key ? 0x65 : 0x41), 0x88, 0x84, (byte)Encoded };
        return new[] { (VideoConfig.From(Sps, Pps, Width, Height), new VideoChunk(Avcc.ToAvcc(new[] { nal }), key, timestamp)) };
    }

    public void SetBitrate(int bitsPerSecond) => Bitrate = bitsPerSecond;

    public void Dispose() => Disposed = true;
}

sealed class FakeEncoders
{
    readonly List<FakeEncoder> made = new();

    public IVideoEncoder Make(int width, int height, int fps, int bitrate)
    {
        var e = new FakeEncoder(width, height, fps);
        lock (made) made.Add(e);
        return e;
    }

    public List<FakeEncoder> Made { get { lock (made) return made.ToList(); } }
}

// A phone's live socket, opened by hand so every handshake header can be chosen. Messages are
// read in the background into a queue; `AutoAck` confirms each binary one, as the page does.
sealed class LiveClient : IAsyncDisposable
{
    public const string TailHost = "pc.example.ts.net";
    public const string Owner = "owner@example.com";
    public const string PageOrigin = "https://pc.example.ts.net";

    readonly TcpClient tcp;
    readonly Channel<(bool Text, byte[] Data)> messages = Channel.CreateUnbounded<(bool Text, byte[] Data)>();
    readonly SemaphoreSlim sendLock = new(1, 1);
    Task? reader;

    public int Status { get; private set; }
    public WebSocket? Socket { get; private set; }
    public volatile bool AutoAck;
    public bool Closed => reader?.IsCompleted ?? true;

    LiveClient(TcpClient tcp) => this.tcp = tcp;

    public static async Task<LiveClient> Connect(int port, string? cookie, string? host = TailHost, string? origin = PageOrigin,
                                                 string? owner = Owner, string? fetchSite = null, bool funnel = false, bool upgrade = true)
    {
        var tcp = new TcpClient();
        await tcp.ConnectAsync(IPAddress.Loopback, port);
        var client = new LiveClient(tcp);
        var stream = tcp.GetStream();
        var request = new StringBuilder("GET /live HTTP/1.1\r\n");
        if (host != null) request.Append("Host: ").Append(host).Append("\r\n");
        if (upgrade)
        {
            request.Append("Upgrade: websocket\r\nConnection: Upgrade\r\nSec-WebSocket-Version: 13\r\n");
            request.Append("Sec-WebSocket-Key: ").Append(Convert.ToBase64String(RandomNumberGenerator.GetBytes(16))).Append("\r\n");
        }
        if (origin != null) request.Append("Origin: ").Append(origin).Append("\r\n");
        if (owner != null) request.Append("Tailscale-User-Login: ").Append(owner).Append("\r\n");
        if (cookie != null) request.Append("Cookie: ").Append(cookie).Append("\r\n");
        if (fetchSite != null) request.Append("Sec-Fetch-Site: ").Append(fetchSite).Append("\r\n");
        if (funnel) request.Append("Tailscale-Funnel-Request: ?1\r\n");
        request.Append("User-Agent: Mozilla/5.0 (iPhone; CPU iPhone OS 17_0 like Mac OS X)\r\n\r\n");
        try
        {
            await stream.WriteAsync(Encoding.ASCII.GetBytes(request.ToString()));
            client.Status = await ReadStatus(stream);
        }
        catch (Exception) { client.Status = 0; }
        if (client.Status == 101)
        {
            client.Socket = WebSocket.CreateFromStream(stream, isServer: false, subProtocol: null, keepAliveInterval: TimeSpan.FromSeconds(30));
            client.reader = Task.Run(client.Read);
        }
        return client;
    }

    // The status code of the answer to the handshake (0: the connection was closed instead).
    // Read a byte at a time, so nothing after the headers is taken from the socket.
    static async Task<int> ReadStatus(NetworkStream stream)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var head = new List<byte>();
        var one = new byte[1];
        while (head.Count < 16 * 1024)
        {
            int n = await stream.ReadAsync(one.AsMemory(), timeout.Token);
            if (n == 0) return 0;
            head.Add(one[0]);
            int c = head.Count;
            if (c >= 4 && head[c - 4] == '\r' && head[c - 3] == '\n' && head[c - 2] == '\r' && head[c - 1] == '\n') break;
        }
        var line = Encoding.ASCII.GetString(head.ToArray()).Split("\r\n")[0];
        var parts = line.Split(' ');
        return parts.Length >= 2 && int.TryParse(parts[1], out var code) ? code : 0;
    }

    async Task Read()
    {
        var buffer = new byte[4 * 1024 * 1024];
        try
        {
            while (Socket!.State == WebSocketState.Open)
            {
                int total = 0;
                ValueWebSocketReceiveResult r;
                do
                {
                    r = await Socket.ReceiveAsync(buffer.AsMemory(total), CancellationToken.None);
                    if (r.MessageType == WebSocketMessageType.Close) return;
                    total += r.Count;
                } while (!r.EndOfMessage);
                bool text = r.MessageType == WebSocketMessageType.Text;
                if (!text && AutoAck) await Send("{\"ack\":1}");
                messages.Writer.TryWrite((text, buffer.AsSpan(0, total).ToArray()));
            }
        }
        catch (Exception) { }
        finally { messages.Writer.TryComplete(); }
    }

    public async Task Send(string json)
    {
        await sendLock.WaitAsync();
        try { await Socket!.SendAsync(Encoding.UTF8.GetBytes(json).AsMemory(), WebSocketMessageType.Text, true, CancellationToken.None); }
        finally { sendLock.Release(); }
    }

    // The next message, or null when none came in time (or the socket closed).
    public async Task<(bool Text, byte[] Data)?> Next(TimeSpan wait)
    {
        using var cts = new CancellationTokenSource(wait);
        try { return await messages.Reader.ReadAsync(cts.Token); }
        catch (OperationCanceledException) { return null; }
        catch (ChannelClosedException) { return null; }
    }

    // Every message that arrives within `wait`.
    public async Task<List<(bool Text, byte[] Data)>> Collect(TimeSpan wait)
    {
        var list = new List<(bool Text, byte[] Data)>();
        var end = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < end)
        {
            var left = end - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) break;
            if (await Next(left) is { } m) list.Add(m);
            else if (Closed) break;
        }
        return list;
    }

    // The first message that `match` accepts, within `wait`.
    public async Task<(bool Text, byte[] Data)?> WaitFor(Func<(bool Text, byte[] Data), bool> match, TimeSpan wait)
    {
        var end = DateTime.UtcNow + wait;
        while (DateTime.UtcNow < end)
        {
            var left = end - DateTime.UtcNow;
            if (left <= TimeSpan.Zero) break;
            if (await Next(left) is not { } m) { if (Closed) return null; continue; }
            if (match(m)) return m;
        }
        return null;
    }

    public async Task<bool> WaitClosed(TimeSpan wait)
    {
        if (reader == null) return true;
        return await Task.WhenAny(reader, Task.Delay(wait)) == reader;
    }

    public static bool IsJpeg((bool Text, byte[] Data) m) => !m.Text && m.Data.Length > 2 && m.Data[0] == 0xFF && m.Data[1] == 0xD8;
    public static bool IsVideo((bool Text, byte[] Data) m) => !m.Text && m.Data.Length > 9 && (m.Data[0] == 1 || m.Data[0] == 2);
    public static bool IsKeyframe((bool Text, byte[] Data) m) => !m.Text && m.Data.Length > 9 && m.Data[0] == 1;
    public static bool IsSetup((bool Text, byte[] Data) m) => m.Text && Encoding.UTF8.GetString(m.Data).StartsWith("{\"video\":", StringComparison.Ordinal);

    public async ValueTask DisposeAsync()
    {
        try { Socket?.Abort(); }
        catch (Exception) { }
        tcp.Dispose();
        if (reader != null)
        {
            try { await reader; }
            catch (Exception) { }
        }
    }
}

// A live socket server on a free port, with a signed-in session, as the tests need it.
sealed class LiveHarness : IAsyncDisposable
{
    readonly string folder = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
    public readonly Settings Settings;
    public readonly Auth Auth;
    public readonly FakeScreens Screens = new();
    public readonly FakeEncoders Encoders = new();
    public readonly ScreenFeed Feed;
    public readonly LiveServer Live;
    public readonly string Token;
    public readonly int Port;
    public volatile bool On = true;
    public volatile bool Allowed = true;
    readonly List<LiveClient> clients = new();

    public string Cookie => $"{LidAwake.Auth.CookieName}={Token}";

    // `encoders`: null for JPEG only (no encoder), or a factory (the fake one when not given).
    // `input`: the input sink for commands on the socket (none: the server's own, which does nothing).
    public LiveHarness(bool video = true, VideoEncoderFactory? encoders = null, TimeSpan? idleStop = null, IInputSink? input = null)
    {
        Settings = new Settings(Path.Combine(folder, "settings"));
        Settings.Set("tailnetHost", LiveClient.TailHost);
        Settings.Set("tailnetOwner", LiveClient.Owner);
        Auth = new Auth(Settings, iterations: 1000);
        Auth.SetPassword("correct horse battery");
        Token = Auth.Login("correct horse battery", "iPhone").Token!;
        Feed = new ScreenFeed(Screens.Open, video ? encoders ?? Encoders.Make : null, idleStop ?? TimeSpan.FromMilliseconds(500));
        Port = FreePort();
        Live = new LiveServer(Auth, new Tailnet(Settings, _ => null), () => On, () => Allowed, Feed, Port, input: input);
        Live.Start();
    }

    public static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var p = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return p;
    }

    public async Task<LiveClient> Connect(string? cookie = "", string? host = LiveClient.TailHost, string? origin = LiveClient.PageOrigin,
                                          string? owner = LiveClient.Owner, string? fetchSite = null, bool funnel = false, bool upgrade = true)
    {
        var c = await LiveClient.Connect(Port, cookie == "" ? Cookie : cookie, host, origin, owner, fetchSite, funnel, upgrade);
        lock (clients) clients.Add(c);
        return c;
    }

    public async ValueTask DisposeAsync()
    {
        List<LiveClient> all;
        lock (clients) all = clients.ToList();
        foreach (var c in all) await c.DisposeAsync();
        Live.Dispose();
        Feed.Dispose();
        try { Directory.Delete(folder, true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
