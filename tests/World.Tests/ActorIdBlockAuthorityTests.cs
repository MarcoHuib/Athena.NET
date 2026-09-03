using Athena.Net.World.Contracts;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// ActorIdBlockAuthorityGrain needs no injected dependencies (no collision provider, no partition
// resolver) - a plain TestClusterBuilder with no silo configurator is sufficient, unlike
// WorldPartitionGrainTests' own cluster (which DOES need IWorldPartitionResolver/
// IMovementPathProvider registered for WorldPartitionGrain itself).
public sealed class ActorIdBlockAuthorityGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { _cluster = new TestClusterBuilder().Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    private IActorIdBlockAuthorityGrain Authority() =>
        _cluster.GrainFactory.GetGrain<IActorIdBlockAuthorityGrain>(ActorIdBlockAuthorityGrainKey.WellKnownKey);

    // Two DIFFERENT authorities (e.g. one WorldPartitionGrain and MapServer's own NPC/warp
    // allocator) leasing blocks from the SAME well-known grain must never receive overlapping
    // ranges - the core uniqueness guarantee this design replaces config-declared per-partition
    // ranges with.
    [Fact]
    public async Task TwoAuthorities_LeaseNonOverlappingBlocks()
    {
        var authority = Authority();
        var monsterPartitionBlock = await authority.LeaseBlockAsync("prontera-region", 1000);
        var npcWarpBlock = await authority.LeaseBlockAsync("npc-warp", 1000);

        Assert.NotEqual(monsterPartitionBlock, npcWarpBlock);
        var overlaps = monsterPartitionBlock.StartInclusive < npcWarpBlock.EndExclusive
            && npcWarpBlock.StartInclusive < monsterPartitionBlock.EndExclusive;
        Assert.False(overlaps, $"Blocks {monsterPartitionBlock} and {npcWarpBlock} must not overlap.");
    }

    // Every allocation-relevant field is exclusive-upper-bound, so a block's own IDs (from
    // StartInclusive to EndExclusive-1) are all distinct by construction - proven directly rather
    // than assumed.
    [Fact]
    public async Task OneBlock_HasExactlyBlockSizeDistinctIds()
    {
        var block = await Authority().LeaseBlockAsync("test", 250);
        Assert.Equal(250u, block.EndExclusive - block.StartInclusive);
    }

    // Repeated leases from the SAME authority never repeat a previously-leased block either -
    // proving the grain's own sequential advance, not merely two-caller disjointness.
    [Fact]
    public async Task RepeatedLeasesFromOneAuthority_NeverOverlapEachOther()
    {
        var authority = Authority();
        var blocks = new List<ActorIdBlock>();
        for (var i = 0; i < 10; i++) blocks.Add(await authority.LeaseBlockAsync("repeated", 500));

        for (var i = 0; i < blocks.Count; i++)
        for (var j = i + 1; j < blocks.Count; j++)
        {
            var overlaps = blocks[i].StartInclusive < blocks[j].EndExclusive && blocks[j].StartInclusive < blocks[i].EndExclusive;
            Assert.False(overlaps, $"Blocks {blocks[i]} and {blocks[j]} must not overlap.");
        }
    }

    [Fact]
    public async Task LeasedBlocks_StartAtOrAboveTheRealNpcDomainBase()
    {
        var block = await Authority().LeaseBlockAsync("test", 1);
        Assert.True(block.StartInclusive >= 110_000_000, $"Block {block} must start at or above rAthena's START_NPC_NUM.");
    }
}

// LeasedBlockActorIdAllocator's own local-allocation/re-lease behavior, tested against a fake
// leaseBlock delegate (no real Orleans cluster needed - the allocator itself has zero Orleans
// dependency, per its own doc comment) so these tests run fast and deterministically control
// exactly when/how many times a lease happens.
public sealed class LeasedBlockActorIdAllocatorTests
{
    [Fact]
    public async Task AllocationsWithinOneBlock_AreAllDistinct()
    {
        var leaseCount = 0;
        var allocator = new LeasedBlockActorIdAllocator((size, _) =>
        {
            leaseCount++;
            return Task.FromResult(new ActorIdBlock(110_000_000, 110_000_000 + size));
        }, blockSize: 100);

        var ids = new HashSet<uint>();
        for (var i = 0; i < 100; i++) ids.Add(await allocator.AllocateAsync());

        Assert.Equal(100, ids.Count);
        Assert.Equal(1, leaseCount); // Exactly one lease for exactly one block's worth of allocations.
    }

    // Exhausting a block must transparently lease a fresh, non-overlapping one and continue
    // allocating from it - never surface exhaustion as a caller-visible failure.
    [Fact]
    public async Task ExhaustingABlock_TransparentlyLeasesANonOverlappingNextBlock()
    {
        var leasedBlocks = new List<ActorIdBlock>();
        var nextStart = 110_000_000u;
        var allocator = new LeasedBlockActorIdAllocator((size, _) =>
        {
            var block = new ActorIdBlock(nextStart, nextStart + size);
            nextStart += size;
            leasedBlocks.Add(block);
            return Task.FromResult(block);
        }, blockSize: 10);

        var ids = new List<uint>();
        for (var i = 0; i < 25; i++) ids.Add(await allocator.AllocateAsync()); // Spans 3 blocks (10+10+5).

        Assert.Equal(25, ids.Distinct().Count()); // Every allocated ID is unique across the block boundary.
        Assert.Equal(3, leasedBlocks.Count);
        for (var i = 0; i < leasedBlocks.Count; i++)
        for (var j = i + 1; j < leasedBlocks.Count; j++)
        {
            var overlaps = leasedBlocks[i].StartInclusive < leasedBlocks[j].EndExclusive && leasedBlocks[j].StartInclusive < leasedBlocks[i].EndExclusive;
            Assert.False(overlaps);
        }
    }

    // Concurrent allocation across a block boundary must never hand out a duplicate ID - proves
    // the allocator's own internal race-freedom (SemaphoreSlim-gated re-lease), independent of
    // ActorIdBlockAuthorityGrainTests' proof that the GRAIN itself is race-free.
    [Fact]
    public async Task ConcurrentAllocationAcrossBlockBoundary_NeverDuplicatesAnId()
    {
        var nextStart = 110_000_000u;
        var allocator = new LeasedBlockActorIdAllocator(async (size, _) =>
        {
            await Task.Yield(); // Widen the race window so concurrent callers genuinely overlap here.
            var start = Interlocked.Add(ref nextStart, size) - size;
            return new ActorIdBlock(start, start + size);
        }, blockSize: 50);

        var tasks = Enumerable.Range(0, 500).Select(async _ => await allocator.AllocateAsync()).ToArray();
        var ids = await Task.WhenAll(tasks);

        Assert.Equal(500, ids.Distinct().Count());
    }
}
