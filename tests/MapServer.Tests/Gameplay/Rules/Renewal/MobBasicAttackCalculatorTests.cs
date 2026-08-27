using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules.Renewal;

// See MobBasicAttackCalculator's own doc comment for the full pinned battle_calc_base_damage /
// battle_calc_defense_reduction trace this mirrors (the "monster attacks a player" counterpart of
// WeaponAttackCalculator's own "player attacks a monster" formula).
public sealed class MobBasicAttackCalculatorTests
{
    private static MobDefinition MakeGPoring(int attack = 1) => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: attack, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872,
        BaseExp: 0, JobExp: 0, Mode: MobMode.CanMove,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static PlayerCombatSnapshot FreshNovice(ushort vit = 1, ushort baseLevel = 1) =>
        new(AccountId: 1, Map: "iz_int03", X: 0, Y: 0, IsAlive: true, BaseLevel: baseLevel, Vitality: vit);

    [Fact]
    public void Calculate_RollsWithinThePinned80To120PercentBand()
    {
        // Attack=100 -> atkmin=80, atkmax=120 (status_base_atk_min/max, status.cpp:2522-2554).
        var attacker = MakeGPoring(attack: 100);
        int? observedRoll = null;
        Func<int, int, int> rollAtk = (min, max) => { observedRoll = (min + max) / 2; return observedRoll.Value; };

        MobBasicAttackCalculator.Calculate(attacker, FreshNovice(), rollAtk);

        Assert.Equal(100, observedRoll); // (80+120)/2
    }

    [Fact]
    public void Calculate_HighDamageAgainstLowVitTarget_DealsRealDamage()
    {
        var attacker = MakeGPoring(attack: 1000);
        var result = MobBasicAttackCalculator.Calculate(attacker, FreshNovice(vit: 1), (min, max) => max);

        Assert.False(result.IsMiss);
        Assert.True(result.Damage > 0);
    }

    [Fact]
    public void Calculate_LowAttackAgainstHighVitTarget_ResultsInAMiss()
    {
        // G_PORING's real Attack=1: atkmax=1*120/100=1. A high-Vit target's def2 (floor((level+vit)/2))
        // easily exceeds that, driving damage below 1 - the pinned "no separate minimum-1 floor" miss.
        var attacker = MakeGPoring(attack: 1);
        var result = MobBasicAttackCalculator.Calculate(attacker, FreshNovice(vit: 99, baseLevel: 99), (min, max) => max);

        Assert.True(result.IsMiss);
        Assert.Equal(0u, result.Damage);
    }

    [Fact]
    public void Calculate_HigherVitality_ReducesDamage()
    {
        var attacker = MakeGPoring(attack: 200);
        var lowVitResult = MobBasicAttackCalculator.Calculate(attacker, FreshNovice(vit: 1), (min, max) => max);
        var highVitResult = MobBasicAttackCalculator.Calculate(attacker, FreshNovice(vit: 50), (min, max) => max);

        Assert.True(lowVitResult.Damage > highVitResult.Damage);
    }

    [Fact]
    public void Calculate_MinRollNeverExceedsMaxRoll()
    {
        // Attack=0 would make atkmin=atkmax=0 - a defensive shape check, not a real mob_db value,
        // proving the atkmin>atkmax guard never inverts the roll bounds it hands to rollAtk.
        var attacker = MakeGPoring(attack: 0);
        var calledWith = (Min: -1, Max: -1);
        MobBasicAttackCalculator.Calculate(attacker, FreshNovice(), (min, max) => { calledWith = (min, max); return min; });

        Assert.True(calledWith.Min <= calledWith.Max);
    }
}
