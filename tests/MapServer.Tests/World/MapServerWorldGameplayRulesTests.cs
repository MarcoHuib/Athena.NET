using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Gameplay.Rates;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.World;

// Proves MapServerWorld.Build takes an already-composed GameplayRuleServices
// bundle and wires it into the composed MonsterCombatCoordinator - it does NOT
// itself inspect GameplayOptions/RagnarokRuleSet or call GameplayRulesFactory
// (that selection now happens exclusively in the MapServer startup/composition
// root, MapServerApp.RunAsync; see GameplayRulesFactoryTests for ruleset-selection
// coverage, including the PreRenewal-throws case, which is composition-root
// behavior, not MapServerWorld behavior).
public sealed class MapServerWorldGameplayRulesTests
{
    [Fact]
    public void Build_WithRenewalRuleServices_ComposesSuccessfully()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()));

        Assert.NotNull(world.Combat);
    }

    [Fact]
    public void Build_ProvidesTheSameImmutableRatePolicyToWorldConsumers()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, QuestBaseExpRate = 200 };
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), rates: rates);
        Assert.Same(rates, world.Rates);
    }

    // No map in this repository has imported collision data (see MapCollisionArtifact/
    // MapCollisionCompiler's own doc comments) - Build must default to a provider that resolves
    // no map at all, never one that silently claims a map is fully open/walkable.
    [Fact]
    public void Build_WithNoCollisionProviderSupplied_DefaultsToEmptyProvider()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()));

        Assert.False(world.Collision.TryGetMap("int_land03", out _));
    }

    [Fact]
    public void Build_WithExplicitCollisionProvider_UsesIt()
    {
        // A real (non-Empty) provider makes Build compose RathenaCompatibleMobSpawnCellSelector
        // (see MapServerWorld.Build's own doc comment on the explicit either/or selector choice),
        // which throws for any SERVED generated spawn map the provider doesn't cover - so this
        // provider must supply every map MapServerHostingScope.ServedMaps declares (both
        // AcademyMobSpawns.GPoringSpawns' int_land/01/02/03/04 - the FULL family, not just the *0N
        // instanced duplicates - and PrtFild08dMobSpawns' prt_fild08d - see ai/world-data.md), each
        // large enough to satisfy the pinned map-edge margin. Plain prt_fild08 (generated but NOT
        // served) is deliberately excluded from both this provider and servedMaps below.
        var maps = new[]
            {
                "int_land", "int_land01", "int_land02", "int_land03", "int_land04",
                "prt_fild08d",
            }
            .Select(name => new MapCollisionMap(name, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray()));
        var provider = new MapCollisionProvider(maps);

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps);

        Assert.True(world.Collision.TryGetMap("int_land03", out var resolved));
        Assert.True(resolved.IsWalkable(0, 0));
    }

    // Proves the EXPLICIT either/or selector choice at composition time (never an internal
    // fallback behind RathenaCompatibleMobSpawnCellSelector): exactly EmptyMapCollisionProvider
    // (the collision-less default) gets the placeholder selector, so a spawn map absent from an
    // otherwise-empty world does not throw.
    [Fact]
    public void Build_WithNoCollisionProviderSupplied_UsesUnverifiedFallbackSelector_DoesNotThrow()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()));

        Assert.NotEmpty(world.Monsters.AllInstances);
        Assert.All(world.Monsters.AllInstances, instance => Assert.True(instance.IsAlive));
    }

    // Any real (non-Empty) provider - even one missing coverage for some of the world's spawn
    // maps - must select RathenaCompatibleMobSpawnCellSelector, which then throws loudly for the
    // uncovered map rather than silently placing that monster via the placeholder selector.
    [Fact]
    public void Build_WithRealButIncompleteCollisionProvider_ThrowsForUncoveredSpawnMap_NeverFallsBackSilently()
    {
        // Covers every int_land family member EXCEPT the generic base map, so this specifically
        // guards against silently tolerating a missing generic/base map (the exact shape of the
        // regression this task fixes) rather than an arbitrary uncovered instanced duplicate.
        var maps = new[] { "int_land01", "int_land02", "int_land03", "int_land04" }
            .Select(name => new MapCollisionMap(name, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray()));
        var provider = new MapCollisionProvider(maps); // Generic int_land deliberately uncovered.

        var exception = Assert.Throws<InvalidOperationException>(
            () => MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider));
        Assert.Contains("int_land", exception.Message);
    }

    // MapServerWorld.Build takes whatever IBasicAttackRules implementation the
    // bundle carries at face value - it has no ruleset awareness to validate
    // against, so a caller could construct a bundle from any IBasicAttackRules
    // implementation without MapServerWorld caring. This is the whole point of the
    // composition boundary: MapServerWorld only ever sees the interface.
    // Test oracle, not a hardcoded runtime behavior: the CURRENT generated Academy mob slice
    // declares exactly 40 G_PORING per int_land family member (int_land/01/02/03/04 - 5 maps), so
    // the composed production world must contain exactly 200 G_PORING instances today. This
    // catches a regeneration regression that silently drops one family member (as happened when
    // an earlier compile-mob-spawn invocation excluded generic int_land, producing 160 instead of
    // 200) without asserting anything about future mob content this branch doesn't know about.
    [Fact]
    public void Build_DefaultComposition_ProducesTwoHundredGPoringInstances_AcrossTheFullIntLandFamily()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()));

        var intLandFamily = new[] { "int_land", "int_land01", "int_land02", "int_land03", "int_land04" };
        var gPoringOnFamily = world.Monsters.AllInstances.Where(instance => intLandFamily.Contains(instance.Map)).ToArray();

        Assert.Equal(200, gPoringOnFamily.Length);
        foreach (var mapName in intLandFamily)
            Assert.Equal(40, gPoringOnFamily.Count(instance => instance.Map == mapName));
    }

    [Fact]
    public void Build_UsesWhicheverBasicAttackRulesImplementationTheBundleCarries()
    {
        var probe = new ProbeBasicAttackRules();

        var world = MapServerWorld.Build(new GameplayRuleServices(probe));

        Assert.NotNull(world.Combat);
    }

    private sealed class ProbeBasicAttackRules : IBasicAttackRules
    {
        public BasicAttackDamageResult Calculate(BasicAttackContext context) => new(0, IsMiss: true);
    }
}

