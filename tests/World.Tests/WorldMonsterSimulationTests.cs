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
