using System.Runtime.CompilerServices;

namespace Athena.Net.MapServer.Tests;

// Test-only runtime configuration, entirely scoped to THIS assembly - never touches MapServer
// production startup, and MapServer itself never calls ThreadPool.SetMinThreads.
//
// Root cause this addresses (see ai/map-server.md's "GitHub Actions CI flake" entry for the full
// evidence trail): this suite contains many real-socket MapClientSession integration fixtures
// (TcpListener/TcpClient, background movement/status/attack loops, real Task.Delay timers), and
// under a genuinely constrained CI runner the CLR's default worker-thread minimum (== processor
// count) cannot absorb a full-suite burst of concurrent socket accepts/timer callbacks - the pool
// only injects new threads reactively, roughly one at a time over seconds, which intermittently
// starves an otherwise-correct async wait well past this suite's existing test-side socket-read
// timeouts. Reproduced empirically: a 2-CPU-constrained Linux run of the full Release suite
// flaked across several otherwise-unrelated fixtures; raising the worker-thread floor before any
// test runs cut the observed failure rate from roughly 40-60% to near zero under the identical
// constraint, while an unconstrained/default-parallelism run was unaffected either way.
internal static class MapServerTestRuntimeConfiguration
{
    [ModuleInitializer]
    internal static void RaiseWorkerThreadFloor()
    {
        ThreadPool.GetMinThreads(out var workerMin, out var ioMin);
        // Only ever raises the worker-thread minimum, never lowers it - an already-higher runtime
        // setting (e.g. from a future CLR default change) is left alone. The I/O completion-port
        // minimum is passed through unchanged; the observed starvation is worker-thread scheduling
        // for the many async continuations/timer callbacks, not I/O completion delivery itself.
        ThreadPool.SetMinThreads(Math.Max(workerMin, 16), ioMin);
    }
}
