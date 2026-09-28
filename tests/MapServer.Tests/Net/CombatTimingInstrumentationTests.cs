using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// The latency instrumentation must be observation-only. These tests pin the two properties the
// instrumentation relies on / could otherwise disturb: the AsyncLocal correlation context never leaks
// out of the async method that set it, and CharacterGameplayStateSession.MutateAsync behaves exactly
// the same with or without the diagnostic-only `reason` argument. No wall-clock threshold is asserted.
public sealed class CombatTimingInstrumentationTests
{
    private static CharacterGameplayState State(ushort hp = 40) =>
        new(CharacterId: 1, Version: 5, JobClass: 0, BaseLevel: 1, JobLevel: 1, BaseExperience: 0, JobExperience: 0,
            CurrentHp: hp, CurrentSp: 10, MaxHp: 40, MaxSp: 10, StatPoints: 0, SkillPoints: 0,
            Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    private sealed class ScriptedPersistence(Func<CharacterGameplayState, CharacterGameplayState, CharacterGameplayState?> onUpdate) : ICharacterGameplayStatePersistence
    {
        public int Calls;
        public CharacterGameplayState? LastExpected;
        public CharacterGameplayState? LastUpdated;
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(null);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            Calls++;
            LastExpected = expected;
            LastUpdated = updated;
            return Task.FromResult(onUpdate(expected, updated));
        }
    }

    private static async Task SetContextInsideAsyncCalleeAsync()
    {
        CombatTiming.SetContext("player-attack:actorId=1:seq=7");
        await Task.Yield();
        Assert.Equal("player-attack:actorId=1:seq=7", CombatTiming.Context); // Visible to everything the callee awaits.
    }

    [Fact]
    public async Task Context_SetInsideAsyncCallee_IsVisibleDownstream_ButDoesNotLeakToCaller()
    {
        var before = CombatTiming.Context;
        await SetContextInsideAsyncCalleeAsync();
        Assert.Equal(before, CombatTiming.Context);
    }

    [Fact]
    public void Formatting_IsInvariantCulture()
    {
        var previous = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            System.Globalization.CultureInfo.CurrentCulture = new System.Globalization.CultureInfo("de-DE");
            Assert.Equal("1.5", CombatTiming.F(1.54));
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = previous; }
    }

    [Fact]
    public async Task MutateAsync_WithAndWithoutReason_PersistAndApplyIdentically()
    {
        var withReason = new ScriptedPersistence((expected, updated) => updated with { Version = expected.Version + 1 });
        var withoutReason = new ScriptedPersistence((expected, updated) => updated with { Version = expected.Version + 1 });
        var sessionWithReason = new CharacterGameplayStateSession(2_000_000, State(), withReason);
        var sessionWithoutReason = new CharacterGameplayStateSession(2_000_000, State(), withoutReason);

        var a = await sessionWithReason.MutateAsync(current => current with { CurrentHp = 12 }, CancellationToken.None, reason: "mob-basic-attack");
        var b = await sessionWithoutReason.MutateAsync(current => current with { CurrentHp = 12 }, CancellationToken.None);

        Assert.Equal(b, a);
        Assert.Equal(sessionWithoutReason.State, sessionWithReason.State);
        Assert.Equal(12u, sessionWithReason.State.CurrentHp);
        Assert.Equal(6UL, sessionWithReason.State.Version);
        Assert.Equal(1, withReason.Calls);
        Assert.Equal(withoutReason.LastUpdated, withReason.LastUpdated);
    }

    [Fact]
    public async Task MutateAsync_PersistenceRejection_LeavesStateUnchanged_AndReturnsNull()
    {
        var persistence = new ScriptedPersistence((_, _) => null);
        var session = new CharacterGameplayStateSession(2_000_000, State(), persistence);

        var result = await session.MutateAsync(current => current with { CurrentHp = 1 }, CancellationToken.None, reason: "experience");

        Assert.Null(result);
        Assert.Equal(40u, session.State.CurrentHp);
        Assert.Equal(5UL, session.State.Version);
    }

    [Fact]
    public async Task MutateAsync_NoOpMutation_StillPersistsOnce_CharacterizesCurrentBehavior()
    {
        // Pins CURRENT behavior the timing log exists to measure: there is no equality short-circuit, so
        // even a mutation that changes nothing (a 0-damage monster attack) performs one persistence call.
        var persistence = new ScriptedPersistence((expected, updated) => updated with { Version = expected.Version + 1 });
        var session = new CharacterGameplayStateSession(2_000_000, State(), persistence);

        await session.MutateAsync(current => current, CancellationToken.None, reason: "mob-basic-attack");

        Assert.Equal(1, persistence.Calls);
    }
}
