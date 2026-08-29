namespace Athena.Net.MapServer.World;

// Pinned legacy/rathena/conf/battle/client.conf `area_size: 14`, independently
// corroborated by the 2026-08-29 stock-iRO Prontera capture. Visibility is the
// square/Chebyshev rule used by rAthena's map range iteration, not Euclidean.
public sealed record WorldVisibilityOptions(ushort AreaSize = 14, ushort BucketSize = 16)
{
    public const ushort DefaultAreaSize = 14;
    public const ushort DefaultBucketSize = 16;

    public static WorldVisibilityOptions Default { get; } = new();

    public bool IsVisible(string viewerMap, ushort viewerX, ushort viewerY, string actorMap, ushort actorX, ushort actorY) =>
        string.Equals(viewerMap, actorMap, StringComparison.OrdinalIgnoreCase) &&
        Math.Abs((int)viewerX - actorX) <= AreaSize &&
        Math.Abs((int)viewerY - actorY) <= AreaSize;
}
