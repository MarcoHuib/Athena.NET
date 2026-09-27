namespace Athena.Net.World.Contracts;

using Orleans;

// One contiguous, exclusive-upper-bound slice of the global actor-ID domain
// (rAthena's NPC/monster/warp domain, START_NPC_NUM = 110,000,000). Leased
// wholesale to one requester by IActorIdBlockAuthorityGrain; the requester
// then allocates individual IDs from within it locally, with no further
// round-trip per actor.
//
// `EndExclusive` is a `ulong`, not `uint`: the domain's real exclusive upper bound is
// `uint.MaxValue + 1` (a block whose last ID is exactly uint.MaxValue must still express an
// exclusive end one past it), which does not fit in a uint. Every ID actually IN a block (i.e.
// every value in [StartInclusive, EndExclusive)) always fits in uint by construction - see
// ActorIdBlockAuthorityGrain.LeaseBlockAsync's own doc comment for the exact reservation that
// guarantees this without wrapping arithmetic anywhere.
[GenerateSerializer]
public readonly record struct ActorIdBlock(
    [property: Id(0)] uint StartInclusive,
    [property: Id(1)] ulong EndExclusive)
{
    public bool Contains(long value) => value >= StartInclusive && value < (long)EndExclusive;
}

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
// ActorIdBlockAuthorityGrainKey.WellKnownKey) so every caller in the cluster resolves to one
// single grain instance - the uniqueness guarantee below depends on there being exactly one.
//
// Grain-activation lifetime: an Orleans grain activation can be deactivated (idle collection,
// rebalancing, etc.) and later reactivated within the SAME running silo process. A naive
// in-memory-only field would reset _nextBlockStart back to the domain base on reactivation and
// could then re-issue already-leased blocks to a caller that still holds them. The real
// implementation (ActorIdBlockAuthorityGrain in Athena.World) therefore persists its cursor via
// Orleans' memory grain-storage provider (IPersistentState, AddMemoryGrainStorage) - this survives
// ordinary activation deactivation/reactivation for the lifetime of the running silo PROCESS, but
// is explicitly NOT durable across a full silo/process restart (it is memory-backed, not disk- or
// database-backed) - see the grain implementation's own doc comment for the full invariant and its
// stated limitation. A future PR wanting restart-safe/HA allocation would swap the storage provider
// (e.g. to a real database-backed one) without changing this interface at all.
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
// currently-held block locally (lock-free in the common case, matching this project's existing
// allocator style) - a round-trip to the authority grain happens only once per `blockSize`
// allocations, never once per actor. Deliberately depends on a plain
// Func<uint,CancellationToken,Task<ActorIdBlock>> delegate rather than IClusterClient/IGrainFactory
// directly, so this type has zero Orleans-hosting dependency itself and can be constructed
// identically by Athena.World's grain composition (closing over
// GrainFactory.GetGrain<IActorIdBlockAuthorityGrain>(...).LeaseBlockAsync) and MapServer's own
// composition root (closing over the same call through its own IClusterClient) - matching this
// codebase's existing "keep Orleans behind an adapter boundary" convention (IWorldRuntime/
// OrleansWorldRuntime).
//
// Concurrency design: state is one immutable LeaseState snapshot (block + a local cursor),
// replaced atomically via Interlocked.CompareExchange - NEVER a bare shared counter incremented
// unconditionally on every attempt (an earlier draft of this type did that, and it is wrong: a
// caller whose candidate fell outside the current block would still have already consumed a slot
// from whatever block eventually became current, and every retry burned another slot even while
// merely re-checking bounds, causing far more block leases than allocations under contention at a
// block boundary). Instead: a caller reads the CURRENT state, tries to atomically claim the next
// slot FROM THAT EXACT STATE OBJECT (CompareExchange on the state field, swapping in a state whose
// cursor is one higher, only if no other caller already advanced past it) - if that succeeds and
// the claimed value is still within the block, allocation is done in one step, no lease and no
// gate. If the block is exhausted (or was never leased), the caller takes the lease gate, leases a
// fresh block ONLY IF the state still `ReferenceEquals` the exact object this caller observed as
// exhausted (another caller may have already installed a fresh state while this one waited for the
// gate - in which case this caller does nothing but retry against that fresh state instead of
// leasing redundantly), then loops back to try claiming a slot again from whatever state is now
// current.
public sealed class LeasedBlockActorIdAllocator
{
    // Large enough that a single map's worth of monster spawns (a few hundred, per
    // MonsterRegistry's existing construction pattern) never exhausts a block on its own, small
    // enough that an idle/rarely-used authority never hoards a disproportionate share of the
    // domain. Not a hard architectural requirement - an implementation detail tunable without
    // changing this type's contract.
    public const uint DefaultBlockSize = 10_000;

