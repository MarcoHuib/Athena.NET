using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Generated.GameData.Items;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.GameData.Quests;
using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.World;

// Same ICharacterQuestPersistence-shaped fake pattern as
// GeneratedCaptainCaroccIntegrationTests.RecordingQuestPersistence, reused
// here so this test derives quest state from the real persistence interface
// shape instead of a raw materialized "active quest IDs" array - Athena's
// runtime has no such array anywhere; every real quest check is
// single-quest-ID-scoped via this exact interface.
internal sealed class RecordingQuestPersistence(uint questId, CharacterQuestStatus initialState) : ICharacterQuestPersistence
{
    private readonly Dictionary<uint, CharacterQuestStatus> _states = new() { [questId] = initialState };
    public Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CancellationToken cancellationToken) =>
        Task.FromResult<CharacterQuestStatus?>(_states.GetValueOrDefault(requestedQuestId, CharacterQuestStatus.Absent));
    public Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint requestedQuestId, CharacterQuestStatus state, CancellationToken cancellationToken)
    {
        _states[requestedQuestId] = state;
        return Task.FromResult(true);
    }
}

// End-to-end DOMAIN vertical slice using the real generated G_PORING/quest
// 21008/Wood data (not test fixtures): quest 21008 active -> G_PORING exists
// -> basic attack(s) -> death -> quest drop resolves -> inventory persists ->
// Wood amount == 1 -> respawn -> second kill -> Wood amount == 2.
//
// This is deliberately a DOMAIN integration test, not a wire/network one: no
// verified iRO attack/death/item-acquisition packet exists to drive this
// through MapClientSession's actual socket path (see report). Everything
// exercised here - MonsterCombatCoordinator, QuestDropResolver,
// CharacterInventorySession - is real production code. Step 6 cutover:
// MonsterCombatCoordinator no longer depends on a live MonsterRegistry/MobInstance at all (position/
// identity/lifecycle are World-authoritative post-cutover) - these tests still use a LOCAL
// MonsterRegistry purely to derive realistic generated spawn data (real G_PORING stats, a real
// RespawnDelay), then bridge each MobInstance's current state into a hand-built WorldMonsterInstance/
// WorldMonsterActorView + WorldMonsterLifeReference, exactly like MonsterCombatCoordinatorTests does,
// never wiring the local MonsterRegistry into the coordinator itself.
public sealed class PoringQuestDropIntegrationTests
{
    private const uint AccountId = 1;
    private const uint CharId = 100;
    private const uint Quest21008 = 21008;

    private static EffectiveCharacterStats StrongEnoughToOneShot() => new(50, 9, 9, 9, 20, 9, 0, 0);

    // The actual production-realistic "fresh Novice who just completed Captain Carocc's tutorial
    // dialogue" state: BaseLevel 1, all six base stats at 1 (GeneratedCaptainCaroccIntegrationTests'
    // own fixture: `new(9, 0, 0, 1, 1, 0, 0, 20, 5, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1)`), with the
    // Blessing (+10 STR/INT/DEX) and Increase AGI (+12 AGI) effective-stat bonuses Captain's script
    // grants applied (CharacterStatusEffectState.Recalculate's documented val2 semantics).
    private static EffectiveCharacterStats RealisticPostTutorialNovice() =>
        new(Strength: 11, Agility: 13, Vitality: 1, Intelligence: 11, Dexterity: 11, Luck: 1, MoveSpeedHaste: 25, AttackSpeedBonus: 10);
    private const ushort RealisticNoviceBaseLevel = 1;

