using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Gameplay.Rules.Renewal;

// Exercises RenewalBasicAttackRules through the public IBasicAttackRules surface -
// MonsterCombatCoordinator and every other consumer only ever see this interface,
// never the concrete Renewal type or its internal WeaponAttackCalculator helper.
public sealed class RenewalBasicAttackRulesTests
{
    private static MobDefinition MakeGPoring() => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872, AttackMotion: 672, DamageMotion: 480,
        BaseExp: 0, JobExp: 0, Mode: MobMode.CanMove,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static EffectiveCharacterStats FreshNovice() => new(9, 9, 9, 9, 9, 9, 0, 0);

    private static WeaponItemDefinition MakeKnife() => new(
        Id: 1201, AegisName: "Knife", Name: "Knife", Stackable: false, ClientViewId: 1201,
        Attack: 17, WeaponLevel: 1, WeaponType: WeaponType.Dagger, Range: 1, EquipLocation: 0x000002,
        Source: new("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    [Fact]
    public void RenewalBasicAttackRules_ImplementsIBasicAttackRules()
    {
        IBasicAttackRules rules = new RenewalBasicAttackRules();
        Assert.IsType<RenewalBasicAttackRules>(rules);
    }

    [Fact]
    public void Calculate_NullEquippedWeapon_UsesUnarmedRenewalPath()
    {
        IBasicAttackRules rules = new RenewalBasicAttackRules();
        var context = new BasicAttackContext(FreshNovice(), AttackerBaseLevel: 1, EquippedWeapon: null, MakeGPoring());

        var result = rules.Calculate(context);

        Assert.False(result.IsMiss);
        Assert.Equal(26u, result.Damage); // 2*batk(14) = 28 pre-DEF, matches WeaponAttackCalculatorTests' own unarmed derivation.
    }

    [Fact]
    public void Calculate_WithEquippedWeapon_DealsMoreDamageThanUnarmed()
    {
        IBasicAttackRules rules = new RenewalBasicAttackRules((min, max) => min);
        var unarmedContext = new BasicAttackContext(FreshNovice(), 1, null, MakeGPoring());
        var armedContext = new BasicAttackContext(FreshNovice(), 1, MakeKnife(), MakeGPoring());

        var unarmed = rules.Calculate(unarmedContext);
        var armed = rules.Calculate(armedContext);

        Assert.True(armed.Damage > unarmed.Damage);
    }

    [Fact]
    public void Calculate_InjectableRoll_IsForwardedToWeaponCalculation()
    {
        int? observedMin = null, observedMax = null;
        IBasicAttackRules rules = new RenewalBasicAttackRules((min, max) => { observedMin = min; observedMax = max; return min; });
        var context = new BasicAttackContext(FreshNovice(), 1, MakeKnife(), MakeGPoring());

        rules.Calculate(context);

        Assert.Equal(16, observedMin);
        Assert.Equal(18, observedMax);
    }
}
