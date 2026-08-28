using Athena.Net.MapServer.Gameplay.Rates;

namespace Athena.Net.MapServer.Tests.Gameplay.Rates;

public sealed class ExperienceRewardServiceTests
{
    [Fact]
    public void ResolveReward_DefaultGlobalRatesPassRawValuesThrough()
    {
        var rates = new GameplayRateOptions();
        var (baseExp, jobExp) = ExperienceRewardService.ResolveReward(rates, 600, 600, ExperienceSource.Script);
        Assert.Equal(600UL, baseExp);
        Assert.Equal(600UL, jobExp);
    }

    [Fact]
    public void ResolveReward_ScriptSourceInheritsGlobalWhenNoQuestOverride()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, JobExpRate = 500 };
        var (baseExp, jobExp) = ExperienceRewardService.ResolveReward(rates, 600, 600, ExperienceSource.Script);
        Assert.Equal(3000UL, baseExp);
        Assert.Equal(3000UL, jobExp);
    }

    [Fact]
    public void ResolveReward_QuestOverrideReplacesGlobalNotStacksWithIt()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, JobExpRate = 500, QuestBaseExpRate = 1000, QuestJobExpRate = 1000 };
        var (baseExp, jobExp) = ExperienceRewardService.ResolveReward(rates, 600, 600, ExperienceSource.Script);
        Assert.Equal(6000UL, baseExp);
        Assert.Equal(6000UL, jobExp);
        Assert.NotEqual(30000UL, baseExp);
        Assert.NotEqual(30000UL, jobExp);
    }

    [Fact]
    public void ResolveReward_MonsterSourceUsesGlobalRatesDirectly()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, JobExpRate = 200 };
        var (baseExp, jobExp) = ExperienceRewardService.ResolveReward(rates, 150, 40, ExperienceSource.Monster);
        Assert.Equal(750UL, baseExp);
        Assert.Equal(80UL, jobExp);
    }

    [Fact]
    public void ResolveReward_ZeroRawExperienceStaysZeroAtAnyRate()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, JobExpRate = 500 };
        var (baseExp, jobExp) = ExperienceRewardService.ResolveReward(rates, 0, 0, ExperienceSource.Monster);
        Assert.Equal(0UL, baseExp);
        Assert.Equal(0UL, jobExp);
    }

    [Fact]
    public void ResolveReward_NegativeRawExperienceThrows()
    {
        var rates = new GameplayRateOptions();
        Assert.Throws<ArgumentOutOfRangeException>(() => ExperienceRewardService.ResolveReward(rates, -1, 0, ExperienceSource.Monster));
    }
}
