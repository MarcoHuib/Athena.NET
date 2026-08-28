using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class QuestDropResolverTests
{
    private const uint Quest21008 = 21008;
    private const int GPoringId = 2401;
    private const int WoodId = 6008;

    private static QuestDropRule[] Quest21008Rule() =>
        [new(Quest21008, GPoringId, WoodId, Count: 1, Rate: 10000, new("rAthena", "abc", "db/re/quest_db.yml", 1))];

    private static Func<uint, CharacterQuestStatus> StatusOf(uint questId, CharacterQuestStatus status) =>
        id => id == questId ? status : CharacterQuestStatus.Absent;

    [Fact]
    public void ActiveQuestAndMatchingMob_AwardsWood()
    {
        var resolver = new QuestDropResolver(Quest21008Rule());
        var drops = resolver.ResolveDrops(StatusOf(Quest21008, CharacterQuestStatus.Active), GPoringId);

        Assert.Single(drops);
        Assert.Equal(WoodId, drops[0].ItemId);
        Assert.Equal(1, drops[0].Count);
    }

    [Fact]
    public void AbsentQuest_AwardsNothing()
    {
        var resolver = new QuestDropResolver(Quest21008Rule());
        var drops = resolver.ResolveDrops(_ => CharacterQuestStatus.Absent, GPoringId);

        Assert.Empty(drops);
    }

    [Fact]
    public void CompletedQuest_AwardsNothing()
    {
        // quest_update_objective (quest.cpp:761-763) explicitly skips Q_COMPLETE entries.
        var resolver = new QuestDropResolver(Quest21008Rule());
        var drops = resolver.ResolveDrops(StatusOf(Quest21008, CharacterQuestStatus.Completed), GPoringId);

        Assert.Empty(drops);
    }

    [Fact]
    public void ActiveQuestButWrongMonster_AwardsNothing()
    {
        var resolver = new QuestDropResolver(Quest21008Rule());
        var drops = resolver.ResolveDrops(StatusOf(Quest21008, CharacterQuestStatus.Active), killedMobId: 1002); // Ordinary Poring, not G_PORING.

        Assert.Empty(drops);
    }

    [Fact]
    public void GuaranteedRate10000_NeverRolls_AlwaysAwards()
    {
        // rnd_chance-style roll is skipped entirely when rate==10000; a
        // random source that always "fails" must not suppress the drop.
        var resolver = new QuestDropResolver(Quest21008Rule(), randomSource: () => 0.9999);
        var drops = resolver.ResolveDrops(StatusOf(Quest21008, CharacterQuestStatus.Active), GPoringId);

        Assert.Single(drops);
    }

    [Fact]
    public void SubGuaranteedRate_RollsAndCanFail()
    {
        var halfRate = new QuestDropRule[] { new(Quest21008, GPoringId, WoodId, 1, Rate: 5000, new("rAthena", "abc", "x.yml", 1)) };

        var alwaysFail = new QuestDropResolver(halfRate, randomSource: () => 0.9);
        var alwaysSucceed = new QuestDropResolver(halfRate, randomSource: () => 0.1);

        Assert.Empty(alwaysFail.ResolveDrops(StatusOf(Quest21008, CharacterQuestStatus.Active), GPoringId));
        Assert.Single(alwaysSucceed.ResolveDrops(StatusOf(Quest21008, CharacterQuestStatus.Active), GPoringId));
    }

    [Fact]
    public void MobIdZero_MatchesAnyMonster()
    {
        var anyMobRule = new QuestDropRule[] { new(Quest21008, MobId: 0, WoodId, 1, Rate: 10000, new("rAthena", "abc", "x.yml", 1)) };
        var resolver = new QuestDropResolver(anyMobRule);

        Assert.Single(resolver.ResolveDrops(StatusOf(Quest21008, CharacterQuestStatus.Active), killedMobId: 999));
    }

    [Fact]
    public void ResolveDrops_DoesNotIncrementAnyKillCounter()
    {
        // There is no mutable state here to increment at all - the resolver
        // is a pure function of (questStatus, killedMobId). This test exists
        // to make that architectural property explicit: calling ResolveDrops
        // repeatedly with the same inputs always produces the same outcome,
        // unlike a stateful Count1++ kill-counter would.
        var resolver = new QuestDropResolver(Quest21008Rule());
        var lookup = StatusOf(Quest21008, CharacterQuestStatus.Active);
        var first = resolver.ResolveDrops(lookup, GPoringId);
        var second = resolver.ResolveDrops(lookup, GPoringId);

        Assert.Equal(first, second);
    }

    // Quest ITEM COLLECTION drop chance (QuestDropRule.Rate) is content balancing owned entirely by
    // generated quest data, never scaled by the central server-wide GameplayRateResolver drop-rate
    // policy. QuestDropResolver takes no GameplayRateOptions at all - there is no code path through
    // which a server item_drop_rate/card_drop_rate/etc. could reach this resolver. A guaranteed
    // Rate=10000 (100%) rule must remain exactly 1 awarded drop regardless of any configured rate;
    // this test exists so a future change wiring rate options into this resolver would have to
    // knowingly break this assertion rather than silently drift.
    [Fact]
    public void ResolveDrops_GuaranteedRateIsUnaffectedByAnyServerDropRatePolicy()
    {
        var resolver = new QuestDropResolver(Quest21008Rule());
        var drops = resolver.ResolveDrops(StatusOf(Quest21008, CharacterQuestStatus.Active), GPoringId);

        var single = Assert.Single(drops);
        Assert.Equal(WoodId, single.ItemId);
        Assert.Equal(1, single.Count); // exact QuestDropRule.Count, never scaled/multiplied by any rate.
    }
}