    // Builds the synchronous quest-status lookup QuestDropResolver/MonsterCombatCoordinator.Attack
    // expect, by resolving (once) each distinct QuestId appearing in the generated drop rules
    // against the given ICharacterQuestPersistence fake - mirroring how a real caller would resolve
    // real quest state before calling Attack, without inventing a materialized "all active quests"
    // concept anywhere in production code.
    private static async Task<Func<uint, CharacterQuestStatus>> BuildQuestStatusLookupAsync(ICharacterQuestPersistence persistence, IEnumerable<uint> questIds)
    {
        var snapshot = new Dictionary<uint, CharacterQuestStatus>();
        foreach (var questId in questIds.Distinct())
        {
            snapshot[questId] = await persistence.GetQuestStateAsync(AccountId, CharId, questId, CancellationToken.None) ?? CharacterQuestStatus.Absent;
        }
        return id => snapshot.GetValueOrDefault(id, CharacterQuestStatus.Absent);
    }

    // Drives one full CalculateAttack -> (local MobInstance) ApplyDamage -> BuildOutcome round trip,
    // mirroring MonsterCombatCoordinatorTests' own FakeHpLedger/DriveHit pattern and exactly how the
    // real MapClientSession composes these two coordinator calls around the real World RPC - post
    // cutover, MonsterCombatCoordinator itself has no HP-mutating Attack method any more, so this
    // test's own local MobInstance (already the sole HP authority the rest of this file uses for
    // respawn/registry bookkeeping) stands in for World's ApplyMonsterDamageAsync ledger.
    private static MonsterAttackOutcome DriveHit(
        MonsterCombatCoordinator combat,
        MobInstance target,
        WorldMonsterActorView actor,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon,
        Func<uint, CharacterQuestStatus>? questStatus)
    {
        var candidate = combat.CalculateAttack(actor, attacker, attackerBaseLevel, equippedWeapon);
        WorldMonsterDamageResult result;
        if (!target.IsAlive)
        {
            result = new WorldMonsterDamageResult(WorldMonsterDamageStatus.AlreadyDead, 0, 0, target.Spawn.Mob.MaxHp, false, null);
        }
        else
        {
            var (hpBefore, hpAfter, killedByThisHit) = target.ApplyDamage(candidate.Damage);
            var engagement = candidate.WouldAcquireEngagement ? WorldMonsterAttackedStatus.Acquired : (WorldMonsterAttackedStatus?)null;
            result = new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, hpBefore, hpAfter, target.Spawn.Mob.MaxHp, killedByThisHit, engagement);
        }
        return combat.BuildOutcome(result, actor, candidate.IsMiss, questStatus);
    }

    // Bridges one local MobInstance's CURRENT state into a (WorldMonsterActorView, WorldMonsterLifeReference)
    // pair against the given epoch, mirroring WorldMonsterMapSimulation.ToWireInstance's own conversion
    // on the real World side.
    private static (WorldMonsterActorView Actor, WorldMonsterLifeReference Life) BridgeToWorldView(MobInstance instance, WorldSimulationEpoch epoch)
    {
        var position = instance.GetPosition();
        var incarnationId = new WorldMonsterIncarnationId(instance.IncarnationId.Value);
        var wireInstance = new WorldMonsterInstance(
            ActorId: instance.ActorId, IncarnationId: incarnationId, MapId: instance.Map, MobId: instance.Spawn.Mob.Id,
            X: position.X, Y: position.Y,
            Lifecycle: instance.IsAlive ? WorldMonsterLifecycleState.Alive : WorldMonsterLifecycleState.Dead,
            IsWalking: instance.IsWalking, DestinationX: instance.MovementDestination.X, DestinationY: instance.MovementDestination.Y,
            Engagement: WorldMonsterEngagementState.Unengaged, EngagedTarget: null,
            CurrentHp: instance.CurrentHp, MaxHp: instance.Spawn.Mob.MaxHp);
        var life = new WorldMonsterLifeReference(instance.Map, epoch, instance.ActorId, incarnationId);
        return (new WorldMonsterActorView(wireInstance), life);
    }

    [Fact]
    public async Task FullVerticalSlice_TwoKillsGrantTwoWood_WithRespawnBetween()
    {
        var clock = new FakeTimeProvider();
        var registry = new MonsterRegistry(
            [GeneratedMobSpawnRegistry.GetForMap("int_land03").Single(s => s.Mob.Id == GeneratedMobs.GPoring.Id)], // matching real generated data.
            new WorldActorIdAllocator().Allocate,
            new FixedCellSelector(50, 50),
            clock);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        var combatState = new MonsterAttackCadenceStore();
        combatState.Register(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value));
        var combat = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules());
        var inventoryPersistence = new FakeInventoryPersistence();
        var inventorySession = new CharacterInventorySession(AccountId, CharId, inventoryPersistence);
        var questPersistence = new RecordingQuestPersistence(Quest21008, CharacterQuestStatus.Active);
        var questStatus = await BuildQuestStatusLookupAsync(questPersistence, GeneratedQuestDrops.All.Select(rule => rule.QuestId));

        Assert.Equal("int_land03", target.Map);
        Assert.Equal(GeneratedMobs.GPoring.Id, target.Spawn.Mob.Id);
        Assert.NotEqual(1002, target.Spawn.Mob.Id); // Must be G_PORING(2401), never ordinary Poring(1002).

        // --- First kill ---
        MonsterAttackOutcome outcome = default;
        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            var (actor, _) = BridgeToWorldView(target, epoch);
            outcome = DriveHit(combat, target, actor, StrongEnoughToOneShot(), attackerBaseLevel: 1, null, questStatus);
            if (outcome.KilledByThisHit) break;
        }
        Assert.True(outcome.KilledByThisHit);
        Assert.Single(outcome.QuestDrops);
        Assert.Equal(GeneratedItems.Wood.Id, outcome.QuestDrops[0].ItemId);

        var firstAward = await inventorySession.AddItemAsync(GeneratedItems.Wood, (uint)outcome.QuestDrops[0].Count, CancellationToken.None);
        Assert.True(firstAward.Success);
        Assert.Equal(1u, firstAward.NewAmount);

        // DriveHit's damage mutation above already landed directly on the local MobInstance (this
        // file's own HP ledger, standing in for World's ApplyMonsterDamageAsync post-cutover - see
        // DriveHit's own doc comment) - target should already be dead from the killing hit.
        registry.ScheduleRespawnIfNeeded(target);
        Assert.False(target.IsAlive);

        // --- Respawn ---
        clock.Advance(TimeSpan.FromMilliseconds(target.Spawn.RespawnDelay + 1));
        var respawned = registry.ProcessDueRespawns();
        Assert.Equal(1, respawned.Count);
        // Mirrors MapTcpServer's own production respawn fan-out, which re-registers each respawned
        // instance's cadence entry (fresh, no NextAttackAt scheduled yet) into the SAME store under
        // its new incarnation. The store no longer tracks HP at all post-cutover - the local
        // MobInstance itself (asserted below) is this test's sole HP authority for the respawn.
        foreach (var instance in respawned)
            combatState.Register(instance.Map, epoch, instance.ActorId, new WorldMonsterIncarnationId(instance.IncarnationId.Value));
        Assert.True(target.IsAlive);
        Assert.Equal(target.Spawn.Mob.MaxHp, target.CurrentHp);
        var respawnedKey = new MonsterCombatKey(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value));
        Assert.True(combatState.TryGet(respawnedKey, out var respawnedState));
        Assert.Null(respawnedState.NextAttackAt); // Fresh registration for the new incarnation has no cadence scheduled yet.

        // --- Second kill ---
        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            var (actor, _) = BridgeToWorldView(target, epoch);
            outcome = DriveHit(combat, target, actor, StrongEnoughToOneShot(), attackerBaseLevel: 1, null, questStatus);
            if (outcome.KilledByThisHit) break;
        }
        Assert.True(outcome.KilledByThisHit);
        Assert.Single(outcome.QuestDrops);

        var secondAward = await inventorySession.AddItemAsync(GeneratedItems.Wood, (uint)outcome.QuestDrops[0].Count, CancellationToken.None);
        Assert.True(secondAward.Success);
        Assert.Equal(2u, secondAward.NewAmount);
    }

    [Fact]
    public async Task WithoutActiveQuest_KillGrantsNoWood()
    {
        var clock = new FakeTimeProvider();
        var registry = new MonsterRegistry(
            [GeneratedMobSpawnRegistry.GetForMap("int_land").Single(s => s.Mob.Id == GeneratedMobs.GPoring.Id)],
            new WorldActorIdAllocator().Allocate,
            new FixedCellSelector(50, 50),
            clock);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        var combatState = new MonsterAttackCadenceStore();
        combatState.Register(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value));
        var combat = new MonsterCombatCoordinator(new QuestDropResolver(GeneratedQuestDrops.All), new RenewalBasicAttackRules());
        var noQuests = new RecordingQuestPersistence(Quest21008, CharacterQuestStatus.Absent);
        var questStatus = await BuildQuestStatusLookupAsync(noQuests, GeneratedQuestDrops.All.Select(rule => rule.QuestId));

        MonsterAttackOutcome outcome = default;
        for (var i = 0; i < 20 && target.IsAlive; i++)
        {
            var (actor, _) = BridgeToWorldView(target, epoch);
            outcome = DriveHit(combat, target, actor, StrongEnoughToOneShot(), 1, null, questStatus);
            if (outcome.KilledByThisHit) break;
        }

        Assert.True(outcome.KilledByThisHit);
        Assert.Empty(outcome.QuestDrops);
    }

    [Fact]
    public async Task RealisticPostTutorialNoviceState_CanCompleteTheSupportedCombatSlice()
    {
        // Proves the ACTUAL supported tutorial character (not synthetic stats) can kill G_PORING
        // and receive Wood - i.e. that the real RenewalBasicAttackRules formula, applied to the real
        // post-Captain-Carocc-buffs stat state, is sufficient to complete this content within a
        // reasonable number of attacks, not merely that the surrounding plumbing works.
        var clock = new FakeTimeProvider();
        var registry = new MonsterRegistry(
            [GeneratedMobSpawnRegistry.GetForMap("int_land").Single(s => s.Mob.Id == GeneratedMobs.GPoring.Id)],
            new WorldActorIdAllocator().Allocate,
            new FixedCellSelector(50, 50),
            clock);
        var target = registry.AllInstances[0];
        var epoch = WorldSimulationEpoch.NewEpoch();
        var combatState = new MonsterAttackCadenceStore();
        combatState.Register(target.Map, epoch, target.ActorId, new WorldMonsterIncarnationId(target.IncarnationId.Value));
        var combat = new MonsterCombatCoordinator(new QuestDropResolver(GeneratedQuestDrops.All), new RenewalBasicAttackRules());
        var inventorySession = new CharacterInventorySession(AccountId, CharId, new FakeInventoryPersistence());
        var questPersistence = new RecordingQuestPersistence(Quest21008, CharacterQuestStatus.Active);
        var questStatus = await BuildQuestStatusLookupAsync(questPersistence, GeneratedQuestDrops.All.Select(rule => rule.QuestId));

        MonsterAttackOutcome outcome = default;
        var attackCount = 0;
        // G_PORING has 55 HP; a fresh post-tutorial Novice's single-digit-per-hit damage
        // (see Gameplay/Rules/Renewal/WeaponAttackCalculatorTests for the exact traced formula)
        // needs several hits, not one.
        for (var i = 0; i < 55 && target.IsAlive; i++, attackCount++)
        {
            var (actor, _) = BridgeToWorldView(target, epoch);
            outcome = DriveHit(combat, target, actor, RealisticPostTutorialNovice(), RealisticNoviceBaseLevel, null, questStatus);
            if (outcome.KilledByThisHit) break;
        }

        Assert.True(outcome.KilledByThisHit, $"The realistic post-tutorial Novice state failed to kill G_PORING within {attackCount} attacks.");
        Assert.Single(outcome.QuestDrops);

        var award = await inventorySession.AddItemAsync(GeneratedItems.Wood, (uint)outcome.QuestDrops[0].Count, CancellationToken.None);
        Assert.True(award.Success);
        Assert.Equal(1u, award.NewAmount);
    }
}
