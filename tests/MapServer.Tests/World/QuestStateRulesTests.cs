using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class QuestStateRulesTests
{
    [Fact]
    public void SetQuest_CreatesActiveAndDuplicateIsStable()
    {
        Assert.Equal(CharacterQuestStatus.Active, QuestStateRules.SetQuest(CharacterQuestStatus.Absent));
        Assert.Equal(CharacterQuestStatus.Active, QuestStateRules.SetQuest(CharacterQuestStatus.Active));
        Assert.Equal(CharacterQuestStatus.Completed, QuestStateRules.SetQuest(CharacterQuestStatus.Completed));
    }

    [Fact]
    public void CompleteQuest_OnlyTransitionsActiveAndPreservesCompleted()
    {
        Assert.Equal(CharacterQuestStatus.Absent, QuestStateRules.CompleteQuest(CharacterQuestStatus.Absent));
        Assert.Equal(CharacterQuestStatus.Completed, QuestStateRules.CompleteQuest(CharacterQuestStatus.Active));
        Assert.Equal(CharacterQuestStatus.Completed, QuestStateRules.CompleteQuest(CharacterQuestStatus.Completed));
    }
}
