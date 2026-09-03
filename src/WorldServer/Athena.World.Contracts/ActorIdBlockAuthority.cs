namespace Athena.Net.World.Contracts;

using Orleans;

// One contiguous, exclusive-upper-bound slice of the global actor-ID domain
// (rAthena's NPC/monster/warp domain, START_NPC_NUM = 110,000,000). Leased
// wholesale to one requester by IActorIdBlockAuthorityGrain; the requester
// then allocates individual IDs from within it locally, with no further
// round-trip per actor.
[GenerateSerializer]
public readonly record struct ActorIdBlock(
    [property: Id(0)] uint StartInclusive,
    [property: Id(1)] uint EndExclusive);

// The single source of truth for the ENTIRE global client-visible actor-ID namespace shared by
// every monster partition, MapServer's own NPC/warp actor domain, and any future world-actor
// authority (an instance-dungeon authority, etc.). Deliberately NOT a config-declared per-caller
// range (see WorldPartitionTopology.cs's own doc comment on why partition topology carries no
// actor-ID concept at all) - a fixed, hand-assigned range per caller does not scale as the number
// of partitions/authorities grows and would require editing numeric config every time one is
// added, renamed, or split. Instead this grain hands out reasonably large, non-overlapping BLOCKS
// on request; every caller is treated identically (a monster partition and MapServer's NPC/warp
// allocator draw from the exact same sequential domain, no type-specific carve-out).
//
// IGrainWithIntegerKey, always addressed at the SAME well-known key (see
// ActorIdBlockAuthorityGrain.WellKnownKey) so every caller in the cluster resolves to one single
// grain instance - the uniqueness guarantee below depends on there being exactly one.
public interface IActorIdBlockAuthorityGrain : IGrainWithIntegerKey
{
    // `requesterId` is carried only for diagnostics/telemetry (which authority leased which
    // block) - it never affects block placement or size; every requester is fungible. `blockSize`
    // is the caller's own choice (LeasedBlockActorIdAllocator's default is 10,000 - see that
    // type's own doc comment for the sizing rationale).
    Task<ActorIdBlock> LeaseBlockAsync(string requesterId, uint blockSize);
}

public static class ActorIdBlockAuthorityGrainKey
{
    // The one well-known IGrainWithIntegerKey value every caller in the cluster must use to reach
    // the SAME ActorIdBlockAuthorityGrain instance. Any single constant works (Orleans grain
    // identity is (type, key), so this is not itself the actor-ID domain's own numeric base) - 0
    // is chosen only for readability.
    public const long WellKnownKey = 0;
}

// Leases actor-ID blocks from IActorIdBlockAuthorityGrain and allocates individual IDs from the
// currently-held block locally (Interlocked, lock-free, matching this project's existing
// allocator style) - a round-trip to the authority grain happens only once per `blockSize`
// allocations, never once per actor. Deliberately depends on a plain
// Func<uint,CancellationToken,Task<ActorIdBlock>> delegate rather than IClusterClient/IGrainFactory
// directly, so this type has zero Orleans-hosting dependency itself and can be constructed
// identically by Athena.World's grain composition (closing over
// GrainFactory.GetGrain<IActorIdBlockAuthorityGrain>(...).LeaseBlockAsync) and MapServer's own
// composition root (closing over the same call through its own IClusterClient) - matching this
// codebase's existing "keep Orleans behind an adapter boundary" convention (IWorldRuntime/
// OrleansWorldRuntime).
public sealed class LeasedBlockActorIdAllocator
{
    // Large enough that a single map's worth of monster spawns (a few hundred, per
    // MonsterRegistry's existing construction pattern) never exhausts a block on its own, small
    // enough that an idle/rarely-used authority never hoards a disproportionate share of the
    // domain. Not a hard architectural requirement - an implementation detail tunable without
    // changing this type's contract.
    public const uint DefaultBlockSize = 10_000;

    private readonly Func<uint, CancellationToken, Task<ActorIdBlock>> _leaseBlock;
    private readonly uint _blockSize;
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private ActorIdBlock _currentBlock;
    private long _next;

    public LeasedBlockActorIdAllocator(Func<uint, CancellationToken, Task<ActorIdBlock>> leaseBlock, uint blockSize = DefaultBlockSize)
    {
        _leaseBlock = leaseBlock;
        _blockSize = blockSize;
        // _next starts past any real block's end, so the very first AllocateAsync call always
        // takes the "block exhausted, lease a fresh one" path below rather than needing a special
        // "not yet leased" sentinel state.
        _next = long.MaxValue;
    }

    public async Task<uint> AllocateAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var candidate = Interlocked.Increment(ref _next);
            // Re-read the block snapshot the SAME candidate was validated against - a concurrent
            // re-lease (below) can move both _currentBlock and _next between this candidate's
            // increment and its bounds check, so both sides of the comparison must be captured
            // together, never the block re-read a second time after possibly changing underneath.
            var block = _currentBlock;
            if (candidate >= block.StartInclusive && candidate < block.EndExclusive) return (uint)candidate;

            // Candidate fell outside the current block - either it was never leased yet, it's
            // exhausted, or (for a caller that lost a concurrent race) it was computed against a
            // block another caller has already replaced. Every one of these cases is handled by
            // the SAME retry: take the gate, lease a fresh block if nobody already did, then loop
            // back and draw a BRAND NEW candidate against the now-current block - a caller must
            // never reuse a candidate computed against a stale block, since that value may already
            // have been (or be about to be) handed out by another caller against the new block.
            await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check under the gate against the LATEST block: another caller may have
                // already leased a fresh one covering this candidate while this call was waiting,
                // in which case there is nothing to do and the loop's next iteration succeeds
                // immediately without a redundant lease.
                if (candidate < _currentBlock.StartInclusive || candidate >= _currentBlock.EndExclusive)
                {
                    var leased = await _leaseBlock(_blockSize, cancellationToken).ConfigureAwait(false);
                    _currentBlock = leased;
                    _next = leased.StartInclusive - 1L;
                }
            }
            finally
            {
                _leaseGate.Release();
            }
        }
    }
}
