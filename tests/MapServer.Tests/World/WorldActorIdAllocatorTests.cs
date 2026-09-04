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

    // REMOVED (Step 6 cutover): this test asserted that WorldMapRegistry (NPC/warp actors) and
    // MonsterRegistry (monster actors) drew from the SAME WorldActorIdAllocator instance inside
    // MapServerWorld.Build(). That premise no longer holds - monster ActorIds are now
    // World-authoritative, leased by the WorldPartitionGrain itself (see MapServerWorld's own
    // top-of-file doc comment: "MapServer no longer allocates a second, competing local ActorId set
    // for monsters at all"). MapServerWorld.Build() no longer constructs a live MonsterRegistry, so
    // there is nothing left of this scenario to assert against.
}
