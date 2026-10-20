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
// Kept in the roaming settings as `remoteScreen`, the Mac's name for it (the control task reads
// the same key).
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
// quality and video choices come in as JSON text. Input and camera messages are not acted on yet.
public sealed class LiveServer : IDisposable
{
    public const int DefaultPort = 8788;
    public const int MaxSockets = 8;
    public const int MaxMessage = 64 * 1024;
    public const string WakingText = "Waking the PC's screen… This usually takes a few seconds.";
    public const string ScreenOffText = "Screen access is off.";

    readonly Auth auth;
    readonly Tailnet tailnet;
    readonly Func<bool> isOn;
    readonly Func<bool> screenAllowed;
    readonly ScreenFeed feed;
    readonly AccessLog? log;
    readonly int port;
    readonly object gate = new();
    readonly Dictionary<Guid, Peer> peers = new();
    volatile WebApplication? app;
    System.Threading.Timer? housekeeping;

    public LiveServer(Auth auth, Tailnet tailnet, Func<bool> isOn, Func<bool> screenAllowed, ScreenFeed feed,
                      int port = DefaultPort, AccessLog? log = null)
    {
        (this.auth, this.tailnet, this.isOn, this.screenAllowed, this.feed) = (auth, tailnet, isOn, screenAllowed, feed);
        this.port = port;
        this.log = log;
    }

    public int Port => port;

    // True after Start succeeded, until Stop or Dispose. Serve maps /live only while it is.
    public bool IsRunning => app != null;

    // Phones watching right now, and a signal whenever that number changes (from the socket's
    // threads). The lid-screen task keeps the display on while it is above 0.
    public int Viewers { get { lock (gate) return peers.Count; } }
    public event Action<int>? ViewersChanged;

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
    // reconnects, and is refused until both are on again.
    public void CloseAll()
    {
        List<Peer> all;
        lock (gate) all = peers.Values.ToList();
        foreach (var p in all) p.Abort();
    }

