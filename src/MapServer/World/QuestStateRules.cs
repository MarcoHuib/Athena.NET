namespace Athena.Net.MapServer.World;

public static class QuestStateRules
{
    public static CharacterQuestStatus SetQuest(CharacterQuestStatus current) => current == CharacterQuestStatus.Absent ? CharacterQuestStatus.Active : current;
    public static CharacterQuestStatus CompleteQuest(CharacterQuestStatus current) => current == CharacterQuestStatus.Active ? CharacterQuestStatus.Completed : current;
}
