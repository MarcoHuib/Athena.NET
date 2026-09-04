using Athena.Net.MapServer.Customs.World;
using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rates;
using Athena.Net.MapServer.Generated.GameData.Quests;
using Athena.Net.MapServer.Generated.World.Izlude.Academy;
using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.World;

// Composed live world: one WorldActorIdAllocator drives the composed WorldMapRegistry (NPC/warp
// actors) - the ONLY actor kind MapServer allocates local ActorIds for post-cutover. Monster
// ActorIds are World-authoritative (leased by the WorldPartitionGrain itself, see that grain's own
// LoadMonsterSpawnsAsync) - MapServer no longer allocates a second, competing local ActorId set for
// monsters at all (there is no local MobInstance for a production monster to allocate one for).
//
// Step 6 cutover: MonsterRegistry/MonsterRuntime are GONE from this record's own production shape -
// see MonsterFeedProjectionRegistry's own doc comment for what replaced them (a per-map,
// World-feed-driven projection, populated by MapTcpServer's own tick loop via IWorldRuntime, never
// constructed here). `MonsterSpawns` retains the raw generated spawn declarations (still needed to
// build a WorldMonsterSpawnBatch for LoadMonsterSpawnsAsync - see WorldMonsterSpawnBatchBuilder) and
// the static MobDefinition data every spawn declaration already embeds (needed by
// MonsterCombatCoordinator's damage formula and WorldMonsterActorView's own GeneratedMobRegistry
// lookup) - WITHOUT ever constructing a live MonsterRegistry/MobInstance to hold it.
public sealed record MapServerWorld(
    WorldMapRegistry Maps,
    IReadOnlyList<MobSpawnDefinition> MonsterSpawns,
    MonsterCombatCoordinator Combat,
    IMapCollisionProvider Collision,
    IMovementPathProvider MovementPathProvider,
    MonsterFeedProjectionRegistry MonsterProjections,
    MonsterCombatStateStore CombatState,
    PlayerPresenceRegistry Players,
    PlayerVisibilityCoordinator PlayerVisibility,
    WorldVisibilityOptions Visibility,
    GameplayRateOptions? Rates = null)
{
    // Compatibility constructor for focused monster/world tests that compose the record directly
    // without going through Build(). It still creates one coherent player-world bundle; it never
    // leaves the new live components null.
    public MapServerWorld(WorldMapRegistry maps, IReadOnlyList<MobSpawnDefinition> monsterSpawns, MonsterCombatCoordinator combat,
        IMapCollisionProvider collision, IMovementPathProvider movementPathProvider,
        MonsterFeedProjectionRegistry monsterProjections, MonsterCombatStateStore combatState,
        GameplayRateOptions? rates = null)
        : this(maps, monsterSpawns, combat, collision, movementPathProvider, monsterProjections, combatState, CreatePlayerWorld(), rates)
    {
    }

    private MapServerWorld(WorldMapRegistry maps, IReadOnlyList<MobSpawnDefinition> monsterSpawns, MonsterCombatCoordinator combat,
        IMapCollisionProvider collision, IMovementPathProvider movementPathProvider,
        MonsterFeedProjectionRegistry monsterProjections, MonsterCombatStateStore combatState,
        (PlayerPresenceRegistry Players, PlayerVisibilityCoordinator Coordinator, WorldVisibilityOptions Options) playerWorld,
        GameplayRateOptions? rates)
        : this(maps, monsterSpawns, combat, collision, movementPathProvider, monsterProjections, combatState,
            playerWorld.Players, playerWorld.Coordinator, playerWorld.Options, rates)
    {
        // Positional order matches the primary record constructor exactly: Maps, MonsterSpawns,
        // Combat, Collision, MovementPathProvider, MonsterProjections, CombatState, Players,
        // PlayerVisibility, Visibility, Rates.
    }

    private static (PlayerPresenceRegistry Players, PlayerVisibilityCoordinator Coordinator, WorldVisibilityOptions Options) CreatePlayerWorld()
    {
        var options = WorldVisibilityOptions.Default;
        var players = new PlayerPresenceRegistry(options);
        return (players, new PlayerVisibilityCoordinator(players, options), options);
    }

    // `gameplayRules` is an ALREADY-COMPOSED bundle from the startup/composition root
    // (MapServerApp.RunAsync -> GameplayRulesFactory.Create). This method never inspects
    // GameplayOptions/RagnarokRuleSet - ruleset selection has already happened by the time a
    // GameplayRuleServices value reaches here.
    // `collisionProvider` defaults to EmptyMapCollisionProvider.Instance: the proprietary source
    // .gat files and any real derived artifact stay local/gitignored, never committed. A REAL
    // provider (the normal production startup case) drives player movement pathing
    // (RathenaCompatibleMovementPathProvider) and combat range checks against the World projection.
    // `customsEnabled` composes Athena.NET's own handwritten Customs/World content alongside the
    // generated world.
    // `servedMaps`: explicit runtime/deployment HOSTING SCOPE - which maps this MapServer build
    // actually serves - supplied by the caller, never inferred from warp graphs, collision data, or
    // any other generated-content signal.
    // `actorIdAllocator` defaults to null, which means "construct the default 110,000,000-based
    // WorldActorIdAllocator here" for NPC/warp actors ONLY - monster ActorIds no longer come from
    // this allocator at all post-cutover (see this record's own top-of-file doc comment).
    public static MapServerWorld Build(GameplayRuleServices gameplayRules, TimeProvider? timeProvider = null, IMapCollisionProvider? collisionProvider = null, GameplayRateOptions? rates = null, bool customsEnabled = false, IReadOnlySet<string>? servedMaps = null, IEnumerable<WarpDefinition>? warpDefinitions = null, IReadOnlySet<string>? mobSpawnMaps = null, WorldActorIdAllocator? actorIdAllocator = null)
    {
        var resolvedCollisionProvider = collisionProvider ?? EmptyMapCollisionProvider.Instance;
        var allocator = actorIdAllocator ?? new WorldActorIdAllocator();
        var builder = new WorldRegistryBuilder();
        GeneratedScriptRegistry.Register(builder);
        if (customsEnabled) CustomWorldRegistry.Register(builder);
        var world = builder.Build();
        var servedWarps = warpDefinitions ?? (servedMaps is null
            ? GeneratedWarpRegistry.All
            : servedMaps.Order(StringComparer.Ordinal).SelectMany(GeneratedWarpRegistry.GetForMap));
        var maps = new WorldMapRegistry(servedWarps, world.Entities, scripts: world.Scripts, allocator: allocator);
        var effectiveMobSpawnMaps = mobSpawnMaps ?? servedMaps;
        var servedMobSpawns = effectiveMobSpawnMaps is null ? world.MobSpawns : world.MobSpawns.Where(spawn => effectiveMobSpawnMaps.Contains(spawn.Map)).ToArray();
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combatState = new MonsterCombatStateStore();
        var combat = new MonsterCombatCoordinator(questDrops, gameplayRules.BasicAttackRules, combatState);
        // Same either/or composition rule the old cell-selector/movement-provider split used:
        // EmptyMapCollisionProvider.Instance keeps the collision-less placeholder path provider
        // (tests/dev fixtures); any real provider gets the collision-backed A* implementation.
        // Player movement (MapClientSession) is this provider's only remaining consumer post-cutover
        // (monster movement/pathing is entirely World-authoritative now).
        IMovementPathProvider movementPathProvider = ReferenceEquals(resolvedCollisionProvider, EmptyMapCollisionProvider.Instance)
            ? new UnverifiedGridLineMovementPathProvider()
            : new RathenaCompatibleMovementPathProvider(resolvedCollisionProvider);
        var visibility = WorldVisibilityOptions.Default;
        var players = new PlayerPresenceRegistry(visibility);
        var playerVisibility = new PlayerVisibilityCoordinator(players, visibility);
        var monsterProjections = new MonsterFeedProjectionRegistry();
        return new MapServerWorld(maps, servedMobSpawns, combat, resolvedCollisionProvider, movementPathProvider, monsterProjections, combatState, players, playerVisibility, visibility, rates ?? new GameplayRateOptions());
    }

    // Production fail-closed guard: called explicitly by MapServerApp.RunAsync BEFORE calling
    // Build, never from inside Build itself. `hasGeneratedMobSpawns` is a plain bool so this method
    // has no dependency on which mob family/content module is generated.
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
