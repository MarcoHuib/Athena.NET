using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.World.Izlude.Academy;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Real end-to-end validation (ai/world-data.md task requirement: prove this against the ACTUAL
// pinned legacy/rathena/db/map_cache.dat, not only synthetic fixtures) that the pinned
// `int_land04,0,0 monster Poring 2401,40,5000` declaration (npc/re/mobs/int_land.txt:15) now
// resolves to real, collision-backed random spawn cells rather than
// UnverifiedFallbackMobSpawnCellSelector's artificial deterministic row.
public sealed class PoringRandomSpawnIntegrationTests
{
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

    private static IMapCollisionProvider LoadRealMapCache()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");
        var maps = RathenaMapCacheReader.ReadAllFromFile(mapCachePath);
        return new MapCollisionProvider(maps);
    }

    [Fact]
    public void RealPinnedMapCache_ContainsIntLand04()
    {
        var provider = LoadRealMapCache();

        Assert.True(provider.TryGetMap("int_land04", out var map));
        Assert.Equal(140, map.Width);
        Assert.Equal(140, map.Height);
    }

    [Fact]
    public void AllFortyGPoringInstances_OnIntLand04_ReceiveValidCollisionBackedCells()
    {
        var provider = LoadRealMapCache();
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider);
        provider.TryGetMap("int_land04", out var map);

        // GeneratedMobs.GPoring / AcademyMobSpawns.GPoringSpawns are the real generated data this
        // task's compile-mob-spawn regeneration produced from the pinned declaration - not a
        // hand-written duplicate of it.
        var spawn = Assert.Single(AcademyMobSpawns.GPoringSpawns, s => s.Map == "int_land04");
        Assert.Same(GeneratedMobs.GPoring, spawn.Mob);
        Assert.Equal(40, spawn.Count);
        Assert.Equal(0, spawn.X);
        Assert.Equal(0, spawn.Y);
        Assert.Equal(0, spawn.Xs);
        Assert.Equal(0, spawn.Ys);

        var positions = new List<(ushort X, ushort Y)>();
        for (var i = 0; i < spawn.Count; i++)
        {
            Assert.True(selector.TrySelectCell(spawn, i, out var position), $"instance {i} failed to find a valid cell");
            positions.Add((position.X, position.Y));

            // Every produced cell passes the same static reachability predicate real gameplay code
            // would use (pinned CELL_CHKREACH via MapCollisionMap.IsTraversalCell), not just "some
            // number came out of the RNG".
            Assert.True(map.IsTraversalCell(position.X, position.Y));
            Assert.True(map.IsWalkable(position.X, position.Y));
        }

        Assert.Equal(40, positions.Count);

        // Must NOT reproduce the previous artificial UnverifiedFallbackMobSpawnCellSelector row
        // ((50,50), (52,50), (54,50), ... stride 2 along X, wrapping every 10 into a new row).
        var oldFallbackRow = Enumerable.Range(0, 40)
            .Select(i => ((ushort)(50 + (i % 10) * 2), (ushort)(50 + (i / 10) * 2)))
            .ToArray();
        Assert.NotEqual(oldFallbackRow, positions);

        // Pinned map_search_freecell does not reserve a cell against other mobs (no "already
        // occupied by another spawn" check anywhere in its traced source) - positions are NOT
        // required to be unique, only individually valid. This assertion documents that
        // expectation rather than asserting uniqueness.
        _ = positions.Distinct().Count();
    }

    [Fact]
    public void MonsterRegistry_ComposedWithRealCollisionData_PlacesIntLand04PoringsOnValidCells()
    {
        // End-to-end through the actual composition path (MonsterRegistry + the real generated
        // spawn data + the real pinned collision provider), not just the selector in isolation.
        var provider = LoadRealMapCache();
        provider.TryGetMap("int_land04", out var map);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider);
        var allSpawns = AcademyMobSpawns.GPoringSpawns.Where(s => s.Map == "int_land04").ToArray();

        var registry = new MonsterRegistry(allSpawns, new WorldActorIdAllocator(), selector, TimeProvider.System);

        var instances = registry.AllInstances.Where(i => i.Map == "int_land04").ToArray();
        Assert.Equal(40, instances.Length);
        foreach (var instance in instances)
        {
            var position = instance.GetPosition();
            Assert.True(map.IsTraversalCell(position.X, position.Y));
        }
    }

    [Fact]
    public void Respawn_OnIntLand04_ReselectsAFreshValidCellEachTime()
    {
        var provider = LoadRealMapCache();
        provider.TryGetMap("int_land04", out var map);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider);
        var spawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land04", 1, 5000, new("rAthena", "abc", "x.txt", 1));
        var clock = new FakeTimeProvider();
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator(), selector, clock);
        var instance = registry.AllInstances.Single();

        instance.ApplyDamage(instance.CurrentHp);
        registry.ScheduleRespawnIfNeeded(instance);
        clock.Advance(TimeSpan.FromMilliseconds(6000));
        registry.ProcessDueRespawns();

        Assert.True(instance.IsAlive);
        var position = instance.GetPosition();
        Assert.True(map.IsTraversalCell(position.X, position.Y));
    }
}