    // Immutable snapshot of "the block currently being allocated from, and how far into it we've
    // gotten" - a NEW instance is installed (via Interlocked.CompareExchange on _state) every time
    // a fresh block is leased; an individual allocation attempt only ever mutates its OWN
    // snapshot's local Cursor field via CompareExchange, never a field shared across snapshots.
    private sealed class LeaseState(ActorIdBlock block)
    {
        public readonly ActorIdBlock Block = block;
        // One less than Block.StartInclusive: the first successful claim increments this to
        // exactly Block.StartInclusive, matching every other allocator in this codebase's
        // "AddInt64 stores the NEWLY allocated value" convention.
        public long Cursor = (long)block.StartInclusive - 1;
    }

    // The empty/never-leased sentinel state: Block.EndExclusive is 0, so EVERY candidate value
    // (Cursor+1, always >= 0) is immediately recognized as out-of-block by TryClaim, driving the
    // very first AllocateAsync call straight into the lease path with no special-cased "not yet
    // leased" branch anywhere else in this type.
    private static readonly LeaseState Empty = new(default);

    private readonly Func<uint, CancellationToken, Task<ActorIdBlock>> _leaseBlock;
    private readonly uint _blockSize;
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private LeaseState _state = Empty;

    public LeasedBlockActorIdAllocator(Func<uint, CancellationToken, Task<ActorIdBlock>> leaseBlock, uint blockSize = DefaultBlockSize)
    {
        _leaseBlock = leaseBlock;
        _blockSize = blockSize;
    }

    public async Task<uint> AllocateAsync(CancellationToken cancellationToken = default)
    {
        while (true)
        {
            var state = Volatile.Read(ref _state);
            if (TryClaim(state, out var claimed)) return claimed;

            // `state`'s block is exhausted (or the sentinel). Only the caller that actually
            // installs a replacement leases a fresh block; every other concurrent caller
            // recognizes (via the ReferenceEquals check below) that _state already moved on and
            // simply retries against whatever is current now - never leasing redundantly.
            await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (ReferenceEquals(Volatile.Read(ref _state), state))
                {
                    var leased = await _leaseBlock(_blockSize, cancellationToken).ConfigureAwait(false);
                    Volatile.Write(ref _state, new LeaseState(leased));
                }
            }
            finally
            {
                _leaseGate.Release();
            }
        }
    }

    // Attempts to claim exactly one slot from `state` (never any other/newer state). Returns false
    // - claiming nothing, mutating nothing else - if `state`'s block cannot satisfy this claim,
    // either because it's exhausted or another concurrent caller's CompareExchange already won the
    // race for the specific slot this call would have claimed (in which case the caller loops and
    // tries again from the CURRENT _state, which may already be this same state with room left, or
    // may by then be a newer one).
    private static bool TryClaim(LeaseState state, out uint claimed)
    {
        while (true)
        {
            var current = Volatile.Read(ref state.Cursor);
            var candidate = current + 1;
            if (!state.Block.Contains(candidate)) { claimed = default; return false; }
            if (Interlocked.CompareExchange(ref state.Cursor, candidate, current) == current)
            {
                claimed = (uint)candidate;
                return true;
            }
            // Another caller claimed `candidate` (or something past it) first - retry the CAS
            // against this SAME state/block; do not fall through to leasing merely because of a
            // lost CAS race, only because the block itself is genuinely exhausted.
        }
    }
}
