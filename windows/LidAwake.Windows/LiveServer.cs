using System.Buffers.Binary;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;

namespace LidAwake;

// The owner's "Allow screen from phone" switch in the tray, off until the owner turns it on.
// Kept in the roaming settings as `remoteScreen`, the Mac's name for it. The one place it is read
// and written: /status, /screen.jpg, the live socket, the tray and RemoteControl (whose control
// switch needs it) all go through here.
public static class ScreenSwitch
{
    public const string Key = "remoteScreen";

    public static bool IsOn(Settings settings)
    {
        try { return settings.Get<bool?>(Key) == true; }
        catch (Exception) { return false; }
    }

    public static void Set(Settings settings, bool on) => settings.Set(Key, on);
}

// The live socket for the Screen tab on 127.0.0.1:8788 (docs/API.md 7; LiveServer.swift on the
// Mac). Tailscale Serve maps https://<pc>.ts.net/live here. The handshake is refused unless the
// Host is this PC's Tailscale name from the owner's account, the Origin is the page itself, the
// session cookie is valid, Lid Awake is on and the owner allows the screen. Pictures go out as
// binary messages (JPEG, or H.264 for pages that can play it), with the Mac's flow control;
// quality and video choices come in as JSON text. Input commands go to the control's IInputSink
// (7.6), and camera messages to the CameraService (7.5): while a phone's camera is open, that
// socket gets the camera instead of the screen.
public sealed class LiveServer : IDisposable
{
    public const int DefaultPort = 8788;
    public const int MaxSockets = 8;
    public const int MaxMessage = 64 * 1024;
    public const string WakingText = "Waking the PC's screen… This usually takes a few seconds.";

    readonly Auth auth;
    readonly Tailnet tailnet;
    readonly Func<bool> isOn;
    readonly Func<bool> screenAllowed;
    readonly ScreenFeed feed;
    readonly IInputSink input;
    readonly CameraService? camera;
    readonly AccessLog? log;
    readonly int port;
    readonly object gate = new();
    readonly Dictionary<Guid, Peer> peers = new();
    volatile WebApplication? app;
    System.Threading.Timer? housekeeping;

    // `input` carries out the phone's input commands (RemoteControl; none: no control on this PC,
    // so commands are checked like the Mac's and then do nothing, and `ping` is still answered).
    // `camera` is the phone's view of the PC's camera and microphone (none: camera messages are
    // ignored).
    public LiveServer(Auth auth, Tailnet tailnet, Func<bool> isOn, Func<bool> screenAllowed, ScreenFeed feed,
                      int port = DefaultPort, AccessLog? log = null, IInputSink? input = null, CameraService? camera = null)
    {
        (this.auth, this.tailnet, this.isOn, this.screenAllowed, this.feed) = (auth, tailnet, isOn, screenAllowed, feed);
        this.port = port;
        this.log = log;
        this.input = input ?? new NoInput(Allowed);
        this.camera = camera;
    }

    public int Port => port;

    // The camera's socket seam is connected (a CameraService was given): CameraFeature.Ready.
    public bool CameraWired => camera != null;

    // True after Start succeeded, until Stop or Dispose. Serve maps /live only while it is.
    public bool IsRunning => app != null;

    // Phones watching the screen right now (a phone whose camera is open watches the camera, not
    // the screen: 7.7), and a signal whenever that number changes (from the socket's threads). The
    // lid-screen task keeps the display on while it is above 0.
    public int Viewers { get { lock (gate) return ScreenViewers(); } }
    public event Action<int>? ViewersChanged;

    // Under gate.
    int ScreenViewers() => peers.Values.Count(p => !p.CameraOn);

    readonly object changedGate = new();
    int reportedViewers;  // under changedGate

    // One notification at a time, each with the count as it is then: sockets closing together
    // (CloseAll) can't tell 0 and then a stale 1, so the last thing told is always the truth.
    void Changed()
    {
        lock (changedGate)
        {
            int now;
            lock (gate) now = ScreenViewers();
            if (now == reportedViewers) return;
            reportedViewers = now;
            try { ViewersChanged?.Invoke(now); }
            catch (Exception) { }
        }
    }

