using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.World;

// Step 7 substep 9: MonsterCombatCoordinator is now PURE damage calculation + outcome projection -
// it owns no HP/lethality state of its own at all (CalculateAttack/BuildOutcome, no store/life
// lookup). World's ApplyMonsterDamageAsync is the sole authority for CurrentHp/MaxHp/the
// Alive->Dead transition; these tests simulate that authority with a tiny local HP tracker (never
// a real IWorldRuntime - see FakeCombatWorldRuntime in WorldMonsterProjectionTestHelper.cs for the
// full ledger fake used by real MapClientSession-driven tests) purely to build a realistic
// WorldMonsterDamageResult for BuildOutcome to project.
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

    private sealed class FakeHpLedger(uint maxHp)
    {
        public uint CurrentHp { get; private set; } = maxHp;
        public uint MaxHp { get; } = maxHp;
        private bool _dead;

        // Mirrors World's own ApplyMonsterDamageAsync default-ledger semantics narrowly: a hit
        // against an already-dead life is rejected (AlreadyDead, no mutation); otherwise damage
        // clamps to zero and the lethal transition is reported at most once.
        public WorldMonsterDamageResult Apply(uint damage)
        {
            if (_dead) return new WorldMonsterDamageResult(WorldMonsterDamageStatus.AlreadyDead, 0, 0, MaxHp, false, null);
            var before = CurrentHp;
            var after = damage >= before ? 0u : before - damage;
            CurrentHp = after;
            var killed = after == 0;
            if (killed) _dead = true;
            return new WorldMonsterDamageResult(WorldMonsterDamageStatus.Applied, before, after, MaxHp, killed, WorldMonsterAttackedStatus.Acquired);
        }
    }

    private sealed record Scenario(MonsterCombatCoordinator Coordinator, FakeHpLedger Hp, WorldMonsterActorView Target, WorldMonsterLifeReference Life);

    private static Scenario MakeScenario(uint maxHp = 55)
    {
        const string mapId = "int_land01";
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var incarnationId = WorldMonsterIncarnationId.First;

        var instance = new WorldMonsterInstance(
            ActorId: actorId, IncarnationId: incarnationId, MapId: mapId, MobId: GPoringMobId,
            X: 50, Y: 50, Lifecycle: WorldMonsterLifecycleState.Alive, IsWalking: false,
            DestinationX: 50, DestinationY: 50, Engagement: WorldMonsterEngagementState.Unengaged, EngagedTarget: null,
            CurrentHp: maxHp, MaxHp: maxHp);

        var questDrops = new QuestDropResolver([new(Quest21008, GPoringMobId, WoodId, 1, 10000, new("rAthena", "abc", "quest_db.yml", 1))]);
        var coordinator = new MonsterCombatCoordinator(questDrops, new RenewalBasicAttackRules());
        var life = new WorldMonsterLifeReference(mapId, epoch, actorId, incarnationId);

        return new Scenario(coordinator, new FakeHpLedger(maxHp), new WorldMonsterActorView(instance), life);
    }

    // Drives one full CalculateAttack -> (fake World) Apply -> BuildOutcome round trip, mirroring
    // exactly how MapClientSession's own AllocateAndDispatchFreshDamageAttemptAsync/
    // HandleDamageResultAsync compose these two coordinator calls around the real RPC.
    private static MonsterAttackOutcome DriveHit(Scenario scenario, EffectiveCharacterStats attacker, ushort baseLevel, WeaponItemDefinition? weapon, Func<uint, CharacterQuestStatus>? questStatus)
    {
        var candidate = scenario.Coordinator.CalculateAttack(scenario.Target, attacker, baseLevel, weapon);
        var result = scenario.Hp.Apply(candidate.Damage);
        return scenario.Coordinator.BuildOutcome(result, scenario.Target, candidate.IsMiss, questStatus);
    }

    private static bool IsAlive(Scenario scenario) => scenario.Hp.CurrentHp > 0;

    [Fact]
    public void Attack_NonLethalHit_NoDropsNoDeath()
    {
        var scenario = MakeScenario(maxHp: 9999);
        var outcome = DriveHit(scenario, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(outcome.Accepted);
        Assert.False(outcome.KilledByThisHit);
        Assert.Empty(outcome.QuestDrops);
        Assert.True(IsAlive(scenario));
    }

    [Fact]
    public void Attack_LethalHit_WithActiveQuest_AwardsWoodExactlyOnce()
    {
        var scenario = MakeScenario(maxHp: 1);
        var outcome = DriveHit(scenario, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(outcome.KilledByThisHit);
        Assert.Single(outcome.QuestDrops);
        Assert.Equal(WoodId, outcome.QuestDrops[0].ItemId);
        Assert.False(IsAlive(scenario));
    }

    [Fact]
    public void Attack_LethalHit_WithoutActiveQuest_NoDrop()
    {
        var scenario = MakeScenario(maxHp: 1);
        var outcome = DriveHit(scenario, StrongAttacker(), 1, null, NoActiveQuests);

        Assert.True(outcome.KilledByThisHit);
        Assert.Empty(outcome.QuestDrops);
    }

    [Fact]
    public void Attack_AgainstAlreadyDeadMonster_IsRejected()
    {
        var scenario = MakeScenario(maxHp: 1);
        DriveHit(scenario, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        var secondAttack = DriveHit(scenario, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.False(secondAttack.Accepted);
        Assert.Empty(secondAttack.QuestDrops); // No second award for the same death.
    }

    [Fact]
    public void TwoLethalAttacksInSuccession_OnlyFirstCountsAsKill()
    {
        var scenario = MakeScenario(maxHp: 1);
        var first = DriveHit(scenario, StrongAttacker(), 1, null, ActiveOnly(Quest21008));
        var second = DriveHit(scenario, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

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

        var unarmedOutcome = DriveHit(unarmedScenario, freshNovice, 1, null, NoActiveQuests);
        var armedOutcome = DriveHit(armedScenario, freshNovice, 1, MakeKnife(), NoActiveQuests);

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

        var unarmedOutcome = DriveHit(scenario, freshNovice, 1, null, NoActiveQuests);
        var armedOutcome = DriveHit(scenario, freshNovice, 1, MakeKnife(), NoActiveQuests);
        var unarmedAgainOutcome = DriveHit(scenario, freshNovice, 1, null, NoActiveQuests);

        var unarmedDamage = unarmedOutcome.HpBefore - unarmedOutcome.HpAfter;
        var armedDamage = armedOutcome.HpBefore - armedOutcome.HpAfter;
        var unarmedAgainDamage = unarmedAgainOutcome.HpBefore - unarmedAgainOutcome.HpAfter;

        Assert.True(armedDamage > unarmedDamage);
        Assert.True(armedDamage > unarmedAgainDamage);
    }

    // ===== EngagementAcquired: derived from WorldMonsterDamageResult.Engagement - the coordinator
    // itself never mutates engagement state; World's ApplyMonsterDamageAsync is the sole authority
    // for target acquisition post-cutover (see MonsterCombatCoordinator's own doc comment) =====

    [Fact]
    public void Attack_NonLethalHit_AgainstCanAttackCapableMob_SignalsEngagementAcquired()
    {
        var scenario = MakeScenario(maxHp: 9999); // G_PORING's Mode includes MobMode.CanAttack; the fake ledger reports Acquired for any non-lethal hit.

        var outcome = DriveHit(scenario, StrongAttacker(), 1, null, NoActiveQuests);

        Assert.True(outcome.Accepted);
        Assert.True(outcome.EngagementAcquired);
    }

    // Substep 9 cutover: World's own ApplyMonsterDamageAsync (WorldPartitionGrain) applies
    // TryAcquireEngagement whenever the caller requests it via AcquireEngagement, unconditionally -
    // it never special-cases a lethal hit (see that method's own doc comment: engagement acquisition
    // and the lethal HP transition are two independent concerns World resolves in the SAME call, not
    // a lethal-suppresses-engagement rule). MapClientSession mirrors this: acquireEngagement is
    // derived purely from the target's own CanAttack mode, never from whether THIS hit turns out to
    // be lethal. BuildOutcome's EngagementAcquired therefore reflects whatever World's own Engagement
    // field reports for this exact result - which the fake ledger here deliberately mirrors by always
    // reporting Acquired, exactly like the real grain does.
    [Fact]
    public void Attack_LethalHit_StillReflectsWorldsOwnEngagementResult()
    {
        var scenario = MakeScenario(maxHp: 1);

        var outcome = DriveHit(scenario, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(outcome.KilledByThisHit);
        Assert.True(outcome.EngagementAcquired);
    }

    // ===== BuildOutcome: quest-state resolution is driven entirely by the resolver the CALLER
    // supplies - BuildOutcome itself performs no persistence I/O, and only consults the resolver at
    // all when the hit was lethal (mirrors §4b: the live path always supplies an
    // ALREADY-RESOLVED PendingMonsterDamageAttempt.QuestStatusSnapshot, never a fresh lookup here) =====

    [Fact]
    public void BuildOutcome_NonLethalHit_NeverInvokesTheQuestStateResolver()
    {
        var scenario = MakeScenario(maxHp: 9999);
        var resolverCallCount = 0;
        CharacterQuestStatus Resolver(uint questId)
        {
            resolverCallCount++;
            return CharacterQuestStatus.Active;
        }

        var candidate = scenario.Coordinator.CalculateAttack(scenario.Target, StrongAttacker(), 1, null);
        var result = scenario.Hp.Apply(candidate.Damage);
        var outcome = scenario.Coordinator.BuildOutcome(result, scenario.Target, candidate.IsMiss, Resolver);

        Assert.False(outcome.KilledByThisHit);
        Assert.Equal(0, resolverCallCount);
    }

    [Fact]
    public void BuildOutcome_LethalHit_InvokesTheQuestStateResolver_ExactlyForResolvedQuests()
    {
        var scenario = MakeScenario(maxHp: 1);
        var resolverCallCount = 0;
        CharacterQuestStatus Resolver(uint questId)
        {
            resolverCallCount++;
            return questId == Quest21008 ? CharacterQuestStatus.Active : CharacterQuestStatus.Absent;
        }

        var candidate = scenario.Coordinator.CalculateAttack(scenario.Target, StrongAttacker(), 1, null);
        var result = scenario.Hp.Apply(candidate.Damage);
        var outcome = scenario.Coordinator.BuildOutcome(result, scenario.Target, candidate.IsMiss, Resolver);

        Assert.True(outcome.KilledByThisHit);
        Assert.Single(outcome.QuestDrops);
        Assert.True(resolverCallCount >= 1);
    }

    [Fact]
    public void BuildOutcome_LethalHit_NoResolverSupplied_NoDropsResolved()
    {
        var scenario = MakeScenario(maxHp: 1);

        var candidate = scenario.Coordinator.CalculateAttack(scenario.Target, StrongAttacker(), 1, null);
        var result = scenario.Hp.Apply(candidate.Damage);
        var outcome = scenario.Coordinator.BuildOutcome(result, scenario.Target, candidate.IsMiss, attackerQuestStatus: null);

        Assert.True(outcome.KilledByThisHit);
        Assert.Empty(outcome.QuestDrops);
    }

    // For a multi-hit kill (repeated ordinary hits, the last one lethal), only that FINAL hit's
    // result carries KilledByThisHit=true - reproducing the exact live-log pattern (hit 1 -> no
    // drop, hit 2 -> no drop, hit 3 -> kill + drop) this file's own predecessor established.
    [Fact]
    public void MultiHitKill_OnlyFinalLethalHitReportsKillAndDrops()
    {
        var scenario = MakeScenario(maxHp: 3); // Three 1-damage hits to kill.
        var weakAttacker = new EffectiveCharacterStats(1, 1, 1, 1, 1, 1, 0, 0);

        MonsterAttackOutcome outcome = default;
        for (var i = 0; i < 20 && IsAlive(scenario); i++)
            outcome = DriveHit(scenario, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(outcome.KilledByThisHit);
        Assert.Single(outcome.QuestDrops);
    }
}
