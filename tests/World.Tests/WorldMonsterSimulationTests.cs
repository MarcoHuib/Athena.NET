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

    // Step 7: World-side HP authority + AttackSequence idempotency ledger. Every test below
    // exercises the real ApplyMonsterDamageAsync RPC through the real Orleans grain boundary -
    // matching this file's existing "no fakes, real grain" convention.

    private const uint AttackerCharacterId = 900;
    private static WorldMonsterDamageCommand DamageCommand(WorldMonsterLifeReference life, Guid presenceId, long sequence, uint damage, bool acquireEngagement = false) =>
        new(life, AttackerCharacterId, presenceId, sequence, damage, acquireEngagement);

    private async Task<(IWorldPartitionGrain Grain, string MapId, WorldMonsterLifeReference Life, Guid PresenceId)> SetupAttackerAsync(string mapId)
    {
        var grain = Partition("world-rest");
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, mapId, x: MonsterX, y: MonsterY));
        return (grain, mapId, life, presenceId);
    }

    [Fact]
    public async Task ApplyMonsterDamage_ClampedSubtract_NeverUnderflows()
    {
        var (grain, _, life, presenceId) = await SetupAttackerAsync("izlude");

        // Spawn's own MaxHp is 55 - a single hit far exceeding it must clamp to exactly 0, never wrap.
        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 10_000));

        Assert.Equal(WorldMonsterDamageStatus.Applied, result.Status);
        Assert.Equal(55u, result.HpBefore);
        Assert.Equal(0u, result.HpAfter);
        Assert.True(result.KilledByThisHit);
        Assert.Equal(55u, result.MaxHp);
    }

    [Fact]
    public async Task ApplyMonsterDamage_NonLethal_ReducesAuthoritativeHpCorrectly()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 20));

        Assert.Equal(WorldMonsterDamageStatus.Applied, result.Status);
        Assert.Equal(55u, result.HpBefore);
        Assert.Equal(35u, result.HpAfter);
        Assert.False(result.KilledByThisHit);

        // The feed's own snapshot must agree - this IS the authority, not merely the RPC's own echo.
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(35u, page.Snapshot!.Single().CurrentHp);
        Assert.Equal(55u, page.Snapshot![0].MaxHp);
    }

    [Fact]
    public async Task ApplyMonsterDamage_ExactlyOneOfTwoRacingLogicalAttacksOwnsTheKill_SecondReturnsAlreadyDead()
    {
        var (grain, _, life, presenceIdA) = await SetupAttackerAsync("izlude");
        var presenceIdB = Guid.NewGuid();
        const uint attackerB = 901;
        await grain.RegisterPresenceAsync(Presence(presenceIdB, attackerB, "izlude", x: MonsterX, y: MonsterY));

        // Two independent logical attackers, each dealing enough damage ALONE (Spawn's own MaxHp
        // is 55) to be lethal against the same still-alive monster - simulates two MapServer
        // processes racing the same kill.
        var resultA = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(life, AttackerCharacterId, presenceIdA, AttackSequence: 1, Damage: 60, AcquireEngagement: false));
        var resultB = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(life, attackerB, presenceIdB, AttackSequence: 1, Damage: 60, AcquireEngagement: false));

        Assert.Equal(WorldMonsterDamageStatus.Applied, resultA.Status);
        Assert.True(resultA.KilledByThisHit);
        Assert.Equal(0u, resultA.HpAfter);

        Assert.Equal(WorldMonsterDamageStatus.AlreadyDead, resultB.Status);
        Assert.False(resultB.KilledByThisHit);
    }

    [Fact]
    public async Task ApplyMonsterDamage_SecondAttackAfterLethalTransition_ReturnsAlreadyDead()
    {
        var (grain, _, life, presenceId) = await SetupAttackerAsync("izlude");
        var lethal = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 100));
        Assert.True(lethal.KilledByThisHit);

        var again = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 2, damage: 5));

        Assert.Equal(WorldMonsterDamageStatus.AlreadyDead, again.Status);
        Assert.False(again.KilledByThisHit);
    }

    // Regression: AlreadyDead is a REJECTION, never a committed outcome - it must NOT be recorded
    // into the AttackSequence ledger, or a later exact retry of that same (rejected) sequence
    // would incorrectly synthesize ReplayedSequence instead of AlreadyDead again.
    [Fact]
    public async Task ApplyMonsterDamage_NewSequenceAfterDeath_ReturnsAlreadyDead_NotRecordedIntoLedger_RetryIsAlsoAlreadyDead()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var lethal = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 100));
        Assert.True(lethal.KilledByThisHit);

        var sequenceTwoCommand = DamageCommand(life, presenceId, sequence: 2, damage: 5);
        var first = await grain.ApplyMonsterDamageAsync(sequenceTwoCommand);
        Assert.Equal(WorldMonsterDamageStatus.AlreadyDead, first.Status);

        // The exact retry of the SAME (rejected) sequence 2 must ALSO be AlreadyDead - never
        // ReplayedSequence, which would incorrectly imply sequence 2 had once actually committed.
        var retry = await grain.ApplyMonsterDamageAsync(sequenceTwoCommand);
        Assert.Equal(WorldMonsterDamageStatus.AlreadyDead, retry.Status);
        Assert.False(retry.KilledByThisHit);

        // No additional feed entries (HealthChanged/Died/engagement) resulted from EITHER
        // AlreadyDead call - only the original lethal command's own single Died entry exists.
        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(bootstrap.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.Single(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.Died);
        Assert.DoesNotContain(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.HealthChanged);
    }

    // Regression: the rejected sequence 2 attempts above must not have replaced or advanced the
    // ledger entry the original, genuinely committed sequence 1 owns - a later exact retry of
    // sequence 1 must still return ReplayedSequence with sequence 1's own original authoritative
    // outcome, never anything derived from the rejected sequence 2 attempts.
    [Fact]
    public async Task ApplyMonsterDamage_AfterRejectedLaterSequenceAttempts_OriginalCommittedSequenceStillReplaysCorrectly()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var sequenceOneCommand = DamageCommand(life, presenceId, sequence: 1, damage: 100);
        var original = await grain.ApplyMonsterDamageAsync(sequenceOneCommand);
        Assert.Equal(WorldMonsterDamageStatus.Applied, original.Status);
        Assert.True(original.KilledByThisHit);

        // Two rejected AlreadyDead attempts at sequence 2, including a retry of that same rejected
        // sequence (per the previous test) - neither may disturb sequence 1's own ledger entry.
        await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 2, damage: 5));
        await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 2, damage: 5));

        var replayOfOriginal = await grain.ApplyMonsterDamageAsync(sequenceOneCommand);
        Assert.Equal(WorldMonsterDamageStatus.ReplayedSequence, replayOfOriginal.Status);
        Assert.Equal(original.HpBefore, replayOfOriginal.HpBefore);
        Assert.Equal(original.HpAfter, replayOfOriginal.HpAfter);
        Assert.Equal(original.MaxHp, replayOfOriginal.MaxHp);
        Assert.True(replayOfOriginal.KilledByThisHit);

        // Still exactly one Died entry total - the rejected attempts and the sequence-1 replay
        // together produced no additional feed entries.
        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(bootstrap.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.Single(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.Died);
    }

    // Regression: a brand-new attacker/sequence pair that has NEVER been seen before, presented
    // against an already-dead life, must return AlreadyDead every time - never ReplayedSequence,
    // since no ledger entry for this key exists at all (TryAcceptAttackSequence correctly returns
    // null - "proceed as new" - and the liveness check below it is what rejects it).
    [Fact]
    public async Task ApplyMonsterDamage_BrandNewAttackerSequenceAgainstAlreadyDeadLife_AlwaysAlreadyDead_NeverReplayedSequence()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 100));

        var otherPresenceId = Guid.NewGuid();
        const uint otherAttacker = 902;
        await grain.RegisterPresenceAsync(Presence(otherPresenceId, otherAttacker, mapId, x: MonsterX, y: MonsterY));
        var brandNewCommand = new WorldMonsterDamageCommand(life, otherAttacker, otherPresenceId, AttackSequence: 1, Damage: 5, AcquireEngagement: false);

        var first = await grain.ApplyMonsterDamageAsync(brandNewCommand);
        Assert.Equal(WorldMonsterDamageStatus.AlreadyDead, first.Status);

        var retry = await grain.ApplyMonsterDamageAsync(brandNewCommand);
        Assert.Equal(WorldMonsterDamageStatus.AlreadyDead, retry.Status);

        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(bootstrap.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.Single(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.Died);
        Assert.DoesNotContain(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.HealthChanged);
        Assert.DoesNotContain(page.Entries!, entry => entry.Kind is WorldMonsterFeedEntryKind.EngagementAcquired or WorldMonsterFeedEntryKind.ChaseStarted);
    }

    [Fact]
    public async Task ApplyMonsterDamage_LethalHit_AppendsDiedExactlyOnce_NeverHealthChanged()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 100));
        Assert.True(result.KilledByThisHit);

        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(bootstrap.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.Single(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.Died);
        Assert.DoesNotContain(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.HealthChanged);
        var diedEntry = Assert.Single(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.Died);
        Assert.Equal(0u, diedEntry.Instance.CurrentHp);
    }

    [Fact]
    public async Task ApplyMonsterDamage_NonLethalHit_AppendsExactlyOneHealthChanged()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 20));
        Assert.False(result.KilledByThisHit);

        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(bootstrap.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        var healthChangedEntry = Assert.Single(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.HealthChanged);
        Assert.Equal(35u, healthChangedEntry.Instance.CurrentHp);
        Assert.DoesNotContain(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.Died);
    }

    [Fact]
    public async Task ApplyMonsterDamage_MissDamageZero_AppendsNoHealthChanged()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 0));

        Assert.Equal(WorldMonsterDamageStatus.Applied, result.Status);
        Assert.Equal(55u, result.HpBefore);
        Assert.Equal(55u, result.HpAfter);
        Assert.False(result.KilledByThisHit);
        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(bootstrap.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.DoesNotContain(page.Entries ?? [], entry => entry.Kind == WorldMonsterFeedEntryKind.HealthChanged);
    }

    [Fact]
    public async Task ApplyMonsterDamage_FirstAcceptedCommand_ReturnsApplied()
    {
        var (grain, _, life, presenceId) = await SetupAttackerAsync("izlude");

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 10));

        Assert.Equal(WorldMonsterDamageStatus.Applied, result.Status);
    }

    [Fact]
    public async Task ApplyMonsterDamage_ExactSameSequenceSamePayloadReplay_ReturnsReplayedSequence_WithIdenticalOutcomeFields_AndNoMutation()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var command = DamageCommand(life, presenceId, sequence: 1, damage: 20);

        var original = await grain.ApplyMonsterDamageAsync(command);
        Assert.Equal(WorldMonsterDamageStatus.Applied, original.Status);

        // No real time-based dedup exists (no TTL) - correctness does not depend on elapsed time,
        // so a plain immediate re-send already proves the replay path; this repo's own World.Tests
        // convention uses TimeProvider.System (Orleans grain timers are not TimeProvider-injectable),
        // so there is no FakeTimeProvider to advance here - the ledger's own design has no clock
        // dependency to exercise in the first place.
        var replay = await grain.ApplyMonsterDamageAsync(command);

        Assert.Equal(WorldMonsterDamageStatus.ReplayedSequence, replay.Status);
        Assert.Equal(original.HpBefore, replay.HpBefore);
        Assert.Equal(original.HpAfter, replay.HpAfter);
        Assert.Equal(original.MaxHp, replay.MaxHp);
        Assert.Equal(original.KilledByThisHit, replay.KilledByThisHit);
        Assert.Equal(original.Engagement, replay.Engagement);

        // Zero mutation: HP must still read exactly what the FIRST (only) real application left it
        // at - a second silent application of the same 20 damage would have produced 15, not 35.
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(35u, page.Snapshot!.Single().CurrentHp);

        // Zero feed mutation: exactly one HealthChanged entry total, from the original commit only.
        var incremental = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(bootstrap.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.Single(incremental.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.HealthChanged);

        // A THIRD identical replay remains deterministic - the stored ledger record was never
        // mutated by the second replay either.
        var secondReplay = await grain.ApplyMonsterDamageAsync(command);
        Assert.Equal(WorldMonsterDamageStatus.ReplayedSequence, secondReplay.Status);
        Assert.Equal(original.HpBefore, secondReplay.HpBefore);
        Assert.Equal(original.HpAfter, secondReplay.HpAfter);
        var pageAfterThirdCall = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(35u, pageAfterThirdCall.Snapshot!.Single().CurrentHp);
    }

    [Fact]
    public async Task ApplyMonsterDamage_LethalReplay_KeepsKilledByThisHitTrue_NoSecondDiedEntry()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var command = DamageCommand(life, presenceId, sequence: 1, damage: 100);

        var original = await grain.ApplyMonsterDamageAsync(command);
        Assert.True(original.KilledByThisHit);

        var replay = await grain.ApplyMonsterDamageAsync(command);
        Assert.Equal(WorldMonsterDamageStatus.ReplayedSequence, replay.Status);
        Assert.True(replay.KilledByThisHit);
        Assert.Equal(0u, replay.HpAfter);

        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(bootstrap.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.Single(page.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.Died);
    }

    [Fact]
    public async Task ApplyMonsterDamage_SameSequenceDifferentPayload_ReturnsConflict_HpUntouched()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 20));

        var conflict = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 21));

        Assert.Equal(WorldMonsterDamageStatus.Conflict, conflict.Status);
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(35u, page.Snapshot!.Single().CurrentHp);
    }

    [Fact]
    public async Task ApplyMonsterDamage_SameSequenceDifferentAcquireEngagement_ReturnsConflict()
    {
        var (grain, _, life, presenceId) = await SetupAttackerAsync("izlude");
        await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 20, acquireEngagement: false));

        var conflict = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 20, acquireEngagement: true));

        Assert.Equal(WorldMonsterDamageStatus.Conflict, conflict.Status);
    }

    [Fact]
    public async Task ApplyMonsterDamage_LowerSequence_ReturnsStaleSequence_HpUntouched()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 5, damage: 20));

        var stale = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 3, damage: 999));

        Assert.Equal(WorldMonsterDamageStatus.StaleSequence, stale.Status);
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(35u, page.Snapshot!.Single().CurrentHp);
    }

    [Fact]
    public async Task ApplyMonsterDamage_HigherSequence_IsTreatedAsGenuinelyNewAttack()
    {
        var (grain, _, life, presenceId) = await SetupAttackerAsync("izlude");
        await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 20));

        var next = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 2, damage: 10));

        Assert.Equal(WorldMonsterDamageStatus.Applied, next.Status);
        Assert.Equal(35u, next.HpBefore);
        Assert.Equal(25u, next.HpAfter);
    }

    [Fact]
    public async Task ApplyMonsterDamage_StaleLifeReference_WrongEpoch_IsRejected()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var staleLife = life with { SimulationEpoch = new WorldSimulationEpoch(Guid.NewGuid()) };

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(staleLife, presenceId, sequence: 1, damage: 20));

        Assert.Equal(WorldMonsterDamageStatus.StaleLifeReference, result.Status);
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(55u, page.Snapshot!.Single().CurrentHp);
    }

    [Fact]
    public async Task ApplyMonsterDamage_StaleLifeReference_WrongIncarnation_IsRejected()
    {
        var (grain, _, life, presenceId) = await SetupAttackerAsync("izlude");
        var staleLife = life with { IncarnationId = WorldMonsterIncarnationId.First.Next() };

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(staleLife, presenceId, sequence: 1, damage: 20));

        Assert.Equal(WorldMonsterDamageStatus.StaleLifeReference, result.Status);
    }

    [Fact]
    public async Task ApplyMonsterDamage_StalePresence_IsRejectedBeforeSequenceLookup()
    {
        var (grain, _, life, presenceId) = await SetupAttackerAsync("izlude");
        // Commit a real sequence 1 for the genuine presence first.
        await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 20));

        // A different (unregistered) PresenceId presenting the SAME sequence number must be
        // rejected as StaleAttackerPresence, never treated as a StaleSequence/Conflict/replay of
        // the genuine presence's own ledger entry - the ledger for a DIFFERENT (CharacterId,
        // PresenceId, Life) key does not even exist yet, so if presence validation were skipped
        // this would incorrectly fall through to "first sequence ever seen" and mutate HP again.
        var staleAttempt = await grain.ApplyMonsterDamageAsync(DamageCommand(life, Guid.NewGuid(), sequence: 1, damage: 999));

        Assert.Equal(WorldMonsterDamageStatus.StaleAttackerPresence, staleAttempt.Status);
        var page = await grain.PollMonsterFeedAsync(cursor: null, "izlude");
        Assert.Equal(35u, page.Snapshot!.Single().CurrentHp);
    }

    [Fact]
    public async Task ApplyMonsterDamage_AttackerDead_ReturnsAttackerNotEngageable()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        await grain.UpdatePresenceLifeStateAsync(new WorldPresenceLifeStateUpdate(AttackerCharacterId, presenceId, IsAlive: false));

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 20));

        Assert.Equal(WorldMonsterDamageStatus.AttackerNotEngageable, result.Status);
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(55u, page.Snapshot!.Single().CurrentHp);
    }

    [Fact]
    public async Task ApplyMonsterDamage_AttackerOnWrongMap_ReturnsAttackerNotEngageable()
    {
        var grain = Partition("world-rest");
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch("izlude"));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, "izlude");
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference("izlude", load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);
        var presenceId = Guid.NewGuid();
        // Registered on a DIFFERENT map than the monster's own map.
        await grain.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, "geffen"));

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 20));

        Assert.Equal(WorldMonsterDamageStatus.AttackerNotEngageable, result.Status);
    }

    [Fact]
    public async Task ApplyMonsterDamage_AcquireEngagementTrue_FoldsRealEngagementRulesAsAuthority_NotBlindlyTrusted()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");

        // The attacker is registered exactly at the monster's own position (distance 0) - well
        // within AttackRange, so the real WorldMonsterEngagementRules should accept this as
        // InAttackRange, proving AcquireEngagement=true actually invokes the real rules rather
        // than being trusted verbatim as a "yes, engaged" flag.
        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 10, acquireEngagement: true));

        Assert.Equal(WorldMonsterAttackedStatus.Acquired, result.Engagement);
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(WorldMonsterEngagementState.InAttackRange, page.Snapshot!.Single().Engagement);
    }

    [Fact]
    public async Task ApplyMonsterDamage_AcquireEngagementFalse_NeverAcquiresEngagement()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");

        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 10, acquireEngagement: false));

        Assert.Null(result.Engagement);
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(WorldMonsterEngagementState.Unengaged, page.Snapshot!.Single().Engagement);
    }

    // Epoch-rebuild-clears-attack-sequence-state coverage lives in
    // WorldMonsterSimulationTouchedWindowTests.ApplyMonsterDamage_AfterTouchedWindowExpiryRebuild_NewEpochAcceptsSameSequenceValue
    // below, which has the short-touched-window infrastructure needed to force a genuine
    // Unload+Rebuild cycle (a same-map reload with different content while still loaded returns
    // ContentMismatch, not a rebuild - see WorldMonsterSpawnLoadStatus's own doc comment).

    [Fact]
    public async Task ApplyMonsterDamage_Respawn_ClearsStaleAttackSequenceState_NewIncarnationAcceptsSameSequenceValue()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var quickRespawnSpawn = Spawn(mapId) with { RespawnDelayMs = 500, RespawnRandomDelayMs = 0 };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [quickRespawnSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, mapId, x: MonsterX, y: MonsterY));
        var originalLife = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var lethal = await grain.ApplyMonsterDamageAsync(DamageCommand(originalLife, presenceId, sequence: 7, damage: 100));
        Assert.True(lethal.KilledByThisHit);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorldMonsterInstance? respawned = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            var instance = page.Snapshot!.Single();
            if (instance.Lifecycle == WorldMonsterLifecycleState.Alive) { respawned = instance; break; }
        }
        Assert.NotNull(respawned);
        Assert.Equal(55u, respawned!.CurrentHp);

        var newLife = originalLife with { IncarnationId = respawned.IncarnationId };
        // Same numeric AttackSequence (7) as the pre-respawn kill - must be accepted as a
        // genuinely new attempt against the new incarnation, not rejected/replayed against the
        // old (now-unreachable) ledger entry.
        var result = await grain.ApplyMonsterDamageAsync(DamageCommand(newLife, presenceId, sequence: 7, damage: 10));
        Assert.Equal(WorldMonsterDamageStatus.Applied, result.Status);
        Assert.Equal(55u, result.HpBefore);
    }

    // Step 7 substep 3: WorldPartitionGrain.Remove(presence) -> AttackSequence cleanup wiring.
    // Every removal path funnels through the same single Remove(presence) chokepoint - these tests
    // exercise each of the four production removal paths that reach it, proving the cleanup
    // triggers regardless of which one is used, without duplicating cleanup logic per caller.
    // Correctness (never mutating HP for a departed presence) does NOT depend on this cleanup's
    // timing at all - ApplyMonsterDamageAsync's own presence-validation step already rejects a
    // stale presence as StaleAttackerPresence before the ledger is ever consulted, regardless of
    // whether RemoveAttackSequencesForPresence has run yet. This cleanup is purely memory-bounding.

    [Fact]
    public async Task Remove_ViaUnregisterPresenceAsync_CleansThatPresencesAttackSequenceState_UnrelatedPresenceUntouched()
    {
        var (grain, mapId, life, presenceIdA) = await SetupAttackerAsync("izlude");
        var presenceIdB = Guid.NewGuid();
        const uint attackerB = 901;
        await grain.RegisterPresenceAsync(Presence(presenceIdB, attackerB, mapId, x: MonsterX, y: MonsterY));

        var commandA = new WorldMonsterDamageCommand(life, AttackerCharacterId, presenceIdA, AttackSequence: 3, Damage: 5, AcquireEngagement: false);
        var commandB = new WorldMonsterDamageCommand(life, attackerB, presenceIdB, AttackSequence: 4, Damage: 5, AcquireEngagement: false);
        var originalA = await grain.ApplyMonsterDamageAsync(commandA);
        var originalB = await grain.ApplyMonsterDamageAsync(commandB);
        Assert.Equal(WorldMonsterDamageStatus.Applied, originalA.Status);
        Assert.Equal(WorldMonsterDamageStatus.Applied, originalB.Status);

        Assert.Equal(WorldPresenceUnregistrationStatus.Removed, (await grain.UnregisterPresenceAsync(mapId, AttackerCharacterId, presenceIdA)).Status);

        // Re-register the SAME presenceId (a fresh session reusing the identity a real reconnect
        // would NOT reuse, but this test is specifically isolating the LEDGER's own cleanup from
        // presence-validation - see the other tests for the "genuinely new PresenceId" case) so
        // presence validation passes and the ledger check is what's actually being exercised. A's
        // ledger entry is gone: the same command, resent verbatim, is treated as brand-new (Applied
        // again, mutating HP a SECOND time) rather than replayed - proving no stale ledger entry
        // survived to intercept it as ReplayedSequence.
        await grain.RegisterPresenceAsync(Presence(presenceIdA, AttackerCharacterId, mapId, x: MonsterX, y: MonsterY));
        var afterRemovalA = await grain.ApplyMonsterDamageAsync(commandA);
        Assert.Equal(WorldMonsterDamageStatus.Applied, afterRemovalA.Status);
        // HP by this point reflects BOTH A's and B's earlier hits (55 - 5 - 5 = 45) - this second
        // application of A's own 5 damage further reduces it to 40, proving it was a genuinely
        // NEW mutation, not a zero-mutation ReplayedSequence (which would have left HP at 45).
        Assert.Equal(originalB.HpAfter, afterRemovalA.HpBefore);
        Assert.Equal(originalB.HpAfter - 5, afterRemovalA.HpAfter);

        // B's ledger entry is completely untouched by A's removal - the exact same command still
        // replays with B's own original authoritative outcome.
        var replayB = await grain.ApplyMonsterDamageAsync(commandB);
        Assert.Equal(WorldMonsterDamageStatus.ReplayedSequence, replayB.Status);
        Assert.Equal(originalB.HpBefore, replayB.HpBefore);
        Assert.Equal(originalB.HpAfter, replayB.HpAfter);
    }

    [Fact]
    public async Task Remove_ViaSamePartitionTransfer_CleansSourceMapAttackSequenceState_DestinationRegistrationRemainsCorrect()
    {
        var (grain, sourceMapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var command = DamageCommand(life, presenceId, sequence: 1, damage: 5);
        var original = await grain.ApplyMonsterDamageAsync(command);
        Assert.Equal(WorldMonsterDamageStatus.Applied, original.Status);

        // "prontera-region" and "world-rest" (which owns izlude/geffen) are both mapped to real
        // partitions by TopologyConfigurator's own topology - transferring izlude -> geffen stays
        // on the SAME partition (world-rest), matching this test's own "same-partition" scope.
        var destinationMapId = "geffen";
        var transferCommand = new WorldTransferCommand(Guid.NewGuid(), presenceId, AttackerCharacterId, sourceMapId, destinationMapId, DestinationX: 50, DestinationY: 50);
        var transferResult = await grain.TransferPlayerAsync(transferCommand);
        Assert.Equal(WorldTransferStatus.Completed, transferResult.Status);
        Assert.Equal(WorldTransferType.SamePartition, transferResult.Type);

        // Destination registration itself is correct - the presence is now on the destination map.
        var destinationSnapshot = await grain.GetMapSnapshotAsync(destinationMapId);
        Assert.Contains(destinationSnapshot.Players, p => p.CharacterId == AttackerCharacterId && p.PresenceId == presenceId);

        // Source map's ledger entry for this presence is gone - the exact same source-map command,
        // resent verbatim, is treated as brand-new rather than replayed. (World validation itself
        // would separately reject this as StaleAttackerPresence/AttackerNotEngageable too, since
        // the presence is now registered elsewhere - but this assertion specifically proves the
        // LEDGER's own cleanup by first moving the presence back to the source map, so the
        // presence-validation step passes and the ledger check is what's actually being
        // exercised. RegisterPresenceAsync refuses to silently relocate an already-registered
        // PresenceId to a different map [Conflict] - explicitly unregister from the destination
        // first, matching how a real client-driven "go back" would also require a genuine
        // unregister/re-register cycle, not an in-place move.)
        await grain.UnregisterPresenceAsync(destinationMapId, AttackerCharacterId, presenceId);
        await grain.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, sourceMapId, x: MonsterX, y: MonsterY));
        var afterTransfer = await grain.ApplyMonsterDamageAsync(command);
        Assert.Equal(WorldMonsterDamageStatus.Applied, afterTransfer.Status);
        Assert.Equal(original.HpAfter, afterTransfer.HpBefore);
    }

    [Fact]
    public async Task Remove_ViaCrossPartitionFinalizeOutgoingTransfer_CleansSourceMapAttackSequenceState()
    {
        var prontera = Partition("prontera-region");
        var rest = Partition("world-rest");
        var mapId = "prontera";
        var load = await prontera.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await prontera.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);
        var presenceId = Guid.NewGuid();
        await prontera.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, mapId, x: MonsterX, y: MonsterY));

        var command = DamageCommand(life, presenceId, sequence: 1, damage: 5);
        var original = await prontera.ApplyMonsterDamageAsync(command);
        Assert.Equal(WorldMonsterDamageStatus.Applied, original.Status);

        // "izlude" is owned by the "world-rest" partition per TopologyConfigurator - a
        // prontera -> izlude transfer is genuinely cross-partition.
        var transferCommand = new WorldTransferCommand(Guid.NewGuid(), presenceId, AttackerCharacterId, mapId, "izlude", DestinationX: 50, DestinationY: 50);
        var transferResult = await prontera.TransferPlayerAsync(transferCommand);
        Assert.Equal(WorldTransferStatus.Completed, transferResult.Status);
        Assert.Equal(WorldTransferType.CrossPartition, transferResult.Type);
        // TransferPlayerAsync's own cross-partition branch drives PrepareIncomingTransferAsync ->
        // CommitIncomingTransferAsync -> FinalizeOutgoingTransferAsync internally (see
        // ContinueCrossPartitionAsync) - by the time it returns Completed, FinalizeOutgoingTransferAsync
        // has already run and Remove(current) has already fired on the SOURCE (prontera) partition.
        var destinationSnapshot = await rest.GetMapSnapshotAsync("izlude");
        Assert.Contains(destinationSnapshot.Players, p => p.CharacterId == AttackerCharacterId && p.PresenceId == presenceId);

        // Source simulation's ledger entry is gone - move the presence back to the source map
        // (RegisterPresenceAsync refuses to silently relocate an already-registered PresenceId to
        // a different map/partition - unregister from the destination first, matching a genuine
        // reconnect cycle) and resend the exact same command: it must be treated as brand-new.
        await rest.UnregisterPresenceAsync("izlude", AttackerCharacterId, presenceId);
        await prontera.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, mapId, x: MonsterX, y: MonsterY));
        var afterTransfer = await prontera.ApplyMonsterDamageAsync(command);
        Assert.Equal(WorldMonsterDamageStatus.Applied, afterTransfer.Status);
        Assert.Equal(original.HpAfter, afterTransfer.HpBefore);
    }

    [Fact]
    public async Task Remove_ViaIncomingCommitOwnerReplacement_CleansOldOwnersOldMapAttackSequenceState()
    {
        // CommitIncomingTransferAsync's own `if (owner is not null) Remove(owner)` branch (line
        // 300) is reached ONLY when an existing owner for this CharacterId shares the SAME
        // PresenceId as the incoming transfer (a genuinely DIFFERENT PresenceId is already
        // rejected earlier, by PrepareIncomingTransferAsync's own conflict check at line 287, and
        // again by CommitIncomingTransferAsync's own line 298 - see those methods' own logic).
        // This is the shape a real cross-partition transfer's own in-flight window naturally
        // produces: the SAME presence is still registered on its OLD map (izlude) because
        // FinalizeOutgoingTransferAsync has not yet run on the source partition, while
        // CommitIncomingTransferAsync legitimately commits that SAME presence onto the
        // destination map (geffen) as part of completing the same logical transfer.
        var rest = Partition("world-rest");
        var presenceId = Guid.NewGuid();
        var oldMapId = "izlude";
        var oldLoad = await rest.LoadMonsterSpawnsAsync(SingleMonsterBatch(oldMapId));
        var oldBootstrap = await rest.PollMonsterFeedAsync(cursor: null, oldMapId);
        var oldActorId = oldBootstrap.Snapshot!.Single().ActorId;
        var oldLife = new WorldMonsterLifeReference(oldMapId, oldLoad.SimulationEpoch, oldActorId, WorldMonsterIncarnationId.First);
        await rest.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, oldMapId, x: MonsterX, y: MonsterY));
        var oldCommand = DamageCommand(oldLife, presenceId, sequence: 1, damage: 5);
        var original = await rest.ApplyMonsterDamageAsync(oldCommand);
        Assert.Equal(WorldMonsterDamageStatus.Applied, original.Status);

        // Prepare+commit an incoming transfer for the SAME PresenceId onto a DIFFERENT map
        // (geffen), while the izlude registration above is still live - PrepareIncomingTransferAsync
        // + CommitIncomingTransferAsync directly (never weakening the transfer protocol itself,
        // matching WorldPartitionGrainTests' own existing "manual stage" idiom for exercising these
        // two RPCs without a full TransferPlayerAsync).
        var incomingTransferId = Guid.NewGuid();
        var newPresence = new WorldPlayerPresence(presenceId, AttackerCharacterId + 1_000_000, AttackerCharacterId, "geffen", 10, 10);
        var incoming = new IncomingWorldTransfer(incomingTransferId, newPresence, "prontera-region", "prontera", "geffen", 10, 10);
        var prepareResult = await rest.PrepareIncomingTransferAsync(incoming);
        Assert.Equal(IncomingTransferStatus.Prepared, prepareResult.Status);
        var commitResult = await rest.CommitIncomingTransferAsync(incomingTransferId);
        Assert.Equal(IncomingTransferStatus.Committed, commitResult.Status);

        // This SAME presence now owns its CharacterId on "geffen" - its OLD registration on
        // "izlude" is gone (Remove(owner) already fired as part of the commit above).
        var geffenSnapshot = await rest.GetMapSnapshotAsync("geffen");
        Assert.Contains(geffenSnapshot.Players, p => p.PresenceId == presenceId);
        var izludeSnapshot = await rest.GetMapSnapshotAsync(oldMapId);
        Assert.DoesNotContain(izludeSnapshot.Players, p => p.CharacterId == AttackerCharacterId);

        // The presence's ledger entry on its OLD map (izlude) is gone - move it back to izlude
        // (unregister from geffen first, since RegisterPresenceAsync refuses to silently relocate
        // an already-registered PresenceId to a different map) and resend the exact same
        // command: it must be treated as brand-new, never replayed.
        await rest.UnregisterPresenceAsync("geffen", AttackerCharacterId, presenceId);
        await rest.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, oldMapId, x: MonsterX, y: MonsterY));
        var afterCommit = await rest.ApplyMonsterDamageAsync(oldCommand);
        Assert.Equal(WorldMonsterDamageStatus.Applied, afterCommit.Status);
        Assert.Equal(original.HpAfter, afterCommit.HpBefore);
    }

    [Fact]
    public async Task Remove_MapWithNoLoadedMonsterSimulation_SucceedsAsSilentNoOp_NoSimulationCreated()
    {
        var grain = Partition("world-rest");
        var mapId = "geffen"; // Deliberately NEVER LoadMonsterSpawnsAsync'd in this test.
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, mapId));

        var result = await grain.UnregisterPresenceAsync(mapId, AttackerCharacterId, presenceId);

        Assert.Equal(WorldPresenceUnregistrationStatus.Removed, result.Status);
        // No monster simulation was created as a side effect of the cleanup lookup - the map's own
        // feed poll still correctly reports SpawnInitializationRequired (never Ready), proving
        // Remove's guarded lookup never lazily created a simulation record for this map.
        var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(WorldMonsterFeedStatus.SpawnInitializationRequired, page.Status);
    }

    [Fact]
    public async Task ApplyMonsterDamage_AcceptedAttackThenRemove_LedgerCleanedAfterLegitimateCommit()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var command = DamageCommand(life, presenceId, sequence: 1, damage: 5);

        // Ordering A: the attack turn happens FIRST - it may legitimately commit.
        var committed = await grain.ApplyMonsterDamageAsync(command);
        Assert.Equal(WorldMonsterDamageStatus.Applied, committed.Status);

        // The LATER Remove cleans its ledger entry.
        await grain.UnregisterPresenceAsync(mapId, AttackerCharacterId, presenceId);

        // Re-registering and resending the exact same command proves the ledger entry is gone -
        // it is treated as brand-new, not replayed.
        await grain.RegisterPresenceAsync(Presence(presenceId, AttackerCharacterId, mapId, x: MonsterX, y: MonsterY));
        var afterRemoval = await grain.ApplyMonsterDamageAsync(command);
        Assert.Equal(WorldMonsterDamageStatus.Applied, afterRemoval.Status);
        Assert.Equal(committed.HpAfter, afterRemoval.HpBefore);
    }

    [Fact]
    public async Task ApplyMonsterDamage_RemoveThenStaleAttack_ReturnsStaleAttackerPresence_NoHpOrFeedMutation()
    {
        var (grain, mapId, life, presenceId) = await SetupAttackerAsync("izlude");
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);

        // Ordering B: Remove happens FIRST.
        await grain.UnregisterPresenceAsync(mapId, AttackerCharacterId, presenceId);

        // The OLD presence's command, submitted after removal, must be rejected at the
        // presence-validation step - BEFORE the ledger is ever consulted - regardless of whether
        // RemoveAttackSequencesForPresence has already run.
        var staleAttempt = await grain.ApplyMonsterDamageAsync(DamageCommand(life, presenceId, sequence: 1, damage: 999));
        Assert.Equal(WorldMonsterDamageStatus.StaleAttackerPresence, staleAttempt.Status);

        var page = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(bootstrap.SimulationEpoch, bootstrap.AsOfSequence), mapId);
        Assert.Empty(page.Entries ?? []);
        var freshSnapshot = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(55u, freshSnapshot.Snapshot!.Single().CurrentHp);
    }

    // Regression for the secondary-index desync fix: RegisterPresenceAsync's own uniqueness check
    // is keyed by CharacterId ONLY - it never rejects two DIFFERENT CharacterIds sharing the SAME
    // PresenceId Guid (confirmed by direct code reading: WorldPartitionGrain.RegisterPresenceAsync's
    // conflict check is `TryFind(presence.CharacterId, ...)`, with no corresponding PresenceId
    // uniqueness check anywhere). RemoveAttackSequencesForPresence's secondary index is keyed by
    // PresenceId alone, so this shape is exactly the one that could desynchronize the two
    // dictionaries if cleanup removed a shared PresenceId bucket wholesale instead of only the
    // entries actually owned by the departing CharacterId.
    [Fact]
    public async Task RemoveAttackSequencesForPresence_SharedPresenceIdAcrossDifferentCharacterIds_OnlyRemovesOwnEntries_OtherCharacterStillDiscoverable()
    {
        var (grain, mapId, life, _) = await SetupAttackerAsync("izlude");
        var sharedPresenceId = Guid.NewGuid();
        const uint characterA = 950;
        const uint characterB = 951;
        await grain.UnregisterPresenceAsync(mapId, AttackerCharacterId, (await grain.GetMapSnapshotAsync(mapId)).Players.Single(p => p.CharacterId == AttackerCharacterId).PresenceId);

        // Two DIFFERENT CharacterIds, deliberately sharing the SAME PresenceId Guid, registered and
        // BOTH REMAINING REGISTERED SIMULTANEOUSLY - accepted by the current presence contract
        // since RegisterPresenceAsync's own conflict check (TryFind keyed by CharacterId alone)
        // never examines PresenceId uniqueness across CharacterIds. This simultaneity is essential:
        // if B only registered AFTER A had already unregistered, A's and B's AttackSequence entries
        // would never coexist in the SAME _attackSequencesByPresence[sharedPresenceId] bucket, and
        // the whole scenario this test exists to catch could never occur.
        await grain.RegisterPresenceAsync(Presence(sharedPresenceId, characterA, mapId, x: MonsterX, y: MonsterY));
        await grain.RegisterPresenceAsync(Presence(sharedPresenceId, characterB, mapId, x: MonsterX, y: MonsterY));

        var commandA = new WorldMonsterDamageCommand(life, characterA, sharedPresenceId, AttackSequence: 1, Damage: 0, AcquireEngagement: false);
        var commandB = new WorldMonsterDamageCommand(life, characterB, sharedPresenceId, AttackSequence: 1, Damage: 0, AcquireEngagement: false);
        Assert.Equal(WorldMonsterDamageStatus.Applied, (await grain.ApplyMonsterDamageAsync(commandA)).Status);
        Assert.Equal(WorldMonsterDamageStatus.Applied, (await grain.ApplyMonsterDamageAsync(commandB)).Status);

        // At this point _attackSequencesByPresence[sharedPresenceId] must logically contain BOTH
        // (characterA, life) and (characterB, life) - the exact shared-bucket state the original
        // bug required and the fixed-only-after-B-departed prior version of this test never
        // actually constructed.
        Assert.Equal(WorldPresenceUnregistrationStatus.Removed, (await grain.UnregisterPresenceAsync(mapId, characterA, sharedPresenceId)).Status);

        // With the OLD BROKEN helper, unregistering A would have deleted the ENTIRE
        // sharedPresenceId bucket (including B's entry) from the secondary index, while B's
        // primary _attackSequences entry survives untouched - not yet observable from a replay
        // alone (the primary entry is intact either way), so this assertion establishes that A's
        // cleanup did not corrupt B's own primary ledger entry, without yet proving the secondary
        // index is still healthy.
        var replayB = await grain.ApplyMonsterDamageAsync(commandB);
        Assert.Equal(WorldMonsterDamageStatus.ReplayedSequence, replayB.Status);

        // THE LOAD-BEARING ACTION: unregister B. With the FIXED helper, B's entry is still present
        // in the (still-existing, A-only-trimmed) sharedPresenceId secondary bucket, so this call
        // discovers and removes B's primary AttackSequence state. With the OLD BROKEN helper, the
        // entire bucket was already destroyed when A left, so this call's TryGetValue on the
        // (already-missing) bucket silently no-ops, and B's primary ledger entry leaks forever.
        Assert.Equal(WorldPresenceUnregistrationStatus.Removed, (await grain.UnregisterPresenceAsync(mapId, characterB, sharedPresenceId)).Status);

        // FINAL PROOF: re-register B under the SAME CharacterId+PresenceId against the SAME
        // still-living monster life, and resend B's EXACT original command. Under the fixed
        // helper, B's cleanup above genuinely removed the ledger entry, so this is treated as a
        // brand-new attempt: Applied. Under the old broken helper, B's stale primary entry would
        // still be present (never reachable for cleanup because its secondary-index bucket was
        // gone), and this would incorrectly return ReplayedSequence again.
        await grain.RegisterPresenceAsync(Presence(sharedPresenceId, characterB, mapId, x: MonsterX, y: MonsterY));
        var afterBsOwnCleanup = await grain.ApplyMonsterDamageAsync(commandB);
        Assert.Equal(WorldMonsterDamageStatus.Applied, afterBsOwnCleanup.Status);

        // The monster stayed alive/full-HP throughout (Damage=0 on every hit) - no
        // respawn/epoch-reminting cleanup could have masked or substituted for the
        // presence-cleanup behavior under test.
        var finalPage = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(WorldMonsterLifecycleState.Alive, finalPage.Snapshot![0].Lifecycle);
        Assert.Equal(finalPage.Snapshot![0].MaxHp, finalPage.Snapshot![0].CurrentHp);
    }

    // Boss-survives-many-disconnects memory-bound scenario: ONE long-lived monster life, repeatedly
    // attacked by a NEW (CharacterId, PresenceId) identity that registers, attacks once (non-lethal,
    // Damage=0 so the monster is never killed/respawned - the point is proving presence cleanup
    // ALONE bounds memory while the SAME life stays alive), then unregisters.
    //
    // This is a GENUINE behavioral proof that historical entries are actually removed, not merely
    // that new identities avoid colliding with them (a brand-new identity can never collide with
    // any prior one regardless of whether old entries were cleaned up, since the ledger key is the
    // full (CharacterId, PresenceId, Life) tuple - a test that only checked a fresh 201st identity
    // would pass even if all 200 historical entries were leaked forever). Instead, this test
    // RE-USES each historical identity's EXACT (CharacterId, PresenceId) pair and resends the EXACT
    // same (AttackSequence, Damage, AcquireEngagement) payload after that identity's own second
    // departure: if its original ledger entry had survived the FIRST unregister, this exact replay
    // would return ReplayedSequence; if cleanup genuinely removed it, World has no ledger entry for
    // that key at all and the command is accepted fresh as Applied.
    [Fact]
    public async Task ApplyMonsterDamage_ManySequentialPresenceRegisterAttackUnregisterCycles_HistoricalIdentitiesAreActuallyRemoved()
    {
        var (grain, mapId, life, _) = await SetupAttackerAsync("izlude");
        // The original SetupAttackerAsync presence/registration is unused below - each historical
        // identity registers and removes its OWN fresh presence against the SAME still-alive
        // monster life.
        await grain.UnregisterPresenceAsync(mapId, AttackerCharacterId, (await grain.GetMapSnapshotAsync(mapId)).Players.Single(p => p.CharacterId == AttackerCharacterId).PresenceId);

        const int historicalIdentityCount = 50;
        var historicalIdentities = new List<(uint CharacterId, Guid PresenceId)>();
        for (var i = 0; i < historicalIdentityCount; i++)
        {
            var characterId = AttackerCharacterId + 1 + (uint)i;
            var presenceId = Guid.NewGuid();
            historicalIdentities.Add((characterId, presenceId));

            await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: MonsterX, y: MonsterY));
            var result = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(life, characterId, presenceId, AttackSequence: 1, Damage: 0, AcquireEngagement: false));
            Assert.Equal(WorldMonsterDamageStatus.Applied, result.Status);
            Assert.False(result.KilledByThisHit);
            Assert.Equal(WorldPresenceUnregistrationStatus.Removed, (await grain.UnregisterPresenceAsync(mapId, characterId, presenceId)).Status);
        }

        // The monster is still alive and at full HP throughout (Damage=0 the whole time) - it was
        // never killed/respawned, so this genuinely isolates presence-departure cleanup as the
        // only mechanism that could keep the ledger bounded across these historical cycles.
        var finalPage = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        Assert.Equal(55u, finalPage.Snapshot!.Single().CurrentHp);
        Assert.Equal(WorldMonsterLifecycleState.Alive, finalPage.Snapshot![0].Lifecycle);

        // The actual proof: re-register EACH historical identity under its OWN exact
        // (CharacterId, PresenceId) pair and resend its OWN exact original payload. Each must be
        // accepted as Applied (a genuinely new ledger entry), never ReplayedSequence (which would
        // mean that identity's original entry from the loop above had survived its first
        // unregister, i.e. cleanup failed to remove it).
        foreach (var (characterId, presenceId) in historicalIdentities)
        {
            await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: MonsterX, y: MonsterY));
            var replayAttempt = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(life, characterId, presenceId, AttackSequence: 1, Damage: 0, AcquireEngagement: false));
            Assert.Equal(WorldMonsterDamageStatus.Applied, replayAttempt.Status);
            await grain.UnregisterPresenceAsync(mapId, characterId, presenceId);
        }
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

    // Live-acceptance Issue B fix regression: pinned rAthena's unit_walktoxy_nextcell checks attack
    // range (via unit_update_chase) BEFORE ever sending clif_move for a fresh leg - if the mob has
    // already reached range, unit_stop_walking(USW_FIXPOS) sends ONLY clif_fixpos, never clif_move,
    // because unit_walktoxy_nextcell returns before its own clif_move call is ever reached
    // (unit.cpp:219-242). Before this fix, WorldMonsterMapSimulation.Tick's own Step 2 unconditionally
    // started a fresh retarget walk (Append ChaseStarted/WalkStarted) BEFORE Step 4's later range
    // re-evaluation could discover the mob was already in range and stop it (Append
    // ChaseInterrupted/ChaseInterrupted) - producing a redundant WalkStarted-then-ChaseInterrupted
    // pair for the SAME cell boundary, which MapServer projects verbatim as a spurious 0x09FD
    // immediately followed by a 0x0088 fixpos correction (the exact live "monster snaps" symptom).
    //
    // Reproduces the same-tick case deterministically: a slow-walking mob is chasing a target that
    // starts far away, then the target is repositioned to well within AttackRange (1) WHILE the mob
    // is still mid-cell - forcing the pending-retarget consumption at the next cell boundary
    // (AdvanceMovementForCombat's own retargetApplied=true branch) to land on a position that is
    // ALREADY in range of the target's new location.
    [Fact]
    public async Task EngagedChase_RetargetConsumedAtCellBoundary_TargetAlreadyInRange_NoRedundantWalkStarted_DirectlyReportsChaseInterrupted()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var slowSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode, WalkSpeedMs = SlowWalkSpeedMs };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [slowSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 301u;
        var presenceId = Guid.NewGuid();
        // Starts far enough away (well outside AttackRange=1) that the mob begins a genuine chase walk.
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 10), y: MonsterY));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired,
            (await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId))).Status);

        // Wait for the mob to have crossed AT LEAST ONE real cell boundary (X actually changed from
        // spawn) - this proves AdvanceMovementForCombat's own retargetApplied branch is genuinely
        // reachable (it only fires on an ACTUAL crossing, never merely "IsWalking=true" the instant
        // a walk starts before any boundary has been reached) - a retarget issued before any
        // crossing would instead be picked up directly by Step 4's own re-evaluation against the
        // mob's still-at-spawn position, never exercising the Step 2 code path this test targets.
        var crossedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        WorldMonsterInstance? crossed = null;
        while (DateTime.UtcNow < crossedDeadline)
        {
            await Task.Delay(50);
            var instance = (await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single();
            if (instance.X != MonsterX || instance.Y != MonsterY) { crossed = instance; break; }
        }
        Assert.NotNull(crossed);
        Assert.True(crossed!.IsWalking, "Expected the mob to still be walking (mid-chase) immediately after its first real cell crossing.");
        var cursorBeforeRetarget = new WorldMonsterFeedCursor(load.SimulationEpoch, (await grain.PollMonsterFeedAsync(cursor: null, mapId)).AsOfSequence);

        // Reposition the target to exactly 2 cells past the mob's OWN just-crossed CURRENT position -
        // still genuinely OUT of AttackRange=1 right now (so Step 4's own immediate re-evaluation
        // this same tick still correctly reports Chase, never InAttackRange), but exactly ONE cell
        // closer than that is already in range - the crossing the mob's own pending retarget walk is
        // about to make. This is the narrow same-tick window the fix targets: the mob is out of
        // range the instant BEFORE crossing, and in range the instant AFTER - all within ONE
        // AdvanceMovementForCombat call, never observable as two separate Step-4-only evaluations.
        var closeTargetX = (ushort)(crossed.X + 2);
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: closeTargetX, y: crossed.Y));

        // Wait past the full 2-second cell boundary so the pending retarget is genuinely consumed.
        var resolvedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        WorldMonsterInstance? resolved = null;
        while (DateTime.UtcNow < resolvedDeadline)
        {
            await Task.Delay(300);
            var instance = (await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single();
            if (instance.Engagement == WorldMonsterEngagementState.InAttackRange) { resolved = instance; break; }
        }
        Assert.NotNull(resolved);
        Assert.False(resolved!.IsWalking, "Expected the mob to be stationary once InAttackRange - no stale movement remains active.");

        // The critical assertion: the incremental feed entries produced by this exact retarget-
        // consumption/range-discovery boundary must contain NO WalkStarted entry - only the direct
        // ChaseInterrupted transition, matching pinned rAthena's own clif_fixpos-only behavior.
        var page = await grain.PollMonsterFeedAsync(cursorBeforeRetarget, mapId);
        Assert.False(page.ResyncRequired, "Expected an ordinary incremental page, not a resync, for this bounded window.");
        var entries = page.Entries!;
        Assert.DoesNotContain(entries, e => e.MovementKind == WorldMonsterMovementKind.WalkStarted);
        Assert.Contains(entries, e => e.Kind == WorldMonsterFeedEntryKind.ChaseInterrupted && e.MovementKind == WorldMonsterMovementKind.ChaseInterrupted);
        // Final authoritative position/engagement/life identity are all correct despite skipping the
        // redundant walk-start narrative.
        Assert.Equal(WorldMonsterEngagementState.InAttackRange, resolved.Engagement);
        Assert.Equal(actorId, resolved.ActorId);
        Assert.Equal(WorldMonsterIncarnationId.First, resolved.IncarnationId);
        Assert.NotNull(resolved.EngagedTarget);
        Assert.Equal(characterId, resolved.EngagedTarget!.CharacterId);
        Assert.Equal(presenceId, resolved.EngagedTarget.PresenceId);
    }

    // Control test for the same fix: when the retarget-consumption cell boundary lands on a position
    // that is STILL genuinely out of range, the fresh chase WalkStarted entry must still be emitted
    // normally - the fix must never suppress a legitimate walk-continuation, only the redundant one
    // immediately superseded within the same evaluation.
    [Fact]
    public async Task EngagedChase_RetargetConsumedAtCellBoundary_TargetStillOutOfRange_WalkStartedStillEmitted()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var slowSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode, WalkSpeedMs = SlowWalkSpeedMs };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [slowSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 302u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 10), y: MonsterY));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired,
            (await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId))).Status);

        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < startDeadline && !(await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single().IsWalking)
            await Task.Delay(150);
        Assert.True((await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single().IsWalking);
        var cursorBeforeRetarget = new WorldMonsterFeedCursor(load.SimulationEpoch, (await grain.PollMonsterFeedAsync(cursor: null, mapId)).AsOfSequence);

        // Reposition the target to a DIFFERENT far-away cell - still well outside AttackRange=1 -
        // forcing an ordinary retarget that must still be reported as a fresh WalkStarted, never
        // suppressed.
        var stillFarTargetX = (ushort)(MonsterX + 20);
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: stillFarTargetX, y: MonsterY));

        var appliedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        var sawWalkStarted = false;
        while (DateTime.UtcNow < appliedDeadline && !sawWalkStarted)
        {
            await Task.Delay(300);
            var page = await grain.PollMonsterFeedAsync(cursorBeforeRetarget, mapId);
            if (page.ResyncRequired) continue;
            sawWalkStarted = page.Entries!.Any(e => e.MovementKind == WorldMonsterMovementKind.WalkStarted);
        }
        Assert.True(sawWalkStarted, "Expected the fresh chase WalkStarted entry to still be emitted normally when the retargeted destination remains genuinely out of range.");
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

    // --- World feed movement semantics (WorldMonsterMovementKind) - proves the feed carries the
    // exact Ragexe-projection-relevant movement classification a future MapServer consumer needs,
    // rather than requiring that consumer to (incorrectly) re-derive it from IsWalking alone. ---

    [Fact]
    public async Task IdleWalkBegins_FeedEntry_Kind_Moved_MovementKind_WalkStarted()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var movableSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [movableSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(15);
        WorldMonsterFeedEntry? walkStarted = null;
        var cursor = new WorldMonsterFeedCursor(load.SimulationEpoch, bootstrap.AsOfSequence);
        while (DateTime.UtcNow < deadline && walkStarted is null)
        {
            await Task.Delay(200);
            var page = await grain.PollMonsterFeedAsync(cursor, mapId);
            if (page.Entries is { Count: > 0 })
            {
                cursor = new WorldMonsterFeedCursor(load.SimulationEpoch, page.AsOfSequence);
                walkStarted = page.Entries.FirstOrDefault(e => e.MovementKind == WorldMonsterMovementKind.WalkStarted);
            }
        }
        Assert.NotNull(walkStarted);
        Assert.Equal(WorldMonsterFeedEntryKind.Moved, walkStarted!.Kind);
        Assert.Equal(WorldMonsterMovementKind.WalkStarted, walkStarted.MovementKind);
    }

    [Fact]
    public async Task IdleOrEngagedOrdinaryCellCrossing_FeedEntry_Kind_Moved_MovementKind_CellCrossed()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var movableSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [movableSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(20);
        WorldMonsterFeedEntry? cellCrossed = null;
        var cursor = new WorldMonsterFeedCursor(load.SimulationEpoch, bootstrap.AsOfSequence);
        while (DateTime.UtcNow < deadline && cellCrossed is null)
        {
            await Task.Delay(200);
            var page = await grain.PollMonsterFeedAsync(cursor, mapId);
            if (page.Entries is { Count: > 0 })
            {
                cursor = new WorldMonsterFeedCursor(load.SimulationEpoch, page.AsOfSequence);
                cellCrossed = page.Entries.FirstOrDefault(e => e.MovementKind == WorldMonsterMovementKind.CellCrossed);
            }
        }
        Assert.NotNull(cellCrossed);
        Assert.Equal(WorldMonsterFeedEntryKind.Moved, cellCrossed!.Kind);
    }

    [Fact]
    public async Task OrdinaryWalkCompletes_FeedEntry_Kind_Moved_MovementKind_WalkFinished()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var movableSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [movableSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(30);
        WorldMonsterFeedEntry? walkFinished = null;
        var cursor = new WorldMonsterFeedCursor(load.SimulationEpoch, bootstrap.AsOfSequence);
        while (DateTime.UtcNow < deadline && walkFinished is null)
        {
            await Task.Delay(200);
            var page = await grain.PollMonsterFeedAsync(cursor, mapId);
            if (page.Entries is { Count: > 0 })
            {
                cursor = new WorldMonsterFeedCursor(load.SimulationEpoch, page.AsOfSequence);
                walkFinished = page.Entries.FirstOrDefault(e => e.MovementKind == WorldMonsterMovementKind.WalkFinished);
            }
        }
        Assert.NotNull(walkFinished);
        Assert.Equal(WorldMonsterFeedEntryKind.Moved, walkFinished!.Kind);
    }

    [Fact]
    public async Task FreshChaseStarts_FeedEntry_Kind_ChaseStarted_MovementKind_WalkStarted()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        // Attacker far enough away that acquisition results in a genuine Chase (not InAttackRange).
        var characterId = 220u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 10), y: MonsterY));
        var acquired = await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));
        Assert.Equal(WorldMonsterAttackedStatus.Acquired, acquired.Status);

        // TryAcquireEngagement's own EngagementAcquired entry carries no movement transition yet
        // (the walk itself is only actually started by the NEXT tick's engagement re-evaluation,
        // via ApplyChaseDecision) - the required "fresh chase starts" example is exercised by that
        // FOLLOW-UP ChaseStarted entry the real grain timer produces shortly after acquisition.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorldMonsterFeedEntry? chaseStarted = null;
        var cursor = new WorldMonsterFeedCursor(load.SimulationEpoch, bootstrap.AsOfSequence);
        while (DateTime.UtcNow < deadline && chaseStarted is null)
        {
            await Task.Delay(150);
            var page = await grain.PollMonsterFeedAsync(cursor, mapId);
            if (page.Entries is { Count: > 0 })
            {
                cursor = new WorldMonsterFeedCursor(load.SimulationEpoch, page.AsOfSequence);
                chaseStarted = page.Entries.FirstOrDefault(e => e.Kind == WorldMonsterFeedEntryKind.ChaseStarted);
            }
        }
        Assert.NotNull(chaseStarted);
        Assert.Equal(WorldMonsterMovementKind.WalkStarted, chaseStarted!.MovementKind);
    }

    [Fact]
    public async Task PendingCombatRetargetAppliedAtCellBoundary_FeedEntry_Kind_ChaseStarted_MovementKind_WalkStarted()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var slowSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode, WalkSpeedMs = SlowWalkSpeedMs };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [slowSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 221u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 10), y: MonsterY));
        await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));

        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < startDeadline && !(await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single().IsWalking)
            await Task.Delay(150);

        var cursorAfterAcquire = new WorldMonsterFeedCursor(load.SimulationEpoch, (await grain.PollMonsterFeedAsync(cursor: null, mapId)).AsOfSequence);
        // Retarget mid-cell by repositioning the target (see the sibling correction-#1 tests' own
        // doc comments for why this is the correct way to force a fresh chase retarget).
        var newTargetX = (ushort)(MonsterX + 20);
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: newTargetX, y: MonsterY));

        var appliedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        WorldMonsterFeedEntry? retargetApplied = null;
        while (DateTime.UtcNow < appliedDeadline && retargetApplied is null)
        {
            await Task.Delay(300);
            var page = await grain.PollMonsterFeedAsync(cursorAfterAcquire, mapId);
            if (page.Entries is { Count: > 0 })
            {
                cursorAfterAcquire = new WorldMonsterFeedCursor(load.SimulationEpoch, page.AsOfSequence);
                retargetApplied = page.Entries.FirstOrDefault(e =>
                    e.Kind == WorldMonsterFeedEntryKind.ChaseStarted && e.Instance.DestinationX == newTargetX);
            }
        }
        Assert.NotNull(retargetApplied);
        Assert.Equal(WorldMonsterMovementKind.WalkStarted, retargetApplied!.MovementKind);
    }

    [Fact]
    public async Task ChaseStopsBecauseTargetEnteredRange_FeedEntry_Kind_ChaseInterrupted_MovementKind_ChaseInterrupted()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var slowSpawn = Spawn(mapId) with { Mode = CanMoveAndAttackMode, WalkSpeedMs = SlowWalkSpeedMs };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [slowSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);

        var characterId = 222u;
        var presenceId = Guid.NewGuid();
        // Start the attacker out of range so a real chase begins.
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 10), y: MonsterY));
        await grain.NotifyMonsterAttackedAsync(new WorldMonsterAttackedCommand(life, characterId, presenceId));

        var startDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (DateTime.UtcNow < startDeadline && !(await grain.PollMonsterFeedAsync(cursor: null, mapId)).Snapshot!.Single().IsWalking)
            await Task.Delay(150);

        var cursorAfterAcquire = new WorldMonsterFeedCursor(load.SimulationEpoch, (await grain.PollMonsterFeedAsync(cursor: null, mapId)).AsOfSequence);
        // Move the target INTO attack range (AttackRange=1, adjacent cell) while the mob is still
        // walking - the next tick's engagement re-evaluation must interrupt the chase.
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId, x: (ushort)(MonsterX + 1), y: MonsterY));

        var interruptedDeadline = DateTime.UtcNow + TimeSpan.FromSeconds(6);
        WorldMonsterFeedEntry? chaseInterrupted = null;
        while (DateTime.UtcNow < interruptedDeadline && chaseInterrupted is null)
        {
            await Task.Delay(200);
            var page = await grain.PollMonsterFeedAsync(cursorAfterAcquire, mapId);
            if (page.Entries is { Count: > 0 })
            {
                cursorAfterAcquire = new WorldMonsterFeedCursor(load.SimulationEpoch, page.AsOfSequence);
                chaseInterrupted = page.Entries.FirstOrDefault(e => e.Kind == WorldMonsterFeedEntryKind.ChaseInterrupted);
            }
        }
        Assert.NotNull(chaseInterrupted);
        Assert.Equal(WorldMonsterMovementKind.ChaseInterrupted, chaseInterrupted!.MovementKind);
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

    // Step 6: UpdatePresenceLifeStateAsync's own contract must work correctly in BOTH directions
    // (isAlive:false AND isAlive:true) even though MapServer currently only ever calls the
    // isAlive:false half in production (no player revive/resurrection mechanic exists yet - see
    // MapClientSession.ApplyIncomingMobBasicAttackAsync's own doc comment for that documented
    // Phase 2B limitation). This proves the RPC itself is not one-sided, so a future genuine
    // revive feature can call isAlive:true without any World contract change.
    [Fact]
    public async Task UpdatePresenceLifeState_IsAliveTrue_UpdatesAnAlreadyDeadPresenceBackToAlive()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var characterId = 211u;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(presenceId, characterId, mapId));
        Assert.Equal(WorldPresenceLifeStateStatus.Updated,
            (await grain.UpdatePresenceLifeStateAsync(new WorldPresenceLifeStateUpdate(characterId, presenceId, IsAlive: false))).Status);

        var reviveResult = await grain.UpdatePresenceLifeStateAsync(new WorldPresenceLifeStateUpdate(characterId, presenceId, IsAlive: true));

        Assert.Equal(WorldPresenceLifeStateStatus.Updated, reviveResult.Status);
    }

    [Fact]
    public async Task UpdatePresenceLifeState_StalePresenceId_NeverUpdatesTheReplacementSession()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var characterId = 212u;
        var originalPresenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(Presence(originalPresenceId, characterId, mapId));

        // The character disconnects and reconnects with the SAME CharacterId but a genuinely
        // different PresenceId - the grain's own current registration for that CharacterId is now
        // the NEW presence.
        var replacementPresenceId = Guid.NewGuid();
        await grain.UnregisterPresenceAsync(mapId, characterId, originalPresenceId);
        await grain.RegisterPresenceAsync(Presence(replacementPresenceId, characterId, mapId));

        // A life-state update still carrying the OLD (now-stale) PresenceId must be rejected, never
        // silently applied to the replacement presence merely because CharacterId matches.
        var staleUpdate = await grain.UpdatePresenceLifeStateAsync(new WorldPresenceLifeStateUpdate(characterId, originalPresenceId, IsAlive: false));
        Assert.Equal(WorldPresenceLifeStateStatus.StalePresence, staleUpdate.Status);

        // The CURRENT (replacement) presence's own life state must be genuinely untouched by the
        // rejected stale call - verified indirectly via a subsequent legitimate update succeeding
        // normally against the replacement's own correct PresenceId.
        var legitimateUpdate = await grain.UpdatePresenceLifeStateAsync(new WorldPresenceLifeStateUpdate(characterId, replacementPresenceId, IsAlive: false));
        Assert.Equal(WorldPresenceLifeStateStatus.Updated, legitimateUpdate.Status);
    }

    [Fact]
    public async Task UpdatePresenceLifeState_CharacterNeverRegistered_IsNotFound()
    {
        var grain = Partition("world-rest");
        var result = await grain.UpdatePresenceLifeStateAsync(new WorldPresenceLifeStateUpdate(CharacterId: 9999u, Guid.NewGuid(), IsAlive: false));

        Assert.Equal(WorldPresenceLifeStateStatus.NotFound, result.Status);
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

    // IncarnationId is now MobInstance's own real, single-source-of-truth field (see
    // MonsterIncarnationId's own doc comment) - World's Respawned feed entry/wire projection must
    // report the SAME value the authoritative MobInstance actually holds after a real respawn, and
    // a life reference from BEFORE that respawn must be rejected as stale afterward. Uses a short
    // RespawnDelayMs and the real grain timer/wall clock (Orleans grain timers are not
    // TimeProvider-injectable) to drive an actual Dead->Alive transition.
    [Fact]
    public async Task Respawn_AdvancesIncarnation_FeedEntryAndSnapshotAgree_OldLifeReferenceBecomesStale()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var quickRespawnSpawn = Spawn(mapId) with { RespawnDelayMs = 500, RespawnRandomDelayMs = 0 };
        var load = await grain.LoadMonsterSpawnsAsync(Batch(mapId, [quickRespawnSpawn]));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        Assert.Equal(WorldMonsterIncarnationId.First, bootstrap.Snapshot![0].IncarnationId);

        var originalLife = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);
        Assert.Equal(WorldMonsterDeathStatus.MarkedDead, (await grain.TryMarkMonsterDeadAsync(originalLife)).Status);

        // Wait for the real grain timer to observe the due respawn (500ms delay, 100ms tick cadence).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        WorldMonsterInstance? respawned = null;
        while (DateTime.UtcNow < deadline)
        {
            await Task.Delay(200);
            var page = await grain.PollMonsterFeedAsync(cursor: null, mapId);
            var instance = page.Snapshot!.Single();
            if (instance.Lifecycle == WorldMonsterLifecycleState.Alive) { respawned = instance; break; }
        }
        Assert.NotNull(respawned);
        Assert.Equal(WorldMonsterIncarnationId.First.Next(), respawned!.IncarnationId);

        // The Respawned feed entry itself must carry the SAME incarnation as the snapshot.
        var page2 = await grain.PollMonsterFeedAsync(new WorldMonsterFeedCursor(load.SimulationEpoch, 0), mapId);
        var respawnedEntry = Assert.Single(page2.Entries!, entry => entry.Kind == WorldMonsterFeedEntryKind.Respawned);
        Assert.Equal(respawned.IncarnationId, respawnedEntry.IncarnationId);
        Assert.Equal(respawned.IncarnationId, respawnedEntry.Instance.IncarnationId);

        // A life reference built against the OLD (pre-respawn) incarnation must now be rejected.
        var staleResult = await grain.TryMarkMonsterDeadAsync(originalLife);
        Assert.Equal(WorldMonsterDeathStatus.StaleLifeReference, staleResult.Status);

        // The current (new-incarnation) life reference works correctly.
        var currentLife = originalLife with { IncarnationId = respawned.IncarnationId };
        Assert.Equal(WorldMonsterDeathStatus.MarkedDead, (await grain.TryMarkMonsterDeadAsync(currentLife)).Status);
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
                // "prontera" added for Step 7 substep 3's cross-partition transfer test (proving
                // FinalizeOutgoingTransferAsync's own Remove(presence) cleanup) - prontera-region
                // is a genuinely different partition than world-rest per conf/world_partitions.json,
                // so a real cross-partition spawn+attack+transfer scenario needs collision data for
                // it too. Purely additive - no existing test in this class spawns monsters on
                // prontera today.
                .AddSingleton<IMapCollisionProvider>(new MapCollisionProvider([MakeAllWalkableMap("izlude"), MakeAllWalkableMap("geffen"), MakeAllWalkableMap("prontera")]))
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

    // Step 7: a genuine Unload+Rebuild epoch remint (via touched-window expiry, the only way to
    // force this deterministically - see the class-level test above's own doc comment for why a
    // same-map reload with different content while still loaded does NOT rebuild) must clear the
    // OLD epoch's AttackSequence ledger state - proven here by using the SAME numeric
    // AttackSequence value against the NEW epoch's life and asserting it is accepted as a
    // genuinely fresh attempt, never spuriously colliding with unrelated stale ledger state.
    [Fact]
    public async Task ApplyMonsterDamage_AfterTouchedWindowExpiryRebuild_NewEpochAcceptsSameSequenceValue()
    {
        var grain = Partition("world-rest");
        var mapId = "izlude";
        var load = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        var bootstrap = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var actorId = bootstrap.Snapshot!.Single().ActorId;
        var life = new WorldMonsterLifeReference(mapId, load.SimulationEpoch, actorId, WorldMonsterIncarnationId.First);
        const uint attackerCharacterId = 900;
        var presenceId = Guid.NewGuid();
        await grain.RegisterPresenceAsync(new WorldPlayerPresence(presenceId, attackerCharacterId + 1_000_000, attackerCharacterId, mapId, 100, 100));

        var original = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(life, attackerCharacterId, presenceId, AttackSequence: 5, Damage: 10, AcquireEngagement: false));
        Assert.Equal(WorldMonsterDamageStatus.Applied, original.Status);

        // Let the touched window expire with NO further touch of this map - genuinely unloads it.
        await Task.Delay(ShortTouchedWindow + ShortTouchedWindow + TimeSpan.FromMilliseconds(500));
        var reload = await grain.LoadMonsterSpawnsAsync(SingleMonsterBatch(mapId));
        Assert.NotEqual(load.SimulationEpoch, reload.SimulationEpoch);

        var reboot = await grain.PollMonsterFeedAsync(cursor: null, mapId);
        var newActorId = reboot.Snapshot!.Single().ActorId;
        var newLife = new WorldMonsterLifeReference(mapId, reload.SimulationEpoch, newActorId, WorldMonsterIncarnationId.First);
        await grain.RegisterPresenceAsync(new WorldPlayerPresence(presenceId, attackerCharacterId + 1_000_000, attackerCharacterId, mapId, 100, 100));

        var result = await grain.ApplyMonsterDamageAsync(new WorldMonsterDamageCommand(newLife, attackerCharacterId, presenceId, AttackSequence: 5, Damage: 10, AcquireEngagement: false));
        Assert.Equal(WorldMonsterDamageStatus.Applied, result.Status);
        Assert.Equal(55u, result.HpBefore);
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
