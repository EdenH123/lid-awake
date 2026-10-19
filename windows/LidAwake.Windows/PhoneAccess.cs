namespace LidAwake;

// Whether the phone can reach the page through Tailscale Serve. Serve is turned on only while
// our own web server holds its port: if another program has taken 127.0.0.1:8787, Serve would
// publish that program to the owner's phone, and the phone would send it the password and the
// session cookie.
public sealed class PhoneAccess
{
    readonly Tailnet tailnet;
    readonly WebServer server;

    public PhoneAccess(Tailnet tailnet, WebServer server) => (this.tailnet, this.server) = (tailnet, server);

    // False when the phone page is not running (its port was taken by another program).
    public bool PageRunning => server.IsRunning;

    public int Port => server.Port;

    // On: serve the page if our server is running, otherwise make sure nothing is served.
    // Off: remove everything. Both go through Tailnet's one-at-a-time path; the task ends when
    // this call has been applied.
    public Task Set(bool on) => tailnet.SetServed(on && server.IsRunning, server.Port);
}