// Production fail-closed composition guard (MapServerApp.RunAsync's own explicit call site, never
// invoked from inside MapServerWorld.Build itself) - see
// MapServerWorld.RequireRealCollisionSourceIfMobSpawnsExist's own doc comment. Found via a live
// Docker run: production MapServer was silently placing generated G_PORING instances on
// UnverifiedFallbackMobSpawnCellSelector's deterministic (50,50)/(52,50)/... raster on unreachable
// terrain, because the real running executable had no collision source configured at all - this
// guard exists specifically so that situation fails startup instead of running with fabricated
// world state.
public sealed class MapServerWorldProductionCollisionGuardTests
{
    [Fact]
    public void RequireRealCollisionSourceIfMobSpawnsExist_MobSpawnsExist_NoRealProvider_Throws()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MapServerWorld.RequireRealCollisionSourceIfMobSpawnsExist(hasGeneratedMobSpawns: true, EmptyMapCollisionProvider.Instance));

        Assert.Contains("Generated monster spawns are configured", exception.Message);
        Assert.Contains("map_cache_path", exception.Message);
    }

    [Fact]
    public void RequireRealCollisionSourceIfMobSpawnsExist_MobSpawnsExist_RealProviderConfigured_DoesNotThrow()
    {
        var provider = new MapCollisionProvider([new MapCollisionMap("int_land", 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray())]);

        MapServerWorld.RequireRealCollisionSourceIfMobSpawnsExist(hasGeneratedMobSpawns: true, provider);
        // No exception - test passes by not throwing.
    }

    [Fact]
    public void RequireRealCollisionSourceIfMobSpawnsExist_NoMobSpawns_NoRealProvider_DoesNotThrow()
    {
        // A collision-less world with no generated monster content at all is a legitimate,
        // deliberate configuration (e.g. a minimal NPC-only slice) - the guard must not demand
        // collision data nothing in the generated world actually needs.
        MapServerWorld.RequireRealCollisionSourceIfMobSpawnsExist(hasGeneratedMobSpawns: false, EmptyMapCollisionProvider.Instance);
    }
}

