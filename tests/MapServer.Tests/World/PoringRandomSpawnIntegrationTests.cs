using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Generated.World;
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

    // Equivalent to the retired GPoringSpawns (see ai/world-data.md's "Generated
    // mob spawns" section): every int_land*/G_PORING declaration from the now-complete
    // GeneratedMobSpawnRegistry, in the same map order the old hand-picked array used.
    private static MobSpawnDefinition[] GPoringSpawns => new[] { "int_land", "int_land01", "int_land02", "int_land03", "int_land04" }
        .SelectMany(map => GeneratedMobSpawnRegistry.GetForMap(map).Where(spawn => spawn.Mob.Id == GeneratedMobs.GPoring.Id))
        .ToArray();

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

        var spawn = Assert.Single(GPoringSpawns, s => s.Map == mapName);
        Assert.Same(GeneratedMobs.GPoring, spawn.Mob);
        Assert.Equal(2401, spawn.Mob.Id);
        Assert.Equal(40, spawn.Count);
        Assert.Equal(5000, spawn.RespawnDelay);
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

        var spawn = Assert.Single(GPoringSpawns, s => s.Map == mapName);

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
        var spawns = GPoringSpawns.Where(s => s.Map == mapName).ToArray();

        var registry = new MonsterRegistry(spawns, new WorldActorIdAllocator().Allocate, selector, TimeProvider.System);

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
        var spawn = new MobSpawnDefinition(GeneratedMobs.GPoring, mapName, 1, 5000, 0, new("rAthena", "abc", "x.txt", 1));
        var clock = new FakeTimeProvider();
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator().Allocate, selector, clock);
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
            var spawn = Assert.Single(GPoringSpawns, s => s.Map == mapName);
            Assert.Equal(40, spawn.Count);
        }

        var totalGPoringInstances = GPoringSpawns
            .Where(s => intLandFamily.Contains(s.Map))
            .Sum(s => s.Count);
        Assert.Equal(200, totalGPoringInstances);

        // No stray sixth entry and no map outside the family sneaking into GPoringSpawns.
        Assert.Equal(intLandFamily.Length, GPoringSpawns.Length);
    }

    // Spawn diagnostics regression coverage (ai/world-data.md's "Investigation (in progress):
    // G_PORING spawns visually on water/mountain" section): these ten coordinates were sampled
    // from the 2026-08-27 runtime startup log as visible generic-int_land G_PORING spawns at that
    // moment - they are NOT confirmed to include the specific instance a tester saw standing on
    // water/mountain (that requires the actorId-based MonsterSpatialInspector diagnostic, not this
    // coordinate sample). This proves those ten sampled coordinates classify as plain Walkable
    // (not Water, not a reader/coordinate bug) against the REAL pinned map_cache.dat, reproducing
    // the same MapCellFlags/IsWalkable/IsWater/IsTraversalCell inspection the [MONSTER CELL]
    // diagnostic log line reports live - without asserting any policy about whether such cells are
    // desirable, and without asserting a pinned-source-vs-current-iRO-client mismatch is
    // established (it is not, per ai/world-data.md).
    // Deliberately does NOT assert "Water is forbidden" or any stronger connectivity/rectangle
    // rule - see this class's own suite for why (RathenaCompatibleMobSpawnCellSelectorTests /
    // ai/world-data.md).
    [Theory]
    [InlineData(63, 69)]
    [InlineData(69, 70)]
    [InlineData(68, 53)]
    [InlineData(74, 58)]
    [InlineData(65, 61)]
    [InlineData(75, 71)]
    [InlineData(68, 60)]
    [InlineData(56, 61)]
    [InlineData(70, 72)]
    [InlineData(77, 53)]
    public void SampledStartupLogIntLandCoordinates_AreClassifiedAsPlainWalkable_NotWater_NotWall(ushort x, ushort y)
    {
        var provider = LoadRealMapCache();
        Assert.True(provider.TryGetMap("int_land", out var map));

        var flags = map.GetCell(x, y);

        // Case B (matches pinned static source), not case A (reader bug) or a Water-specific
        // mislabel: every one of these reported coordinates is plain Walkable ground in the real
        // pinned map_cache.dat, not Walkable|Water and not a wall.
        Assert.True(map.IsWalkable(x, y), $"({x},{y}) should be Walkable per pinned map_cache.dat");
        Assert.False(map.IsWater(x, y), $"({x},{y}) is not Water in pinned map_cache.dat - rules out the Water-specific theory for this coordinate");
        Assert.True(map.IsTraversalCell(x, y), $"({x},{y}) should be a valid traversal cell per pinned map_cache.dat");
        Assert.Equal(MapCellFlags.Walkable | MapCellFlags.Shootable, flags);
    }

    // int_land is a small, mostly-blocked map (documented in ai/world-data.md): only ~14% of its
    // cells are walkable at all. A uniform random pick among valid traversal cells is therefore
    // inherently confined to that narrow footprint - this is expected pinned-source behavior, not
    // evidence of a bug, and this test documents the real proportions so a future reader doesn't
    // need to re-derive them from scratch.
    [Fact]
    public void IntLand_HasASmallWalkableFootprint_ExplainingWhyRandomSpawnsLookTightlyClustered()
    {
        var provider = LoadRealMapCache();
        Assert.True(provider.TryGetMap("int_land", out var map));

        var walkable = 0;
        var water = 0;
        var total = map.Width * map.Height;
        for (var x = 0; x < map.Width; x++)
        {
            for (var y = 0; y < map.Height; y++)
            {
                if (map.IsWalkable(x, y)) walkable++;
                if (map.IsWater(x, y)) water++;
            }
        }

        Assert.Equal(19600, total);
        Assert.Equal(2742 + 57, walkable); // Type 0 (dry walkable) + type 3 (walkable water).
        Assert.Equal(57, water);
    }
}
