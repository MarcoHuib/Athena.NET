using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterHealServiceTests
{
    [Fact]
    public async Task HealsHpOnly()
    {
        var store = new Store(State());
        var session = new CharacterGameplayStateSession(7, State(), store);
        var result = await new CharacterHealService(session).HealAsync(10, 0, default);
        Assert.NotNull(result);
        Assert.Equal(30U, result.Value.After.CurrentHp);
        Assert.Equal(11U, result.Value.After.CurrentSp);
        Assert.True(result.Value.HpChanged);
        Assert.False(result.Value.SpChanged);
    }

    [Fact]
    public async Task HealsSpOnly()
    {
        var store = new Store(State());
        var session = new CharacterGameplayStateSession(7, State(), store);
        var result = await new CharacterHealService(session).HealAsync(0, 5, default);
        Assert.NotNull(result);
        Assert.Equal(20U, result.Value.After.CurrentHp);
        Assert.Equal(16U, result.Value.After.CurrentSp);
        Assert.False(result.Value.HpChanged);
        Assert.True(result.Value.SpChanged);
    }

    [Fact]
    public async Task HealsHpAndSpTogether()
    {
        var store = new Store(State());
        var session = new CharacterGameplayStateSession(7, State(), store);
        var result = await new CharacterHealService(session).HealAsync(5, 5, default);
        Assert.NotNull(result);
        Assert.Equal(25U, result.Value.After.CurrentHp);
        Assert.Equal(16U, result.Value.After.CurrentSp);
        Assert.True(result.Value.HpChanged);
        Assert.True(result.Value.SpChanged);
    }

    [Fact]
    public async Task ClampsToMaximumHpAndSp()
    {
        var store = new Store(State());
        var session = new CharacterGameplayStateSession(7, State(), store);
        var result = await new CharacterHealService(session).HealAsync(9999, 9999, default);
        Assert.NotNull(result);
        Assert.Equal(40U, result.Value.After.CurrentHp);
        Assert.Equal(20U, result.Value.After.CurrentSp);
    }

    [Fact]
    public async Task AlreadyFullHealChangesNothingAndDoesNotPersist()
    {
        var full = State() with { CurrentHp = 40, CurrentSp = 20 };
        var store = new Store(full);
        var session = new CharacterGameplayStateSession(7, full, store);
        var result = await new CharacterHealService(session).HealAsync(9999, 9999, default);
        Assert.NotNull(result);
        Assert.False(result.Value.HpChanged);
        Assert.False(result.Value.SpChanged);
        Assert.Equal(0, store.Updates);
    }

    [Fact]
    public async Task NegativeAmountsAreRejected()
    {
        var store = new Store(State());
        var session = new CharacterGameplayStateSession(7, State(), store);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => new CharacterHealService(session).HealAsync(-1, 0, default));
    }

    [Fact]
    public async Task StaleVersionFailurePersistsNothingAndLeavesLocalStateUnchanged()
    {
        var store = new Store(State()) { Fail = true };
        var session = new CharacterGameplayStateSession(7, State(), store);
        var result = await new CharacterHealService(session).HealAsync(10, 0, default);
        Assert.Null(result);
        Assert.Equal(State(), session.State);
    }

    private static CharacterGameplayState State() => new(9, 0, 0, 1, 1, 0, 0, 20, 11, 40, 20, 48, 0, 1, 1, 1, 1, 1, 1);

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
