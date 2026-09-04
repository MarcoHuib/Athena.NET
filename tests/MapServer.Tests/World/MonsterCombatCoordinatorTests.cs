using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.World;

// Step 6 cutover: MonsterCombatCoordinator no longer takes a MonsterRegistry dependency and no
// longer mutates any local MobInstance target/engagement/respawn state at all (see
// MonsterCombatCoordinator's own top-of-file doc comment - TryAcquireTarget/ScheduleRespawnIfNeeded
// are GONE; World's own NotifyMonsterAttackedAsync/TryMarkMonsterDeadAsync own that now). These
// tests exercise the coordinator's own REMAINING responsibility - damage calculation against
// MonsterCombatStateStore, the EngagementAcquired local signal, and quest-drop resolution - against
// a hand-built WorldMonsterActorView (wrapping a WorldMonsterInstance) rather than a live MobInstance,
// since production no longer constructs one for this path either.
public sealed class MonsterCombatCoordinatorTests
{
    private const uint Quest21008 = 21008;
    private const int WoodId = 6008;
    private const int GPoringMobId = 2401; // Resolves through GeneratedMobRegistry - MaxHp 55, Mode includes CanAttack.

    private static EffectiveCharacterStats StrongAttacker() => new(50, 9, 9, 9, 20, 9, 0, 0);

