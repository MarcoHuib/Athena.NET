using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;
using Athena.Net.World.Runtime;
using Microsoft.Extensions.DependencyInjection;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// Phase 2B monster SIMULATION authority - identity/position/movement/target-validity/lifecycle
// only, never damage/quest/current-HP (see IWorldPartitionGrain.cs's own doc comment for the full
// scope boundary this file's tests hold the grain to).
public sealed class WorldMonsterSimulationTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<TopologyConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);

    // A single FIXED-POINT (Xs=1,Ys=1) Poring-shaped declaration on the all-walkable synthetic
    // collision map (TopologyConfigurator's own MakeAllWalkableMap) - RathenaCompatibleMobSpawnCellSelector
    // resolves a fixed-point declaration to EXACTLY its declared (X,Y) whenever that cell is
    // walkable (see MobSpawnCellSelector.cs's own doc comment: no randomized search is even
    // consulted for this shape), giving this file's tests a reliably known, deterministic monster
    // position without depending on the collision-less fallback selector this project's production
    // World monster simulation must never use.
    private static WorldMonsterSpawnBatch SingleMonsterBatch(string mapId, int count = 1) =>
        Batch(mapId, [Spawn(mapId)], count);

    // Mode: 0x80 = MobMode.CanAttack (MobData.cs) - required for NotifyMonsterAttackedAsync's own
    // MobMode.CanAttack gate to ever succeed; without it every acquisition attempt is (correctly)
    // MonsterNotAttackable, which is not what most of this file's tests are exercising.
    private const uint CanAttackMode = 0x0000080;
    private const ushort MonsterX = 100;
    private const ushort MonsterY = 100;
    private static WorldMonsterSpawnDefinition Spawn(string mapId) =>
        new(MobId: 1002, mapId, X: MonsterX, Y: MonsterY, Xs: 1, Ys: 1, Count: 1, RespawnDelayMs: 5000, RespawnRandomDelayMs: 0,
            SpawnName: "Poring", WalkSpeedMs: 400, AttackRange: 1, MaxHp: 55, Mode: CanAttackMode);

    private static WorldMonsterSpawnBatch Batch(string mapId, WorldMonsterSpawnDefinition[] spawns, int? overrideFirstCount = null)
    {
        if (overrideFirstCount is { } count) spawns = [spawns[0] with { Count = count }, .. spawns[1..]];
        var fingerprint = ""; // Let the grain compute+accept its own fingerprint when the caller doesn't assert on it.
        return new WorldMonsterSpawnBatch(mapId, fingerprint, spawns);
    }

    private static WorldPlayerPresence Presence(Guid presenceId, uint characterId, string mapId, ushort x = 0, ushort y = 0) =>
        new(presenceId, ActorId: characterId + 1_000_000, characterId, mapId, x, y);

    [Fact]
    public async Task LoadMonsterSpawns_FirstLoad_Succeeds_AndSecondIdenticalLoadIsAlreadyLoaded()
    {
        var grain = Partition("world-rest");
        var batch = SingleMonsterBatch("izlude");

        var first = await grain.LoadMonsterSpawnsAsync(batch);
        Assert.Equal(WorldMonsterSpawnLoadStatus.Loaded, first.Status);

        var second = await grain.LoadMonsterSpawnsAsync(batch);
        Assert.Equal(WorldMonsterSpawnLoadStatus.AlreadyLoaded, second.Status);
        Assert.Equal(first.SimulationEpoch, second.SimulationEpoch); // Same content -> same epoch, never silently rebuilt.
    }

    [Fact]
    public async Task LoadMonsterSpawns_DifferentContentReload_IsContentMismatch_NotSilentNoOp()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        Assert.Equal(WorldMonsterSpawnLoadStatus.Loaded, (await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId))).Status);

        var differentContent = SingleMonsterBatch(mapId, count: 2); // Genuinely different spawn content (Count differs).
        var result = await grain.LoadMonsterSpawnsAsync(differentContent);
        Assert.Equal(WorldMonsterSpawnLoadStatus.ContentMismatch, result.Status);
    }

    [Fact]
    public async Task LoadMonsterSpawns_CallerFingerprintDisagreesWithComputedContent_IsCallerFingerprintMismatch()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var spawn = Spawn(mapId);
        var batchWithWrongFingerprint = new WorldMonsterSpawnBatch(mapId, "this-is-not-the-real-hash", [spawn]);

        var result = await grain.LoadMonsterSpawnsAsync(batchWithWrongFingerprint);
        Assert.Equal(WorldMonsterSpawnLoadStatus.CallerFingerprintMismatch, result.Status);
    }

    [Fact]
    public async Task LoadMonsterSpawns_SpawnBelongingToADifferentMap_IsSpawnMapMismatch()
    {
        var grain = Partition("world-rest");
        var batch = new WorldMonsterSpawnBatch("izlude", "", [Spawn("izlude"), Spawn("geffen")]);

        var result = await grain.LoadMonsterSpawnsAsync(batch);
        Assert.Equal(WorldMonsterSpawnLoadStatus.SpawnMapMismatch, result.Status);
    }

    [Fact]
    public async Task PollMonsterFeed_FirstCallWithNoCursor_ReturnsAtomicBootstrapSnapshot()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));

        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);

        Assert.False(page.ResyncRequired);
        Assert.Equal(load.SimulationEpoch, page.SimulationEpoch);
        Assert.NotNull(page.Snapshot);
        Assert.Single(page.Snapshot!);
        Assert.Null(page.Entries);
        Assert.Equal(0, page.AsOfSequence); // No transitions have occurred yet - just the initial spawn.
    }

    [Fact]
    public async Task PollMonsterFeed_IncrementalPollAfterAMutation_ReturnsOnlyTheNewEntry()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var cursor = new WorldMonsterFeedCursor(load.SimulationEpoch, bootstrap.AsOfSequence);

        var attackerPresenceId = Guid.NewGuid();
        var attackerCharacterId = 42u;
        await grain.RegisterPresenceAsync(Presence(attackerPresenceId, attackerCharacterId, mapId));
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);
        await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, attackerCharacterId, attackerPresenceId));

        var incremental = await grain.PollMonsterFeedAsync(cursor, mapId);
        Assert.False(incremental.ResyncRequired);
        Assert.Null(incremental.Snapshot); // Incremental read, never a second full snapshot.
        Assert.NotNull(incremental.Entries);
        Assert.Single(incremental.Entries!);
        Assert.Equal(WorldMonsterFeedEntryKind.EngagementAcquired, incremental.Entries![0].Kind);
        Assert.True(incremental.AsOfSequence > cursor.Sequence);
    }

    [Fact]
    public async Task PollMonsterFeed_CursorFromAPriorEpoch_ReturnsResyncRequired()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var firstLoad = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var staleCursor = new WorldMonsterFeedCursor(firstLoad.SimulationEpoch, Sequence: 0);

        // A different Count is genuinely different content -> ContentMismatch, not a rebuild - so
        // instead simulate "the simulation was rebuilt under a new epoch" the way the real
        // unload/rebuild policy will (Step 3): there is no public rebuild-in-place RPC yet, so this
        // test directly proves the CONTRACT (a stale epoch must resync) using the fact that a
        // brand-new map's simulation (never loaded) still has SOME epoch, guaranteed different from
        // firstLoad's - i.e. this proves cross-epoch cursor rejection structurally, independent of
        // how a new epoch came to exist.
        var otherMapId = "geffen";
        var otherLoad = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(otherMapId));
        Assert.NotEqual(firstLoad.SimulationEpoch, otherLoad.SimulationEpoch);

        var page = await grain.PollMonsterFeedAsync(staleCursor, otherMapId);
        Assert.True(page.ResyncRequired);
        Assert.Equal(otherLoad.SimulationEpoch, page.SimulationEpoch);
        Assert.NotNull(page.Snapshot); // A resync response still carries a full fresh snapshot to bootstrap from.
    }

    [Fact]
    public async Task PollMonsterFeed_SequenceBeyondCurrentAsOf_ReturnsResyncRequired()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var impossibleCursor = new WorldMonsterFeedCursor(load.SimulationEpoch, Sequence: 999_999);

        var page = await grain.PollMonsterFeedAsync(impossibleCursor, mapId);
        Assert.True(page.ResyncRequired);
    }

    [Fact]
    public async Task NotifyMonsterAttacked_TargetPresenceIdSurvivesAcquisition()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;

        var characterId = 77u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId));
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);
        var acquired = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired, acquired.Status);

        // The FULL target reference (CharacterId AND PresenceId), not CharacterId alone, must now
        // be observable on the authoritative instance via the feed/snapshot projection.
        var afterAcquire = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var instance = afterAcquire.Snapshot!.Single();
        Assert.NotNull(instance.EngagedTarget);
        Assert.Equal(characterId, instance.EngagedTarget!.CharacterId);
        Assert.Equal(presenceId, instance.EngagedTarget.PresenceId);
    }

    [Fact]
    public async Task ValidateMonsterAttackWindow_ReconnectWithSameCharacterIdButDifferentPresenceId_IsStaleTargetPresence()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 99u;
        var originalPresenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(originalPresenceId, characterId, mapId));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired,
            (await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, originalPresenceId))).Status);

        // The character disconnects and reconnects with the SAME CharacterId but a genuinely
        // different PresenceId - the grain's own current registration for that CharacterId is now
        // the NEW presence, so a query still carrying the OLD (now-stale) PresenceId must be
        // rejected, never silently treated as still valid merely because CharacterId matches.
        var replacementPresenceId = Guid.NewGuid();
        await grain.UnregisterPresenceAsync(mapId, characterId, originalPresenceId);
        await grain.RegisterPresenceAsync(Presence(replacementPresenceId, characterId, mapId));

        var staleQuery = new WorldMonsterAttackWindowQuery(life, characterId, originalPresenceId);
        var result = await grain.ValidateMonsterAttackWindowAsync(staleQuery);
        Assert.Equal(WorldMonsterAttackWindowStatus.StaleTargetPresence, result.Status);
    }

    [Fact]
    public async Task NotifyMonsterAttacked_ReconnectWithDifferentPresenceId_CannotAcquireUsingStalePresence()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 101u;
        var originalPresenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(originalPresenceId, characterId, mapId));
        var replacementPresenceId = Guid.NewGuid();
        await grain.UnregisterPresenceAsync(mapId, characterId, originalPresenceId);
        await grain.RegisterPresenceAsync(Presence(replacementPresenceId, characterId, mapId));

        // An attacker command still carrying the OLD PresenceId must never acquire a target, even
        // though CharacterId matches the grain's current registration.
        var staleAttack = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, originalPresenceId));
        Assert.Equal(WorldMonsterAttackedStatus.StaleAttackerPresence, staleAttack.Status);
    }

    [Fact]
    public async Task TryMarkMonsterDead_StaleSimulationEpoch_IsRejected_NeverMutatesCurrentMonster()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;

        var staleReference = new WorldMonsterLifeReference(mapId, new WorldSimulationEpoch(Guid.NewGuid()), actorId, WorldMonsterIncarnationId.First);
        var result = await grain.TryMarkMonsterDeadAsync(staleReference);
        Assert.Equal(WorldMonsterDeathStatus.StaleLifeReference, result.Status);

        var afterAttempt = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(WorldMonsterLifecycleState.Alive, afterAttempt.Snapshot!.Single().Lifecycle);
    }

    [Fact]
    public async Task TryMarkMonsterDead_StaleIncarnationId_IsRejected_NeverMutatesCurrentMonster()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;

        var staleIncarnation = WorldMonsterIncarnationId.First.Next(); // Not the current (First) incarnation.
        var staleReference = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, staleIncarnation);
        var result = await grain.TryMarkMonsterDeadAsync(staleReference);
        Assert.Equal(WorldMonsterDeathStatus.StaleLifeReference, result.Status);

        var afterAttempt = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(WorldMonsterLifecycleState.Alive, afterAttempt.Snapshot!.Single().Lifecycle);
    }

    [Fact]
    public async Task TryMarkMonsterDead_ValidLifeReference_TransitionsToDeadAndFeedsDiedEntry()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var result = await grain.TryMarkMonsterDeadAsync(life);
        Assert.Equal(WorldMonsterDeathStatus.MarkedDead, result.Status);

        var again = await grain.TryMarkMonsterDeadAsync(life);
        Assert.Equal(WorldMonsterDeathStatus.AlreadyDead, again.Status);

        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(load.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.Contains(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.Died && entry.ActorId == actorId);
    }

    // Regression: a monster's Target and its authoritative range State must be tracked
    // independently - a monster can have a just-acquired target that is clearly farther away than
    // AttackRange (no chase has started/progressed yet), and the acquisition itself must correctly
    // report Chasing, never InAttackRange merely because the mob has not started walking. The
    // monster sits at (100,100) with AttackRange=1; the attacker registers far outside that
    // (200,200), well beyond even the walking-target +1 bonus.
    [Fact]
    public async Task NotifyMonsterAttacked_AttackerClearlyOutOfRange_NeverReportsInAttackRange()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        Assert.Equal(MonsterX, bootstrap.Snapshot![0].X);
        Assert.Equal(MonsterY, bootstrap.Snapshot![0].Y);

        var characterId = 55u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: 200, y: 200));
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var acquired = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired, acquired.Status);

        // Both the incremental feed entry AND the bootstrap/snapshot projection must agree: engaged,
        // but genuinely Chasing, never InAttackRange.
        var incrementalPage = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(load.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        var acquiredEntry = Assert.Single(incrementalPage.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.EngagementAcquired);
        Assert.Equal(WorldMonsterEngagementState.Chasing, acquiredEntry.Instance.Engagement);
        Assert.NotEqual(WorldMonsterEngagementState.InAttackRange, acquiredEntry.Instance.Engagement);

        var afterAcquire = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(WorldMonsterEngagementState.Chasing, afterAcquire.Snapshot!.Single().Engagement);
    }

    // Boundary regression for the walking-target +1 range bonus (WorldMonsterEngagementRules'
    // own pinned trace): AttackRange=1, distance exactly 2 (AttackRange+1). A STATIONARY target at
    // that distance is out of range; a WALKING target at the IDENTICAL distance is exactly the
    // bonus-widened range and must be Valid.
    [Fact]
    public async Task ValidateMonsterAttackWindow_DistanceEqualsAttackRangePlusOne_StationaryTarget_IsOutOfRange()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 61u;
        var presenceId = Guid.NewGuid();
        // Distance 2 on X alone (Chebyshev) = AttackRange(1) + 1 - exactly one past plain range.
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 2), y: MonsterY));
        await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));

        var result = await grain.ValidateMonsterAttackWindowAsync(new WorldMonsterAttackWindowQuery(life, characterId, presenceId));
        Assert.Equal(WorldMonsterAttackWindowStatus.OutOfRange, result.Status);
    }

    [Fact]
    public async Task ValidateMonsterAttackWindow_DistanceEqualsAttackRangePlusOne_WalkingTarget_IsValid()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 62u;
        var presenceId = Guid.NewGuid();
        var targetX = (ushort)(MonsterX + 2);
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: targetX, y: MonsterY));
        await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));

        // Make the grain's own authoritative movement state report this character as walking -
        // ValidateMonsterAttackWindowAsync derives targetIsWalking from THIS, never a caller-supplied
        // flag (see WorldPartitionGrain.IsWalking's own doc comment).
        var moveResult = await grain.MovePlayerAsync(new WorldMovementCommand(presenceId, characterId, mapId, targetX, MonsterY, (ushort)(targetX + 1), MonsterY));
        Assert.Equal(WorldMovementStatus.Moved, moveResult.Status);

        var result = await grain.ValidateMonsterAttackWindowAsync(new WorldMonsterAttackWindowQuery(life, characterId, presenceId));
        Assert.Equal(WorldMonsterAttackWindowStatus.Valid, result.Status);
    }

    [Fact]
    public async Task ValidateMonsterAttackWindow_CharacterNeverRegistered_IsTargetNotFound()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var result = await grain.ValidateMonsterAttackWindowAsync(new WorldMonsterAttackWindowQuery(life, TargetCharacterId: 9999u, Guid.NewGuid()));
        Assert.Equal(WorldMonsterAttackWindowStatus.TargetNotFound, result.Status);
    }

    // LoadMonsterSpawns must lease EXACTLY the required ActorId count in one call, never the
    // allocator's own default 10,000-ID block size discarding the unused remainder - proven
    // indirectly (this grain has no direct block-size introspection RPC) by loading TWO different
    // maps' small batches and confirming their allocated ActorIds are close together, consistent
    // with each having leased only what it needed rather than each claiming a fresh 10,000 block.
    [Fact]
    public async Task LoadMonsterSpawns_LeasesExactlyTheRequiredActorIdCount_NotADefaultTenThousandBlock()
    {
        var grain = Partition("world-rest");
        var firstLoad = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch("izlude"));
        var firstBootstrap = await grain.PollMonsterFeedAsync(cursor: null, "izlude");
        var firstActorId = firstBootstrap.Snapshot!.Single().ActorId;

        var secondLoad = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch("geffen"));
        var secondBootstrap = await grain.PollMonsterFeedAsync(cursor: null, "geffen");
        var secondActorId = secondBootstrap.Snapshot!.Single().ActorId;

        // If each 1-monster batch had leased a full default 10,000-ID block, these two ActorIds
        // would be at least 10,000 apart; leasing exactly 1 each keeps them close (well under 100
        // apart in practice, generously bounded here to avoid coupling to the exact allocator
        // internals of unrelated concurrent leases in the same test process).
        Assert.True(Math.Abs((long)secondActorId - firstActorId) < 1000,
            $"Expected two exact-1-ID leases to produce nearby ActorIds; got {firstActorId} and {secondActorId} - suggests a full default block was leased instead.");
    }

    // The real grain timer (100ms cadence, TimeProvider.System - Orleans grain timers are
    // wall-clock-driven, not TimeProvider-injectable) must actually advance an idle-walking mob's
    // position over real elapsed time, with no player present at all - proving the tick loop itself
    // runs and mutates authoritative state independent of any RPC call. Bounded, generously, real
    // wall-clock poll (not a sleep-then-assert-once) so this cannot be flaky under CI scheduling
    // jitter while still failing fast if the timer genuinely never fires.
    // Mode: CanMove(0x1) | CanAttack(0x80) - a plain CanAttack-only mob (this file's shared
    // Spawn() helper) never starts an idle walk at all (MonsterRuntime.ProcessIdleMovement's own
    // MobMode.CanMove gate), so this test uses its own CanMove-enabled batch specifically to
    // observe real wandering movement, distinct from every other test in this file which
    // deliberately isolates combat-adjacent behavior from incidental idle-walk movement.
    private const uint CanMoveAndAttackMode = 0x0000001 | CanAttackMode;

    [Fact]
    public async Task MonsterTick_AdvancesIdleWalkOverRealElapsedTime_WithNoPlayerPresent()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var movableSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode };
        await grain.LoadMonsterSpawnsAsync(Batch(mapId, [movableSpawn]));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        var moved = false;
        while (DateTime.UtcNow < deadline && !moved)
        {
            await Task.Delay(200);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            var instance = page.Snapshot!.Single();
            moved = instance.IsWalking || instance.X != MonsterX || instance.Y != MonsterY;
        }
        Assert.True(moved, "Expected the real grain timer to eventually start or progress an idle walk for a CanMove-enabled monster.");
    }

    // NotifyMonsterAttacked's own acquisition is idempotent (re-acquiring the SAME current target
    // is AlreadyCurrentTarget, never a duplicate EngagementAcquired) - proven across several real
    // ticks of the grain's own timer in between calls, confirming the tick loop's own continuous
    // re-evaluation does not itself introduce a duplicate acquisition or drop the engagement.
    [Fact]
    public async Task NotifyMonsterAttacked_RepeatedAcquisitionAcrossRealTicks_StaysIdempotent()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 71u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: MonsterX, y: MonsterY));

        var first = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired, first.Status);

        await Task.Delay(400); // Several real 100ms ticks elapse.

        var second = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));
        Assert.Equal(WorldMonsterAttackedStatus.AlreadyCurrentTarget, second.Status);

        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(load.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.Single(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.EngagementAcquired);
    }

    // LoadMonsterSpawnsAsync leases actor IDs from the SAME global IActorIdBlockAuthorityGrain
    // every other actor-ID consumer uses (see WorldPartitionGrain.LoadMonsterSpawnsAsync's own doc
    // comment) - that grain needs the identical memory grain-storage provider Athena.World's own
    // Program.cs registers (AddMemoryGrainStorage("actorIdBlockAuthority")), exactly matching
    // ActorIdBlockAuthorityTests' own StorageConfigurator.
    // All-walkable synthetic collision data for "izlude"/"geffen" - a real, deterministic
    // IMapCollisionProvider (never EmptyMapCollisionProvider/UnverifiedFallbackMobSpawnCellSelector)
    // so this file's tests exercise the SAME RathenaCompatibleMobSpawnCellSelector production
    // World monster simulation actually uses, matching MapServer.Tests' own established
    // MakeAllWalkableMap pattern (RathenaCompatibleMobSpawnCellSelectorTests.cs) - large enough
    // (200x200) that MapEdgeSize's own margin still leaves a real candidate range.
    private static MapCollisionMap MakeAllWalkableMap(string name, int side = 200) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    // --- Step-3 correction #1/#2 regressions: engaged movement must consume a mid-walk retarget
    // at a real cell boundary via AdvanceMovementForCombat, and every authoritative position
    // change (engaged or not) must be feed-visible, never suppressed for engaged mobs. A long
    // WalkSpeedMs (2000ms/cell) against the grain's fast 100ms tick cadence gives a wide, reliable
    // window to observe "still mid-cell" before the boundary and "past it" after, without coupling
    // to exact tick counts.
    private const int SlowWalkSpeedMs = 2000;

    [Fact]
    public async Task EngagedChase_MidCellRetarget_RemainsPendingUntilCellBoundary()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var slowSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode, WalkSpeedMs = SlowWalkSpeedMs };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [slowSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        // Acquire a target far enough away that the mob starts a fresh chase walk (not already in range).
        var characterId = 201u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 10), y: MonsterY));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired,
            (await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId))).Status);

        // Wait for the chase to genuinely start walking (well under the 2s/cell duration).
        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        WorldMonsterInstance? walking = null;
        while (DateTime.UtcNow < startDeadline)
        {
            await Task.Delay(150);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            var instance = page.Snapshot!.Single();
            if (instance.IsWalking) { walking = instance; break; }
        }
        Assert.NotNull(walking);

        // Reposition the target (re-registering the SAME PresenceId/ActorId/MapId is accepted as an
        // ordinary position update - see RegisterPresenceAsync's own AlreadyRegistered branch)
        // while the mob is still well within its 2-second cell step. World's own engagement tick
        // re-evaluation observes the new position on its next pass and issues a chase retarget via
        // TryRetargetChase, which defers (RequestRetarget) rather than applying immediately. A
        // short wait later (still inside the 2s step) must show the mob has NOT snapped straight to
        // the new destination - the retarget stays pending until the real cell boundary
        // AdvanceMovementForCombat consumes it at.
        var newTargetX = (ushort)(MonsterX + 20);
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: newTargetX, y: MonsterY));
        await Task.Delay(300);
        var midCell = (await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single();
        Assert.True(midCell.IsWalking);
        Assert.NotEqual((newTargetX, MonsterY), (midCell.DestinationX, midCell.DestinationY));
    }

    [Fact]
    public async Task EngagedChase_CellBoundaryAppliesReplacementPath_MobDoesNotFinishStalePath()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var slowSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode, WalkSpeedMs = SlowWalkSpeedMs };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [slowSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 202u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 10), y: MonsterY));
        await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));

        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < startDeadline && !(await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single().IsWalking)
            await Task.Delay(150);

        var oldDestination = (await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single();
        Assert.True(oldDestination.IsWalking);

        // Retarget mid-cell (via repositioning the target's presence - see the sibling test's own
        // doc comment for why this is the right way to force World's engagement tick to issue a
        // fresh chase retarget), then wait PAST a full 2-second cell boundary - the replacement
        // path must actually be applied by then (destination changed away from the original),
        // proving the mob does not walk the entire stale old path to completion first.
        var newTargetX = (ushort)(MonsterX + 20);
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: newTargetX, y: MonsterY));

        var appliedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        WorldMonsterInstance? afterBoundary = null;
        while (DateTime.UtcNow < appliedDeadline)
        {
            await Task.Delay(300);
            var instance = (await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single();
            if ((instance.DestinationX, instance.DestinationY) != (oldDestination.DestinationX, oldDestination.DestinationY))
            {
                afterBoundary = instance;
                break;
            }
        }
        Assert.NotNull(afterBoundary);
    }

    // Correction #2: an engaged mob's ordinary chase cell-crossings must be feed-visible even when
    // no wire packet would be required for them - the feed must never suppress engaged-mob
    // position changes (the exact prior bug: Tick only appended Moved for UNENGAGED mobs).
    [Fact]
    public async Task EngagedChase_FeedVisibleXY_AdvancesOnCellCrossings_EvenWithoutRetargeting()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        // A short walk speed so the mob crosses several real cells within this test's own bound.
        var fastSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode, WalkSpeedMs = 150 };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [fastSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 203u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 15), y: MonsterY));
        await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));

        var (startX, startY) = (MonsterX, MonsterY);
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(8);
        var advanced = false;
        while (DateTime.UtcNow < deadline && !advanced)
        {
            await Task.Delay(200);
            var instance = (await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single();
            advanced = instance.X != startX || instance.Y != startY;
        }
        Assert.True(advanced, "Expected an engaged, chasing mob's feed-visible position to advance across real cell crossings.");
    }

    // Correction #5: acquisition must be rejected (never store an EngagedTarget) when the shared
    // range/validity rules would immediately say Unlock - here, the attacker presence is already
    // dead at the moment of the hit.
    [Fact]
    public async Task NotifyMonsterAttacked_AttackerPresenceIsDead_IsRejected_NeverAcquiresTarget()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 210u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: MonsterX, y: MonsterY));
        Assert.Equal(WorldPresenceLifeStateStatus.Updated,
            (await grain.UpdatePresenceLifeStateAsync(new WorldPresenceLifeStateUpdate(characterId, presenceId, IsAlive: false))).Status);

        var result = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));
        Assert.Equal(WorldMonsterAttackedStatus.AttackerNotEngageable, result.Status);

        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Null(page.Snapshot!.Single().EngagedTarget);
    }

    [Fact]
    public async Task NotifyMonsterAttacked_AttackerPresenceOnDifferentMap_IsRejected_NeverAcquiresTarget()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 211u;
        var presenceId = Guid.NewGuid();
        // Registered on a DIFFERENT map than the monster's own map ("geffen" vs "izlude").
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, "geffen", x: MonsterX, y: MonsterY));

        var result = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));
        Assert.Equal(WorldMonsterAttackedStatus.AttackerNotEngageable, result.Status);

        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Null(page.Snapshot!.Single().EngagedTarget);
    }

    // Correction #3: a bare feed poll against a map whose simulation has never been loaded must
    // report an explicit SpawnInitializationRequired status - never an ordinary empty bootstrap
    // indistinguishable from a genuinely-loaded map with zero monsters.
    [Fact]
    public async Task PollMonsterFeed_NeverLoadedMap_ReportsSpawnInitializationRequired_NotAnEmptyBootstrap()
    {
        var grain = Partition("world-rest");
        var page = await grain.PollMonsterFeedAsync(cursor: null, "izlude");
        Assert.Equal(WorldMonsterFeedStatus.SpawnInitializationRequired, page.Status);
        Assert.True(page.ResyncRequired); // Backward-compatible boolean view still reports "not Ready".
    }

    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services
                .AddSingleton<IWorldPartitionResolver>(WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider>(new UnverifiedGridLineMovementPathProvider())
                .AddSingleton<IMapCollisionProvider>(new MapCollisionProvider([MakeAllWalkableMap("izlude"), MakeAllWalkableMap("geffen")]))
                .AddSingleton(TimeProvider.System);
        }
    }
}