// Live stock-iRO acceptance reproduced a real gap the guard above cannot catch: a served map with
// ZERO generated monster spawns (real example: "prontera") still needs collision data for
// ordinary player movement, but RequireRealCollisionSourceIfMobSpawnsExist only ever checks
// collision existence indirectly through GeneratedScriptRegistry.MobSpawns. See
// MapServerHostingScope.RequireCollisionForAllServedMaps's own doc comment for the full
// architecture: this is a SEPARATE, broader hosting-scope invariant, deliberately not implemented
// inside MonsterRegistry, and MapServerHostingScope.ServedMaps itself is never derived from
// collision coverage (a hand-declared set - see that type's own doc comment).
public sealed class MapServerHostingScopeStartupValidationTests
{
    private static MapCollisionProvider CollisionProviderFor(params string[] mapNames) =>
        new(mapNames.Select(name => new MapCollisionMap(name, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray())));

    // The exact real regression: "prontera" is declared served (MapServerHostingScope.ServedMaps)
    // but has zero generated MobSpawnDefinition rows anywhere - proving this validation catches a
    // gap the mob-spawn-only guard genuinely cannot see. Every other served map is covered so this
    // isolates prontera specifically as the missing one.
    [Fact]
    public void RequireCollisionForAllServedMaps_ProponentServedMapWithZeroMobSpawns_StillFailsWhenCollisionAbsent()
    {
        Assert.DoesNotContain(GeneratedScriptRegistry.MobSpawns, spawn => string.Equals(spawn.Map, "prontera", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("prontera", MapServerHostingScope.ServedMaps);

        var provider = CollisionProviderFor(
            "int_land", "int_land01", "int_land02", "int_land03", "int_land04",
            "iz_int", "iz_int01", "iz_int02", "iz_int03", "iz_int04",
            "izlude_d", "prt_fild08d"); // prontera deliberately absent.

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MapServerHostingScope.RequireCollisionForAllServedMaps(provider));
        Assert.Contains("prontera", exception.Message);
    }

    [Fact]
    public void RequireCollisionForAllServedMaps_AllServedMapsCovered_DoesNotThrow()
    {
        var provider = CollisionProviderFor(MapServerHostingScope.ServedMaps.ToArray());

        MapServerHostingScope.RequireCollisionForAllServedMaps(provider);
        // No exception - test passes by not throwing.
    }

    [Fact]
    public void RequireCollisionForAllServedMaps_UnservedMapMissingCollision_IsAllowed()
    {
        // An unserved map (not in ServedMaps at all) having no collision data is explicitly fine -
        // this validation says nothing about maps outside the declared hosting scope.
        var provider = CollisionProviderFor(MapServerHostingScope.ServedMaps.ToArray());
        Assert.False(provider.TryGetMap("some_unserved_map", out _));

        MapServerHostingScope.RequireCollisionForAllServedMaps(provider);
        // No exception - test passes by not throwing.
    }

    [Fact]
    public void RequireCollisionForAllServedMaps_MultipleServedMapsMissing_NamesEveryOneOfThem()
    {
        var provider = CollisionProviderFor("int_land", "int_land01", "int_land02", "int_land03", "int_land04");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MapServerHostingScope.RequireCollisionForAllServedMaps(provider));

        foreach (var missingMap in MapServerHostingScope.ServedMaps.Except(["int_land", "int_land01", "int_land02", "int_land03", "int_land04"]))
            Assert.Contains(missingMap, exception.Message);
    }

    [Fact]
    public void RequireCollisionForAllServedMaps_EmptyCollisionProvider_ThrowsNamingEveryServedMap()
    {
        var exception = Assert.Throws<InvalidOperationException>(() =>
            MapServerHostingScope.RequireCollisionForAllServedMaps(EmptyMapCollisionProvider.Instance));

        foreach (var servedMap in MapServerHostingScope.ServedMaps)
            Assert.Contains(servedMap, exception.Message);
    }
}

