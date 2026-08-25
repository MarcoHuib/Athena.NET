using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Minimal deterministic TimeProvider fake: no real clock, no timers, advanced only
// by explicit test calls. Avoids adding a new package dependency for one test file.
internal sealed class FakeTimeProvider : TimeProvider
{
    private DateTimeOffset _now = DateTimeOffset.UnixEpoch;
    public override DateTimeOffset GetUtcNow() => _now;
    public void Advance(TimeSpan delta) => _now += delta;
}

public sealed class CharacterStatusEffectStateTests
{
    [Fact]
    public void BlessingAddsLevelToStrIntDex()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10);

        var effective = state.Recalculate(BaseState());

        Assert.Equal((ushort)(BaseState().Strength + 10), effective.Strength);
        Assert.Equal((ushort)(BaseState().Intelligence + 10), effective.Intelligence);
        Assert.Equal((ushort)(BaseState().Dexterity + 10), effective.Dexterity);
        Assert.Equal(BaseState().Agility, effective.Agility);
        Assert.Equal(BaseState().Vitality, effective.Vitality);
        Assert.Equal(BaseState().Luck, effective.Luck);
    }

    [Fact]
    public void BlessingDoesNotMutateBasePersistedStats()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10);
        var baseState = BaseState();

        state.Recalculate(baseState);

        Assert.Equal(BaseState(), baseState);
    }

    [Fact]
    public void IncreaseAgiAddsTwoPlusLevelToAgiAndGrantsMoveHasteAndAspdBonus()
    {
        // Pinned legacy/rathena/src/map/status.cpp:10844-10854 (status_change_start_post_delay's
        // val-settings switch) sets val2 = 2 + val1 for SC_INCREASEAGI regardless of caller
        // (script sc_start or the real AL_INCAGI skill cast both pass val2=0 and let this
        // switch compute it); status_calc_agi (status.cpp:6843-6844) adds that val2 to AGI.
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock);
        state.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 240000, 10);

        var effective = state.Recalculate(BaseState());

        Assert.Equal((ushort)(BaseState().Agility + 12), effective.Agility);
        Assert.Equal(25, effective.MoveSpeedHaste);
        Assert.Equal(10, effective.AttackSpeedBonus);
    }

    [Fact]
    public void ReapplyingAStatusOverwritesItsValuesAndDurationRatherThanStacking()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 100000, 3);

        var effective = state.Recalculate(BaseState());

        Assert.Equal((ushort)(BaseState().Strength + 3), effective.Strength);
        clock.Advance(TimeSpan.FromMilliseconds(100001));
        Assert.False(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
    }

    [Fact]
    public void StatusExpiresAfterItsDuration()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 1000, 10);

        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        clock.Advance(TimeSpan.FromMilliseconds(999));
        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        clock.Advance(TimeSpan.FromMilliseconds(2));
        Assert.False(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));

        var effective = state.Recalculate(BaseState());
        Assert.Equal(BaseState().Strength, effective.Strength);
    }

    [Fact]
    public void MultipleActiveStatusesExpireIndependently()
    {
        var clock = new FakeTimeProvider();
        var state = new CharacterStatusEffectState(clock);
        state.Start(CharacterStatusEffectState.StatusIds.Blessing, 1000, 10);
        state.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 5000, 10);

        clock.Advance(TimeSpan.FromMilliseconds(1500));

        Assert.False(state.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.True(state.TryGet(CharacterStatusEffectState.StatusIds.IncreaseAgi, out _));
        Assert.Single(state.ActiveStatuses);

        var effective = state.Recalculate(BaseState());
        Assert.Equal(BaseState().Strength, effective.Strength);
        Assert.Equal(25, effective.MoveSpeedHaste);
    }

    [Fact]
    public void EachStatusStateInstanceIsIndependent()
    {
        var clock = new FakeTimeProvider();
        var sessionOne = new CharacterStatusEffectState(clock);
        var sessionTwo = new CharacterStatusEffectState(clock);

        sessionOne.Start(CharacterStatusEffectState.StatusIds.Blessing, 240000, 10);

        Assert.True(sessionOne.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));
        Assert.False(sessionTwo.TryGet(CharacterStatusEffectState.StatusIds.Blessing, out _));

        var effectiveTwo = sessionTwo.Recalculate(BaseState());
        Assert.Equal(BaseState().Strength, effectiveTwo.Strength);
    }

    [Fact]
    public void NonPositiveDurationIsRejected()
    {
        var state = new CharacterStatusEffectState(new FakeTimeProvider());
        Assert.Throws<ArgumentOutOfRangeException>(() => state.Start(CharacterStatusEffectState.StatusIds.Blessing, 0, 10));
    }

    private static CharacterGameplayState BaseState() => new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 2, 3, 4, 5, 6);
}