    public void Start()
    {
        if (app != null) return;
        var builder = WebApplication.CreateSlimBuilder(new WebApplicationOptions());
        // Only the one address below, whatever the environment says (as in WebServer).
        ((IConfigurationBuilder)builder.Configuration).Sources.Clear();
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(o =>
        {
            o.AddServerHeader = false;
            o.Listen(IPAddress.Loopback, port);
            // At most 8 sockets, counted from their first byte, as on the Mac. The handshake must
            // arrive within 10 seconds.
            o.Limits.MaxConcurrentConnections = MaxSockets;
            o.Limits.MaxConcurrentUpgradedConnections = MaxSockets;
            o.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            o.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            o.Limits.KeepAliveTimeout = TimeSpan.FromSeconds(10);
        });
        var built = builder.Build();
        built.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        built.Run(Handle);
        try
        {
            Task.Run(() => built.StartAsync()).GetAwaiter().GetResult();
            var urls = built.Urls.ToList();
            if (urls.Count == 0 || !urls.All(IsOurAddress))
                throw new InvalidOperationException($"The live socket would listen on {string.Join(", ", urls)} instead of 127.0.0.1:{port}.");
        }
        catch (Exception)
        {
            Shutdown(built);
            throw;
        }
        app = built;
        housekeeping = new System.Threading.Timer(_ => Housekeeping(), null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
    }

    bool IsOurAddress(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) && u.Scheme == "http" && u.Host == "127.0.0.1" && u.Port == port;

    public void Stop()
    {
        try { housekeeping?.Dispose(); }
        catch (Exception) { }
        housekeeping = null;
        CloseAll();
        var running = app;
        app = null;
        if (running != null) Shutdown(running);
    }

    static void Shutdown(WebApplication running)
    {
        try
        {
            Task.Run(async () =>
            {
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
                try { await running.StopAsync(cts.Token); }
                catch (Exception) { }
                await running.DisposeAsync();
            }).Wait(TimeSpan.FromSeconds(5));
        }
        catch (Exception) { }
    }

    public void Dispose() => Stop();

    // Lid Awake turned off, or the owner withdrew the screen: every socket closes now. The page
    // reconnects, and is refused until both are on again. A button or modifier key a phone was
    // holding is let go (the Mac's closeAll does the same).
    public void CloseAll()
    {
        List<Peer> all;
        lock (gate) all = peers.Values.ToList();
        foreach (var p in all) p.Abort();
        ReleaseInput();
    }

    void ReleaseInput()
    {
        try { input.ReleaseHeld(); }
        catch (Exception) { }
    }

    // Every 2 seconds, as on the Mac: off or not allowed closes everything; a signed-out or
    // expired session closes its sockets (and so its camera); a socket that sent nothing for 15
    // seconds while it had something to send (the phone stopped reading) closes. A camera that
    // closed by itself (the owner's camera switch, the 10 minutes: CameraService's own 2-second
    // check) gives the socket back its screen.
    internal void Housekeeping()
    {
        try
        {
            List<Peer> all;
            lock (gate) all = peers.Values.ToList();
            if (all.Count == 0) return;
            if (!Allowed()) { CloseAll(); return; }
            foreach (var p in all)
            {
                if (!SessionValid(p.Session) || p.Stuck(15)) p.Abort();
                else SyncCamera(p);
            }
        }
        catch (Exception) { }
    }

    bool Allowed()
    {
        try { return isOn() && screenAllowed(); }
        catch (Exception) { return false; }
    }

    bool SessionValid(string token)
    {
        try { return auth.IsValid(token); }
        catch (Exception) { return false; }
    }

    // ----- Handshake (7.1) -----

    async Task Handle(HttpContext c)
    {
        try
        {
            if (!c.WebSockets.IsWebSocketRequest || HandshakeToken(c.Request) is not { } token)
            {
                await Refuse(c);
                return;
            }
            var device = Auth.DeviceName(c.Request.Headers.UserAgent.ToString());
            bool full;
            lock (gate) full = peers.Count >= MaxSockets;
            if (full) { await Refuse(c); return; }
            using var socket = await c.WebSockets.AcceptWebSocketAsync();
            await Serve(new Peer(socket, token, device));
        }
        catch (Exception)
        {
            // A socket that broke stays inside its own request.
        }
    }

    static async Task Refuse(HttpContext c)
    {
        try
        {
            if (c.Response.HasStarted) return;
            c.Response.StatusCode = 400;
            c.Response.ContentType = "text/plain; charset=utf-8";
            // The connection goes too: a refused phone doesn't keep one of the 8 places.
            c.Response.Headers.Connection = "close";
            await c.Response.WriteAsync("Bad request");
        }
        catch (Exception) { }
    }

    // The session token of an allowed handshake, or null to refuse it. Same rules as the Mac:
    // never through Funnel; the Host is this PC's Tailscale name with the owner's Tailscale login
    // (the PC's own 127.0.0.1 is refused, as in docs/API.md 2.3); the Origin is exactly the page;
    // Sec-Fetch-Site, when sent, is same-origin; a valid session; on; the screen allowed.
    string? HandshakeToken(HttpRequest req)
    {
        if (req.Headers.ContainsKey("Tailscale-Funnel-Request")) return null;
        var host = (req.Host.Host ?? "").ToLowerInvariant();
        if (Tailnet.IsLoopback(host) || !tailnet.IsOwner(host, req.Headers["Tailscale-User-Login"].ToString())) return null;
        if (req.Headers.Origin.ToString() != "https://" + host) return null;
        if (req.Headers.ContainsKey("Sec-Fetch-Site") && req.Headers["Sec-Fetch-Site"].ToString() != "same-origin") return null;
        // The same choice as POST /camera/unlock, so a camera ticket matches this socket's session.
        var token = WebServer.SessionOf(auth, WebServer.CookieTokens(req));
        if (token == null) return null;
        if (!Allowed()) return null;
        return token;
    }

    // ----- One phone -----

    async Task Serve(Peer peer)
    {
        lock (gate) peers[peer.Id] = peer;
        // Turned off (or the screen withdrawn) between the handshake and here: CloseAll may have
        // looked before this phone was listed, so it closes now rather than at the 2-second check.
        if (!Allowed()) peer.Abort();
        Changed();
        LogQuietly("Screen viewing started · " + peer.Device);
        var sender = Task.Run(() => SendLoop(peer));
        try
        {
            if (camera != null)
                peer.CameraSocket = new WireCameraSocket(peer.Session, peer.Device,
                    text => SendRaw(peer, Encoding.UTF8.GetBytes(text)), bytes => EnqueueCamera(peer, bytes));
            Subscribe(peer);
            await ReceiveLoop(peer);
        }
        catch (Exception) { }
        finally
        {
            peer.Abort();
            // The camera stops with its socket (the last phone's turns the webcam light off).
            if (camera != null && peer.CameraSocket is { } cs)
            {
                try { camera.Closed(cs); }
                catch (Exception) { }
            }
            // Under CameraGate: a camera switch-over can't subscribe this closed phone again.
            lock (peer.CameraGate) feed.Unsubscribe(peer.Id);
            lock (gate) peers.Remove(peer.Id);
            Changed();
            LogQuietly("Screen viewing ended · " + peer.Device);
            // Don't leave a mouse button or a modifier held if the phone was mid-drag (7.6).
            if (peer.UsedInput) ReleaseInput();
            try { await sender; }
            catch (Exception) { }
            peer.Release();
        }
    }

    // The screen's pictures for this phone, unless its camera is open.
    void Subscribe(Peer peer) =>
        feed.Subscribe(peer.Id,
            jpeg => { if (!peer.CameraOn) peer.SetPending(jpeg); },
            () => { if (!peer.CameraOn) SendText(peer, new { error = WakingText }); });

    // The camera opened or closed for this phone (after a camera message, and every 2 seconds):
    // open, its screen stops (no screen picture or video is sent, and what waits of it is dropped)
    // so only one video uses the link (7.5); closed, the screen comes back, as JPEG until the page
    // asks for video again.
    // A phone on its camera is no screen viewer, so the count is told again after a switch-over.
    void SyncCamera(Peer peer)
    {
        if (camera == null || peer.CameraSocket is not { } cs) return;
        lock (peer.CameraGate)
        {
            if (peer.Closed) return;
            bool open;
            try { open = camera.IsOpen(cs); }
            catch (Exception) { open = false; }
            if (open == peer.CameraOn) return;
            // Under gate, so the count of screen viewers never sees it half-way.
            lock (gate) peer.CameraOn = open;
            if (open)
            {
                feed.Unsubscribe(peer.Id);
                peer.Video = false;
                peer.SetPending(null);
                peer.ClearOutbox();
            }
            else
            {
                peer.DropCamera();
                Subscribe(peer);
            }
        }
        Changed();
    }

    // Camera video and sound (binary, 7.2): no confirmations, so never held back by the screen's
    // flow control. More than 90 waiting: dropped, and the camera starts again from a keyframe.
    void EnqueueCamera(Peer peer, byte[] data)
    {
        switch (peer.Enqueue(data, text: false, video: false, camera: true))
        {
            case Peer.Queued.CameraDropped:
                // Not from inside the camera's own call (ICameraSocket): it is told on another thread.
                if (camera is { } c) ThreadPool.QueueUserWorkItem(_ => { try { c.RequestKeyframe(); } catch (Exception) { } });
                break;
            case Peer.Queued.Overflow: peer.Abort(); break;
        }
    }

    // A camera text message, already JSON.
    static void SendRaw(Peer peer, byte[] json)
    {
        if (peer.Enqueue(json, text: true, video: false) == Peer.Queued.Overflow) peer.Abort();
    }

    void LogQuietly(string text)
    {
        try { log?.Write(text); }
        catch (Exception) { }
    }

    static async Task SendLoop(Peer peer)
    {
        try
        {
            while (!peer.Closed)
            {
                if (peer.Next() is not { } item)
                {
                    await peer.WaitForWork();
                    continue;
                }
                await peer.Socket.SendAsync(item.Data.AsMemory(), item.Text ? WebSocketMessageType.Text : WebSocketMessageType.Binary, true, peer.Cancel);
                peer.Sent();
            }
        }
        catch (Exception) { peer.Abort(); }
    }

    async Task ReceiveLoop(Peer peer)
    {
        var buffer = new byte[MaxMessage];
        while (!peer.Closed)
        {
            int total = 0;
            ValueWebSocketReceiveResult r;
            do
            {
                if (total >= buffer.Length)
                {
                    // Bigger than 64 KB: the Mac's limit too.
                    try { await peer.Socket.CloseOutputAsync(WebSocketCloseStatus.MessageTooBig, "Too big", peer.Cancel); }
                    catch (Exception) { }
                    return;
                }
                r = await peer.Socket.ReceiveAsync(buffer.AsMemory(total), peer.Cancel);
                if (r.MessageType == WebSocketMessageType.Close)
                {
                    try { await peer.Socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, peer.Cancel); }
                    catch (Exception) { }
                    return;
                }
                total += r.Count;
            } while (!r.EndOfMessage);
            if (total > 0) HandleMessage(peer, buffer.AsSpan(0, total));
        }
    }

