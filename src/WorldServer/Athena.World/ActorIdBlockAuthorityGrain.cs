using Athena.Net.World.Contracts;
using Athena.Net.World.Telemetry;
using Orleans;
using Orleans.Runtime;

namespace Athena.Net.World;

// Persisted cursor state for ActorIdBlockAuthorityGrain. Deliberately just the one field - see the
// grain's own doc comment for why this needs to be persisted (via Orleans memory grain storage, not
// left as a bare in-memory field) at all.
[GenerateSerializer]
public sealed class ActorIdBlockAuthorityState
{
    // rAthena's NPC/monster/warp actor-ID domain base (npc.hpp:START_NPC_NUM). Starts here on the
    // grain's very first activation ever (a brand-new/never-before-persisted state); every
    // subsequent lease advances it monotonically, and that advance is what memory grain storage
    // preserves across activation deactivation/reactivation within one running silo process.
    [Id(0)]
    public uint NextBlockStart { get; set; } = 110_000_000;

    // Set once a lease has consumed the domain's true final ID (uint.MaxValue) - see
    // LeaseBlockAsync's own doc comment for why this is a separate persisted flag rather than an
    // out-of-uint-range sentinel value stored in NextBlockStart itself.
    [Id(1)]
    public bool Exhausted { get; set; }
}

// The single grain instance backing IActorIdBlockAuthorityGrain (always addressed at
// ActorIdBlockAuthorityGrainKey.WellKnownKey - see that constant's own doc comment). Orleans'
// single-threaded per-grain-activation turn execution model is what makes LeaseBlockAsync race-free
// with zero explicit locking here: at most one call to this method ever executes at a time for this
// activation, the same property WorldPartitionGrain itself already relies on for its own state, so
// two concurrent leases (from two different partitions, or a partition and MapServer's NPC/warp
// allocator) can never observe/advance the cursor concurrently and therefore can never receive
// overlapping blocks - AS LONG AS there is only ever one live activation's worth of cursor state in
// play, which is exactly the property persistence below exists to guarantee.
//
// GRAIN-ACTIVATION LIFETIME (why this state is persisted, not a bare field): an Orleans grain
// activation for a given key can be deactivated (idle-collection, silo rebalancing, an explicit
// DeactivateOnIdle call elsewhere, etc.) and later reactivated - within the SAME running silo
// process - the next time a call resolves to this grain's key. A bare in-memory field would reset
// to the domain base (110,000,000) on every such reactivation, and the grain would then start
// re-issuing blocks it had already leased out earlier in the SAME process's lifetime - silently
// violating the entire uniqueness guarantee this type exists to provide, even though nothing about
// the silo PROCESS itself ever restarted.
//
// Fixed by persisting the cursor via Orleans' memory grain-storage provider
// (AddMemoryGrainStorage in Program.cs, IPersistentState<ActorIdBlockAuthorityState> here):
// - Ordinary activation deactivation/reactivation within one running silo process: SAFE. Memory
//   grain storage lives in the silo process's own memory, independent of any single grain
//   activation's lifetime - a reactivation reads the SAME persisted cursor a prior activation last
//   wrote, so leased blocks are never reissued merely because Orleans recycled the activation.
// - A COMPLETE silo/process restart: NOT SAFE, and this is a real, explicitly disclosed limitation
//   of this PR, not an oversight - memory grain storage is, by design, only backed by process
//   memory. A full restart of the Athena.World process resets NextBlockStart back to 110,000,000,
//   and any actor IDs a still-running MapServer process already leased/allocated before the
//   restart could theoretically be reissued to a newly-leased block after it. This PR does not
//   claim restart/HA safety for actor-ID allocation - a future PR wanting that would swap the
//   storage provider for a real durable one (e.g. a database-backed IGrainStorage implementation)
//   without changing this grain's own logic, IActorIdBlockAuthorityGrain's contract, or
//   LeasedBlockActorIdAllocator at all; this is exactly the seam AddMemoryGrainStorage's own
//   replaceability is meant to provide.
// [PersistentState(stateName, storageName)] - `stateName` ("cursor") is just this state's own key
// within the provider; `storageName` ("actorIdBlockAuthority") MUST match the name
// AddMemoryGrainStorage("actorIdBlockAuthority") is registered under (Program.cs) - omitting the
// second argument makes Orleans look for a "Default" storage provider instead, which this silo
// deliberately does not register (see ActorIdBlockAuthorityGrainTests.StorageConfigurator for the
// equivalent test-side registration, which must use the identical name).
public sealed class ActorIdBlockAuthorityGrain([PersistentState("cursor", "actorIdBlockAuthority")] IPersistentState<ActorIdBlockAuthorityState> state)
    : Grain, IActorIdBlockAuthorityGrain
{
    public async Task<ActorIdBlock> LeaseBlockAsync(string requesterId, uint blockSize)
    {
        if (blockSize == 0) throw new ArgumentOutOfRangeException(nameof(blockSize), "A leased actor-ID block must have a positive size.");

        // `_exhausted` is a persisted sentinel distinct from NextBlockStart's own uint range: once
        // a lease consumes the domain's true final ID (uint.MaxValue), there is no valid uint value
        // left to store as "the next start" (uint.MaxValue + 1 does not fit in uint) - rather than
        // clamp NextBlockStart to a value that would then be silently reissued, exhaustion is
        // tracked explicitly and checked first, independent of any arithmetic on NextBlockStart.
        if (state.State.Exhausted)
            throw new InvalidOperationException("The global actor-ID domain is exhausted.");

        var start = state.State.NextBlockStart;
        // Computed in ulong specifically so a block whose last real ID is uint.MaxValue itself
        // (start + blockSize == 4294967296) is representable as an exclusive upper bound without
        // wrapping - see ActorIdBlock.EndExclusive's own doc comment for why that field is ulong,
        // not uint.
        var end = (ulong)start + blockSize;
        if (end > (ulong)uint.MaxValue + 1UL)
            throw new InvalidOperationException("The global actor-ID domain is exhausted.");

        if (end > uint.MaxValue) state.State.Exhausted = true; // This lease reached the domain's true end; nothing left to start a further block from.
        else state.State.NextBlockStart = (uint)end;

        await state.WriteStateAsync();

        WorldTelemetry.ActorIdBlockLeases.Add(1, new KeyValuePair<string, object?>("world.actorid.requester", requesterId));
        return new ActorIdBlock(start, end);
    }
}
