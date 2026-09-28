using Athena.Net.MapServer.Net;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Deterministic, timing-free coverage of the primitive itself (see its own doc comment for the exact
// live multiplayer race it closes) - no Task.Delay, no sleeps: every assertion is driven by real
// completion state.
public sealed class LethalAttackProjectionGateTests
{
    private static WorldMonsterLifeReference Life(uint actorId = 1) =>
        new("prt_fild08", WorldSimulationEpoch.NewEpoch(), actorId, WorldMonsterIncarnationId.First);

    [Fact]
    public void WaitAsync_WithNoOpenGate_CompletesImmediately()
    {
        var gate = new LethalAttackProjectionGate();
        Assert.True(gate.WaitAsync(Life()).IsCompletedSuccessfully);
    }

    [Fact]
    public void WaitAsync_WhileOpen_DoesNotComplete_ThenCompletesTheInstantExitIsCalled()
    {
        var gate = new LethalAttackProjectionGate();
        var life = Life();

        gate.Enter(life);
        var waiter = gate.WaitAsync(life);
        Assert.False(waiter.IsCompleted);

        gate.Exit(life);
        Assert.True(waiter.IsCompletedSuccessfully);
    }

    [Fact]
    public void Exit_WithNoOpenGate_IsASilentNoOp()
    {
        var gate = new LethalAttackProjectionGate();
        gate.Exit(Life()); // Never entered - must not throw.
        gate.Enter(Life());
        gate.Exit(Life());
        gate.Exit(Life()); // Second Exit for the same (already-closed) life - must not throw.
    }

    [Fact]
    public void Enter_IsIdempotent_TheOriginalWaiterStillCompletesOnTheFirstExit()
    {
        var gate = new LethalAttackProjectionGate();
        var life = Life();

        gate.Enter(life);
        var waiter = gate.WaitAsync(life);
        gate.Enter(life); // Redundant re-Enter (e.g. a retry re-dispatch) must not replace the open gate.
        Assert.False(waiter.IsCompleted);

        gate.Exit(life);
        Assert.True(waiter.IsCompletedSuccessfully);
    }

    [Fact]
    public void DifferentLives_AreNeverSerializedAgainstEachOther()
    {
        var gate = new LethalAttackProjectionGate();
        var lifeA = Life(1);
        var lifeB = Life(2);

        gate.Enter(lifeA);
        var waiterA = gate.WaitAsync(lifeA);
        var waiterB = gate.WaitAsync(lifeB); // Never entered - must be immediately complete regardless of lifeA's own open gate.

        Assert.False(waiterA.IsCompleted);
        Assert.True(waiterB.IsCompletedSuccessfully);

        gate.Exit(lifeA);
        Assert.True(waiterA.IsCompletedSuccessfully);
    }

    [Fact]
    public void EnterThenExit_NeverLeaksAnEntry()
    {
        var gate = new LethalAttackProjectionGate();
        var life = Life();
        gate.Enter(life);
        Assert.Equal(1, gate.OpenCountForTest);
        gate.Exit(life);
        Assert.Equal(0, gate.OpenCountForTest);
    }
}