    // ----- Messages from the phone (7.3), in the Mac's order -----

    void HandleMessage(Peer peer, ReadOnlySpan<byte> data)
    {
        JsonDocument doc;
        try { doc = JsonDocument.Parse(data.ToArray()); }
        catch (JsonException) { return; }
        using (doc)
        {
            var m = doc.RootElement;
            if (m.ValueKind != JsonValueKind.Object) return;
            if (Bool(m, "acks") == true) peer.FlowControl = true;
            // The camera's own messages first: `camera` (with its ticket), `cameraKey`, `talk`.
            if (camera != null && peer.CameraSocket is { } cs)
            {
                bool handled;
                try { handled = camera.Receive(cs, m); }
                catch (Exception) { handled = false; }
                if (handled) { SyncCamera(peer); return; }
            }
            // Never input, whatever they hold (and ignored without a camera).
            if (Bool(m, "camera") != null || m.TryGetProperty("cameraKey", out _) || m.TryGetProperty("talk", out _)) return;
            // A video switch and a decoder restart each force a keyframe: they count toward this
            // socket's 240 a second, like input, and a flood of them is dropped silently. While
            // the camera is open the screen's video can't be turned on.
            bool videoChange = !peer.CameraOn && Bool(m, "video") is { } asked && asked != peer.Video;
            if ((videoChange || m.TryGetProperty("videoRestart", out _)) && !peer.InputRate.TryTake(Environment.TickCount64)) return;
            if (videoChange && Bool(m, "video") is { } wantsVideo)
            {
                peer.Video = wantsVideo;
                // A JPEG still waiting is old news once video starts; video waiting is dropped on
                // the way back to JPEG.
                if (wantsVideo) peer.SetPending(null);
                feed.SetVideo(peer.Id, wantsVideo
                    ? new VideoSink(packet => { if (!peer.CameraOn) Enqueue(peer, packet); }, () => !peer.CameraOn && peer.VideoReady())
                    : null);
                if (!wantsVideo) peer.ClearOutbox();
            }
            if (m.TryGetProperty("videoRestart", out _))
            {
                if (!peer.CameraOn) feed.RestartVideo(peer.Id);
                return;
            }
            if (m.TryGetProperty("ack", out _))
            {
                if (peer.Ack() is { } confirmed && peer.Video && !confirmed.Key) feed.VideoFeedback(confirmed.Extra);
                peer.Wake();
                return;
            }
            if (m.TryGetProperty("quality", out var q) && q.ValueKind == JsonValueKind.String && feed.SetPreset(q.GetString())) return;
            if (m.TryGetProperty("video", out _) || m.TryGetProperty("acks", out _) || m.TryGetProperty("camera", out _)) return;
            HandOffInput(peer, m);
        }
    }

