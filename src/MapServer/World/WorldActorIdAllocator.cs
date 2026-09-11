namespace Athena.Net.MapServer.World;

// NPC/warp actor-ID allocator. Production (MapServerApp.RunAsync) seeds this from a block leased
// from the global Athena.World.Contracts.IActorIdBlockAuthorityGrain (see
// Athena.Net.World.Contracts.LeasedBlockActorIdAllocator's own doc comment for the leasing design)
// rather than a hardcoded 110,000,000 base - the leased block's own [StartInclusive, EndExclusive)
// bounds are what this allocator enforces below, so it can never allocate past what it was actually
// granted and collide with a block leased to a different authority (a monster partition, a future
// world-actor authority, etc.). The default constructor's unbounded 109,999,999-seeded behavior is
// kept ONLY for existing tests/callers that construct a world without leasing a real block (e.g.
// WorldMapRegistry.Tutorial-style fixtures, which have no cluster to lease from).
public sealed class WorldActorIdAllocator
{
    private readonly long _endExclusive;
    private long _lastId;

    // Unbounded (up to uint.MaxValue) - matches this type's original historical behavior, for
    // callers with no real leased block to enforce.
    public WorldActorIdAllocator() : this(109_999_999, (long)uint.MaxValue + 1) { }

    // `seedExclusive`: the first Allocate() call returns seedExclusive + 1 - matches this type's
    // own historical convention (the default constructor's 109,999,999 base produces a first
    // allocation of 110,000,000, rAthena's START_NPC_NUM). Unbounded (no upper enforcement) - use
    // the (seedExclusive, endExclusive) overload when a real leased block's boundary must be
    // enforced.
    public WorldActorIdAllocator(long seedExclusive) : this(seedExclusive, (long)uint.MaxValue + 1) { }

    // `endExclusive` is the leased block's own EndExclusive (Athena.Net.World.Contracts.ActorIdBlock)
    // - Allocate() throws once it would return a value >= endExclusive, rather than silently
    // continuing to hand out IDs past what was actually granted. This is the enforcement mechanism
    // that makes the "leased block never gets crossed" guarantee real rather than a comment.
    public WorldActorIdAllocator(long seedExclusive, long endExclusive)
    {
        _lastId = seedExclusive;
        _endExclusive = endExclusive;
    }

    public uint Allocate()
    {
        var value = Interlocked.Increment(ref _lastId);
        if (value >= _endExclusive)
        {
            // Roll back: a failed allocation must not permanently burn a slot from the (possibly
            // still-bounded-differently) counter - a caller catching this and retrying via a fresh
            // allocator/lease must see the exact same next candidate this call itself rejected.
            Interlocked.Decrement(ref _lastId);
            throw new InvalidOperationException("The leased actor-ID block is exhausted.");
        }

        return (uint)value;
    }
}
