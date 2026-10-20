using System.Net.WebSockets;
using System.Text;
using LidAwake;
using Xunit;

// One phone's queue on the live socket: what waits is capped (a phone that floods replies, or
// stops reading, is closed), dropping video keeps text, and input is held to 240 a second.
public class LivePeerTests
{
    static LiveServer.Peer NewPeer() =>
        new(WebSocket.CreateFromStream(Stream.Null, isServer: true, subProtocol: null, keepAliveInterval: TimeSpan.Zero), "token", "iPhone");

    static byte[] Text(string s) => Encoding.UTF8.GetBytes(s);
    static byte[] Video(bool key) => new byte[] { (byte)(key ? 1 : 2), 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 4, 0x41, 1, 2, 3 };

    [Fact]
    public void AFloodOfRepliesIsCappedAndClosesTheSocket()
    {
        var peer = NewPeer();
        for (int i = 0; i < LiveServer.Peer.MaxQueued; i++)
            Assert.Equal(LiveServer.Peer.Queued.Ok, peer.Enqueue(Text("{\"pong\":true}"), text: true, video: false));
        Assert.Equal(LiveServer.Peer.Queued.Overflow, peer.Enqueue(Text("{\"pong\":true}"), text: true, video: false));
    }

    [Fact]
    public void TooManyBytesWaitingAlsoCloseIt()
    {
        var peer = NewPeer();
        var big = new byte[5 * 1024 * 1024];
        Assert.Equal(LiveServer.Peer.Queued.Ok, peer.Enqueue(big, text: false, video: false));
        Assert.Equal(LiveServer.Peer.Queued.Overflow, peer.Enqueue(big, text: false, video: false));
    }

    [Fact]
    public void BackToJpegDropsOnlyTheVideoPictures()
    {
        var peer = NewPeer();
        peer.Enqueue(Text("{\"video\":{}}"), text: true, video: true);
        peer.Enqueue(Video(true), text: false, video: true);
        peer.Enqueue(Text("{\"error\":\"Screen access is off.\"}"), text: true, video: false);
        peer.Enqueue(Video(false), text: false, video: true);
        peer.ClearOutbox();
        Assert.Equal(new[] { true, true }, peer.Waiting());
    }

    [Fact]
    public void TooMuchVideoWaitingIsDroppedButTextStays()
    {
        var peer = NewPeer();
        peer.Enqueue(Text("{\"video\":{}}"), text: true, video: true);
        var result = LiveServer.Peer.Queued.Ok;
        for (int i = 0; i < 100 && result == LiveServer.Peer.Queued.Ok; i++)
            result = peer.Enqueue(Video(i == 0), text: false, video: true);
        Assert.Equal(LiveServer.Peer.Queued.VideoDropped, result);
        Assert.Equal(new[] { true }, peer.Waiting());
    }
}
