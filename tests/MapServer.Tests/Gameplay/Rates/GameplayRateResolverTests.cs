using Athena.Net.MapServer.Gameplay.Rates;

namespace Athena.Net.MapServer.Tests.Gameplay.Rates;

public sealed class GameplayRateResolverTests
{
    [Theory]
    [InlineData(100u, 7UL, 7UL)]
    [InlineData(200u, 7UL, 14UL)]
    [InlineData(500u, 7UL, 35UL)]
    public void ApplyMultipliesAtGivenPercentage(uint rate, ulong raw, ulong expected) =>
        Assert.Equal(expected, GameplayRateOptions.Apply(raw, rate));

    [Fact]
    public void ResolveExperienceRate_MonsterAlwaysUsesGlobalRates()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, JobExpRate = 200 };
        var (baseRate, jobRate) = GameplayRateResolver.ResolveExperienceRate(rates, ExperienceSource.Monster);
        Assert.Equal(500u, baseRate);
        Assert.Equal(200u, jobRate);
    }

    [Fact]
    public void ResolveExperienceRate_QuestInheritsGlobalWhenOverrideIsNull()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, JobExpRate = 500 };
        var (baseRate, jobRate) = GameplayRateResolver.ResolveExperienceRate(rates, ExperienceSource.Quest);
        Assert.Equal(500u, baseRate);
        Assert.Equal(500u, jobRate);
    }

    [Fact]
    public void ResolveExperienceRate_QuestOverrideReplacesGlobalRatherThanStacking()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, JobExpRate = 500, QuestBaseExpRate = 1000, QuestJobExpRate = 1000 };
        var (baseRate, jobRate) = GameplayRateResolver.ResolveExperienceRate(rates, ExperienceSource.Quest);
        // The override REPLACES the global (1000), not 500*1000 or 500+1000.
        Assert.Equal(1000u, baseRate);
        Assert.Equal(1000u, jobRate);
    }

    [Fact]
    public void ResolveExperienceRate_ScriptSourceBehavesLikeQuest()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 100, QuestBaseExpRate = 300 };
        var (baseRate, _) = GameplayRateResolver.ResolveExperienceRate(rates, ExperienceSource.Script);
        Assert.Equal(300u, baseRate);
    }

    [Fact]
    public void ResolveExperienceRate_MvpInheritsGlobalWhenOverrideIsNull()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 300, JobExpRate = 300 };
        var (baseRate, jobRate) = GameplayRateResolver.ResolveExperienceRate(rates, ExperienceSource.Mvp);
        Assert.Equal(300u, baseRate);
        Assert.Equal(300u, jobRate);
    }

    [Fact]
    public void ResolveExperienceRate_MvpOverrideReplacesGlobal()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 300, MvpBaseExpRate = 150 };
        var (baseRate, _) = GameplayRateResolver.ResolveExperienceRate(rates, ExperienceSource.Mvp);
        Assert.Equal(150u, baseRate);
    }

    [Fact]
    public void ResolveDropRate_NormalMonsterInheritsGlobalWhenNoOverride()
    {
        var rates = new GameplayRateOptions { ItemDropRate = 200 };
        var rate = GameplayRateResolver.ResolveDropRate(rates, new DropContext(DropSource.Monster, RewardKind.NormalDrop, ItemCategory.Card));
        Assert.Equal(200u, rate);
    }

    [Fact]
    public void ResolveDropRate_CardOverrideReplacesGlobalRatherThanCombining()
    {
        var rates = new GameplayRateOptions { ItemDropRate = 200, ItemRateCard = 100 };
        var rate = GameplayRateResolver.ResolveDropRate(rates, new DropContext(DropSource.Monster, RewardKind.NormalDrop, ItemCategory.Card));
        // Must resolve to 100 (explicit override), never 200 combined with 100.
        Assert.Equal(100u, rate);
    }

    [Fact]
    public void ResolveDropRate_BossCategoryInheritsBossDropRateThenGlobal()
    {
        var globalOnly = new GameplayRateOptions { ItemDropRate = 200 };
        Assert.Equal(200u, GameplayRateResolver.ResolveDropRate(globalOnly, new DropContext(DropSource.Boss, RewardKind.NormalDrop, ItemCategory.Common)));

        var withBossOverride = globalOnly with { BossItemDropRate = 150 };
        Assert.Equal(150u, GameplayRateResolver.ResolveDropRate(withBossOverride, new DropContext(DropSource.Boss, RewardKind.NormalDrop, ItemCategory.Common)));

        var withCategoryOverride = withBossOverride with { ItemRateCommonBoss = 300 };
        Assert.Equal(300u, GameplayRateResolver.ResolveDropRate(withCategoryOverride, new DropContext(DropSource.Boss, RewardKind.NormalDrop, ItemCategory.Common)));
    }

    [Fact]
    public void ResolveDropRate_CardDropRateIsActuallyConsumedForNormalMonsterCard()
    {
        // card_drop_rate unset => normal monster Card inherits item_drop_rate.
        var globalOnly = new GameplayRateOptions { ItemDropRate = 200 };
        Assert.Equal(200u, GameplayRateResolver.ResolveDropRate(globalOnly, new DropContext(DropSource.Monster, RewardKind.NormalDrop, ItemCategory.Card)));

        // card_drop_rate set => REPLACES item_drop_rate for Card, never combines.
        var withCardDropRate = globalOnly with { CardDropRate = 100 };
        Assert.Equal(100u, GameplayRateResolver.ResolveDropRate(withCardDropRate, new DropContext(DropSource.Monster, RewardKind.NormalDrop, ItemCategory.Card)));

        // item_rate_card set => most specific, REPLACES card_drop_rate.
        var withCategoryOverride = withCardDropRate with { ItemRateCard = 50 };
        Assert.Equal(50u, GameplayRateResolver.ResolveDropRate(withCategoryOverride, new DropContext(DropSource.Monster, RewardKind.NormalDrop, ItemCategory.Card)));
    }

    [Fact]
    public void ResolveDropRate_BossCardFallsThroughFullPrecedenceChain()
    {
        // exact source+category (item_rate_card_boss) beats source-level (boss_item_drop_rate)
        // beats generic category (card_drop_rate) beats global (item_drop_rate).
        var rates = new GameplayRateOptions
        {
            ItemDropRate = 200,
            CardDropRate = 100,
            BossItemDropRate = 300,
        };

        // item_rate_card_boss unset => falls to source-level boss_item_drop_rate (300),
        // not the generic card_drop_rate (100) and not the global (200).
        Assert.Equal(300u, GameplayRateResolver.ResolveDropRate(rates, new DropContext(DropSource.Boss, RewardKind.NormalDrop, ItemCategory.Card)));

        // item_rate_card_boss set => most specific, wins over everything else.
        var withExactOverride = rates with { ItemRateCardBoss = 50 };
        Assert.Equal(50u, GameplayRateResolver.ResolveDropRate(withExactOverride, new DropContext(DropSource.Boss, RewardKind.NormalDrop, ItemCategory.Card)));
    }

    [Fact]
    public void ResolveDropRate_BossCardFallsBackToGenericCardWhenNoSourceOverride()
    {
        // No boss_item_drop_rate configured => Boss Card falls through to the
        // generic card_drop_rate before the global.
        var rates = new GameplayRateOptions { ItemDropRate = 200, CardDropRate = 100 };
        Assert.Equal(100u, GameplayRateResolver.ResolveDropRate(rates, new DropContext(DropSource.Boss, RewardKind.NormalDrop, ItemCategory.Card)));
    }

    [Fact]
    public void ResolveDropRate_MvpNormalDropCardFallsThroughFullPrecedenceChain()
    {
        var rates = new GameplayRateOptions
        {
            ItemDropRate = 200,
            CardDropRate = 100,
            MvpItemDropRate = 400,
        };

        // item_rate_card_mvp unset => falls to source-level mvp_item_drop_rate (400).
        Assert.Equal(400u, GameplayRateResolver.ResolveDropRate(rates, new DropContext(DropSource.Mvp, RewardKind.NormalDrop, ItemCategory.Card)));

        // item_rate_card_mvp set => most specific, wins.
        var withExactOverride = rates with { ItemRateCardMvp = 25 };
        Assert.Equal(25u, GameplayRateResolver.ResolveDropRate(withExactOverride, new DropContext(DropSource.Mvp, RewardKind.NormalDrop, ItemCategory.Card)));

        // No mvp_item_drop_rate configured => falls through to generic card_drop_rate.
        var withoutSourceOverride = rates with { MvpItemDropRate = null };
        Assert.Equal(100u, GameplayRateResolver.ResolveDropRate(withoutSourceOverride, new DropContext(DropSource.Mvp, RewardKind.NormalDrop, ItemCategory.Card)));
    }

    [Fact]
    public void ResolveDropRate_MvpNormalDropDistinctFromMvpDirectReward()
    {
        var rates = new GameplayRateOptions { ItemDropRate = 200, MvpItemDropRate = 500, ItemRateMvp = 700 };

        // item_rate_*_mvp family: a normal drop-table item dropped BY an MVP monster.
        var normalDropFromMvp = GameplayRateResolver.ResolveDropRate(rates, new DropContext(DropSource.Mvp, RewardKind.NormalDrop, ItemCategory.Common));
        Assert.Equal(500u, normalDropFromMvp);

        // item_rate_mvp: the MVP's own direct reward item - a completely separate rate family.
        var directMvpReward = GameplayRateResolver.ResolveDropRate(rates, new DropContext(DropSource.Mvp, RewardKind.MvpReward));
        Assert.Equal(700u, directMvpReward);
    }

    [Fact]
    public void ResolveDropRate_DirectMvpRewardFallsBackToMvpItemDropRateThenGlobal()
    {
        // item_rate_mvp unset => falls to mvp_item_drop_rate, not straight to global.
        var withSourceOverride = new GameplayRateOptions { ItemDropRate = 200, MvpItemDropRate = 500 };
        Assert.Equal(500u, GameplayRateResolver.ResolveDropRate(withSourceOverride, new DropContext(DropSource.Mvp, RewardKind.MvpReward)));

        // Neither configured => falls all the way to the global.
        var globalOnly = new GameplayRateOptions { ItemDropRate = 200 };
        Assert.Equal(200u, GameplayRateResolver.ResolveDropRate(globalOnly, new DropContext(DropSource.Mvp, RewardKind.MvpReward)));
    }
}
