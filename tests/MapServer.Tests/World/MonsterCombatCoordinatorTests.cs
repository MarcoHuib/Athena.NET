using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class MonsterCombatCoordinatorTests
{
    private const uint Quest21008 = 21008;
    private const int WoodId = 6008;

    private static MobDefinition MakeGPoring(uint maxHp = 55) => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: maxHp,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872,
        BaseExp: 0, JobExp: 0, Mode: MobMode.CanMove,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static EffectiveCharacterStats StrongAttacker() => new(50, 9, 9, 9, 20, 9, 0, 0);

    private static WeaponItemDefinition MakeKnife() => new(
        Id: 1201, AegisName: "Knife", Name: "Knife", Stackable: false, ClientViewId: 1201,
        Attack: 17, WeaponLevel: 1, WeaponType: WeaponType.Dagger, Range: 1, EquipLocation: 0x000002,
        Source: new("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    private static Func<uint, CharacterQuestStatus> ActiveOnly(uint questId) => id => id == questId ? CharacterQuestStatus.Active : CharacterQuestStatus.Absent;
    private static readonly Func<uint, CharacterQuestStatus> NoActiveQuests = _ => CharacterQuestStatus.Absent;

    private static (MonsterCombatCoordinator Coordinator, MobInstance Instance) MakeScenario(uint maxHp = 55, TimeProvider? clock = null)
    {
        var spawn = new MobSpawnDefinition(MakeGPoring(maxHp), "int_land01", 1, 5000, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator(), new FixedCellSelector(50, 50), clock ?? new FakeTimeProvider());
        var questDrops = new QuestDropResolver([new(Quest21008, 2401, WoodId, 1, 10000, new("rAthena", "abc", "quest_db.yml", 1))]);
        return (new MonsterCombatCoordinator(registry, questDrops, new RenewalBasicAttackRules()), registry.AllInstances[0]);
    }

    [Fact]
    public void Attack_NonLethalHit_NoDropsNoDeath()
    {
        var (coordinator, instance) = MakeScenario(maxHp: 9999);
        var outcome = coordinator.Attack(instance, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(outcome.Accepted);
        Assert.False(outcome.KilledByThisHit);
        Assert.Empty(outcome.QuestDrops);
        Assert.True(instance.IsAlive);
    }

    [Fact]
    public void Attack_LethalHit_WithActiveQuest_AwardsWoodExactlyOnce()
    {
        var (coordinator, instance) = MakeScenario(maxHp: 1);
        var outcome = coordinator.Attack(instance, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.True(outcome.KilledByThisHit);
        Assert.Single(outcome.QuestDrops);
        Assert.Equal(WoodId, outcome.QuestDrops[0].ItemId);
        Assert.False(instance.IsAlive);
    }

    [Fact]
    public void Attack_LethalHit_WithoutActiveQuest_NoDrop()
    {
        var (coordinator, instance) = MakeScenario(maxHp: 1);
        var outcome = coordinator.Attack(instance, StrongAttacker(), 1, null, NoActiveQuests);

        Assert.True(outcome.KilledByThisHit);
        Assert.Empty(outcome.QuestDrops);
    }

    [Fact]
    public void Attack_AgainstAlreadyDeadMonster_IsRejected()
    {
        var (coordinator, instance) = MakeScenario(maxHp: 1);
        coordinator.Attack(instance, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        var secondAttack = coordinator.Attack(instance, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        Assert.False(secondAttack.Accepted);
        Assert.Empty(secondAttack.QuestDrops); // No second award for the same death.
    }

    [Fact]
    public void Attack_LethalHit_SchedulesRespawn()
    {
        var clock = new FakeTimeProvider();
        var (coordinator, instance) = MakeScenario(maxHp: 1, clock: clock);
        coordinator.Attack(instance, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

        clock.Advance(TimeSpan.FromMilliseconds(5000));
        Assert.False(instance.TryRespawn(clock.GetUtcNow().UtcTicks - 1, () => (true, new MobPosition(0, 0)))); // Not due yet at an earlier instant.
        Assert.True(instance.TryRespawn(clock.GetUtcNow().UtcTicks, () => (true, new MobPosition(0, 0))));
        Assert.True(instance.IsAlive);
    }

    [Fact]
    public void TwoLethalAttacksInSuccession_OnlyFirstCountsAsKill()
    {
        var (coordinator, instance) = MakeScenario(maxHp: 1);
        var first = coordinator.Attack(instance, StrongAttacker(), 1, null, ActiveOnly(Quest21008));
        var second = coordinator.Attack(instance, StrongAttacker(), 1, null, ActiveOnly(Quest21008));

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
        var (unarmedCoordinator, unarmedInstance) = MakeScenario(maxHp: 9999);
        var (armedCoordinator, armedInstance) = MakeScenario(maxHp: 9999);

        var unarmedOutcome = unarmedCoordinator.Attack(unarmedInstance, freshNovice, 1, null, NoActiveQuests);
        var armedOutcome = armedCoordinator.Attack(armedInstance, freshNovice, 1, MakeKnife(), NoActiveQuests);

        Assert.True(unarmedOutcome.Accepted);
        Assert.True(armedOutcome.Accepted);
        var unarmedDamage = unarmedOutcome.HpBefore - unarmedOutcome.HpAfter;
        var armedDamage = armedOutcome.HpBefore - armedOutcome.HpAfter;
        Assert.True(armedDamage > unarmedDamage);
    }

    // Re-equipping/unequipping mid-session must change the very next attack's
    // calculation with no coordinator-side caching to invalidate - the coordinator
    // never resolves equipment itself, so this just confirms passing null vs a weapon
    // on successive calls against the SAME instance both take effect immediately.
    [Fact]
    public void Attack_SameInstance_SwitchingWeaponArgumentBetweenCalls_ChangesCalculatorUsed()
    {
        var freshNovice = new EffectiveCharacterStats(9, 9, 9, 9, 9, 9, 0, 0);
        var (coordinator, instance) = MakeScenario(maxHp: 999999);

        var unarmedOutcome = coordinator.Attack(instance, freshNovice, 1, null, NoActiveQuests);
        var armedOutcome = coordinator.Attack(instance, freshNovice, 1, MakeKnife(), NoActiveQuests);
        var unarmedAgainOutcome = coordinator.Attack(instance, freshNovice, 1, null, NoActiveQuests);

        var unarmedDamage = unarmedOutcome.HpBefore - unarmedOutcome.HpAfter;
        var armedDamage = armedOutcome.HpBefore - armedOutcome.HpAfter;
        var unarmedAgainDamage = unarmedAgainOutcome.HpBefore - unarmedAgainOutcome.HpAfter;

        Assert.True(armedDamage > unarmedDamage);
        Assert.True(armedDamage > unarmedAgainDamage);
    }
}