// End-to-end proof against the REAL pinned legacy/rathena/db/map_cache.dat that the exact
// production composition path (MapServerWorld.Build with a real collision provider, matching what
// MapServerApp.RunAsync actually builds once map_cache_path is configured) produces genuinely
// collision-backed, non-fallback monster positions - not merely that the selector works in
// isolation (see PoringRandomSpawnIntegrationTests for that), but that composing the WHOLE
// production world this way never regresses back to the fabricated deterministic raster.
public sealed class MapServerWorldProductionCollisionCompositionTests
{
    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

    [Fact]
    public void Build_WithRealPinnedMapCache_ProducesGenuinelyCollisionBackedPositions_NotTheFallbackRaster()
    {
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");
        var maps = RathenaMapCacheReader.ReadAllFromFile(mapCachePath);
        var provider = new MapCollisionProvider(maps);

        // servedMaps: pinned map_cache.dat genuinely has no collision data for plain prt_fild08
        // (only its a/b/c/d instanced duplicates - see MapServerHostingScope's own doc comment), so
        // this uses the real production hosting scope rather than every generated spawn map
        // unfiltered - matching exactly what MapServerApp.RunAsync composes against this same real
        // map cache.
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps);

        var intLandFamily = new[] { "int_land", "int_land01", "int_land02", "int_land03", "int_land04" };
        var gPorings = world.Monsters.AllInstances.Where(instance => intLandFamily.Contains(instance.Map)).ToArray();
        Assert.Equal(200, gPorings.Length);

        foreach (var instance in gPorings)
        {
            provider.TryGetMap(instance.Map, out var map);
            var position = instance.GetPosition();
            Assert.True(map.IsTraversalCell(position.X, position.Y), $"{instance.Map} ({position.X},{position.Y}) is not a valid traversal cell");
            Assert.True(map.IsWalkable(position.X, position.Y));
        }

        // The fabricated UnverifiedFallbackMobSpawnCellSelector raster for the first 40 instances
        // on one map: (50,50),(52,50),...,(68,50),(50,52),... (stride 2, 10 columns per row). Real
        // collision-backed selection must not reproduce this exact deterministic pattern.
        var firstMapPositions = gPorings.Where(i => i.Map == intLandFamily[0]).Select(i => i.GetPosition()).Select(p => (p.X, p.Y)).ToArray();
        var fallbackRaster = Enumerable.Range(0, 40)
            .Select(i => ((ushort)(50 + (i % 10) * 2), (ushort)(50 + (i / 10) * 2)))
            .ToArray();
        Assert.NotEqual(fallbackRaster, firstMapPositions);
    }
}

// Proves MapServerWorld.Build's `servedMaps` hosting-scope filter (MapServerHostingScope) does
// exactly what it claims: an unserved map's generated content is retained as source truth but
// never instantiated; a served map instantiates normally and fails loudly if collision data is
// missing - regardless of WHICH mechanism made that map reachable (static warp, scripted/OnTouch
// warp, or a character start_point with no warp at all). See MapServerHostingScope's own doc
// comment for why this is a hand-declared set, never derived from the warp graph.
public sealed class MapServerWorldServedMapsTests
{
    private static MapCollisionProvider CollisionProviderFor(params string[] mapNames) =>
        new(mapNames.Select(name => new MapCollisionMap(name, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray())));

    // int_land (the generic/base tutorial map) has no static WarpDefinition leading to it at all -
    // it is only reachable through #intro_to_izlude_d's runtime WarpAsync script call. A served
    // start map with no static warp must still be retained/instantiated normally.
    [Fact]
    public void ServedStartMapWithNoStaticWarp_IsInstantiatedNormally()
    {
        var provider = CollisionProviderFor("int_land", "int_land01", "int_land02", "int_land03", "int_land04", "prt_fild08d");

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps);