    private static WeaponItemDefinition MakeKnife() => new(
        Id: 1201, AegisName: "Knife", Name: "Knife", Stackable: false, ClientViewId: 1201,
        Attack: 17, WeaponLevel: 1, WeaponType: WeaponType.Dagger, Range: 1, EquipLocation: 0x000002,
        Source: new("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    private static Func<uint, CharacterQuestStatus> ActiveOnly(uint questId) => id => id == questId ? CharacterQuestStatus.Active : CharacterQuestStatus.Absent;
    private static readonly Func<uint, CharacterQuestStatus> NoActiveQuests = _ => CharacterQuestStatus.Absent;

    private sealed record Scenario(MonsterCombatCoordinator Coordinator, MonsterCombatStateStore CombatState, WorldMonsterActorView Target, WorldMonsterLifeReference Life);

    private static Scenario MakeScenario(uint maxHp = 55)
    {
        const string mapId = "int_land01";
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var incarnationId = WorldMonsterIncarnationId.First;

        var instance = new WorldMonsterInstance(
            ActorId: actorId, IncarnationId: incarnationId, MapId: mapId, MobId: GPoringMobId,
            X: 50, Y: 50, Lifecycle: WorldMonsterLifecycleState.Alive, IsWalking: false,
            DestinationX: 50, DestinationY: 50, Engagement: WorldMonsterEngagementState.Unengaged, EngagedTarget: null);

        var combatState = new MonsterCombatStateStore();
        combatState.Register(mapId, epoch, actorId, incarnationId, maxHp);

        var questDrops = new QuestDropResolver([new(Quest21008, GPoringMobId, WoodId, 1, 10000, new("rAthena", "abc", "quest_db.yml", 1))]);
        var coordinator = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules(), combatState);
        var life = new WorldMonsterLifeReference(mapId, epoch, actorId, incarnationId);

        return new Scenario(coordinator, combatState, new WorldMonsterActorView(instance), life);
    }

    private static bool IsAlive(Scenario scenario) => scenario.CombatState.TryGet(scenario.Life, out var state) && state.CurrentHp > 0;

    [Fact]
    public void Attack_NonLethalHit_NoDropsNoDeath()
    {
        var scenario = MakeScenario(maxHp: 9999);
        var outcome = scenario.Coordinator.Attack(scenario.Target, scenario.Life, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(outcome.Accepted);
        Assert.False(outcome.KilledByThisHit);
        Assert.Empty(outcome.QuestDrops);
        Assert.True(IsAlive(scenario));
    }

    [Fact]
    public void Attack_LethalHit_WithActiveQuest_AwardsWoodExactlyOnce()
    {
        var scenario = MakeScenario(maxHp: 1);
        var outcome = scenario.Coordinator.Attack(scenario.Target, scenario.Life, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(outcome.KilledByThisHit);
        Assert.Single(outcome.QuestDrops);
        Assert.Equal(WoodId, outcome.QuestDrops[0].ItemId);
        Assert.False(IsAlive(scenario));
    }

    [Fact]
    public void Attack_LethalHit_WithoutActiveQuest_NoDrop()
    {
        var scenario = MakeScenario(maxHp: 1);
        var outcome = scenario.Coordinator.Attack(scenario.Target, scenario.Life, StrongAttacker(), 1, null, NoActiveQuests);

        Assert.True(outcome.KilledByThisHit);
        Assert.Empty(outcome.QuestDrops);
    }

    [Fact]
    public void Attack_AgainstAlreadyDeadMonster_IsRejected()
    {
        var scenario = MakeScenario(maxHp: 1);
        scenario.Coordinator.Attack(scenario.Target, scenario.Life, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        var secondAttack = scenario.Coordinator.Attack(scenario.Target, scenario.Life, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.False(secondAttack.Accepted);
        Assert.Empty(secondAttack.QuestDrops); // No second award for the same death.
    }

    [Fact]
    public void Attack_StaleLife_IsRejected()
    {
        // A life reference that does not match anything ever Register()'d against this store
        // (different IncarnationId) - MonsterCombatStateStore.ApplyDamage must report StaleLife,
        // never silently create/mutate an entry (see that store's own doc comment).
        var scenario = MakeScenario(maxHp: 55);
        var staleLife = scenario.Life with { IncarnationId = scenario.Life.IncarnationId.Next() };

        var outcome = scenario.Coordinator.Attack(scenario.Target, staleLife, StrongAttacker(), 1, null, NoActiveQuests);

        Assert.False(outcome.Accepted);
    }

    [Fact]
    public void TwoLethalAttacksInSuccession_OnlyFirstCountsAsKill()
    {
        var scenario = MakeScenario(maxHp: 1);
        var first = scenario.Coordinator.Attack(scenario.Target, scenario.Life, StrongAttacker(), 1, null, ActiveOnly(Quest21008));
        var second = scenario.Coordinator.Attack(scenario.Target, scenario.Life, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(first.KilledByThisHit);
        Assert.False(second.Accepted);
        Assert.Single(first.QuestDrops);
    }

    // A weak (fresh-Novice-like) attacker unarmed frequently misses G_PORING; the same
    // attacker with a Knife equipped should deal real damage - proving the coordinator
    // actually dispatches to WeaponAttackCalculator (not silently reusing the unarmed
    // path) whenever a non-null WeaponItemDefinition is supplied, without depending on
    // either calculator's exact per-hit value.
    [Fact]
    public void Attack_WithEquippedWeapon_DispatchesToWeaponCalculator_DealsMoreDamageThanUnarmed()
    {
        var freshNovice = new EffectiveCharacterStats(9, 9, 9, 9, 9, 9, 0, 0);
        var unarmedScenario = MakeScenario(maxHp: 9999);
        var armedScenario = MakeScenario(maxHp: 9999);

        var unarmedOutcome = unarmedScenario.Coordinator.Attack(unarmedScenario.Target, unarmedScenario.Life, freshNovice, 1, null, NoActiveQuests);
        var armedOutcome = armedScenario.Coordinator.Attack(armedScenario.Target, armedScenario.Life, freshNovice, 1, MakeKnife(), NoActiveQuests);

        Assert.True(unarmedOutcome.Accepted);
        Assert.True(armedOutcome.Accepted);
        var unarmedDamage = unarmedOutcome.HpBefore - unarmedOutcome.HpAfter;
        var armedDamage = armedOutcome.HpBefore - armedOutcome.HpAfter;
        Assert.True(armedDamage > unarmedDamage);
    }

    // Re-equipping/unequipping mid-session must change the very next attack's
    // calculation with no coordinator-side caching to invalidate - the coordinator
    // never resolves equipment itself, so this just confirms passing null vs a weapon
    // on successive calls against the SAME life both take effect immediately.
    [Fact]
    public void Attack_SameLife_SwitchingWeaponArgumentBetweenCalls_ChangesCalculatorUsed()
    {
        var freshNovice = new EffectiveCharacterStats(9, 9, 9, 9, 9, 9, 0, 0);
        var scenario = MakeScenario(maxHp: 999999);

        var unarmedOutcome = scenario.Coordinator.Attack(scenario.Target, scenario.Life, freshNovice, 1, null, NoActiveQuests);
        var armedOutcome = scenario.Coordinator.Attack(scenario.Target, scenario.Life, freshNovice, 1, MakeKnife(), NoActiveQuests);
        var unarmedAgainOutcome = scenario.Coordinator.Attack(scenario.Target, scenario.Life, freshNovice, 1, null, NoActiveQuests);

        var unarmedDamage = unarmedOutcome.HpBefore - unarmedOutcome.HpAfter;
        var armedDamage = armedOutcome.HpBefore - armedOutcome.HpAfter;
        var unarmedAgainDamage = unarmedAgainOutcome.HpBefore - unarmedAgainOutcome.HpAfter;

        Assert.True(armedDamage > unarmedDamage);
        Assert.True(armedDamage > unarmedAgainDamage);
    }

    // ===== EngagementAcquired: the coordinator's own LOCAL signal (never a mutation - World's
    // NotifyMonsterAttackedAsync is the sole authority for target acquisition post-cutover; see
    // MonsterCombatCoordinator's own doc comment) =====

    [Fact]
    public void Attack_NonLethalHit_AgainstCanAttackCapableMob_SignalsEngagementAcquired()
    {
        var scenario = MakeScenario(maxHp: 9999); // G_PORING's Mode includes MobMode.CanAttack.

        var outcome = scenario.Coordinator.Attack(scenario.Target, scenario.Life, StrongAttacker(), 1, null, NoActiveQuests);

        Assert.True(outcome.Accepted);
        Assert.True(outcome.EngagementAcquired);
    }

    [Fact]
    public void Attack_LethalHit_NeverSignalsEngagementAcquired()
    {
        var scenario = MakeScenario(maxHp: 1);

        var outcome = scenario.Coordinator.Attack(scenario.Target, scenario.Life, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(outcome.KilledByThisHit);
        Assert.False(outcome.EngagementAcquired);
    }

    // ===== Section 15: quest-state resolution is LAZY, only on a killing hit =====

    [Fact]
    public async Task AttackAsync_NonLethalHit_NeverInvokesTheQuestStateResolver()
    {
        var scenario = MakeScenario(maxHp: 9999);
        var resolverCallCount = 0;
        Task<Func<uint, CharacterQuestStatus>> Resolver()
        {
            resolverCallCount++;
            return Task.FromResult(ActiveOnly(Quest21008));
        }

        var outcome = await scenario.Coordinator.AttackAsync(scenario.Target, scenario.Life, StrongAttacker(), 1, null, Resolver);

        Assert.False(outcome.KilledByThisHit);
        Assert.Equal(0, resolverCallCount);
    }

    [Fact]
    public async Task AttackAsync_LethalHit_InvokesTheQuestStateResolverExactlyOnce()
    {
        var scenario = MakeScenario(maxHp: 1);
        var resolverCallCount = 0;
        Task<Func<uint, CharacterQuestStatus>> Resolver()
        {
            resolverCallCount++;
            return Task.FromResult(ActiveOnly(Quest21008));
        }

        var outcome = await scenario.Coordinator.AttackAsync(scenario.Target, scenario.Life, StrongAttacker(), 1, null, Resolver);

        Assert.True(outcome.KilledByThisHit);
        Assert.Single(outcome.QuestDrops);
        Assert.Equal(1, resolverCallCount);
    }

    // For a multi-hit kill (repeated ordinary hits, the last one lethal), only that FINAL hit may
    // ever invoke the resolver - reproducing the exact live-log pattern (hit 1 -> roundtrip, hit 2
    // -> roundtrip, hit 3 -> kill) this optimization fixes.
    [Fact]
    public async Task AttackAsync_MultiHitKill_ResolverInvokedOnlyOnTheFinalLethalHit()
    {
        var scenario = MakeScenario(maxHp: 3); // Three 1-damage hits to kill.
        var weakAttacker = new EffectiveCharacterStats(1, 1, 1, 1, 1, 1, 0, 0);
        var resolverCallCount = 0;
        Task<Func<uint, CharacterQuestStatus>> Resolver()
        {
            resolverCallCount++;
            return Task.FromResult(ActiveOnly(Quest21008));
        }

        MonsterAttackOutcome outcome = default;
        for (var i = 0; i < 20 && IsAlive(scenario); i++)
            outcome = await scenario.Coordinator.AttackAsync(scenario.Target, scenario.Life, StrongAttacker(), 1, null, Resolver);

        Assert.True(outcome.KilledByThisHit);
        Assert.Equal(1, resolverCallCount);
    }
}
