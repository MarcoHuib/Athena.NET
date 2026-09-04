namespace Athena.Net.MapServer.World;

public readonly record struct MonsterCellDiagnostics(
    uint ActorId,
    string MobAegisName,
    string Map,
    ushort X,
    ushort Y,
    MapCellFlags Flags,
    bool IsWalkable,
    bool IsWater,
    bool IsShootable,
    bool IsTraversalCell);

// Step 6 cutover: reads the World-authoritative projection (MonsterFeedProjectionRegistry) instead
// of a local MonsterRegistry - position/identity are World-owned post-cutover, so this diagnostic
// must observe whatever the shared per-map projection currently reports, never a local
// (non-existent) MobInstance. MobAegisName is resolved via GeneratedMobRegistry keyed by the
// projected instance's own MobId - the same static lookup WorldMonsterActorView itself uses.
public sealed class MonsterSpatialInspector(MonsterFeedProjectionRegistry projections, IMapCollisionProvider collisionProvider)
{
    public bool TryDescribe(uint actorId, string mapName, out MonsterCellDiagnostics diagnostics)
    {
        if (!projections.TryGet(mapName, out var projection) ||
            !projection.TryGetInstance(actorId, out var instance) ||
            !collisionProvider.TryGetMap(instance.MapId, out var map))
        {
            diagnostics = default;
            return false;
        }

        if (!map.IsInBounds(instance.X, instance.Y))
        {
            diagnostics = default;
            return false;
        }

        diagnostics = new MonsterCellDiagnostics(
            instance.ActorId,
            Generated.GameData.Mobs.GeneratedMobRegistry.Get(instance.MobId).AegisName,
            instance.MapId,
            instance.X,
            instance.Y,
            map.GetCell(instance.X, instance.Y),
            map.IsWalkable(instance.X, instance.Y),
            map.IsWater(instance.X, instance.Y),
            map.IsShootable(instance.X, instance.Y),
            map.IsTraversalCell(instance.X, instance.Y));
        return true;
    }
}
