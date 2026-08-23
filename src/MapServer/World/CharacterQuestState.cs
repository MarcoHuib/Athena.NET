namespace Athena.Net.MapServer.World;

public enum CharacterQuestStatus : byte { Absent = 0, Active = 1, Completed = 2 }
public sealed record CharacterQuestState(uint CharacterId, uint QuestId, CharacterQuestStatus Status);
