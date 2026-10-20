using System.Runtime.CompilerServices;

// Waiting for background work in the tests.
//
// Many tests hold a thread-pool thread on purpose (a fake Tailscale, camera or display-off that
// hangs until the test lets it go), and the app holds some too (WebServer.Start and Stop wait for
// Kestrel). With the pool's usual minimum, one thread per core, other background work then waits
// for the pool to add threads, only one or two a second, and a test that gave its background work
// 5 seconds could run out of time on a busy runner. Enough threads from the start keep it prompt.
static class TestThreads
{
#pragma warning disable CA2255 // the test assembly is the program here: this runs before any test
    [ModuleInitializer]
#pragma warning restore CA2255
    internal static void Start()
    {
        ThreadPool.GetMinThreads(out var workers, out var io);
        ThreadPool.SetMinThreads(Math.Max(workers, 64), Math.Max(io, 64));
    }

    // How long a test waits for background work that should happen at once. Only a failing test
    // waits this long.
    public static readonly TimeSpan Patience = TimeSpan.FromSeconds(30);

    // Polls `condition` without holding a thread while it waits. False if it never came true.
    public static async Task<bool> Until(Func<bool> condition, TimeSpan? within = null)
    {
        var deadline = DateTime.UtcNow + (within ?? Patience);
        while (!condition())
        {
            if (DateTime.UtcNow >= deadline) return false;
            await Task.Delay(10);
        }
        return true;
    }
}
