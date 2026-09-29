using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;

namespace Athena.Net.World.Tests;

// Step 7 substep 11: exercises WorldMonsterMapSimulation DIRECTLY (never through a grain RPC) -
// this is the memory-bound proof the approved substep-11 plan requires for the AttackSequence
// ledger: that _attackSequences/_attackSequencesByPresence stay bounded by CURRENTLY-live
// attackers across many kill/respawn cycles of the SAME ActorId, never growing with historical
// incarnation count. Reaches WorldMonsterMapSimulation's own internal surface via
// InternalsVisibleTo("World.Tests") (Athena.World's Properties/AssemblyInfo.cs) - no reflection,
// no Orleans-contract exposure of the underlying dictionaries. It is deliberately NOT sufficient
// to prove only that an old command returns StaleLifeReference (that proves correctness, not
// memory-boundedness) - this file asserts the actual ledger counts after every single cycle.
public sealed class WorldMonsterMapSimulationMemoryBoundTests
{
    private const uint CanAttackMode = 0x0000080; // MobMode.CanAttack
    private const ushort MonsterX = 100;
    private const ushort MonsterY = 100;
    private const int RespawnDelayMs = 50; // Short, deterministic - this file drives time explicitly, never sleeps.

    private static MapCollisionMap MakeAllWalkableMap(string name, int side = 50) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    private static WorldMonsterSpawnDefinition Spawn(string mapId) =>
        new(MobId: 1002, mapId, X: MonsterX, Y: MonsterY, Xs: 1, Ys: 1, Count: 1, RespawnDelayMs: RespawnDelayMs, RespawnRandomDelayMs: 0,
            SpawnName: "Poring", WalkSpeedMs: 400, AttackRange: 1, MaxHp: 55, Mode: CanAttackMode);

