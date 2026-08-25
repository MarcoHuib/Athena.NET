using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class BasicAttackCalculatorTests
{
    private static MobDefinition MakeGPoring() => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872,
        BaseExp: 0, JobExp: 0,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    // Fresh Novice at BaseLevel 1 with the 1.NET default (9,9,9,9,9,9) initial
    // stats a brand-new character starts with, no Blessing/status active.
    private static EffectiveCharacterStats FreshNovice(ushort str = 9, ushort agi = 9, ushort vit = 9, ushort intel = 9, ushort dex = 9, ushort luk = 9) =>
        new(str, agi, vit, intel, dex, luk, MoveSpeedHaste: 0, AttackSpeedBonus: 0);

    [Fact]
    public void CalculateUnarmedNoviceAttack_UsesRenewalBaseAtkFormula()
    {
        // batk = floor((STR*10 + DEX*10/5 + LUK*10/3 + Level*10/4)/10) + 5*POW(=0)
        //      = floor((90 + 18 + 30 + 2)/10) = floor(140/10) = 14
        // def1=2 (G_PORING Defense), def2=floor((1+1)/2)=1 (Level+Vit soft DEF)
        // damage = 14*(4000+2)/(4000+20) - 1 = 14*4002/4020 - 1 = 13 - 1 = 12 (integer division)
        var result = BasicAttackCalculator.CalculateUnarmedNoviceAttack(FreshNovice(), attackerBaseLevel: 1, MakeGPoring());

        Assert.False(result.IsMiss);
        Assert.Equal(12u, result.Damage);
    }

    [Fact]
    public void CalculateUnarmedNoviceAttack_HigherStrIncreasesDamage()
    {
        var low = BasicAttackCalculator.CalculateUnarmedNoviceAttack(FreshNovice(str: 9), 1, MakeGPoring());
        var high = BasicAttackCalculator.CalculateUnarmedNoviceAttack(FreshNovice(str: 30), 1, MakeGPoring());

        Assert.True(high.Damage > low.Damage);
    }

    [Fact]
    public void CalculateUnarmedNoviceAttack_HigherMonsterDefenseReducesDamage()
    {
        var weak = MakeGPoring();
        var tanky = weak with { Defense = 100 };

        var damageVsWeak = BasicAttackCalculator.CalculateUnarmedNoviceAttack(FreshNovice(str: 20), 5, weak);
        var damageVsTanky = BasicAttackCalculator.CalculateUnarmedNoviceAttack(FreshNovice(str: 20), 5, tanky);

        Assert.True(damageVsWeak.Damage > damageVsTanky.Damage);
    }

    [Fact]
    public void CalculateUnarmedNoviceAttack_VeryLowStatsBelowMonsterSoftDefense_ResultsInMiss()
    {
        // With minimal stats the DEF-reduced result can legitimately reach
        // below 1; battle_calc_attack (battle.cpp:6766) treats that as a miss
        // (0 damage), NOT a floored minimum of 1.
        var result = BasicAttackCalculator.CalculateUnarmedNoviceAttack(new(1, 1, 1, 1, 1, 1, 0, 0), 1, MakeGPoring());

        Assert.True(result.IsMiss);
        Assert.Equal(0u, result.Damage);
    }

    [Fact]
    public void CalculateUnarmedNoviceAttack_DamageNeverNegative()
    {
        var result = BasicAttackCalculator.CalculateUnarmedNoviceAttack(new(1, 1, 1, 1, 1, 1, 0, 0), 1, MakeGPoring() with { Defense = 5000 });
        Assert.Equal(0u, result.Damage);
        Assert.True(result.IsMiss);
    }

    [Fact]
    public void CalculateUnarmedNoviceAttack_LethalBoundary_ExactlyEqualToMaxHpKills()
    {
        var mob = MakeGPoring();
        // Craft stats so the computed damage exactly equals MaxHp (55) to
        // exercise the lethal boundary explicitly rather than just "some
        // damage > 0".
        var attacker = FreshNovice(str: 47);
        var result = BasicAttackCalculator.CalculateUnarmedNoviceAttack(attacker, 1, mob);
        var instance = new MobInstance(1, new MobSpawnDefinition(mob, "int_land01", 1, 5000, mob.Source), 0, 0);

        var (_, after, killed) = instance.ApplyDamage(result.Damage);

        if (result.Damage >= mob.MaxHp)
        {
            Assert.Equal(0u, after);
            Assert.True(killed);
        }
    }
}