    // Every 2 seconds, as on the Mac: off or not allowed closes everything; a signed-out or
    // expired session closes its sockets; a socket that sent nothing for 15 seconds while it had
    // something to send (the phone stopped reading) closes.
    void Housekeeping()
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
        var token = WebServer.CookieTokens(req).FirstOrDefault(SessionValid);
        if (token == null) return null;
        if (!Allowed()) return null;
        return token;
    }

    // ----- One phone -----

    async Task Serve(Peer peer)
    {
        int count;
        lock (gate) { peers[peer.Id] = peer; count = peers.Count; }
        Changed(count);
        LogQuietly("Screen viewing started · " + peer.Device);
        var sender = Task.Run(() => SendLoop(peer));
        try
        {
            feed.Subscribe(peer.Id,
                jpeg => { peer.SetPending(jpeg); },
                () => SendText(peer, new { error = WakingText }));
            await ReceiveLoop(peer);
        }
        catch (Exception) { }
        finally
        {
            peer.Abort();
            feed.Unsubscribe(peer.Id);
            lock (gate) { peers.Remove(peer.Id); count = peers.Count; }
            Changed(count);
            LogQuietly("Screen viewing ended · " + peer.Device);
            try { await sender; }
            catch (Exception) { }
            peer.Release();
        }
    }

    void Changed(int count)
    {
        try { ViewersChanged?.Invoke(count); }
        catch (Exception) { }
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
            // The camera comes with a later task: its messages are ignored for now.
            if (Bool(m, "camera") != null || m.TryGetProperty("cameraKey", out _) || m.TryGetProperty("talk", out _)) return;
            if (Bool(m, "video") is { } wantsVideo && wantsVideo != peer.Video)
            {
                peer.Video = wantsVideo;
                // A JPEG still waiting is old news once video starts; video waiting is dropped on
                // the way back to JPEG.
                if (wantsVideo) peer.SetPending(null);
                feed.SetVideo(peer.Id, wantsVideo ? new VideoSink(packet => Enqueue(peer, packet), peer.VideoReady) : null);
                if (!wantsVideo) peer.ClearOutbox();
            }
            if (m.TryGetProperty("videoRestart", out _))
            {
                feed.RestartVideo(peer.Id);
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

    // Everything else is an input command (docs/API.md 7.6 and 8). This is the one place they
    // arrive: the control task connects its input sink here. Until then only `ping` is answered
    // (after the screen check); clicks, typing and scrolling do nothing.
    void HandOffInput(Peer peer, JsonElement message)
    {
        // At most 240 a second per socket (7.6); extra ones get nothing, not even an answer.
        if (!peer.InputRate.TryTake(Environment.TickCount64)) return;
        if (!Allowed()) { SendText(peer, new { error = ScreenOffText }); return; }
        if (message.TryGetProperty("type", out var type) && type.ValueKind == JsonValueKind.String && type.GetString() == "ping")
        {
            SendText(peer, new { pong = true });
            return;
        }
        // Input commands: not carried out yet. Task 23 plugs its IInputSink in here (after its
        // control-switch and permission checks), handing it `message`.
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

    // At most 240 input messages a second on one socket (7.6), counted per whole second as on
    // the Mac. A stand-in with the same TryTake as phase 3's InputRateLimiter (RemoteInput.cs,
    // PerSecond = 240), which replaces it when the control task is merged.
    internal sealed class InputRate
    {
        public const int PerSecond = 240;
        long windowStart = long.MinValue / 2;
        int count;

        public bool TryTake(long nowMs)
        {
            if (nowMs - windowStart >= 1000) { windowStart = nowMs; count = 0; }
            return ++count <= PerSecond;
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

        public enum Queued { Ok, VideoDropped, Overflow }

        public readonly Guid Id = Guid.NewGuid();
        public readonly InputRate InputRate = new();
        public readonly WebSocket Socket;
        public readonly string Session;  // the session token the handshake used
        public readonly string Device;
        readonly CancellationTokenSource cancel = new();
        readonly SemaphoreSlim work = new(0, 1);
        readonly object gate = new();

        // Under gate.
        bool sending;
        byte[]? pending;                                   // JPEG: the newest picture waiting its turn
        readonly Queue<(byte[] Data, bool Text)> outbox = new();  // text and video, all in order
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

        // VideoDropped: more than 60 video messages waited and the binary ones were dropped (the
        // caller restarts from a keyframe). Overflow: too much waits in all; the caller closes
        // the socket.
        public Queued Enqueue(byte[] data, bool text, bool video)
        {
            var result = Queued.Ok;
            lock (gate)
            {
                outbox.Enqueue((data, text));
                outboxBytes += data.Length;
                if (video && outbox.Count > MaxVideoQueued)
                {
                    DropBinary();
                    result = Queued.VideoDropped;
                }
                if (outbox.Count > MaxQueued || outboxBytes > MaxQueuedBytes) result = Queued.Overflow;
                backlog = outbox.Count + (sending ? 1 : 0);
            }
            if (result != Queued.Overflow) Wake();
            return result;
        }

        // Back to JPEG: the video pictures waiting go; text (a setup, an error, a pong) stays.
        public void ClearOutbox()
        {
            lock (gate)
            {
                DropBinary();
                backlog = outbox.Count + (sending ? 1 : 0);
            }
        }

        // Under gate.
        void DropBinary()
        {
            var keep = outbox.Where(m => m.Text).ToList();
            outbox.Clear();
            foreach (var m in keep) outbox.Enqueue(m);
            outboxBytes = keep.Sum(m => (long)m.Data.Length);
        }

        // For the tests: what waits, oldest first (true: text).
        internal List<bool> Waiting() { lock (gate) return outbox.Select(m => m.Text).ToList(); }

        // The next message to send, or null (nothing waits, or the JPEG must wait for a
        // confirmation). Marks it as being sent.
        public (byte[] Data, bool Text)? Next()
        {
            lock (gate)
            {
                if (sending) return null;
                (byte[] Data, bool Text, bool Key) item;
                if (outbox.Count > 0)
                {
                    var next = outbox.Dequeue();
                    outboxBytes -= next.Data.Length;
                    backlog = outbox.Count + 1;
                    item = (next.Data, next.Text, !next.Text && next.Data.Length > 0 && next.Data[0] == 1);
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
                    item = (pending, false, false);
                    pending = null;
                }
                else return null;
                sending = true;
                sendStarted = Now();
                if (!item.Text)
                {
                    if (unconfirmed == 0) waitingSince = Now();
                    unconfirmed++;
                    if (FlowControl) sentAt.Add((Now(), item.Data.Length, item.Key));
                }
                return (item.Data, item.Text);
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