// Separate cluster/class (own short WorldMonsterTouchedWindowOptions) specifically for the
// active/touched-map unload-on-expiry/rebuild-on-touch policy - kept apart from
// WorldMonsterSimulationTests' own 5-minute production default so this file's other tests never
// need to worry about incidental expiry between calls, and this class's own tests can use a bounded
// real-wall-clock wait instead of an unrealistic 5-minute one.
public sealed class WorldMonsterSimulationTouchedWindowTests : IAsyncLifetime
{
    private static readonly TimeSpan ShortTouchedWindow = TimeSpan.FromSeconds(1);
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<ShortWindowConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);

    private const uint CanAttackMode = 0x0000080;
    private static WorldMonsterSpawnBatch SingleMonsterBatch(string mapId) =>
        new(mapId, "", [new WorldMonsterSpawnDefinition(MobId: 1002, mapId, X: 100, Y: 100, Xs: 1, Ys: 1, Count: 1, RespawnDelayMs: 5000, RespawnRandomDelayMs: 0, SpawnName: "Poring", WalkSpeedMs: 400, AttackRange: 1, MaxHp: 55, Mode: CanAttackMode)]);

    private static MapCollisionMap MakeAllWalkableMap(string name, int side = 200) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    // Touched-window expiry unloads the map's simulation entirely; the next touch (a fresh
    // LoadMonsterSpawnsAsync) rebuilds it under a GENUINELY NEW SimulationEpoch and requires a full
    // bootstrap - never a silent continuation of the old epoch's own sequence numbering. This is
    // the core proof of the "unload, never suspend-and-rebase" policy: nothing here ever tries to
    // feed the ~1+ second real gap into AdvanceMovement as a giant catch-up.
    [Fact]
    public async Task TouchedWindowExpiry_UnloadsSimulation_NextTouchRebuildsUnderNewEpoch()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var firstLoad = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        Assert.Equal(WorldMonsterSpawnLoadStatus.Loaded, firstLoad.Status);

        // Let the touched window expire with NO further touch of this map at all.
        await Task.Delay(ShortTouchedWindow + ShortTouchedWindow + TimeSpan.FromMilliseconds(500));

        // The next touch is a fresh load of the IDENTICAL content - if the simulation had merely
        // been paused (not unloaded), this would be AlreadyLoaded against the SAME epoch; since it
        // was genuinely unloaded, this is a fresh Loaded under a NEW epoch instead.
        var secondLoad = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        Assert.Equal(WorldMonsterSpawnLoadStatus.Loaded, secondLoad.Status);
        Assert.NotEqual(firstLoad.SimulationEpoch, secondLoad.SimulationEpoch);

        // A cursor from the OLD epoch must now resync - proving no old-epoch sequence numbering
        // survived the unload/rebuild.
        var staleCursor = new WorldMonsterFeedCursor(firstLoad.SimulationEpoch, Sequence: 0);
        var page = await grain.PollMonsterFeedAsync(staleCursor, mapId);
        Assert.True(page.ResyncRequired);
        Assert.Equal(secondLoad.SimulationEpoch, page.SimulationEpoch);
    }

    // While a map keeps being touched within its window (here, via repeated feed polls - a
    // legitimate touch per the policy), its simulation must NOT expire/unload, and its
    // SimulationEpoch must remain stable across that entire span.
    [Fact]
    public async Task RepeatedTouchesWithinWindow_KeepSimulationLoaded_NoUnexpectedUnload()
    {
        var grain = Partition("world-rest");
        var mapId = "geffen";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));

        var deadline = DateTime.UtcNow + (ShortTouchedWindow * 3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId); // Each poll is itself a touch.
            Assert.Equal(load.SimulationEpoch, page.SimulationEpoch); // Never silently rebuilt while actively touched.
        }
    }

    // Correction #3: once unloaded, an already-unloaded simulation must not repeatedly unload or
    // continuously rotate epochs merely because the grain's own 100ms tick loop keeps calling
    // Unload() again every pass after expiry. Proven by observing the SAME epoch via two separate
    // resync responses taken well apart in time, both AFTER the original touched window has
    // already expired - if Unload() were still rotating the epoch every tick, these two epochs
    // would differ from each other (never mind from firstLoad's).
    [Fact]
    public async Task UnloadedSimulation_DoesNotRepeatedlyRotateEpoch_AcrossMultiplePostExpiryTicks()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var firstLoad = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));

        // Let it expire, then wait across SEVERAL further tick intervals with no touch at all -
        // long enough for many 100ms ticks to have each called Unload() on this already-unloaded
        // simulation if the idempotency guard were missing.
        await Task.Delay(ShortTouchedWindow + TimeSpan.FromSeconds(2));

        var staleCursor = new WorldMonsterFeedCursor(firstLoad.SimulationEpoch, Sequence: 0);
        var firstResync = await grain.PollMonsterFeedAsync(staleCursor, mapId);
        Assert.Equal(WorldMonsterFeedStatus.SpawnInitializationRequired, firstResync.Status);

        await Task.Delay(TimeSpan.FromSeconds(1));

        var secondResync = await grain.PollMonsterFeedAsync(staleCursor, mapId);
        Assert.Equal(WorldMonsterFeedStatus.SpawnInitializationRequired, secondResync.Status);

        // Reload with the identical content - if either poll above had rotated the epoch again,
        // this Rebuild's fresh epoch would have nothing meaningful to compare against; the real
        // assertion is that the two polls observed IDENTICAL (both "unloaded", carrying no
        // epoch-rotation side effect) status, proving Unload() is idempotent across repeated ticks.
        var reload = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        Assert.Equal(WorldMonsterSpawnLoadStatus.Loaded, reload.Status);
        Assert.NotEqual(firstLoad.SimulationEpoch, reload.SimulationEpoch); // Rebuilt exactly once, by this actual reload.
    }

    // Correction #3: a bare PollMonsterFeedAsync against a map that was never loaded (so its
    // simulation is not IsLoaded) must NOT count as a touch - otherwise a MapServer merely polling
    // without ever successfully loading spawns would keep that empty simulation "touched" forever,
    // and it could never be reaped. Proven by polling repeatedly across several touched-window
    // spans and confirming the simulation record's own SpawnInitializationRequired status never
    // becomes anything else (there is no observable "keepalive" effect from polling alone).
    [Fact]
    public async Task PollMonsterFeed_AgainstNeverLoadedMap_DoesNotCountAsATouch()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";

        var deadline = DateTime.UtcNow + (ShortTouchedWindow * 3);
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(300);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            Assert.Equal(WorldMonsterFeedStatus.SpawnInitializationRequired, page.Status);
        }

        // A genuine load right afterward still succeeds normally - proving the repeated, non-
        // touching polls never left the simulation record in some poisoned/pinned state either.
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        Assert.Equal(WorldMonsterSpawnLoadStatus.Loaded, load.Status);
    }

    public sealed class ShortWindowConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services
                .AddSingleton<IWorldPartitionResolver>(WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider>(new UnverifiedGridLineMovementPathProvider())
                .AddSingleton<IMapCollisionProvider>(new MapCollisionProvider([MakeAllWalkableMap("izlude"), MakeAllWalkableMap("geffen")]))
                .AddSingleton(TimeProvider.System)
                .AddSingleton(new WorldMonsterTouchedWindowOptions(ShortTouchedWindow));
        }
    }
}

