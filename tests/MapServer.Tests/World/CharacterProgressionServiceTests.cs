using Athena.Net.MapServer.Gameplay.Rates;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterProgressionServiceTests
{
    [Theory]
    [InlineData(7UL, 100u, 7UL)]
    [InlineData(7UL, 200u, 14UL)]
    [InlineData(7UL, 500u, 35UL)]
    [InlineData(1UL, 50u, 0UL)]
    public void RateMultiplicationUsesExactTruncatingPercentageArithmetic(ulong raw, uint rate, ulong expected) =>
        Assert.Equal(expected, GameplayRateOptions.Apply(raw, rate));

    [Fact]
    public void RateMultiplicationCapsOverflowAtPinnedMaximumExperience() =>
        Assert.Equal((ulong)long.MaxValue, GameplayRateOptions.Apply(ulong.MaxValue, int.MaxValue));

    [Fact]
    public void BaseExperience_BelowExactAndRemainderUseGeneratedThreshold()
    {
        Assert.Equal(547UL, CharacterProgressionService.Calculate(State(), 547, 0).After.BaseExperience);
        var exact = CharacterProgressionService.Calculate(State(), 548, 0);
        Assert.Equal((ushort)2, exact.After.BaseLevel);
        Assert.Equal(0UL, exact.After.BaseExperience);
        Assert.Equal(51U, exact.After.StatPoints);
        Assert.Equal(45U, exact.After.CurrentHp);
        Assert.Equal(12U, exact.After.CurrentSp);
        Assert.Equal(5UL, CharacterProgressionService.Calculate(State(), 553, 0).After.BaseExperience);
    }

    [Fact]
    public void JobExperience_BelowExactAndRemainderUseGeneratedThreshold()
    {
        Assert.Equal(9UL, CharacterProgressionService.Calculate(State(), 0, 9).After.JobExperience);
        var exact = CharacterProgressionService.Calculate(State(), 0, 10);
        Assert.Equal((ushort)2, exact.After.JobLevel);
        Assert.Equal(0UL, exact.After.JobExperience);
        Assert.Equal(1U, exact.After.SkillPoints);
        Assert.Equal(5UL, CharacterProgressionService.Calculate(State(), 0, 15).After.JobExperience);
    }

    [Fact]
    public void CombinedAwardProgressesBaseAndJobIndependently()
    {
        var result = CharacterProgressionService.Calculate(State(), 548, 10);
        Assert.Equal((ushort)2, result.After.BaseLevel);
        Assert.Equal((ushort)2, result.After.JobLevel);
        Assert.Equal(51U, result.After.StatPoints);
        Assert.Equal(1U, result.After.SkillPoints);
    }

    [Fact]
    public void PinnedDefaultSingleLevelPolicyCapsOvercarry()
    {
        var result = CharacterProgressionService.Calculate(State(), 548 + 894 + 5, 10 + 18 + 28 + 1);
        Assert.Equal((ushort)2, result.After.BaseLevel);
        Assert.Equal(547UL, result.After.BaseExperience);
        Assert.Equal((ushort)2, result.After.JobLevel);
        Assert.Equal(9UL, result.After.JobExperience);
    }

    [Fact]
    public void MaximumLevelsCapExperienceAndZeroAwardDoesNothing()
    {
        var maximum = State() with { BaseLevel = 99, JobLevel = 10, BaseExperience = 99_999_998, JobExperience = 999_999_998 };
        var result = CharacterProgressionService.Calculate(maximum, ulong.MaxValue, ulong.MaxValue);
        Assert.Equal(99_999_999UL, result.After.BaseExperience);
        Assert.Equal(999_999_999UL, result.After.JobExperience);
        Assert.Equal(maximum, CharacterProgressionService.Calculate(maximum, 0, 0).After);
    }

    [Fact]
    public void JobLevelUsesNewGeneratedBonusAndPreservesCurrentHpSp()
    {
        var state = State() with { JobLevel = 5, Vitality = 99, MaxHp = 79, CurrentHp = 70, JobExperience = 0 };
        var result = CharacterProgressionService.Calculate(state, 0, 91);
        Assert.Equal((ushort)6, result.After.JobLevel);
        Assert.Equal(80U, result.After.MaxHp);
        Assert.Equal(70U, result.After.CurrentHp);
    }

    // CharacterProgressionService now receives only already-rated final Base/Job
    // EXP - it has no ExperienceAwardSource/rate concept at all. Rate selection
    // is exercised separately via GameplayRateResolver/ExperienceRewardService
    // (see GameplayRateResolverTests / ExperienceRewardServiceTests).
    [Fact]
    public async Task AddExperienceAsyncAppliesFinalValuesBeforeThresholds()
    {
        var store = new Store(State());
        var result = await new CharacterProgressionService(new(7, State(), store))
            .AddExperienceAsync(550, 10, default);
        Assert.NotNull(result);
        Assert.Equal(550UL, result.Value.BaseExperienceAwarded);
        Assert.Equal(10UL, result.Value.JobExperienceAwarded);
        Assert.Equal((ushort)2, result.Value.After.BaseLevel);
        Assert.Equal(2UL, result.Value.After.BaseExperience);
        Assert.Equal((ushort)2, result.Value.After.JobLevel);
        Assert.Equal(1, store.Updates);
    }

    [Fact]
    public async Task ZeroFinalExperienceDoesNotPersist()
    {
        var store = new Store(State());
        var result = await new CharacterProgressionService(new(7, State(), store))
            .AddExperienceAsync(0, 0, default);
        Assert.NotNull(result);
        Assert.Equal(0, store.Updates);
        Assert.Equal(State(), result.Value.After);
    }

    [Fact]
    public async Task AwardIsOneVersionedMutationAndFailureKeepsLocalState()
    {
        var store = new Store(State());
        var session = new CharacterGameplayStateSession(7, State(), store);
        var result = await new CharacterProgressionService(session).AddExperienceAsync(548, 10, default);
        Assert.NotNull(result);
        Assert.Equal(1, store.Updates);
        Assert.Equal(1UL, session.State.Version);

        var failingStore = new Store(State()) { Fail = true };
        var failingSession = new CharacterGameplayStateSession(7, State(), failingStore);
        Assert.Null(await new CharacterProgressionService(failingSession).AddExperienceAsync(548, 10, default));
        Assert.Equal(State(), failingSession.State);
    }

    [Fact]
    public async Task ReconnectReloadsPersistedProgression()
    {
        var store = new Store(State());
        var first = new CharacterGameplayStateSession(7, State(), store);
        await new CharacterProgressionService(first).AddExperienceAsync(548, 10, default);
        Assert.Equal(first.State, await store.GetAsync(7, 9, default));
    }

    private static CharacterGameplayState State() => new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);

    private sealed class Store(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        private CharacterGameplayState _state = state;
        public bool Fail { get; init; }
        public int Updates { get; private set; }
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(_state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            Updates++;
            if (Fail || expected.Version != _state.Version) return Task.FromResult<CharacterGameplayState?>(null);
            _state = updated with { Version = expected.Version + 1 };
            return Task.FromResult<CharacterGameplayState?>(_state);
        }
    }
}
