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

    // Proves the leased-block boundary is actually ENFORCED (per plan-review correction), not
    // merely documented in a comment: the allocator must succeed for exactly the block's own last
    // ID and then fail loudly on the next call - never silently continue into a range another
    // authority (a monster partition, a future world-actor authority) may have been leased.
    [Fact]
    public void BoundedAllocator_SucceedsForLastIdInBlock_ThenThrowsRatherThanCrossingIntoAnotherAuthoritysRange()
    {
        // A tiny leased block: [1000, 1003) - exactly 3 allocatable IDs (1000, 1001, 1002).
        var allocator = new WorldActorIdAllocator(seedExclusive: 999, endExclusive: 1003);

        Assert.Equal(1000u, allocator.Allocate());
        Assert.Equal(1001u, allocator.Allocate());
        Assert.Equal(1002u, allocator.Allocate()); // Last ID actually inside the leased block.

        var ex = Assert.Throws<InvalidOperationException>(() => allocator.Allocate());
        Assert.Contains("exhausted", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    // A failed allocation attempt must not permanently burn a slot - repeated calls after
    // exhaustion keep throwing (never "recover" into a bogus value), and the rejection is
    // deterministic/repeatable, not a one-shot fluke.
    [Fact]
    public void BoundedAllocator_RepeatedCallsAfterExhaustion_KeepThrowing()
    {
        var allocator = new WorldActorIdAllocator(seedExclusive: 4_999_999, endExclusive: 5_000_001);
        Assert.Equal(5_000_000u, allocator.Allocate());
        Assert.Throws<InvalidOperationException>(() => allocator.Allocate());
        Assert.Throws<InvalidOperationException>(() => allocator.Allocate());
    }

    [Fact]
    public void MapServerWorldBuild_SharesOneAllocator_MapAndMonsterActorIdsNeverCollide()
    {
        // MapServerWorld.Build() is the composition root: WorldMapRegistry (NPC/warp actors) and
        // MonsterRegistry (monster actors) must draw from the SAME WorldActorIdAllocator instance,
        // not two independently-numbered ones - this test would fail if that wiring regressed back
        // to two separate `new WorldActorIdAllocator()` calls (as it did before this fix), since
        // both would then start from 110,000,000 and immediately collide.
        var world = MapServerWorld.Build(new GameplayRuleServices(new RenewalBasicAttackRules()), warpDefinitions: []);

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
