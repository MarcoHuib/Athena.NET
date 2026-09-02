using Athena.Net.MapServer.Customs.World;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rates;
using Athena.Net.MapServer.Generated.GameData.Quests;
using Athena.Net.MapServer.Generated.World.Izlude.Academy;
using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.World;

// Composed live world: one WorldActorIdAllocator shared by the composed
// WorldMapRegistry (NPC/warp actors) and MonsterRegistry (monster actors),
// so every actor kind draws from one ID namespace - matching rAthena's own
// single NPC/monster actor-ID domain rather than giving each content kind an
// arbitrary disjoint sub-range. Built once by the MapServer startup path
// (MapServerApp.RunAsync) and threaded explicitly through
// MapTcpServer/MapClientSession from there; nothing in that live path should
// fall back to WorldMapRegistry.Tutorial once this exists; that static
// singleton remains only for existing tests/legacy standalone callers that
// don't combine world data with a monster runtime.
public sealed record MapServerWorld(WorldMapRegistry Maps, MonsterRegistry Monsters, MonsterCombatCoordinator Combat, IMapCollisionProvider Collision, MonsterSpatialInspector SpatialInspector, IMovementPathProvider MovementPathProvider, MonsterRuntime MonsterRuntime, PlayerPresenceRegistry Players, PlayerVisibilityCoordinator PlayerVisibility, WorldVisibilityOptions Visibility, GameplayRateOptions? Rates = null)
{
    // Compatibility constructor for focused monster/world tests that compose the record directly.
    // It still creates one coherent player-world bundle; it never leaves the new live components null.
    public MapServerWorld(WorldMapRegistry maps, MonsterRegistry monsters, MonsterCombatCoordinator combat,
        IMapCollisionProvider collision, MonsterSpatialInspector spatialInspector,
        IMovementPathProvider movementPathProvider, MonsterRuntime monsterRuntime,
        GameplayRateOptions? rates = null)
        : this(maps, monsters, combat, collision, spatialInspector, movementPathProvider, monsterRuntime, CreatePlayerWorld(), rates)
    {
    }

    private MapServerWorld(WorldMapRegistry maps, MonsterRegistry monsters, MonsterCombatCoordinator combat,
        IMapCollisionProvider collision, MonsterSpatialInspector spatialInspector,
        IMovementPathProvider movementPathProvider, MonsterRuntime monsterRuntime,
        (PlayerPresenceRegistry Players, PlayerVisibilityCoordinator Coordinator, WorldVisibilityOptions Options) playerWorld,
        GameplayRateOptions? rates)
        : this(maps, monsters, combat, collision, spatialInspector, movementPathProvider, monsterRuntime,
            playerWorld.Players, playerWorld.Coordinator, playerWorld.Options, rates)
    {
    }

    private static (PlayerPresenceRegistry Players, PlayerVisibilityCoordinator Coordinator, WorldVisibilityOptions Options) CreatePlayerWorld()
    {
        var options = WorldVisibilityOptions.Default;
        var players = new PlayerPresenceRegistry(options);
        return (players, new PlayerVisibilityCoordinator(players, options), options);
    }

