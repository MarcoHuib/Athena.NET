using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Converts MapServer's own generated spawn declarations (MobSpawnDefinition - see that type's own
// doc comment, embedding the FULL static MobDefinition, never a bare MobId) into the wire-facing
// WorldMonsterSpawnDefinition/WorldMonsterSpawnBatch shape LoadMonsterSpawnsAsync expects. Every
// field the contract needs is directly reachable off one MobSpawnDefinition - no separate
// generated-data lookup step is required (see this project's own spawn-initialization feasibility
// finding).
public static class WorldMonsterSpawnBatchBuilder
{
    // Builds the batch for exactly ONE map's spawns - callers filter `allSpawns` down to the
    // target map themselves (or pre-group by map) since a batch's own contract requires every
    // spawn in it to belong to the SAME map (SpawnMapMismatch is a hard rejection, never silently
    // dropped - see WorldMonsterSpawnBatch's own doc comment).
    //
    // `fingerprint` is left EMPTY - the grain computes its own canonical fingerprint from the
    // batch's actual content and never trusts a caller-supplied value as proof of identity (see
    // WorldMonsterSpawnBatch's own doc comment); duplicating that canonical-fingerprint algorithm
    // here merely to populate an optional caller convenience field would be a second, divergence-
    // prone implementation of logic the grain already owns. An empty Fingerprint is an explicitly
    // supported "no caller fingerprint asserted" value (WorldPartitionGrain's own
    // LoadMonsterSpawnsAsync treats a null/empty Fingerprint as "skip the caller-fingerprint check
    // entirely", never a special mismatch case).
    public static WorldMonsterSpawnBatch Build(string mapId, IEnumerable<MobSpawnDefinition> allSpawns)
    {
        var spawns = allSpawns
            .Where(spawn => string.Equals(spawn.Map, mapId, StringComparison.OrdinalIgnoreCase))
            .Select(spawn => new WorldMonsterSpawnDefinition(
                MobId: spawn.Mob.Id,
                MapId: spawn.Map,
                X: (ushort)spawn.X,
                Y: (ushort)spawn.Y,
                Xs: (ushort)spawn.Xs,
                Ys: (ushort)spawn.Ys,
                Count: spawn.Count,
                RespawnDelayMs: spawn.RespawnDelay,
                RespawnRandomDelayMs: spawn.RespawnRandomDelay,
                SpawnName: spawn.SpawnName,
                WalkSpeedMs: spawn.Mob.WalkSpeed,
                AttackRange: spawn.Mob.AttackRange,
                MaxHp: spawn.Mob.MaxHp,
                Mode: (uint)spawn.Mob.Mode))
            .ToArray();
        return new WorldMonsterSpawnBatch(mapId, Fingerprint: "", spawns);
    }
}
