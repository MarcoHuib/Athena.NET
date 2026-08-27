using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Generated.GameData.Quests;
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
public sealed record MapServerWorld(WorldMapRegistry Maps, MonsterRegistry Monsters, MonsterCombatCoordinator Combat, IMapCollisionProvider Collision)
{
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
    public static MapServerWorld Build(GameplayRuleServices gameplayRules, IMobSpawnCellSelector? cellSelector = null, TimeProvider? timeProvider = null, IMapCollisionProvider? collisionProvider = null)
    {
        var resolvedCollisionProvider = collisionProvider ?? EmptyMapCollisionProvider.Instance;
        var allocator = new WorldActorIdAllocator();
        var maps = WorldMapRegistry.LoadGenerated(allocator);
        // Explicit either/or choice, not a fallback: EmptyMapCollisionProvider.Instance IS the
        // collision-less/dev case; anything else is a real collision-backed world.
        IMobSpawnCellSelector defaultCellSelector = ReferenceEquals(resolvedCollisionProvider, EmptyMapCollisionProvider.Instance)
            ? new UnverifiedFallbackMobSpawnCellSelector()
            : new RathenaCompatibleMobSpawnCellSelector(resolvedCollisionProvider);
        var monsters = new MonsterRegistry(
            GeneratedScriptRegistry.MobSpawns,
            allocator,
            cellSelector ?? defaultCellSelector,
            timeProvider ?? TimeProvider.System);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(monsters, questDrops, gameplayRules.BasicAttackRules);
        return new MapServerWorld(maps, monsters, combat, resolvedCollisionProvider);
    }
}
