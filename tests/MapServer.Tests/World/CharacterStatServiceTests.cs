using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterStatServiceTests
{
    // JobClass 0 (Novice) resolves through the real GeneratedProgressionRegistry, giving a real
    // pinned MaxBaseStat (99, JobParameterCategory.Normal) without hand-authoring fixture
    // progression data - CharacterStatService always reads the cap from generated data, never
    // a hardcoded constant, so using the real Novice registration IS the fixture.
    private static CharacterGameplayState State(uint statPoints = 100, ushort strength = 1, ushort agility = 1, ushort vitality = 1, ushort intelligence = 1, ushort dexterity = 1, ushort luck = 1) => new(
        CharacterId: 9, Version: 0, JobClass: 0, BaseLevel: 2, JobLevel: 2,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 11, MaxHp: 40, MaxSp: 11,
        StatPoints: statPoints, SkillPoints: 0, Strength: strength, Agility: agility, Vitality: vitality, Intelligence: intelligence, Dexterity: dexterity, Luck: luck);

    [Theory]
    [InlineData(CharacterBaseStat.Strength)]
    [InlineData(CharacterBaseStat.Agility)]
    [InlineData(CharacterBaseStat.Vitality)]
    [InlineData(CharacterBaseStat.Intelligence)]
    [InlineData(CharacterBaseStat.Dexterity)]
    [InlineData(CharacterBaseStat.Luck)]
    public void IncreaseSucceeds_WithEnoughStatusPoints_ForEveryBaseStat(CharacterBaseStat stat)
    {
        var result = CharacterStatService.ValidateIncrease(State(statPoints: 10), stat, increaseAmount: 1);
        Assert.True(result.IsValid);
        Assert.Equal((ushort)1, result.PreviousValue);
        Assert.Equal((ushort)2, result.NewValue);
        Assert.Equal(2U, result.StatusPointsSpent); // pinned PC_STATUS_POINT_COST(1) = 2 + (1-1)/10 = 2
        Assert.Equal(8U, result.RemainingStatusPoints);
    }

    [Fact]
    public void InsufficientStatusPoints_Rejects()
    {
        var result = CharacterStatService.ValidateIncrease(State(statPoints: 1), CharacterBaseStat.Strength, increaseAmount: 1);
        Assert.False(result.IsValid);
        Assert.Equal(StatIncreaseRejectionReason.InsufficientStatusPoints, result.RejectionReason);
    }

    [Fact]
    public void ZeroIncreaseAmount_Rejects()
    {
        var result = CharacterStatService.ValidateIncrease(State(), CharacterBaseStat.Strength, increaseAmount: 0);
        Assert.False(result.IsValid);
        Assert.Equal(StatIncreaseRejectionReason.InvalidIncreaseAmount, result.RejectionReason);
    }

    [Fact]
    public void NegativeIncreaseAmount_Rejects()
    {
        var result = CharacterStatService.ValidateIncrease(State(), CharacterBaseStat.Strength, increaseAmount: -1);
        Assert.False(result.IsValid);
        Assert.Equal(StatIncreaseRejectionReason.InvalidIncreaseAmount, result.RejectionReason);
    }

    [Fact]
    public void UnknownStat_Rejects()
    {
        var result = CharacterStatService.ValidateIncrease(State(), (CharacterBaseStat)999, increaseAmount: 1);
        Assert.False(result.IsValid);
        Assert.Equal(StatIncreaseRejectionReason.UnknownStat, result.RejectionReason);
    }

    [Fact]
    public void IncreaseAtStatMax_Rejects()
    {
        // Novice/JobParameterCategory.Normal caps at 99 (conf/battle/player.conf max_parameter).
        var result = CharacterStatService.ValidateIncrease(State(strength: 99), CharacterBaseStat.Strength, increaseAmount: 1);
        Assert.False(result.IsValid);
        Assert.Equal(StatIncreaseRejectionReason.MaxValueReached, result.RejectionReason);
    }

    [Fact]
    public void IncreaseExceedingStatMax_Rejects_EvenWithEnoughStatusPoints()
    {
        var result = CharacterStatService.ValidateIncrease(State(statPoints: 100_000, strength: 98), CharacterBaseStat.Strength, increaseAmount: 3);
        Assert.False(result.IsValid);
        Assert.Equal(StatIncreaseRejectionReason.MaxValueReached, result.RejectionReason);
    }

    [Fact]
    public void HugeIncreaseAmount_DoesNotOverflow_AndRejectsAsMaxValueReached()
    {
        var result = CharacterStatService.ValidateIncrease(State(statPoints: uint.MaxValue), CharacterBaseStat.Strength, increaseAmount: int.MaxValue);
        Assert.False(result.IsValid);
        Assert.Equal(StatIncreaseRejectionReason.MaxValueReached, result.RejectionReason);
    }

    // Source-backed parity fixtures for CharacterStatService.CumulativeCost / pinned
    // PC_STATUS_POINT_COST(low) = 2 + (low-1)/10 (Renewal, src/map/pc.cpp). These exact
    // breakpoints were chosen (not merely 1/10/20/50/90 for their own sake) because the
    // formula's integer-division term ((low-1)/10) only changes value at 10-unit boundaries -
    // 1->2 and 10->11 deliberately land in DIFFERENT decades (0 vs 0? see below) to prove the
    // service does not hardcode "1 point per level":
    //   1  -> 2  : PC_STATUS_POINT_COST(1)  = 2 + 0/10  = 2
    //   10 -> 11 : PC_STATUS_POINT_COST(10) = 2 + 9/10  = 2  (same decade as low=1..9, still 2)
    //   20 -> 21 : PC_STATUS_POINT_COST(20) = 2 + 19/10 = 3  (crossed into the next decade)
    //   50 -> 51 : PC_STATUS_POINT_COST(50) = 2 + 49/10 = 6
    //   90 -> 91 : PC_STATUS_POINT_COST(90) = 2 + 89/10 = 10
    [Theory]
    [InlineData((ushort)1, 2u)]
    [InlineData((ushort)10, 2u)]
    [InlineData((ushort)20, 3u)]
    [InlineData((ushort)50, 6u)]
    [InlineData((ushort)90, 10u)]
    public void CumulativeCost_MatchesPinnedFormula_AtBoundaryFixtures(ushort currentValue, uint expectedCost)
    {
        Assert.Equal(expectedCost, CharacterStatService.CumulativeCost(currentValue, increaseAmount: 1));
    }

    [Fact]
    public void MultiLevelIncrease_CalculatesCumulativeCost_NotFlatPerLevelCost()
    {
        // STR 10 -> 15: PC_STATUS_POINT_COST(low) = 2 + (low-1)/10, so per-step costs at
        // 10,11,12,13,14 are 2,3,3,3,3 (only low=10 has (10-1)/10=0; low=11..14 all have
        // (low-1)/10=1) = 14 total, not the naive 5*2=10 a flat "1 point per level" model
        // would predict - the cost genuinely changes WITHIN this 5-step request, not just
        // across the calls in the boundary-fixture Theory above.
        var result = CharacterStatService.ValidateIncrease(State(statPoints: 100, strength: 10), CharacterBaseStat.Strength, increaseAmount: 5);
        Assert.True(result.IsValid);
        Assert.Equal((ushort)10, result.PreviousValue);
        Assert.Equal((ushort)15, result.NewValue);
        Assert.Equal(14U, result.StatusPointsSpent);
        Assert.Equal(86U, result.RemainingStatusPoints);
    }

    [Fact]
    public void MultiLevelIncrease_AcrossADecadeBoundary_CostsMoreThanFlatPerLevelWouldPredict()
    {
        // STR 18 -> 23: per-step costs at 18,19,20,21,22 are 3,3,3,4,4 (18,19 -> (low-1)/10=1;
        // 20,21,22 -> (low-1)/10=1,2,2 respectively: (20-1)/10=1 so cost 3, (21-1)/10=2 so
        // cost 4, (22-1)/10=2 so cost 4) = 3+3+3+4+4 = 17.
        var result = CharacterStatService.ValidateIncrease(State(statPoints: 100, strength: 18), CharacterBaseStat.Strength, increaseAmount: 5);
        Assert.True(result.IsValid);
        Assert.Equal(17U, result.StatusPointsSpent);
        Assert.NotEqual(15U, result.StatusPointsSpent); // would be wrong (5*3) if cost were naively flat at the starting value's rate
    }

    [Fact]
    public void MultiLevelIncrease_ExactlyAffordable_Succeeds()
    {
        var result = CharacterStatService.ValidateIncrease(State(statPoints: 14, strength: 10), CharacterBaseStat.Strength, increaseAmount: 5);
        Assert.True(result.IsValid);
        Assert.Equal(0U, result.RemainingStatusPoints);
    }

    [Fact]
    public void MultiLevelIncrease_OneShortOfAffordable_Rejects()
    {
        var result = CharacterStatService.ValidateIncrease(State(statPoints: 13, strength: 10), CharacterBaseStat.Strength, increaseAmount: 5);
        Assert.False(result.IsValid);
        Assert.Equal(StatIncreaseRejectionReason.InsufficientStatusPoints, result.RejectionReason);
    }
}
