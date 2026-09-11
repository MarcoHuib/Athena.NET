using Athena.Net.World.Contracts;
using Orleans.Hosting;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

// ActorIdBlockAuthorityGrain needs its persisted-state storage provider registered - matching
// Athena.World's own Program.cs (AddMemoryGrainStorage("actorIdBlockAuthority")) exactly, since a
// mismatched/missing provider name is a real production-configuration bug this test cluster must
// be able to catch too, not paper over. No other dependency (collision provider, partition
// resolver) is needed, unlike WorldPartitionGrainTests' own cluster.
public sealed class ActorIdBlockAuthorityGrainTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;
    public async Task InitializeAsync() { var builder = new TestClusterBuilder(); builder.AddSiloBuilderConfigurator<StorageConfigurator>(); _cluster = builder.Build(); await _cluster.DeployAsync(); }
    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    public sealed class StorageConfigurator : ISiloConfigurator
    {
        public void Configure(ISiloBuilder siloBuilder) => siloBuilder.AddMemoryGrainStorage("actorIdBlockAuthority");
    }

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

    // Proves the GRAIN-ACTIVATION-LIFETIME invariant directly: forcing this grain's activation to
    // deactivate and reactivate (within the SAME running TestCluster/silo, matching what ordinary
    // idle-collection/rebalancing does in production) must NOT reset the cursor back to the domain
    // base. Without persisted state (an earlier draft of this grain used a bare in-memory field),
    // this test would observe the second lease re-issuing the exact same block the first one got -
    // a real, silent uniqueness violation this test exists specifically to catch.
    [Fact]
    public async Task ActivationRecycling_DoesNotResetTheBlockSequence()
    {
        var authority = Authority();
        var beforeDeactivation = await authority.LeaseBlockAsync("before", 100);

        // Orleans.TestingHost exposes no direct "deactivate this grain" API on the grain reference
        // itself; the management grain's DeactivateOnIdle is the standard TestingHost mechanism for
        // forcing exactly this within a running cluster (as opposed to stopping/restarting a silo,
        // which is a different, HEAVIER scenario this test deliberately does not need - persisted
        // state surviving mere activation recycling is the invariant under test here, not full silo
        // restart, which ActorIdBlockAuthorityGrain's own doc comment already discloses as NOT
        // guaranteed).
        var managementGrain = _cluster.Client.GetGrain<Orleans.Runtime.IManagementGrain>(0);
        await managementGrain.ForceActivationCollection(TimeSpan.Zero);

        var afterReactivation = await authority.LeaseBlockAsync("after", 100);

        var overlaps = beforeDeactivation.StartInclusive < afterReactivation.EndExclusive
            && afterReactivation.StartInclusive < beforeDeactivation.EndExclusive;
        Assert.False(overlaps, $"Blocks {beforeDeactivation} and {afterReactivation} must not overlap even across activation recycling.");
        Assert.True(afterReactivation.StartInclusive >= beforeDeactivation.EndExclusive,
            "The post-reactivation block must continue the SAME sequence, never restart from the domain base.");
    }

    // The domain's true final ID (uint.MaxValue) is a legitimately leasable/allocatable value, and
    // a lease reaching it must not wrap EndExclusive to 0 (an earlier draft stored EndExclusive as
    // uint, which could) - proven by leasing right up to the boundary and confirming both that the
    // boundary lease succeeds with the correct EndExclusive and that a further lease past it fails
    // rather than silently wrapping.
    [Fact]
    public async Task LeasingAtTheExactDomainUpperBound_SucceedsAndFurtherLeasesFail()
    {
        // A fresh grain key (not the well-known one every other test in this class shares) so this
        // test can drive its own cursor all the way to the domain's real upper bound without
        // interfering with/being interfered by the other tests' leases against the shared authority.
        var isolatedAuthority = _cluster.GrainFactory.GetGrain<IActorIdBlockAuthorityGrain>(unchecked((long)0xACD0_ACD0_ACD0_ACD0));
        // Drain the domain down to exactly one remaining ID (uint.MaxValue itself) in one big lease.
        const uint DomainStart = 110_000_000;
        var almostEverything = await isolatedAuthority.LeaseBlockAsync("drain", uint.MaxValue - DomainStart);
        Assert.Equal((ulong)uint.MaxValue, almostEverything.EndExclusive);

        var lastId = await isolatedAuthority.LeaseBlockAsync("last-id", 1);
        Assert.Equal(uint.MaxValue, lastId.StartInclusive);
        Assert.Equal((ulong)uint.MaxValue + 1UL, lastId.EndExclusive); // Exact exclusive domain end - must NOT wrap to 0.

        await Assert.ThrowsAsync<InvalidOperationException>(() => isolatedAuthority.LeaseBlockAsync("past-the-end", 1));
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

    // Concurrent allocation across MULTIPLE block boundaries must (a) never hand out a duplicate
    // ID and (b) never lease more blocks than the allocation count actually requires - proving the
    // fix for the specific bug an earlier draft of LeasedBlockActorIdAllocator had: a shared
    // ever-incrementing counter meant every caller whose candidate fell outside the current block
    // (including callers merely re-checking bounds on retry) still consumed a slot from whatever
    // block eventually became current, so many concurrent callers piling up at one boundary could
    // trigger a CHAIN of redundant leases far beyond what was actually needed. 500 allocations at
    // blockSize=50 need EXACTLY 10 blocks if leasing is precise - never "approximately 10, plus
    // however many extra a race caused".
    [Fact]
    public async Task ConcurrentAllocationAcrossMultipleBlockBoundaries_LeasesExactlyTheBlocksRequired()
    {
        var nextStart = 110_000_000u;
        var leaseCount = 0;
        var allocator = new LeasedBlockActorIdAllocator(async (size, _) =>
        {
            Interlocked.Increment(ref leaseCount);
            await Task.Yield(); // Widen the race window so concurrent callers genuinely overlap here.
            var start = Interlocked.Add(ref nextStart, size) - size;
            return new ActorIdBlock(start, start + size);
        }, blockSize: 50);

        var tasks = Enumerable.Range(0, 500).Select(async _ => await allocator.AllocateAsync()).ToArray();
        var ids = await Task.WhenAll(tasks);

        Assert.Equal(500, ids.Distinct().Count()); // No duplicate ever handed out.
        Assert.Equal(10, leaseCount); // 500 / 50 = exactly 10 blocks - no redundant leases from boundary contention.
    }
}
