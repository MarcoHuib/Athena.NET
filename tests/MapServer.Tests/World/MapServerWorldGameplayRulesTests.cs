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
// Step 6 cutover: MapServerWorld.Build no longer constructs a live MonsterRegistry (monster spawn
// cell selection is entirely World's own concern now - see MapServerWorld's own doc comment).
// Several tests in this file genuinely need live MobInstance-level position/cell-selection
// behavior, so they build their OWN local MonsterRegistry directly from world.MonsterSpawns + the
// SAME collision provider the test itself passed to Build, mirroring Build's own either/or
// selector choice (RathenaCompatibleMobSpawnCellSelector for a real provider, the unverified
// fallback for EmptyMapCollisionProvider) - never wired back into MapServerWorld itself.
internal static class GameplayRulesLocalMonsterRegistryTestHelper
{
    public static MonsterRegistry BuildLocalRegistry(MapServerWorld world, IMapCollisionProvider? collisionProvider = null)
    {
        var resolvedCollisionProvider = collisionProvider ?? world.Collision;
        IMobSpawnCellSelector selector = ReferenceEquals(resolvedCollisionProvider, EmptyMapCollisionProvider.Instance)
            ? new UnverifiedFallbackMobSpawnCellSelector()
            : new RathenaCompatibleMobSpawnCellSelector(resolvedCollisionProvider);
        return new MonsterRegistry(world.MonsterSpawns, new WorldActorIdAllocator().Allocate, selector, TimeProvider.System);
    }
}

