namespace Athena.Net.MapServer.World;

// One resolved snapshot of a monster instance's current position and static collision state -
// purely a read-only diagnostic value, never consulted for spawn eligibility/gameplay decisions.
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

// Small, reusable, READ-ONLY spatial inspection capability composed alongside the rest of
// MapServerWorld - deliberately NOT threading IMapCollisionProvider into MonsterRegistry merely
// for logging, and deliberately NOT relying on RathenaCompatibleMobSpawnCellSelector's own
// spawn-time diagnostic for actor-identity correlation (that selector runs BEFORE
// WorldActorIdAllocator.Allocate() assigns the instance's real actorId in MonsterRegistry's
// constructor, so it can log a spawn decision but can never itself answer "what is at actorId
// N right now"). This type exists specifically to answer that question, on demand, from the two
// pieces of already-composed state that jointly know the answer: MonsterRegistry (actorId ->
// MobInstance) and IMapCollisionProvider (map name -> static terrain). See
// MapClientSession's 0x0368 actor-info-request handler for the live caller: stock-client
// hover/click -> actorId -> this -> diagnostic log line, entirely independent of gameplay logic.
public sealed class MonsterSpatialInspector(MonsterRegistry monsters, IMapCollisionProvider collisionProvider)
{
    public bool TryDescribe(uint actorId, string mapName, out MonsterCellDiagnostics diagnostics)
    {
        if (!monsters.TryGetInstance(actorId, mapName, out var instance) ||
            !collisionProvider.TryGetMap(instance.Map, out var map))
        {
            diagnostics = default;
            return false;
        }

        var position = instance.GetPosition();
        if (!map.IsInBounds(position.X, position.Y))
        {
            diagnostics = default;
            return false;
        }

        diagnostics = new MonsterCellDiagnostics(
            instance.ActorId,
            instance.Spawn.Mob.AegisName,
            instance.Map,
            position.X,
            position.Y,
            map.GetCell(position.X, position.Y),
            map.IsWalkable(position.X, position.Y),
            map.IsWater(position.X, position.Y),
            map.IsShootable(position.X, position.Y),
            map.IsTraversalCell(position.X, position.Y));
        return true;
    }
}
