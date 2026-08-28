namespace Athena.Net.MapServer.Gameplay.Rates;

// The single place server rate policy is resolved. Every reward source (monster
// kill, NPC/quest script getexp, MVP, event, item drop) must go through this
// resolver rather than computing its own rate. An override REPLACES the global
// rate it would otherwise inherit - overrides never stack/multiply with the
// global. Pure, stateless, synchronous: no I/O, no persistence, no packets.
public static class GameplayRateResolver
{
    // Resolves the single effective Base/Job EXP rate pair for a reward source.
    // Monster and Script/Event awards always use the plain global rates (there is
    // no "monster_exp_rate"/"event_exp_rate" override concept - only Quest and
    // Mvp sources have optional overrides). An override, when set, REPLACES the
    // corresponding global rate rather than combining with it.
    public static (uint BaseRate, uint JobRate) ResolveExperienceRate(GameplayRateOptions rates, ExperienceSource source) =>
        source switch
        {
            ExperienceSource.Quest or ExperienceSource.Script => (
                rates.QuestBaseExpRate ?? rates.BaseExpRate,
                rates.QuestJobExpRate ?? rates.JobExpRate),
            ExperienceSource.Mvp => (
                rates.MvpBaseExpRate ?? rates.BaseExpRate,
                rates.MvpJobExpRate ?? rates.JobExpRate),
            ExperienceSource.Monster or ExperienceSource.Event => (rates.BaseExpRate, rates.JobExpRate),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };

    // Resolves the single effective drop rate for one drop context. Resolution
    // order, each level REPLACING (never stacking with) the level below it when
    // present:
    //   1. The most specific configured category/kind override for this context.
    //   2. The source-level override (Quest/Boss/Mvp item drop rate), if any.
    //   3. The global ItemDropRate.
    // ItemRateMvp (direct MVP reward) is a completely separate rate family from
    // the ItemRate*Mvp normal-drop categories and is only used for
    // RewardKind.MvpReward.
    public static uint ResolveDropRate(GameplayRateOptions rates, DropContext context)
    {
        if (context.Kind == RewardKind.MvpReward)
            return rates.ItemRateMvp ?? rates.ItemDropRate;

        return context.Source switch
        {
            DropSource.Mvp => ResolveCategory(
                context.Category,
                rates.ItemRateCommonMvp, rates.ItemRateHealMvp, rates.ItemRateUseMvp, rates.ItemRateEquipMvp, rates.ItemRateCardMvp,
                rates.MvpItemDropRate ?? rates.ItemDropRate),
            DropSource.Boss => ResolveCategory(
                context.Category,
                rates.ItemRateCommonBoss, rates.ItemRateHealBoss, rates.ItemRateUseBoss, rates.ItemRateEquipBoss, rates.ItemRateCardBoss,
                rates.BossItemDropRate ?? rates.ItemDropRate),
            DropSource.Quest => rates.QuestItemDropRate ?? rates.ItemDropRate,
            DropSource.Monster or DropSource.Script or DropSource.Event => ResolveCategory(
                context.Category,
                rates.ItemRateCommon, rates.ItemRateHeal, rates.ItemRateUse, rates.ItemRateEquip, rates.ItemRateCard,
                rates.ItemDropRate),
            _ => throw new ArgumentOutOfRangeException(nameof(context)),
        };
    }

    private static uint ResolveCategory(
        ItemCategory? category,
        uint? common, uint? heal, uint? use, uint? equip, uint? card,
        uint fallback)
    {
        var categoryOverride = category switch
        {
            ItemCategory.Common => common,
            ItemCategory.Heal => heal,
            ItemCategory.Use => use,
            ItemCategory.Equip => equip,
            ItemCategory.Card => card,
            null => null,
            _ => throw new ArgumentOutOfRangeException(nameof(category)),
        };
        return categoryOverride ?? fallback;
    }
}
