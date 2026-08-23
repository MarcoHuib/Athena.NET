namespace Athena.Net.MapServer.World;

public static class TutorialQuestCatalog
{
    private static readonly HashSet<uint> KnownIds = [21001, 21008];
    public static bool Contains(uint questId) => KnownIds.Contains(questId);
}