    // `cellSelector` defaults to null, which means "explicitly choose ONE of the two selectors
    // based on `collisionProvider`'s identity, right here at composition time" - never an internal
    // fallback INSIDE a selector (see RathenaCompatibleMobSpawnCellSelector's own doc comment for
    // why that distinction matters: a missing/broken map inside an otherwise real collision-backed
    // world must be a hard error, not silently recovered by the placeholder selector). Exactly
    // `EmptyMapCollisionProvider.Instance` (the collision-less default) selects
    // UnverifiedFallbackMobSpawnCellSelector; ANY other configured provider (map_cache.dat now
    // makes a real provider the normal startup case - see ai/world-data.md) selects
    // RathenaCompatibleMobSpawnCellSelector. An explicit `cellSelector` argument always wins over
    // this derivation (existing tests construct their own FixedCellSelector/etc. this way).
    // `gameplayRules` is an ALREADY-COMPOSED bundle from the startup/composition
    // root (MapServerApp.RunAsync -> GameplayRulesFactory.Create). This method never
    // inspects GameplayOptions/RagnarokRuleSet and never calls GameplayRulesFactory
    // itself - ruleset selection has already happened by the time a
    // GameplayRuleServices value reaches here, so this class stays entirely unaware
    // of which ruleset is active. Required (not optional/defaulted) precisely so
    // this method cannot quietly re-introduce a ruleset decision of its own; callers
    // that don't care about ruleset selection (most existing tests) construct
    // `new GameplayRuleServices(new RenewalBasicAttackRules())` directly, the same
    // way they already construct every other dependency this method takes.
    // `collisionProvider` defaults to EmptyMapCollisionProvider.Instance: no map in this
    // repository has imported collision data yet (see MapCollisionArtifact/MapCollisionCompiler's
    // own doc comments - the proprietary source .gat files and any real derived artifact stay
    // local/gitignored, never committed). Threaded through composition now so a future branch can
    // supply a real provider without touching this signature's callers again; nothing in the
    // current gameplay runtime consumes it yet.
    // `customsEnabled` composes Athena.NET's own handwritten Customs/World content (currently
    // just the Athena Test NPC - see ai/map-server.md's "Handwritten custom world content"
    // section) alongside the generated world on the SAME WorldRegistryBuilder instance
    // GeneratedScriptRegistry.Register already populates - never a second/parallel registry, and
    // never by mutating GeneratedScriptRegistry's own static Result. Defaults to false so every
    // existing caller (including every test that doesn't pass it) keeps building a customs-free
    // world exactly as before.
    // `servedMaps`: explicit runtime/deployment HOSTING SCOPE - which maps this MapServer build
    // actually serves - supplied by the caller, never inferred from warp graphs, collision data, or
    // any other generated-content signal. "Reachable via a warp" and "served" are different
    // concepts: a map can be served with no static warp at all (a character start_point, a
    // persisted reconnect position, a save point, or a future non-warp entry mechanism), and a map
    // could theoretically appear warp-reachable without this build actually intending to host it.
    // Deliberately INDEPENDENT of collision-data availability too - a missing collision entry for a
    // served map must still fail loudly (via the existing RathenaCompatibleMobSpawnCellSelector
    // below), never be silently indistinguishable from a map this build never intended to serve at
    // all. `null` (every existing caller/test) means "do not filter" - every generated spawn is
    // instantiated exactly as before this parameter existed. When supplied, a generated
    // MobSpawnDefinition whose Map is NOT in the set is silently excluded before MonsterRegistry
    // construction (generated source data is untouched either way - see
    // GeneratedScriptRegistry.MobSpawns/PrtFild08MobSpawns.cs, which still losslessly includes
    // every pinned prt_fild08* family member); a spawn whose Map IS in the set flows through
    // normally and still hits the existing fail-loud missing-collision-data check. The production
    // composition root (MapServerApp.RunAsync) always passes an explicit literal set
    // (MapServerHostingScope.ServedMaps) declaring what Athena.NET genuinely hosts today, never
    // relies on this default and never derives it from WorldMapRegistry.ReachableMaps (that
    // property remains a purely diagnostic/navigation view of the warp graph - see its own doc
    // comment - not a hosting-scope source).
    public static MapServerWorld Build(GameplayRuleServices gameplayRules, IMobSpawnCellSelector? cellSelector = null, TimeProvider? timeProvider = null, IMapCollisionProvider? collisionProvider = null, GameplayRateOptions? rates = null, bool customsEnabled = false, IReadOnlySet<string>? servedMaps = null, IEnumerable<WarpDefinition>? warpDefinitions = null, IReadOnlySet<string>? mobSpawnMaps = null)
    {
        var resolvedCollisionProvider = collisionProvider ?? EmptyMapCollisionProvider.Instance;
        var allocator = new WorldActorIdAllocator();
        var builder = new WorldRegistryBuilder();
        GeneratedScriptRegistry.Register(builder);
        if (customsEnabled) CustomWorldRegistry.Register(builder);
        var world = builder.Build();
        var servedWarps = warpDefinitions ?? (servedMaps is null
            ? GeneratedWarpRegistry.All
            : servedMaps.Order(StringComparer.Ordinal).SelectMany(GeneratedWarpRegistry.GetForMap));
        var maps = new WorldMapRegistry(servedWarps, world.Entities, scripts: world.Scripts, allocator: allocator);
        // Explicit either/or choice, not a fallback: EmptyMapCollisionProvider.Instance IS the
        // collision-less/dev case; anything else is a real collision-backed world.
        IMobSpawnCellSelector defaultCellSelector = ReferenceEquals(resolvedCollisionProvider, EmptyMapCollisionProvider.Instance)
            ? new UnverifiedFallbackMobSpawnCellSelector()
            : new RathenaCompatibleMobSpawnCellSelector(resolvedCollisionProvider);
        var effectiveMobSpawnMaps = mobSpawnMaps ?? servedMaps;
        var servedMobSpawns = effectiveMobSpawnMaps is null ? world.MobSpawns : world.MobSpawns.Where(spawn => effectiveMobSpawnMaps.Contains(spawn.Map)).ToArray();
        var monsters = new MonsterRegistry(
            servedMobSpawns,
            allocator,
            cellSelector ?? defaultCellSelector,
            timeProvider ?? TimeProvider.System);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(monsters, questDrops, gameplayRules.BasicAttackRules);
        var spatialInspector = new MonsterSpatialInspector(monsters, resolvedCollisionProvider);
        // Same either/or composition rule as the mob spawn cell selector above (see that field's
        // own doc comment): EmptyMapCollisionProvider.Instance keeps the collision-less placeholder
        // path provider (the ONLY other IMovementPathProvider implementation, used by tests/dev
        // fixtures - see that type's own doc comment); any real provider gets the collision-backed
        // A* implementation. Player movement (MapClientSession) and monster idle movement
        // (MonsterRuntime) both consume this SAME instance - one pathfinding foundation, not two.
        IMovementPathProvider movementPathProvider = ReferenceEquals(resolvedCollisionProvider, EmptyMapCollisionProvider.Instance)
            ? new UnverifiedGridLineMovementPathProvider()
            : new RathenaCompatibleMovementPathProvider(resolvedCollisionProvider);
        var monsterRuntime = new MonsterRuntime(monsters, resolvedCollisionProvider, movementPathProvider, timeProvider ?? TimeProvider.System);
        var visibility = WorldVisibilityOptions.Default;
        var players = new PlayerPresenceRegistry(visibility);
        var playerVisibility = new PlayerVisibilityCoordinator(players, visibility);
        return new MapServerWorld(maps, monsters, combat, resolvedCollisionProvider, spatialInspector, movementPathProvider, monsterRuntime, players, playerVisibility, visibility, rates ?? new GameplayRateOptions());
    }

