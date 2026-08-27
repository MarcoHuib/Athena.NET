using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.World.Izlude.Academy;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Real end-to-end validation (ai/world-data.md task requirement: prove this against the ACTUAL
// pinned legacy/rathena/db/map_cache.dat, not only synthetic fixtures) that the pinned
// `int_land{,01,02,03,04},0,0 monster Poring 2401,40,5000` declarations
// (npc/re/mobs/int_land.txt:11-15) resolve to real, collision-backed random spawn cells rather
// than UnverifiedFallbackMobSpawnCellSelector's artificial deterministic row - across the COMPLETE
// int_land family, not just the *0N instanced duplicates. An earlier compile-mob-spawn
// regeneration invocation used --exclude-map int_land on the (by-then-stale) assumption that
// generic int_land was never registered by the compiled Academy world; once Captain Carocc/Lumin/
// #intro_to_izlude were restored there (see WorldMapRegistryFamilyTests), that exclusion silently
// left the generic int_land tutorial destination with zero Porings - this suite is parameterized
// across all five family members specifically to catch that class of regression again.
public sealed class PoringRandomSpawnIntegrationTests
{
    public static TheoryData<string> Suffixes => new("", "01", "02", "03", "04");

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

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void RealPinnedMapCache_ContainsEachIntLandFamilyMember_AsItsOwnIndependentRecord(string suffix)
    {
        var provider = LoadRealMapCache();
        var mapName = "int_land" + suffix;

        Assert.True(provider.TryGetMap(mapName, out var map), $"map_cache.dat has no record for '{mapName}'");
        Assert.Equal(140, map.Width);
        Assert.Equal(140, map.Height);
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void GPoringSpawns_ContainsExactlyOneMapWideDeclarationPerFamilyMember(string suffix)
    {
        var mapName = "int_land" + suffix;

        var spawn = Assert.Single(AcademyMobSpawns.GPoringSpawns, s => s.Map == mapName);
        Assert.Same(GeneratedMobs.GPoring, spawn.Mob);
        Assert.Equal(2401, spawn.Mob.Id);
        Assert.Equal(40, spawn.Count);
        Assert.Equal(5000, spawn.RespawnDelayMs);
        Assert.Equal(0, spawn.X);
        Assert.Equal(0, spawn.Y);
        Assert.Equal(0, spawn.Xs);
        Assert.Equal(0, spawn.Ys);
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void AllFortyGPoringInstances_OnEachFamilyMember_ReceiveValidCollisionBackedCells(string suffix)
    {
        var provider = LoadRealMapCache();
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider);
        var mapName = "int_land" + suffix;
        provider.TryGetMap(mapName, out var map);

        var spawn = Assert.Single(AcademyMobSpawns.GPoringSpawns, s => s.Map == mapName);

        var positions = new List<(ushort X, ushort Y)>();
        for (var i = 0; i < spawn.Count; i++)
        {
            Assert.True(selector.TrySelectCell(spawn, i, out var position), $"instance {i} on '{mapName}' failed to find a valid cell");
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

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void MonsterRegistry_ComposedWithRealCollisionData_PlacesFamilyMemberPoringsOnValidCells(string suffix)
    {
        // End-to-end through the actual composition path (MonsterRegistry + the real generated
        // spawn data + the real pinned collision provider), not just the selector in isolation.
        var provider = LoadRealMapCache();
        var mapName = "int_land" + suffix;
        provider.TryGetMap(mapName, out var map);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider);
        var spawns = AcademyMobSpawns.GPoringSpawns.Where(s => s.Map == mapName).ToArray();

        var registry = new MonsterRegistry(spawns, new WorldActorIdAllocator(), selector, TimeProvider.System);

        var instances = registry.AllInstances.Where(i => i.Map == mapName).ToArray();
        Assert.Equal(40, instances.Length);
        foreach (var instance in instances)
        {
            var position = instance.GetPosition();
            Assert.True(map.IsTraversalCell(position.X, position.Y));
        }
    }

    [Theory]
    [MemberData(nameof(Suffixes))]
    public void Respawn_OnEachFamilyMember_ReselectsAFreshValidCellEachTime(string suffix)
    {
        var provider = LoadRealMapCache();
        var mapName = "int_land" + suffix;
        provider.TryGetMap(mapName, out var map);
        var selector = new RathenaCompatibleMobSpawnCellSelector(provider);
        var spawn = new MobSpawnDefinition(GeneratedMobs.GPoring, mapName, 1, 5000, new("rAthena", "abc", "x.txt", 1));
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

    // Composition-level invariant proving the exact regression this task fixes cannot recur
    // silently: the full generated Academy mob world must contain 40 Porings for EVERY int_land
    // family member (5 x 40 = 200 total), never just the *0N instanced duplicates (4 x 40 = 160,
    // the exact count observed at runtime before this fix).
    [Fact]
    public void GeneratedAcademyMobWorld_ContainsFortyPoringsPerIntLandFamilyMember_TwoHundredTotal()
    {
        var intLandFamily = new[] { "int_land", "int_land01", "int_land02", "int_land03", "int_land04" };

        foreach (var mapName in intLandFamily)
        {
            var spawn = Assert.Single(AcademyMobSpawns.GPoringSpawns, s => s.Map == mapName);
            Assert.Equal(40, spawn.Count);
        }

        var totalGPoringInstances = AcademyMobSpawns.GPoringSpawns
            .Where(s => intLandFamily.Contains(s.Map))
            .Sum(s => s.Count);
        Assert.Equal(200, totalGPoringInstances);

        // No stray sixth entry and no map outside the family sneaking into GPoringSpawns.
        Assert.Equal(intLandFamily.Length, AcademyMobSpawns.GPoringSpawns.Length);
    }
}
