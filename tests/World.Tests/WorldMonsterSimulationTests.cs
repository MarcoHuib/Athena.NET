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

    // A single map-wide (X=Y=Xs=Ys=0) Poring-shaped declaration - UnverifiedFallbackMobSpawnCellSelector
    // (the collision-less selector this grain's spawn construction uses for Phase 2B - no real
    // World-side collision provider exists yet) places every instance deterministically, so a
    // count of 1 gives exactly one, reliably locatable, monster instance per test.
    private static WorldMonsterSpawnBatch SingleMonsterBatch(string mapId, int count = 1) =>
        Batch(mapId, [Spawn(mapId)], count);

    // Mode: 0x80 = MobMode.CanAttack (MobData.cs) - required for NotifyMonsterAttackedAsync's own
    // MobMode.CanAttack gate to ever succeed; without it every acquisition attempt is (correctly)
    // MonsterNotAttackable, which is not what most of this file's tests are exercising.
    private const uint CanAttackMode = 0x0000080;
    private static WorldMonsterSpawnDefinition Spawn(string mapId) =>
        new(MobId: 1002, mapId, X: 0, Y: 0, Xs: 0, Ys: 0, Count: 1, RespawnDelayMs: 5000, RespawnRandomDelayMs: 0,
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

    // LoadMonsterSpawnsAsync leases actor IDs from the SAME global IActorIdBlockAuthorityGrain
    // every other actor-ID consumer uses (see WorldPartitionGrain.LoadMonsterSpawnsAsync's own doc
    // comment) - that grain needs the identical memory grain-storage provider Athena.World's own
    // Program.cs registers (AddMemoryGrainStorage("actorIdBlockAuthority")), exactly matching
    // ActorIdBlockAuthorityTests' own StorageConfigurator.
    public sealed class TopologyConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder)
        {
            siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
            siloBuilder.Services
                .AddSingleton<IWorldPartitionResolver>(WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["izlude", "geffen"]))
                .AddSingleton<IMovementPathProvider>(new UnverifiedGridLineMovementPathProvider())
                .AddSingleton(TimeProvider.System);
        }
    }
}
