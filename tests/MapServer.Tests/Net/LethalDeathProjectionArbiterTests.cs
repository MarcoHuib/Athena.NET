using Athena.Net.MapServer.Net;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 7 substep 6 (§14 of the World-monster-authority migration plan): isolated, in-memory
// coverage of LethalDeathProjectionArbiter's terminal-state extension - the `_alreadyProjected` set
// and the resulting three-way TryDeferDiedWhileInFlight behavior (defer / suppress / ordinary).
// Deliberately exercises the arbiter directly (it is internal, InternalsVisibleTo'd to this test
// assembly) rather than only through a full socket-backed MapClientSession, so every branch/edge
// case can be proven without needing to drive an actual attack sequence for each one - the
// session-level integration is already covered by MapClientSessionLethalDeathProjectionRaceTests.
public sealed class LethalDeathProjectionArbiterTests
{
    private static WorldMonsterLifeReference Life(string mapId, WorldSimulationEpoch epoch, uint actorId, WorldMonsterIncarnationId incarnation) =>
        new(mapId, epoch, actorId, incarnation);

    private static WorldSimulationEpoch Epoch() => WorldSimulationEpoch.NewEpoch();

    // A. Ordinary: no in-flight, no already-projected -> false.
    [Fact]
    public void TryDeferDiedWhileInFlight_NoInFlightNoProjected_ReturnsFalse()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var life = Life("int_land03", Epoch(), 1, WorldMonsterIncarnationId.First);

