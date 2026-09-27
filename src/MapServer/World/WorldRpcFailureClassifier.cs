using System.Net.Sockets;
using Orleans.Runtime;

namespace Athena.Net.MapServer.World;

// Step 6 final correctness pass, item 3: the SINGLE shared boundary classifying an exception caught
// around an IWorldRuntime call as one of exactly three categories - used identically by
// MapClientSession's own repeat-attack loop and MapTcpServer's own monster tick loop, so both apply
// the SAME narrow rule rather than each inventing its own "probably transient" guess.
//
// This is deliberately NOT a generic resilience framework - it is a closed `is` pattern match over
// the actual Orleans 10.3 client-side exception surface this project depends on
// (Microsoft.Orleans.Client 10.3.0), confirmed by direct inspection of Orleans.Core.Abstractions/
// Orleans.Core's own public exception types: Orleans.Runtime.SiloUnavailableException (no reachable
// silo/gateway), Orleans.Runtime.GatewayTooBusyException (client-side gateway backpressure),
// Orleans.Runtime.OrleansMessageRejectionException (the message itself was rejected in transit -
// e.g. a stale/broken connection), plus the ordinary BCL transport-shaped types every network client
// in this codebase already treats as transient (System.TimeoutException, System.Net.Sockets.
// SocketException, System.IO.IOException - the last one already gates every other Orleans RPC call
// site in this project, e.g. PollAndReconcileMapAsync's own `catch (IOException) { return; }`).
//
// Explicitly NOT included, and never added on a "probably fine" basis: InvalidOperationException
// (this codebase's OWN local invariant-violation type - see WorldMonsterActorView/
// NotifyMonsterMovedAsync's own mismatched-actor/combat-state guard - indistinguishable BY TYPE from
// an Orleans-internal one, so treating it as transient here would risk swallowing a real programming
// bug forever), NullReferenceException/ArgumentException/ArgumentOutOfRangeException (always a
// programming defect, never a legitimate transient network condition), KeyNotFoundException (this
// project's own established deterministic-invariant example - see
// MapTcpServer.IsDeterministicInvariantFailure's identical exclusion), and any CharServer/
// persistence-layer exception (a completely different subsystem from the World Orleans RPC boundary
// this classifier exists for - conflating the two would let an unrelated CharServer failure be
// silently mislabeled "World RPC transient").
public static class WorldRpcFailureClassifier
{
    // True only for the closed set of exception types known to represent a genuinely transient
    // Orleans transport/gateway/timeout condition - one that might plausibly succeed on a LATER
    // attempt with no code or configuration change. Callers must still separately check
    // OperationCanceledException against their OWN session/loop cancellation token before reaching
    // this method (a genuine shutdown cancellation is not a "failure" to classify at all) - this
    // method deliberately does not accept OperationCanceledException as an input concern of its own,
    // to keep the two decisions (shutdown vs. retry) visibly separate at each call site.
    public static bool IsTransientWorldRpcFailure(Exception ex) =>
        ex is TimeoutException
            or SocketException
            or IOException
            or SiloUnavailableException
            or GatewayTooBusyException
            or OrleansMessageRejectionException;
}
