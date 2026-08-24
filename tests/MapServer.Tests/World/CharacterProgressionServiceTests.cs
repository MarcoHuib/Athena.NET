using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterProgressionServiceTests
{
    [Fact]
    public void BaseExperienceUsesPerLevelCostsAndKeepsRemainder()
    {
        Assert.Equal(State() with { BaseExperience = 547 }, CharacterProgressionService.Calculate(State(), 547, 0).After);
        var exact = CharacterProgressionService.Calculate(State(), 548, 0);
        Assert.Equal((ushort)2, exact.After.BaseLevel);
        Assert.Equal(0UL, exact.After.BaseExperience);
        Assert.Equal(51U, exact.After.StatPoints);
        Assert.Equal(45U, exact.After.CurrentHp);
        Assert.Equal(12U, exact.After.CurrentSp);

        var multiple = CharacterProgressionService.Calculate(State(), 548 + 894 + 5, 0);
        Assert.Equal((ushort)3, multiple.After.BaseLevel);
        Assert.Equal(5UL, multiple.After.BaseExperience);
        Assert.Equal(54U, multiple.After.StatPoints);
        Assert.Equal((ushort)2, multiple.BaseLevelsGained);
    }

    [Fact]
    public void BaseAndJobProgressIndependentlyAndAcrossMultipleLevels()
    {
        var jobOnly = CharacterProgressionService.Calculate(State(), 0, 10 + 18 + 28 + 1);
        Assert.Equal((ushort)1, jobOnly.After.BaseLevel);
        Assert.Equal((ushort)4, jobOnly.After.JobLevel);
        Assert.Equal(1UL, jobOnly.After.JobExperience);
        Assert.Equal(3U, jobOnly.After.SkillPoints);

        var baseOnly = CharacterProgressionService.Calculate(State(), 548, 0);
        Assert.Equal((ushort)1, baseOnly.After.JobLevel);

        var combined = CharacterProgressionService.Calculate(State(), 548, 10);
        Assert.Equal((ushort)2, combined.After.BaseLevel);
        Assert.Equal((ushort)2, combined.After.JobLevel);
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
        var reloaded = await store.GetAsync(7, 9, default);
        Assert.Equal(first.State, reloaded);
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