    // Everything else is an input command (docs/API.md 7.6 and 8), checked in the Mac's order:
    // the switches (Refusal: off, or view only, each answered with its sentence), then at most 240
    // a second on this socket (extra ones get nothing, not even an answer), then the text length.
    // `ping` is carried out like any command (it only wakes the screen) and answered.
    void HandOffInput(Peer peer, JsonElement message)
    {
        string? refusal;
        try { refusal = input.Refusal(); }
        catch (Exception) { refusal = RemoteControl.ScreenOffText; }
        if (refusal != null) { SendText(peer, new { error = refusal }); return; }
        if (!peer.InputRate.TryTake(Environment.TickCount64)) return;
        if (input.TooLong(message)) { SendText(peer, new { error = RemoteControl.TooMuchText }); return; }
        if (!peer.UsedInput)
        {
            peer.UsedInput = true;
            LogQuietly("PC controlled from the phone · " + peer.Device);
        }
        try { input.Perform(message); }
        catch (Exception) { }
        if (message.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "ping")
            SendText(peer, new { pong = true });
    }

    // No control on this PC: only the screen check, and nothing is carried out.
    sealed class NoInput : IInputSink
    {
        readonly Func<bool> allowed;
        public NoInput(Func<bool> allowed) => this.allowed = allowed;
        public string? Refusal() => allowed() ? null : RemoteControl.ScreenOffText;
        public bool TooLong(JsonElement command) => InputInjector.TooLong(command);
        public void Perform(JsonElement command) { }
        public void ReleaseHeld() { }
    }