        Assert.False(arbiter.TryDeferDiedWhileInFlight(life));
    }

    // B. In-flight defer: BeginInFlight -> TryDefer == true -> CompleteInFlight(false) == true.
    [Fact]
    public void TryDeferDiedWhileInFlight_InFlight_DefersAndReportsObservedOnCompletion()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var life = Life("int_land03", Epoch(), 1, WorldMonsterIncarnationId.First);

        arbiter.BeginInFlight(life);
        Assert.True(arbiter.TryDeferDiedWhileInFlight(life));
        Assert.True(arbiter.CompleteInFlight(life, markProjected: false));
    }

    // C. In-flight, no Died observed: CompleteInFlight(false) == false, and a LATER TryDefer for the
    // same (now-retired, not-projected) life is ordinary (false) - the registration is genuinely
    // gone, not merely resolved.
    [Fact]
    public void CompleteInFlight_NoDiedObserved_ReturnsFalse_AndLaterDeferIsOrdinary()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var life = Life("int_land03", Epoch(), 1, WorldMonsterIncarnationId.First);

        arbiter.BeginInFlight(life);
        Assert.False(arbiter.CompleteInFlight(life, markProjected: false));
        Assert.False(arbiter.TryDeferDiedWhileInFlight(life));
    }

    // D. Successful direct projection, then a LATE Died: CompleteInFlight(true) == false (no Died was
    // observed WHILE pending), but every subsequent TryDefer for this exact life - repeated any
    // number of times - must return true (suppress), since the session already projected its own
    // authoritative death.
    [Fact]
    public void CompleteInFlight_MarkProjectedTrue_SuppressesRepeatedLaterDied()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var life = Life("int_land03", Epoch(), 1, WorldMonsterIncarnationId.First);

        arbiter.BeginInFlight(life);
        Assert.False(arbiter.CompleteInFlight(life, markProjected: true));

        Assert.True(arbiter.TryDeferDiedWhileInFlight(life));
        Assert.True(arbiter.TryDeferDiedWhileInFlight(life)); // Repeated - still suppressed, no state consumed.
        Assert.True(arbiter.TryDeferDiedWhileInFlight(life));
    }

    // E. Died observed during flight, then the session STILL completes with its own successful
    // direct projection (markProjected: true): CompleteInFlight itself reports true (a Died WAS
    // observed while pending - the caller must still perform its OWN deferred handling for that
    // signal), and separately, the `_alreadyProjected` marker this call also wrote means a LATER,
    // independent Died for this exact life is likewise suppressed.
    [Fact]
    public void DiedDuringFlight_ThenMarkProjectedTrue_CompletionReportsObserved_AndLaterDiedStillSuppressed()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var life = Life("int_land03", Epoch(), 1, WorldMonsterIncarnationId.First);

        arbiter.BeginInFlight(life);
        Assert.True(arbiter.TryDeferDiedWhileInFlight(life));
        Assert.True(arbiter.CompleteInFlight(life, markProjected: true));

        Assert.True(arbiter.TryDeferDiedWhileInFlight(life));
    }

    // F. markProjected:false does not suppress a later Died - the life is genuinely gone from both
    // sets after this retirement, so a later Died for it is treated as an ordinary (non-suppressed)
    // case, exactly like test C.
    [Fact]
    public void CompleteInFlight_MarkProjectedFalse_DoesNotSuppressLaterDied()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var life = Life("int_land03", Epoch(), 1, WorldMonsterIncarnationId.First);

        arbiter.BeginInFlight(life);
        arbiter.CompleteInFlight(life, markProjected: false);

        Assert.False(arbiter.TryDeferDiedWhileInFlight(life));
    }

    // G. Exact-life isolation: marking life A (ActorId=1, IncarnationId=First) as projected must NOT
    // affect a DIFFERENT life B sharing the same ActorId but a different IncarnationId - a Died for B
    // is an ordinary case, never suppressed by A's own projected marker.
    [Fact]
    public void ForgetProjected_ExactLifeIsolation_DifferentIncarnationSameActorId_NotSuppressed()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var mapId = "int_land03";
        var epoch = Epoch();
        var lifeA = Life(mapId, epoch, actorId: 1, WorldMonsterIncarnationId.First);
        var lifeB = Life(mapId, epoch, actorId: 1, WorldMonsterIncarnationId.First.Next());

        arbiter.BeginInFlight(lifeA);
        arbiter.CompleteInFlight(lifeA, markProjected: true);

        Assert.False(arbiter.TryDeferDiedWhileInFlight(lifeB));
    }

    // G (continued): isolation across MapId - same ActorId/Incarnation/Epoch, different MapId.
    [Fact]
    public void ExactLifeIsolation_DifferentMapId_NotSuppressed()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var epoch = Epoch();
        var incarnation = WorldMonsterIncarnationId.First;
        var lifeA = Life("int_land03", epoch, actorId: 1, incarnation);
        var lifeOnDifferentMap = Life("int_land04", epoch, actorId: 1, incarnation);

        arbiter.BeginInFlight(lifeA);
        arbiter.CompleteInFlight(lifeA, markProjected: true);

        Assert.False(arbiter.TryDeferDiedWhileInFlight(lifeOnDifferentMap));
    }

    // G (continued): isolation across SimulationEpoch - same MapId/ActorId/Incarnation, different epoch.
    [Fact]
    public void ExactLifeIsolation_DifferentEpoch_NotSuppressed()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var mapId = "int_land03";
        var incarnation = WorldMonsterIncarnationId.First;
        var lifeA = Life(mapId, Epoch(), actorId: 1, incarnation);
        var lifeUnderDifferentEpoch = Life(mapId, Epoch(), actorId: 1, incarnation);

        arbiter.BeginInFlight(lifeA);
        arbiter.CompleteInFlight(lifeA, markProjected: true);

        Assert.False(arbiter.TryDeferDiedWhileInFlight(lifeUnderDifferentEpoch));
    }

    // G (continued): isolation across ActorId - same MapId/Epoch/Incarnation, different ActorId.
    [Fact]
    public void ExactLifeIsolation_DifferentActorId_NotSuppressed()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var mapId = "int_land03";
        var epoch = Epoch();
        var incarnation = WorldMonsterIncarnationId.First;
        var lifeA = Life(mapId, epoch, actorId: 1, incarnation);
        var lifeDifferentActor = Life(mapId, epoch, actorId: 2, incarnation);

        arbiter.BeginInFlight(lifeA);
        arbiter.CompleteInFlight(lifeA, markProjected: true);

        Assert.False(arbiter.TryDeferDiedWhileInFlight(lifeDifferentActor));
    }

    // H. Absent completion cannot manufacture projected state: calling CompleteInFlight(markProjected:
    // true) against a life with NO in-flight registration must be a pure no-op - it must not add the
    // life to `_alreadyProjected` merely because the caller asked for markProjected:true.
    [Fact]
    public void CompleteInFlight_MarkProjectedTrue_WithoutInFlightEntry_DoesNotManufactureProjectedState()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var life = Life("int_land03", Epoch(), 1, WorldMonsterIncarnationId.First);

        // No BeginInFlight call at all.
        Assert.False(arbiter.CompleteInFlight(life, markProjected: true));
        Assert.False(arbiter.TryDeferDiedWhileInFlight(life));
    }

    // I. ForgetProjectedForActor: removes stale incarnations for the same map+epoch+actor, preserves
    // exceptIncarnationId, and preserves unrelated actor/map/epoch entries untouched.
    [Fact]
    public void ForgetProjectedForActor_RemovesStaleIncarnations_PreservesExceptedAndUnrelatedEntries()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var mapId = "int_land03";
        var epoch = Epoch();
        var oldIncarnation = WorldMonsterIncarnationId.First;
        var currentIncarnation = oldIncarnation.Next();

        var staleLife = Life(mapId, epoch, actorId: 1, oldIncarnation);
        var currentLife = Life(mapId, epoch, actorId: 1, currentIncarnation);
        var unrelatedActorLife = Life(mapId, epoch, actorId: 2, oldIncarnation);
        var unrelatedMapLife = Life("int_land04", epoch, actorId: 1, oldIncarnation);
        var unrelatedEpochLife = Life(mapId, Epoch(), actorId: 1, oldIncarnation);

        foreach (var life in new[] { staleLife, currentLife, unrelatedActorLife, unrelatedMapLife, unrelatedEpochLife })
        {
            arbiter.BeginInFlight(life);
            arbiter.CompleteInFlight(life, markProjected: true);
        }

        // Sanity: every one of them is currently suppressed before the sweep.
        Assert.True(arbiter.TryDeferDiedWhileInFlight(staleLife));
        Assert.True(arbiter.TryDeferDiedWhileInFlight(currentLife));
        Assert.True(arbiter.TryDeferDiedWhileInFlight(unrelatedActorLife));
        Assert.True(arbiter.TryDeferDiedWhileInFlight(unrelatedMapLife));
        Assert.True(arbiter.TryDeferDiedWhileInFlight(unrelatedEpochLife));

        arbiter.ForgetProjectedForActor(mapId, epoch, actorId: 1, exceptIncarnationId: currentIncarnation);

        // The stale (same map+epoch+actor, different incarnation) entry is gone.
        Assert.False(arbiter.TryDeferDiedWhileInFlight(staleLife));
        // The excepted incarnation is preserved.
        Assert.True(arbiter.TryDeferDiedWhileInFlight(currentLife));
        // Every unrelated entry (different actor/map/epoch) is preserved untouched.
        Assert.True(arbiter.TryDeferDiedWhileInFlight(unrelatedActorLife));
        Assert.True(arbiter.TryDeferDiedWhileInFlight(unrelatedMapLife));
        Assert.True(arbiter.TryDeferDiedWhileInFlight(unrelatedEpochLife));
    }

    // J. Multi-respawn backlog: incarnations 1 and 2 were both marked projected (e.g. this session's
    // own arbiter fell behind two respawns before ever observing a Respawned entry), incarnation 3 is
    // the current life. A single ForgetProjectedForActor(except: 3) call must clear BOTH stale
    // incarnations 1 and 2 in one sweep - not just the immediately-prior one - while incarnation 3's
    // own projected entry (if it has one) remains untouched.
    [Fact]
    public void ForgetProjectedForActor_MultiRespawnBacklog_ClearsAllStaleIncarnations_PreservesCurrent()
    {
        var arbiter = new LethalDeathProjectionArbiter();
        var mapId = "int_land03";
        var epoch = Epoch();
        var incarnation1 = WorldMonsterIncarnationId.First;
        var incarnation2 = incarnation1.Next();
        var incarnation3 = incarnation2.Next();

        var life1 = Life(mapId, epoch, actorId: 1, incarnation1);
        var life2 = Life(mapId, epoch, actorId: 1, incarnation2);
        var life3 = Life(mapId, epoch, actorId: 1, incarnation3);

        foreach (var life in new[] { life1, life2, life3 })
        {
            arbiter.BeginInFlight(life);
            arbiter.CompleteInFlight(life, markProjected: true);
        }

        arbiter.ForgetProjectedForActor(mapId, epoch, actorId: 1, exceptIncarnationId: incarnation3);

        // Late Died for incarnation 1 and incarnation 2 are both now ordinary (not suppressed).
        Assert.False(arbiter.TryDeferDiedWhileInFlight(life1));
        Assert.False(arbiter.TryDeferDiedWhileInFlight(life2));
        // Incarnation 3's own projected entry remains untouched.
        Assert.True(arbiter.TryDeferDiedWhileInFlight(life3));
    }
}