    // Exercises AT LEAST 25 logical incarnations of the SAME ActorId, against one attacker
    // (CharacterId, PresenceId) pair, asserting after EVERY cycle that the ledger's own counts
    // never exceed the small, bounded envelope the approved plan specifies - never merely that
    // the sequence increases monotonically, which would be true even under an unbounded leak.
    //
    // Real wall-clock time (TimeProvider.System), matching this project's own established
    // integration-test style for respawn timing (see WorldMonsterSimulationTests.cs's identical
    // Task.Delay-paced polling against the real grain timer) - MonsterRegistry.ScheduleRespawnIfNeeded/
    // ProcessDueRespawns read time from the TimeProvider passed to Rebuild, not from Tick's own `now`
    // parameter, so a fake/manually-advanced clock here would never actually make a respawn become
    // due. Bounded polling only, never an arbitrary sleep used FOR ordering.
    [Fact]
    public async Task AttackSequenceLedger_ManyKillRespawnCycles_StaysBoundedByLiveAttackers_NeverGrowsHistorically()
    {
        var mapId = "izlude";
        var collisionProvider = new MapCollisionProvider([MakeAllWalkableMap(mapId)]);
        var movementPathProvider = new UnverifiedGridLineMovementPathProvider();
        var simulation = new WorldMonsterMapSimulation(mapId, DateTimeOffset.UtcNow);

        var nextActorId = 1u;
        simulation.Rebuild([Spawn(mapId)], fingerprint: "test", allocateActorId: () => nextActorId++, TimeProvider.System, collisionProvider, movementPathProvider);

        Assert.True(simulation.TryFind(1u, out var initialInstance));
        var actorId = initialInstance.ActorId;

        var attackerCharacterId = 900u;
        var attackerPresenceId = Guid.NewGuid();
        var attackSequence = 0L;

        // Before any attack has ever happened, the ledger is genuinely empty - the baseline every
        // subsequent cycle-boundary assertion below is measured against.
        Assert.Equal(0, simulation.AttackSequenceCountForTest);
        Assert.Equal(0, simulation.AttackSequencePresenceBucketCountForTest);

        var maxAttackSequenceCount = 0;
        var maxPresenceBucketCount = 0;

        const int cycles = 27; // >= 25 per the approved plan.
        for (var cycle = 0; cycle < cycles; cycle++)
        {
            Assert.True(simulation.TryFind(actorId, out var instance), $"Cycle {cycle}: expected the same ActorId to still resolve.");
            Assert.True(instance.IsAlive, $"Cycle {cycle}: expected the current incarnation to be Alive before attacking.");
            var oldIncarnationValue = instance.IncarnationId.Value;
            var life = new WorldMonsterLifeReference(mapId, simulation.SimulationEpoch, actorId, new WorldMonsterIncarnationId(oldIncarnationValue));

            // One accepted, genuinely lethal attack per cycle - Poring's 55 MaxHp in one hit.
            attackSequence++;
            var command = new WorldMonsterDamageCommand(life, attackerCharacterId, attackerPresenceId, attackSequence, Damage: 55, AcquireEngagement: false);
            var replay = simulation.TryAcceptAttackSequence(command);
            Assert.Null(replay); // A genuinely new sequence for this (CharacterId, PresenceId, Life) key.
            var attackAction = new WorldPlayerAttackAction(attackerCharacterId, attackerCharacterId, attackerPresenceId, command.Damage, 0, 0, Hit: true, Lethal: false);
            var (hpBefore, hpAfter, killedByThisHit, maxHp) = simulation.ApplyDamage(instance, command.Damage, attackAction);
            Assert.True(killedByThisHit, $"Cycle {cycle}: expected this exact hit to be lethal.");
            Assert.Equal(0u, hpAfter);
            var result = new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, hpBefore, hpAfter, maxHp, killedByThisHit, null);
            simulation.RecordAttackSequenceResult(command, result);

            // Immediately after the accepted attack: at most ONE current attack-sequence entry for
            // this Life, and at most ONE corresponding presence-bucket entry - never more.
            Assert.Equal(1, simulation.AttackSequenceCountForTest);
            Assert.Equal(1, simulation.AttackSequencePresenceBucketCountForTest);
            maxAttackSequenceCount = Math.Max(maxAttackSequenceCount, simulation.AttackSequenceCountForTest);
            maxPresenceBucketCount = Math.Max(maxPresenceBucketCount, simulation.AttackSequencePresenceBucketCountForTest);

            // Poll the real wall clock (Tick's own ProcessDueRespawns) until the SAME ActorId
            // reports a NEW IncarnationId - state-driven, never an assumed tick count.
            WorldMonsterInstance? respawnedWire = null;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
            while (respawnedWire is null && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
                simulation.Tick(DateTimeOffset.UtcNow, resolvePresence: static _ => null, isWalking: static _ => false);
                if (simulation.TryFind(actorId, out var candidate) && candidate.IsAlive && candidate.IncarnationId.Value != oldIncarnationValue)
                    respawnedWire = simulation.ToWireInstance(candidate);
            }
            Assert.NotNull(respawnedWire);
            Assert.Equal(actorId, respawnedWire!.ActorId); // Same ActorId/spawn point across every cycle.
            Assert.NotEqual(oldIncarnationValue, respawnedWire.IncarnationId.Value); // Genuinely new incarnation.
            Assert.Equal(respawnedWire.MaxHp, respawnedWire.CurrentHp); // Full HP on the new incarnation.

            // OnRespawnObserved (invoked by Tick's own ProcessDueRespawns loop) must have already
            // removed the now-permanently-unreachable OLD incarnation's ledger entry - immediately
            // after respawn cleanup, and BEFORE the next cycle's attack, the ledger must be
            // genuinely EMPTY, not merely "not yet grown". This is the load-bearing proof against
            // historical growth (1, 2, 3, 4, ... would fail this exact assertion on cycle 2).
            Assert.Equal(0, simulation.AttackSequenceCountForTest);
            Assert.Equal(0, simulation.AttackSequencePresenceBucketCountForTest);
        }

        // Across all cycles, the maximum observed count never exceeded the bounded envelope - it
        // did not grow with historical incarnation count.
        Assert.Equal(1, maxAttackSequenceCount);
        Assert.Equal(1, maxPresenceBucketCount);
    }
}
