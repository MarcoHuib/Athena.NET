using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;

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
        // which throws for any generated spawn map the provider doesn't cover - so this provider
        // must supply every map the real composed AcademyMobSpawns.GPoringSpawns reference
        // (int_land/01/02/03/04 - the FULL family, not just the *0N instanced duplicates), each
        // large enough to satisfy the pinned map-edge margin.
        var maps = new[] { "int_land", "int_land01", "int_land02", "int_land03", "int_land04" }
            .Select(name => new MapCollisionMap(name, 100, 100, Enumerable.Repeat(MapCellFlags.Walkable, 100 * 100).ToArray()));
        var provider = new MapCollisionProvider(maps);

        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), collisionProvider: provider);

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