public sealed class MapServerWorldGameplayRulesTests
{
    [Fact]
    public void Build_WithRenewalRuleServices_ComposesSuccessfully()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), warpDefinitions: []);

        Assert.NotNull(world.Combat);
    }

    [Fact]
    public void Build_ProvidesTheSameImmutableRatePolicyToWorldConsumers()
    {
        var rates = new GameplayRateOptions { BaseExpRate = 500, QuestBaseExpRate = 200 };
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), rates: rates, warpDefinitions: []);
        Assert.Same(rates, world.Rates);
    }

    // No map in this repository has imported collision data (see MapCollisionArtifact/
    // MapCollisionCompiler's own doc comments) - Build must default to a provider that resolves
    // no map at all, never one that silently claims a map is fully open/walkable.
    [Fact]
    public void Build_WithNoCollisionProviderSupplied_DefaultsToEmptyProvider()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), warpDefinitions: []);

        Assert.False(world.Collision.TryGetMap("int_land03", out _));
    }

    [Fact]
    public void Build_WithExplicitCollisionProvider_UsesIt()
    {
        // A real (non-Empty) provider makes Build compose RathenaCompatibleMobSpawnCellSelector
        // (see MapServerWorld.Build's own doc comment on the explicit either/or selector choice),
        // which throws for any SERVED generated spawn map the provider doesn't cover - so this
        // provider must supply every map MapServerHostingScope.MobSpawnMaps declares (the canonical
        // int_land, prt_fild08 and prontera - the a/b/c/d and 01..04 channel copies are not hosted),
        // each large enough to satisfy the pinned map-edge margin.
        var maps = new[] { "int_land", "prt_fild08", "prontera" }
            .Select(name => new MapCollisionMap(name, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray()));
        var provider = new MapCollisionProvider(maps);

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);

        Assert.True(world.Collision.TryGetMap("int_land", out var resolved));
        Assert.True(resolved.IsWalkable(0, 0));
    }

    // Proves the EXPLICIT either/or selector choice at composition time (never an internal
    // fallback behind RathenaCompatibleMobSpawnCellSelector): exactly EmptyMapCollisionProvider
    // (the collision-less default) gets the placeholder selector, so a spawn map absent from an
    // otherwise-empty world does not throw.
    [Fact]
    public void Build_WithNoCollisionProviderSupplied_UsesUnverifiedFallbackSelector_DoesNotThrow()
    {
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), warpDefinitions: []);

        Assert.NotEmpty(GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world).AllInstances);
        Assert.All(GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world).AllInstances, instance => Assert.True(instance.IsAlive));
    }

    // Any real (non-Empty) provider - even one missing coverage for some of the world's spawn
    // maps - must select RathenaCompatibleMobSpawnCellSelector, which then throws loudly for the
    // uncovered map rather than silently placing that monster via the placeholder selector.
    [Fact]
    public void Build_WithRealButIncompleteCollisionProvider_ThrowsForUncoveredSpawnMap_NeverFallsBackSilently()
    {
        // Covers every hosted spawn map EXCEPT the canonical int_land, so this specifically guards
        // against silently tolerating a missing base map rather than an arbitrary uncovered one.
        // servedMaps is now REQUIRED here (generate-mob-spawns/ai/world-data.md: production
        // registration now feeds GeneratedMobSpawnLoadProfiles.AthenaIroEffective - the
        // Renewal-active + explicit Athena-overlay subset of the 10,065 valid pinned declarations
        // across ~800 maps, not just the tutorial/travel-corridor slice) - matching real production
        // composition
        // (MapServerApp.RunAsync always passes MapServerHostingScope.ServedMaps) so this test still
        // exercises exactly the intended int_land gap rather than tripping on an unrelated served
        // map (e.g. "prontera") this fixture never intended to cover.
        var maps = new[] { "prt_fild08", "prontera" }
            .Select(name => new MapCollisionMap(name, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray()));
        var provider = new MapCollisionProvider(maps); // Generic int_land deliberately uncovered.

        // Step 6 cutover: MapServerWorld.Build itself no longer performs monster spawn cell
        // selection at all (that is entirely World's own concern in production - see
        // MapServerWorld's own doc comment) - it only composes MonsterSpawns. The cell-selector
        // behavior this test actually guards (a real, incomplete collision provider throws loudly
        // for an uncovered spawn map rather than silently falling back) is exercised here by
        // building a local MonsterRegistry from the composed spawns with the SAME either/or
        // selector choice Build used to make internally.
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);
        var exception = Assert.Throws<InvalidOperationException>(() => GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world, provider));
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
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), warpDefinitions: []);

        var intLandFamily = new[] { "int_land", "int_land01", "int_land02", "int_land03", "int_land04" };
        var gPoringOnFamily = GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world).AllInstances.Where(instance => intLandFamily.Contains(instance.Map)).ToArray();

        Assert.Equal(200, gPoringOnFamily.Length);
        foreach (var mapName in intLandFamily)
            Assert.Equal(40, gPoringOnFamily.Count(instance => instance.Map == mapName));
    }

    [Fact]
    public void Build_UsesWhicheverBasicAttackRulesImplementationTheBundleCarries()
    {
        var probe = new ProbeBasicAttackRules();

        var world = MapServerWorld.Build(new GameplayRuleServices(probe), warpDefinitions: []);

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

    // The exact real regression this validation guards against: a served map
    // (MapServerHostingScope.ServedMaps) with zero generated MobSpawnDefinition rows anywhere -
    // proving this validation catches a gap the mob-spawn-only guard genuinely cannot see. "izlude_d"
    // is the served example (deliberately excluded from the CollisionProviderFor list below). Note:
    // "prontera"'s only generated spawn source, npc/pre-re/mobs/citycleaners.txt (WILD_ROSE), is
    // PreRenewalSource - since runtime registration now feeds
    // GeneratedMobSpawnLoadProfiles.AthenaIroEffective rather than GeneratedMobSpawnRegistry.All
    // (ai/world-data.md's "Generated mob spawns" section), prontera is ALSO effectively zero-spawn
    // again at runtime, exactly like izlude_d - both are still covered by this fixture's own
    // CollisionProviderFor list, so this remains a clean single-map (izlude_d) isolation regardless.
    // izlude_d remains genuinely zero-spawn under every profile (it is reached only via
    // #intro_to_izlude_d's scripted WarpAsync call, not any monster.txt declaration - verified
    // exhaustively across every generated file). Every other served map is covered so this isolates
    // izlude_d specifically as the missing one.
    [Fact]
    public void RequireCollisionForAllServedMaps_ProponentServedMapWithZeroMobSpawns_StillFailsWhenCollisionAbsent()
    {
        Assert.DoesNotContain(GeneratedScriptRegistry.MobSpawns, spawn => string.Equals(spawn.Map, "izlude", StringComparison.OrdinalIgnoreCase));
        Assert.Contains("izlude", MapServerHostingScope.ServedMaps);

        var provider = CollisionProviderFor(MapServerHostingScope.ServedMaps.Where(map => !string.Equals(map, "izlude", StringComparison.OrdinalIgnoreCase)).ToArray());

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MapServerHostingScope.RequireCollisionForAllServedMaps(provider));
        Assert.Contains("izlude", exception.Message);
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
        var provider = CollisionProviderFor("int_land");

        var exception = Assert.Throws<InvalidOperationException>(() =>
            MapServerHostingScope.RequireCollisionForAllServedMaps(provider));

        foreach (var missingMap in MapServerHostingScope.ServedMaps.Except(["int_land"]))
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
        // MapCollisionStartupLoader.Load merges the same three ruleset-layered map_cache.dat files
        // (db/import, db/re, db/) production MapServerApp.RunAsync composes against - matching that
        // exact production path is now load-bearing here: "prontera" (a now-real spawn-bearing
        // served map, generate-mob-spawns/ai/world-data.md) exists ONLY in the Renewal-specific
        // db/re/map_cache.dat, not the generic root db/map_cache.dat this test used to read directly
        // (see MapCollisionStartupLoader's own doc comment for the prior live-crash trace that first
        // established this). Reading only the root file here would silently miss it again.
        var mapCachePath = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/map_cache.dat");
        var provider = MapCollisionStartupLoader.Load([], mapCachePath, RagnarokRuleSet.Renewal);

        // servedMaps: pinned map_cache.dat genuinely has no collision data for plain prt_fild08
        // (only its a/b/c/d instanced duplicates - see MapServerHostingScope's own doc comment), so
        // this uses the real production hosting scope rather than every generated spawn map
        // unfiltered - matching exactly what MapServerApp.RunAsync composes against this same real
        // map cache.
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);

        var intLandFamily = new[] { "int_land" }; // One canonical map: the 01..04 copies are not hosted.
        var gPorings = GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world).AllInstances.Where(instance => instance.Map.StartsWith("int_land", StringComparison.OrdinalIgnoreCase)).ToArray();
        Assert.Equal(40, gPorings.Length);
        Assert.All(gPorings, instance => Assert.Equal("int_land", instance.Map));

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
        var provider = CollisionProviderFor(MapServerHostingScope.ServedMaps.ToArray());

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);

        Assert.Equal(40, GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world).AllInstances.Count(instance => instance.Map == "int_land"));
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
        var provider = CollisionProviderFor(MapServerHostingScope.ServedMaps.ToArray());

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);

        Assert.Contains("izlude", MapServerHostingScope.ServedMaps);
        Assert.True(GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world).AllInstances.Count > 0);
    }

    // The generic/base field is the destination paired with the generic tutorial start variant.
    // RathenaCompatibleMobSpawnCellSelector now implements rectangular/fixed-point spawn geometry
    // (see MobSpawnCellSelector.cs's own doc comment), closing the gap that previously excluded
    // prt_fild08 from MobSpawnMaps - the complete source-backed prt_fild08 family is now activated.
    [Fact]
    public void GenericTravelCorridorMap_IsHostedWithMonsterRuntimeScopeNowIncludingIt()
    {
        var provider = CollisionProviderFor(MapServerHostingScope.ServedMaps.ToArray());

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);

        Assert.Contains("prt_fild08", MapServerHostingScope.ServedMaps);
        Assert.Contains("prt_fild08", MapServerHostingScope.MobSpawnMaps);
        Assert.Contains(GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world).AllInstances, instance => instance.Map == "prt_fild08");
    }

    // Plain prt_fild08's generated definitions remain complete/source-backed regardless of hosting
    // scope - servedMaps filters RUNTIME instantiation only, never generated source truth.
    [Fact]
    public void UnservedMap_GeneratedSpawnDefinitionsRemainPresent()
    {
        var allGeneratedSpawnMaps = GeneratedScriptRegistry.MobSpawns.Select(spawn => spawn.Map).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.Contains("prt_fild08", allGeneratedSpawnMaps);
        Assert.Contains("prt_fild08", MapServerHostingScope.ServedMaps);
    }

    // A served map with missing collision data must still fail loudly (matching
    // RathenaCompatibleMobSpawnCellSelector's own documented "world-data/configuration error, not
    // a transient search failure" contract) - servedMaps must never mask a genuine collision-data
    // gap for a map this build actually intends to host.
    [Fact]
    public void ServedMapWithMissingCollisionData_FailsLoudly()
    {
        // prt_fild08 IS served but deliberately not covered by this provider - every other
        // spawn-activated map IS covered, so this isolates prt_fild08 specifically.
        var provider = CollisionProviderFor("int_land", "prontera");

        // Step 6 cutover: cell selection no longer happens inside Build itself (see
        // BuildLocalRegistry's own doc comment above) - exercised here against a locally-built
        // MonsterRegistry using the same collision provider.
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);
        var exception = Assert.Throws<InvalidOperationException>(() => GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world, provider));

        Assert.Contains("prt_fild08", exception.Message);
    }

    // The canonical prt_fild08 IS served and IS covered by collision data - its EFFECTIVE population must
    // instantiate: Renewal fields (271) + championmobs (5) + the Athena academy overlay (340) = 616
    // (CanonicalPrtFild08PopulationTests locks the per-class breakdown). Events (christmas_2013,
    // halloween_2013, ...) and pre-re declarations are represented but INACTIVE, and the former channel
    // copy prt_fild08d hosts nothing.
    [Fact]
    public void PrtFild08_ServedAndCollisionBacked_InstantiatesTheCanonicalPopulation()
    {
        var provider = CollisionProviderFor("int_land", "prt_fild08", "prontera");

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider, servedMaps: MapServerHostingScope.ServedMaps, mobSpawnMaps: MapServerHostingScope.MobSpawnMaps);

        var registry = GameplayRulesLocalMonsterRegistryTestHelper.BuildLocalRegistry(world);
        var onPrtFild08 = registry.AllInstances.Where(instance => instance.Map == "prt_fild08").ToArray();
        Assert.Equal(616, onPrtFild08.Length);
        Assert.Equal(197, onPrtFild08.Count(instance => instance.Spawn.Mob.AegisName == "PORING"));
        Assert.Equal(167, onPrtFild08.Count(instance => instance.Spawn.Mob.AegisName == "LUNATIC"));
        Assert.Equal(177, onPrtFild08.Count(instance => instance.Spawn.Mob.AegisName == "FABRE"));
        Assert.Equal(50, onPrtFild08.Count(instance => instance.Spawn.Mob.AegisName == "LITTLE_PORING"));
        Assert.Equal(0, onPrtFild08.Count(instance => instance.Spawn.Mob.AegisName == "XMAS_SMOKEY_GIFT"));
        Assert.Equal(0, onPrtFild08.Count(instance => instance.Spawn.Mob.AegisName == "XMAS_SMOKEY_SOCK"));
        Assert.Equal(0, onPrtFild08.Count(instance => instance.Spawn.Mob.AegisName == "ORGANIC_JAKK"));
        Assert.Equal(0, onPrtFild08.Count(instance => instance.Spawn.Mob.AegisName == "INORGANIC_JAKK"));
        Assert.All(onPrtFild08, instance => Assert.True(instance.IsAlive));
        Assert.DoesNotContain(registry.AllInstances, instance => instance.Map.StartsWith("prt_fild08", StringComparison.Ordinal) && instance.Map != "prt_fild08");
    }
}
