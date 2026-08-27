using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules.Renewal;

public sealed class WeaponAttackCalculatorTests
{
    private static MobDefinition MakeGPoring() => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872,
        BaseExp: 0, JobExp: 0, Mode: MobMode.CanMove,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static EffectiveCharacterStats FreshNovice(ushort str = 9, ushort agi = 9, ushort vit = 9, ushort intel = 9, ushort dex = 9, ushort luk = 9) =>
        new(str, agi, vit, intel, dex, luk, MoveSpeedHaste: 0, AttackSpeedBonus: 0);

    private static WeaponItemDefinition MakeKnife() => new(
        Id: 1201, AegisName: "Knife", Name: "Knife", Stackable: false, ClientViewId: 1201,
        Attack: 17, WeaponLevel: 1, WeaponType: WeaponType.Dagger, Range: 1, EquipLocation: 0x000002,
        Source: new("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    [Fact]
    public void Calculate_WithWeapon_UsesRenewalWeaponFormula_MinRoll()
    {
        // batk = floor((9*10 + 9*10/5 + 9*10/3 + 1*10/4)/10) = floor(140/10) = 14
        // statusAtk = 2*14 = 28
        // watk=17 (refine 0, no active watk SC), variance=5*17*1/100=0.85, base_stat_bonus=17*9/200=0.765
        // atkmin=(int)(17-0.85+0.765)=(int)16.915=16, atkmax=(int)(17+0.85+0.765)=(int)18.615=18
        // Pinning the roll to atkmin(16): pre-def damage = 28+16 = 44
        // def1=2, def2=floor((1+1)/2)=1: 44*4002/4020 - 1 = floor(43.82...) - 1 = 43 - 1 = 42
        var result = WeaponAttackCalculator.Calculate(
            FreshNovice(), attackerBaseLevel: 1, MakeKnife(), MakeGPoring(), (min, max) => min);

        Assert.False(result.IsMiss);
        Assert.Equal(42u, result.Damage);
    }

    [Fact]
    public void Calculate_WithWeapon_UsesRenewalWeaponFormula_MaxRoll()
    {
        // Same as above but pinned to atkmax(18): pre-def damage = 28+18 = 46
        // 46*4002/4020 - 1 = floor(45.81...) - 1 = 45 - 1 = 44
        var result = WeaponAttackCalculator.Calculate(
            FreshNovice(), attackerBaseLevel: 1, MakeKnife(), MakeGPoring(), (min, max) => max);

        Assert.False(result.IsMiss);
        Assert.Equal(44u, result.Damage);
    }

    [Fact]
    public void Calculate_WithWeapon_RollIsClampedToComputedAtkMinAtkMaxRange()
    {
        int? observedMin = null, observedMax = null;
        WeaponAttackCalculator.Calculate(
            FreshNovice(), 1, MakeKnife(), MakeGPoring(), (min, max) => { observedMin = min; observedMax = max; return min; });

        Assert.Equal(16, observedMin);
        Assert.Equal(18, observedMax);
    }

    [Fact]
    public void Calculate_WithWeapon_DealsMoreDamageThanUnarmed_SameStats()
    {
        var unarmed = WeaponAttackCalculator.Calculate(FreshNovice(), 1, null, MakeGPoring(), (min, max) => min);
        var armed = WeaponAttackCalculator.Calculate(FreshNovice(), 1, MakeKnife(), MakeGPoring(), (min, max) => min);

        Assert.True(armed.Damage > unarmed.Damage);
    }

    [Fact]
    public void Calculate_WithWeapon_HigherWeaponLevelIncreasesVarianceRange()
    {
        var lv1 = MakeKnife();
        var lv4 = lv1 with { WeaponLevel = 4 };

        int? maxLv1 = null, maxLv4 = null;
        WeaponAttackCalculator.Calculate(FreshNovice(), 1, lv1, MakeGPoring(), (min, max) => { maxLv1 = max; return min; });
        WeaponAttackCalculator.Calculate(FreshNovice(), 1, lv4, MakeGPoring(), (min, max) => { maxLv4 = max; return min; });

        Assert.True(maxLv4 > maxLv1);
    }

    [Fact]
    public void Calculate_WithWeapon_HigherMonsterDefenseReducesDamage()
    {
        var weak = MakeGPoring();
        var tanky = weak with { Defense = 100 };

        var damageVsWeak = WeaponAttackCalculator.Calculate(FreshNovice(), 1, MakeKnife(), weak, (min, max) => min);
        var damageVsTanky = WeaponAttackCalculator.Calculate(FreshNovice(), 1, MakeKnife(), tanky, (min, max) => min);

        Assert.True(damageVsWeak.Damage > damageVsTanky.Damage);
    }

    [Fact]
    public void Calculate_WithWeapon_ExtremeDefenseResultsInMiss_NotNegativeDamage()
    {
        var result = WeaponAttackCalculator.Calculate(
            new(1, 1, 1, 1, 1, 1, 0, 0), 1, MakeKnife(), MakeGPoring() with { Defense = 50000 }, (min, max) => min);

        Assert.True(result.IsMiss);
        Assert.Equal(0u, result.Damage);
    }

    // Source-correctness regression: an unarmed RENEWAL PC (battle.cpp:4140-4142's
    // `if (sd)` branch is gated on "is this a PC", never on weapon presence) still
    // doubles statusAtk (battle.cpp:3911) and adds a weaponAtk of exactly 0
    // (battle_calc_base_weapon_attack's own equip_index guard, battle.cpp:2453).
    [Fact]
    public void Calculate_NoWeapon_DoublesStatusAtkAndUsesZeroWeaponContribution()
    {
        // batk = 14 (see the armed test above for the derivation). statusAtk = 2*14 = 28.
        // def1=2, def2=1: 28*4002/4020 - 1 = floor(27.88...) - 1 = 27 - 1 = 26.
        var result = WeaponAttackCalculator.Calculate(FreshNovice(), attackerBaseLevel: 1, null, MakeGPoring());

        Assert.False(result.IsMiss);
        Assert.Equal(26u, result.Damage);
    }

    [Fact]
    public void Calculate_NoWeapon_HigherStrIncreasesDamage()
    {
        var low = WeaponAttackCalculator.Calculate(FreshNovice(str: 9), 1, null, MakeGPoring());
        var high = WeaponAttackCalculator.Calculate(FreshNovice(str: 30), 1, null, MakeGPoring());

        Assert.True(high.Damage > low.Damage);
    }

    [Fact]
    public void Calculate_NoWeapon_VeryLowStatsBelowMonsterSoftDefense_ResultsInMiss()
    {
        var result = WeaponAttackCalculator.Calculate(new(1, 1, 1, 1, 1, 1, 0, 0), 1, null, MakeGPoring());

        Assert.True(result.IsMiss);
        Assert.Equal(0u, result.Damage);
    }

    [Fact]
    public void Calculate_NoWeapon_DamageNeverNegative()
    {
        var result = WeaponAttackCalculator.Calculate(new(1, 1, 1, 1, 1, 1, 0, 0), 1, null, MakeGPoring() with { Defense = 5000 });

        Assert.Equal(0u, result.Damage);
        Assert.True(result.IsMiss);
    }
}