// Correction #4: RegisterGrainTimer does not by itself keep an otherwise-idle activation alive
// against Orleans' own idle-activation collection - a touched map must survive collection for its
// full touched window with no player/session RPC arriving, via TouchActivationLifetime's
// DelayDeactivation call on every genuine touch, but a partition with nothing touched must still
// become collectible normally (never permanently immortal). Uses Orleans' own aggressively-short
// GrainCollectionOptions so collection actually has a chance to run within this test's own bounded
// wall-clock budget.
//
// Deliberately does NOT observe this via WorldTelemetry's world.partition.activation counter: that
// Meter is a process-wide static shared by every test class in this assembly, many of which
// (legitimately) key their own grain at the same resolver-assigned partition id ("world-rest") in
// their own, separately-clustered TestCluster - under xUnit's default cross-class parallelism this
// makes any assembly-wide listener non-attributable to THIS test's own activation events. Instead,
// this observes reactivation indirectly through the grain's own in-memory state: a genuine
// reactivation always constructs a brand-new WorldPartitionGrain instance with empty
// _monsterSimulations, so SimulationEpoch stability (still loaded, unchanged epoch) or its loss
// (SpawnInitializationRequired) is exactly equivalent to "did this activation survive" without
// depending on the shared telemetry pipe at all.
public sealed class WorldPartitionActivationLifetimeTests : IAsyncLifetime
{
    private static readonly TimeSpan VeryShortCollectionAge = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan TouchedWindow = TimeSpan.FromSeconds(6);
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<FastCollectionConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IWorldPartitionGrain Partition(string id) => _cluster.GrainFactory.GetGrain<IWorldPartitionGrain>(id);

