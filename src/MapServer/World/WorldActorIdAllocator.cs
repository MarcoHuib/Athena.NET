namespace Athena.Net.MapServer.World;

// NPC/warp actor-ID allocator. Now seeded from `conf/world_partitions.json`'s reserved
// `npcWarpActorIdRange` (see WorldPartitionTopologyDocument.NpcWarpActorIdRange), NOT a hardcoded
// 110,000,000 base - since Phase 2B monster authority moved into per-partition World ranges
// carved out of that SAME 110,000,000+ actor-ID domain (conf/world_partitions.json's per-partition
// actorIdRange entries), this allocator's own range must be reserved and validated alongside those
// (WorldPartitionActorRanges.ValidateAll) rather than independently starting at the domain's own
// base, which would silently overlap a monster partition's range. The default constructor's
// 109,999,999 base is kept ONLY for existing tests/callers that construct a world without loading
// real topology config (e.g. WorldMapRegistry.Tutorial-style fixtures) - MapServerApp.RunAsync's
// real production composition always uses the seeded-range constructor.
public sealed class WorldActorIdAllocator
{
    private long _lastId;

    public WorldActorIdAllocator() : this(109_999_999) { }

    // `seedExclusive`: the first Allocate() call returns seedExclusive + 1 - matches this type's
    // own historical convention (the default constructor's 109,999,999 base produces a first
    // allocation of 110,000,000, rAthena's START_NPC_NUM).
    public WorldActorIdAllocator(long seedExclusive) => _lastId = seedExclusive;

    public uint Allocate()
    {
        var value = Interlocked.Increment(ref _lastId);
        if (value > uint.MaxValue)
        {
            throw new InvalidOperationException("The world actor ID domain is exhausted.");
        }

        return (uint)value;
    }
}
