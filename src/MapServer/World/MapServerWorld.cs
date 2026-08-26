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
public sealed record MapServerWorld(WorldMapRegistry Maps, MonsterRegistry Monsters, MonsterCombatCoordinator Combat)
{
    // `cellSelector` defaults to UnverifiedFallbackMobSpawnCellSelector because
    // Athena.NET has no GAT/mapcache/collision data anywhere in this repository
    // or the pinned legacy/rathena submodule (confirmed by a full-repo search:
    // no .gat/.rsw files, no checked-in mapcache output, no map-dimension data
    // of any kind - see MobSpawnCellSelector.cs). This is a genuine external-
    // data gap, not a shortcut: monster POSITIONS placed this way are a
    // documented placeholder, not real rAthena spawn-cell parity, and callers
    // must not describe them as authoritative until real GAT data exists.
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
    public static MapServerWorld Build(GameplayRuleServices gameplayRules, IMobSpawnCellSelector? cellSelector = null, TimeProvider? timeProvider = null)
    {
        var allocator = new WorldActorIdAllocator();
        var maps = WorldMapRegistry.LoadGenerated(allocator);
        var monsters = new MonsterRegistry(
            GeneratedScriptRegistry.MobSpawns,
            allocator,
            cellSelector ?? new UnverifiedFallbackMobSpawnCellSelector(),
            timeProvider ?? TimeProvider.System);
        var questDrops = new QuestDropResolver(GeneratedQuestDrops.All);
        var combat = new MonsterCombatCoordinator(monsters, questDrops, gameplayRules.BasicAttackRules);
        return new MapServerWorld(maps, monsters, combat);
    }
}