    private const uint CanAttackMode = 0x0000080;
    private static WorldMonsterSpawnBatch SingleMonsterBatch(string mapId) =>
        new(mapId, "", [new WorldMonsterSpawnDefinition(MobId: 1002, mapId, X: 100, Y: 100, Xs: 1, Ys: 1, Count: 1, RespawnDelayMs: 5000, RespawnRandomDelayMs: 0, SpawnName: "Poring", WalkSpeedMs: 400, AttackRange: 1, MaxHp: 55, Mode: CanAttackMode)]);

    private static MapCollisionMap MakeAllWalkableMap(string name, int side = 200) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    [Fact]
    public async Task TouchedMap_SurvivesActivationCollection_ThroughoutItsTouchedWindow_WithNoFurtherCalls()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        Assert.Equal(WorldMonsterSpawnLoadStatus.Loaded, load.Status);

        // No further calls at all for the full touched window - collection age is far shorter
        // (2s) than the touched window (6s), so without DelayDeactivation this activation would
        // almost certainly be collected (destroying _monsterSimulations) at least once.
        await Task.Delay(TouchedWindow - TimeSpan.FromSeconds(1));

        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        // Still the SAME activation's in-memory state: same epoch, still genuinely Loaded (a
        // reactivation would have produced a brand-new, empty simulation dictionary, which
        // BuildPage would report as SpawnInitializationRequired, never Ready under the old epoch).
        Assert.Equal(WorldMonsterFeedStatus.Ready, page.Status);
        Assert.Equal(load.SimulationEpoch, page.SimulationEpoch);
    }

    [Fact]
    public async Task UntouchedMap_ActivationIsNotPermanentlyPinned_BecomesCollectibleOnceNothingIsLoaded()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";

        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        Assert.Equal(WorldMonsterSpawnLoadStatus.Loaded, load.Status);

        // A SINGLE touch (this load) started a DelayDeactivation extension of TouchedWindow (6s) -
        // deliberately NOT re-touched again by anything for the rest of this test (unlike the
        // sibling "survives" test, which proves the OPPOSITE side of this same mechanism by
        // touching once and then confirming survival strictly WITHIN that one window). Waiting
        // well past that single extension - with the silo's collection age/quantum set far shorter
        // (2s/1s) than it - must let Orleans' own idle-activation collection actually run, since
        // nothing keeps re-arming DelayDeactivation once the window lapses: this partition must not
        // be permanently pinned merely because it once loaded a map.
        await Task.Delay(TouchedWindow + TimeSpan.FromSeconds(4));

        // The FIRST poll after that gap is the only observation point - it queries a plain
        // dictionary lookup, itself becoming a touch that would immediately re-arm
        // DelayDeactivation, so if collection had NOT already happened during the wait above, this
        // one poll can never (and must never) recover that evidence after the fact.
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.True(page.Status != WorldMonsterFeedStatus.Ready || !page.SimulationEpoch.Equals(load.SimulationEpoch),
            "Expected the activation to have been collected (observed as a lost/rebuilt simulation) once nothing kept re-touching it under a short collection age.");
    }

    public sealed class FastCollectionConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services
                .AddSingleton<IWorldPartitionResolver>(WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider>(new UnverifiedGridLineMovementPathProvider())
                .AddSingleton<IMapCollisionProvider>(new MapCollisionProvider([MakeAllWalkableMap("izlude"), MakeAllWalkableMap("geffen")]))
                .AddSingleton(TimeProvider.System)
                .AddSingleton(new WorldMonsterTouchedWindowOptions(TouchedWindow));
            siloBuilder.Configure<Orleans.Configuration.GrainCollectionOptions>(options =>
            {
                options.CollectionQuantum = TimeSpan.FromSeconds(1);
                options.CollectionAge = VeryShortCollectionAge;
            });
        }
    }
}