    static bool? Bool(JsonElement m, string name) =>
        m.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    // A text reply. A phone that lets too much pile up is closed (Peer.Enqueue).
    static void SendText(Peer peer, object message)
    {
        if (peer.Enqueue(JsonSerializer.SerializeToUtf8Bytes(message), text: true, video: false) == Peer.Queued.Overflow) peer.Abort();
    }

    // Video for one phone: the setup as text, each picture as [1 keyframe | 2 other][8-byte
    // timestamp, µs, big-endian][AVCC] (7.2). More than 60 waiting: dropped, and started again
    // from a keyframe once the phone catches up.
    void Enqueue(Peer peer, VideoPacket packet)
    {
        byte[] data;
        bool text;
        switch (packet)
        {
            case VideoPacket.Setup s:
                data = JsonSerializer.SerializeToUtf8Bytes(new
                {
                    video = new
                    {
                        codec = s.Config.Codec,
                        description = Convert.ToBase64String(s.Config.Description),
                        width = s.Config.Width,
                        height = s.Config.Height,
                    },
                });
                text = true;
                break;
            case VideoPacket.Picture p:
                data = new byte[9 + p.Chunk.Data.Length];
                data[0] = (byte)(p.Chunk.Key ? 1 : 2);
                BinaryPrimitives.WriteInt64BigEndian(data.AsSpan(1, 8), p.Chunk.Timestamp);
                p.Chunk.Data.CopyTo(data, 9);
                text = false;
                break;
            default:
                return;
        }
        switch (peer.Enqueue(data, text, video: true))
        {
            case Peer.Queued.VideoDropped: feed.RestartVideo(peer.Id); break;
            case Peer.Queued.Overflow: peer.Abort(); break;
        }
    }

    // One phone's socket. A send "completes" once the data is handed to the network, not when the
    // phone has it, so a page that confirms each picture ("acks") gets at most two unconfirmed
    // JPEGs (newer ones replace the one waiting: the latest wins), and video is only encoded while
    // fewer than 6 pictures are unconfirmed and fewer than 4 messages wait (VideoReady). A page
    // that stops confirming for 3 seconds starts afresh, so the stream never freezes.
    internal sealed class Peer
    {
        // Everything waiting for one phone, text and video together: past this the phone isn't
        // reading (or floods replies), and its socket is closed.
        public const int MaxQueued = 256;
        public const long MaxQueuedBytes = 8L * 1024 * 1024;
        public const int MaxVideoQueued = 60;
        // The camera's video and sound waiting (the Mac's camera limit).
        public const int MaxCameraQueued = 90;

