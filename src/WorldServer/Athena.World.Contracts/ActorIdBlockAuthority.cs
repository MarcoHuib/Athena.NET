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
            if (candidate < _currentBlock.EndExclusive) return (uint)candidate;

            // Current block exhausted (or never leased). Only one caller actually leases a fresh
            // block; concurrent callers that lose the race simply retry their own allocation
            // against the block the winner just installed.
            await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                // Re-check under the gate: another caller may have already leased a fresh block
                // while this one was waiting.
                if (candidate >= _currentBlock.EndExclusive)
                {
                    _currentBlock = await _leaseBlock(_blockSize, cancellationToken).ConfigureAwait(false);
                    _next = _currentBlock.StartInclusive - 1L;
                }
            }
            finally
            {
                _leaseGate.Release();
            }
        }
    }
}