        Assert.Equal(40, world.Monsters.AllInstances.Count(instance => instance.Map == "int_land"));
    }

    // izlude_d is reached exclusively via #intro_to_izlude_d's scripted WarpAsync call (see
    // IntroToIzludeOnTouchScript) - it has no static WarpDefinition pointing AT it either. Served
    // scripted-warp-destination maps must be retained/instantiated normally the same way. izlude_d
    // itself has no generated mob spawns, so this proves the map is accepted into the served set
    // without throwing, using prt_fild08d (reached via a real static WarpDefinition FROM izlude_d)
    // as the observable instantiation signal for the same collision-backed composition pass.
    [Fact]
    public void ServedScriptedWarpMap_DoesNotBlockCompositionOfTheRestOfTheWorld()
    {
        var provider = CollisionProviderFor("int_land", "int_land01", "int_land02", "int_land03", "int_land04", "prt_fild08d");

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps);

        Assert.Contains("izlude_d", MapServerHostingScope.ServedMaps);
        Assert.True(world.Monsters.AllInstances.Count > 0);
    }

    // Plain prt_fild08 (generic/base family member) is NOT in MapServerHostingScope.ServedMaps -
    // its generated PrtFild08dMobSpawns.PoringSpawns/etc. rows for that map must be silently
    // excluded before MonsterRegistry construction, never instantiated, and never throw even
    // though no collision data is supplied for it at all.
    [Fact]
    public void UnservedMapWithGeneratedMobs_IsNotInstantiated()
    {
        var provider = CollisionProviderFor("int_land", "int_land01", "int_land02", "int_land03", "int_land04", "prt_fild08d");

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps);

        Assert.DoesNotContain(world.Monsters.AllInstances, instance => instance.Map == "prt_fild08");
    }

    // Plain prt_fild08's generated definitions remain complete/source-backed regardless of hosting
    // scope - servedMaps filters RUNTIME instantiation only, never generated source truth.
    [Fact]
    public void UnservedMap_GeneratedSpawnDefinitionsRemainPresent()
    {
        var allGeneratedSpawnMaps = GeneratedScriptRegistry.MobSpawns.Select(spawn => spawn.Map).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("prt_fild08", allGeneratedSpawnMaps);
        Assert.DoesNotContain("prt_fild08", MapServerHostingScope.ServedMaps);
    }

    // A served map with missing collision data must still fail loudly (matching
    // RathenaCompatibleMobSpawnCellSelector's own documented "world-data/configuration error, not
    // a transient search failure" contract) - servedMaps must never mask a genuine collision-data
    // gap for a map this build actually intends to host.
    [Fact]
    public void ServedMapWithMissingCollisionData_FailsLoudly()
    {
        // prt_fild08d IS served but deliberately not covered by this provider.
        var provider = CollisionProviderFor("int_land", "int_land01", "int_land02", "int_land03", "int_land04");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps));

        Assert.Contains("prt_fild08d", exception.Message);
    }

    // prt_fild08d IS served and IS covered by collision data - its full source-backed population
    // (110 Poring + 100 Lunatic + 100 Fabre + 30 Little Poring = 340, matching
    // izlude-prontera-travel-trace.txt/legacy/rathena/npc/re/mobs/academy.txt) must instantiate.
    [Fact]
    public void PrtFild08d_ServedAndCollisionBacked_InstantiatesFullSourceBackedPopulation()
    {
        var provider = CollisionProviderFor("int_land", "int_land01", "int_land02", "int_land03", "int_land04", "prt_fild08d");

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps);

        var onPrtFild08d = world.Monsters.AllInstances.Where(instance => instance.Map == "prt_fild08d").ToArray();
        Assert.Equal(340, onPrtFild08d.Length);
        Assert.Equal(110, onPrtFild08d.Count(instance => instance.Spawn.Mob.AegisName == "PORING"));
        Assert.Equal(100, onPrtFild08d.Count(instance => instance.Spawn.Mob.AegisName == "LUNATIC"));
        Assert.Equal(100, onPrtFild08d.Count(instance => instance.Spawn.Mob.AegisName == "FABRE"));
        Assert.Equal(30, onPrtFild08d.Count(instance => instance.Spawn.Mob.AegisName == "LITTLE_PORING"));
        Assert.All(onPrtFild08d, instance => Assert.True(instance.IsAlive));
    }
}