        public enum Queued { Ok, VideoDropped, CameraDropped, Overflow }

        // What a message in the outbox is: text, the screen's video (confirmed by the page's
        // acks), or the camera's video and sound (never confirmed).
        enum Kind { Text, Screen, Camera }

        public readonly Guid Id = Guid.NewGuid();
        // At most 240 input messages a second (7.6).
        public readonly InputRateLimiter InputRate = new();
        public readonly WebSocket Socket;
        public readonly string Session;  // the session token the handshake used
        public readonly string Device;
        readonly CancellationTokenSource cancel = new();
        readonly SemaphoreSlim work = new(0, 1);
        readonly object gate = new();

        // Under gate.
        bool sending;
        byte[]? pending;                                   // JPEG: the newest picture waiting its turn
        readonly Queue<(byte[] Data, Kind Kind)> outbox = new();  // text, video and camera, all in order
        long outboxBytes;
        int unconfirmed;
        double waitingSince;
        readonly List<(double At, int Bytes, bool Key)> sentAt = new();  // unconfirmed pictures
        double minDelay = 0.3;                             // usual send → confirm time on this link
        int backlog;                                       // video messages queued or being sent
        double lastProgress, sendStarted;

        public Peer(WebSocket socket, string token, string device)
        {
            Socket = socket;
            Session = token;
            Device = device;
            lastProgress = sendStarted = Now();
        }

        static double Now() => Environment.TickCount64 / 1000.0;

        public CancellationToken Cancel => cancel.Token;
        public bool Closed => cancel.IsCancellationRequested;
        public volatile bool FlowControl;
        public volatile bool Video;
        // The camera's side of this socket (with a CameraService), and whether its camera is open
        // (then the screen sends this phone nothing). CameraGate keeps the switch-over in order.
        public ICameraSocket? CameraSocket;
        public volatile bool CameraOn;
        public readonly object CameraGate = new();
        // This phone sent an input command that was carried out (its leaving lets go of held keys).
        public volatile bool UsedInput;

        public void Abort()
        {
            try { cancel.Cancel(); }
            catch (Exception) { }
            try { Socket.Abort(); }
            catch (Exception) { }
        }

        // Once the socket's loops have ended.
        public void Release()
        {
            Abort();
            try { cancel.Dispose(); }
            catch (Exception) { }
            try { work.Dispose(); }
            catch (Exception) { }
        }

        public void Wake()
        {
            try { if (work.CurrentCount == 0) work.Release(); }
            catch (SemaphoreFullException) { }
            catch (ObjectDisposedException) { }
        }

        public Task WaitForWork() => work.WaitAsync(cancel.Token);

        // A new JPEG replaces the one waiting (null: none waits).
        public void SetPending(byte[]? jpeg)
        {
            lock (gate) pending = jpeg;
            if (jpeg != null) Wake();
        }

        // VideoDropped: more than 60 video messages waited and the screen's video was dropped (the
        // caller restarts from a keyframe). CameraDropped: more than 90 camera messages waited and
        // they were dropped (the caller asks the camera for a keyframe). Overflow: too much waits
        // in all (256 messages or 8 MB); the caller closes the socket. `camera`: a camera picture
        // or sound (binary).
        public Queued Enqueue(byte[] data, bool text, bool video, bool camera = false)
        {
            var result = Queued.Ok;
            var kind = text ? Kind.Text : camera ? Kind.Camera : Kind.Screen;
            lock (gate)
            {
                outbox.Enqueue((data, kind));
                outboxBytes += data.Length;
                if (video && outbox.Count > MaxVideoQueued)
                {
                    Drop(Kind.Screen);
                    result = Queued.VideoDropped;
                }
                if (kind == Kind.Camera && outbox.Count(m => m.Kind == Kind.Camera) > MaxCameraQueued)
                {
                    Drop(Kind.Camera);
                    result = Queued.CameraDropped;
                }
                if (outbox.Count > MaxQueued || outboxBytes > MaxQueuedBytes) result = Queued.Overflow;
                backlog = outbox.Count + (sending ? 1 : 0);
            }
            if (result != Queued.Overflow) Wake();
            return result;
        }