    // Production fail-closed guard: called explicitly by MapServerApp.RunAsync (the live
    // executable's own composition root) BEFORE calling Build, never from inside Build itself -
    // Build must stay freely usable by tests that intentionally want the collision-less/
    // fallback-selector default (see Build's own doc comment on why EmptyMapCollisionProvider ->
    // UnverifiedFallbackMobSpawnCellSelector is a legitimate, explicit choice in that context).
    // This guard exists because a REAL running MapServer is a different situation: once generated
    // content includes monster spawn declarations, starting it without real collision data would
    // silently place those monsters on UnverifiedFallbackMobSpawnCellSelector's fabricated
    // deterministic raster ((50,50), (52,50), ... - observed live on generic int_land before this
    // fix) rather than genuine pinned-rAthena-compatible cells - i.e. inventing authoritative world
    // state instead of failing loudly. `hasGeneratedMobSpawns` is a plain bool (not a live query
    // against GeneratedScriptRegistry.MobSpawns) so this method has no dependency on which mob
    // family/content module is generated - it protects any FUTURE mob family the same way, not
    // just G_PORING specifically.
    public static void RequireRealCollisionSourceIfMobSpawnsExist(bool hasGeneratedMobSpawns, IMapCollisionProvider collisionProvider)
    {
        if (hasGeneratedMobSpawns && ReferenceEquals(collisionProvider, EmptyMapCollisionProvider.Instance))
        {
            throw new InvalidOperationException(
                "Generated monster spawns are configured but no real map collision source is loaded. " +
                "Configure map_cache_path (or an explicit map_collision_artifact source) so monsters spawn on real, " +
                "collision-backed cells instead of fabricated placeholder coordinates.");
        }
    }
}
