using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class WorldActorIdAllocatorTests
{
    [Fact]
    public void DefaultStart_MatchesRathenaNpcDomain()
    {
        var allocator = new WorldActorIdAllocator();
        Assert.Equal(110_000_000u, allocator.Allocate());
        Assert.Equal(110_000_001u, allocator.Allocate());
    }

    [Fact]
    public void SharedAllocator_ProducesUniqueIdsAcrossManyAllocations()
    {
        var allocator = new WorldActorIdAllocator();
        var ids = Enumerable.Range(0, 200).Select(_ => allocator.Allocate()).ToArray();

        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void MapServerWorldBuild_SharesOneAllocator_MapAndMonsterActorIdsNeverCollide()
    {
        // MapServerWorld.Build() is the composition root: WorldMapRegistry (NPC/warp actors) and
        // MonsterRegistry (monster actors) must draw from the SAME WorldActorIdAllocator instance,
        // not two independently-numbered ones - this test would fail if that wiring regressed back
        // to two separate `new WorldActorIdAllocator()` calls (as it did before this fix), since
        // both would then start from 110,000,000 and immediately collide.
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()));

        var npcAndWarpActorIds = world.Maps
            .GetVisibleWarpActors("iz_int01", 0, 0, range: ushort.MaxValue)
            .Concat(world.Maps.GetVisibleWarpActors("int_land01", 0, 0, range: ushort.MaxValue))
            .Concat(world.Maps.GetVisibleWarpActors("int_land02", 0, 0, range: ushort.MaxValue))
            .Concat(world.Maps.GetVisibleWarpActors("int_land03", 0, 0, range: ushort.MaxValue))
            .Concat(world.Maps.GetVisibleWarpActors("int_land04", 0, 0, range: ushort.MaxValue))
            .Select(actor => actor.ActorId)
            .ToHashSet();
        var monsterActorIds = world.Monsters.AllInstances.Select(instance => instance.ActorId).ToHashSet();

        Assert.NotEmpty(npcAndWarpActorIds);
        Assert.NotEmpty(monsterActorIds);
        Assert.Empty(npcAndWarpActorIds.Intersect(monsterActorIds));
    }
}