        // Back to JPEG (or over to the camera): the screen's video waiting goes; text (a setup, an
        // error, a pong) and the camera's messages stay.
        public void ClearOutbox()
        {
            lock (gate)
            {
                Drop(Kind.Screen);
                backlog = outbox.Count + (sending ? 1 : 0);
            }
        }

        // The camera closed: its pictures and sound still waiting go.
        public void DropCamera()
        {
            lock (gate)
            {
                Drop(Kind.Camera);
                backlog = outbox.Count + (sending ? 1 : 0);
            }
        }

        // Under gate.
        void Drop(Kind kind)
        {
            var keep = outbox.Where(m => m.Kind != kind).ToList();
            outbox.Clear();
            foreach (var m in keep) outbox.Enqueue(m);
            outboxBytes = keep.Sum(m => (long)m.Data.Length);
        }

        // For the tests: what waits, oldest first (true: text).
        internal List<bool> Waiting() { lock (gate) return outbox.Select(m => m.Kind == Kind.Text).ToList(); }

        // For the tests: how many camera messages wait.
        internal int CameraWaiting() { lock (gate) return outbox.Count(m => m.Kind == Kind.Camera); }

        // For the tests: screen pictures sent and not yet confirmed.
        internal int Unconfirmed { get { lock (gate) return unconfirmed; } }

        // The next message to send, or null (nothing waits, or the JPEG must wait for a
        // confirmation). Marks it as being sent.
        public (byte[] Data, bool Text)? Next()
        {
            lock (gate)
            {
                if (sending) return null;
                (byte[] Data, Kind Kind, bool Key) item;
                if (outbox.Count > 0)
                {
                    var next = outbox.Dequeue();
                    outboxBytes -= next.Data.Length;
                    backlog = outbox.Count + 1;
                    item = (next.Data, next.Kind, next.Kind == Kind.Screen && next.Data.Length > 0 && next.Data[0] == 1);
                }
                else if (pending != null)
                {
                    if (FlowControl)
                    {
                        bool full = unconfirmed >= 2;
                        // A page that stopped confirming (backgrounded) mustn't freeze the stream.
                        bool stuck = full && Now() - waitingSince > 3;
                        if (stuck) { unconfirmed = 0; sentAt.Clear(); }
                        if (full && !stuck) return null;
                    }
                    item = (pending, Kind.Screen, false);
                    pending = null;
                }
                else return null;
                sending = true;
                sendStarted = Now();
                // Only the screen's pictures are confirmed by the page (the camera's never are).
                if (item.Kind == Kind.Screen)
                {
                    if (unconfirmed == 0) waitingSince = Now();
                    unconfirmed++;
                    if (FlowControl) sentAt.Add((Now(), item.Data.Length, item.Key));
                }
                return (item.Data, item.Kind == Kind.Text);
            }
        }

        public void Sent()
        {
            lock (gate)
            {
                sending = false;
                lastProgress = Now();
                backlog = outbox.Count;
            }
        }

        // Sending something for `seconds` without finishing.
        public bool Stuck(double seconds)
        {
            lock (gate) return sending && Now() - Math.Max(lastProgress, sendStarted) > seconds;
        }

        // {"ack":1}: the oldest unconfirmed picture arrived. Returns it, with how much slower than
        // usual it was, for the bit rate.
        public (bool Key, double Extra)? Ack()
        {
            lock (gate)
            {
                unconfirmed = Math.Max(0, unconfirmed - 1);
                waitingSince = Now();
                if (sentAt.Count == 0) return null;
                var frame = sentAt[0];
                sentAt.RemoveAt(0);
                double seconds = Now() - frame.At;
                minDelay = Math.Min(minDelay + 0.002, seconds);
                return (frame.Key, seconds - minDelay);
            }
        }

        // May the next screen picture be encoded for this phone? (The Mac's Peer.videoReady.)
        public bool VideoReady()
        {
            lock (gate)
            {
                if (backlog >= 4) return false;  // the phone stopped reading
                if (sentAt.Count == 0) return true;
                double age = Now() - sentAt[0].At;
                if (age > 3) { unconfirmed = 0; sentAt.Clear(); return true; }  // the page stopped confirming
                return unconfirmed < 6 && age < minDelay + 0.15;
            }
        }
    }
}
