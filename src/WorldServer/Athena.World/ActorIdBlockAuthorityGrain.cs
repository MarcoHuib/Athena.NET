using Athena.Net.World.Contracts;
using Athena.Net.World.Telemetry;
using Orleans;

namespace Athena.Net.World;

// The single grain instance backing IActorIdBlockAuthorityGrain (always addressed at
// ActorIdBlockAuthorityGrainKey.WellKnownKey - see that constant's own doc comment). Orleans'
// single-threaded per-grain-activation turn execution model is what makes LeaseBlockAsync race-free
// with zero explicit locking here: at most one call to this method ever executes at a time for this
// activation, the same property WorldPartitionGrain itself already relies on for its own state, so
// two concurrent leases (from two different partitions, or a partition and MapServer's NPC/warp
// allocator) can never observe/advance _nextBlockStart concurrently and therefore can never receive
// overlapping blocks.
public sealed class ActorIdBlockAuthorityGrain : Grain, IActorIdBlockAuthorityGrain
{
    // rAthena's NPC/monster/warp actor-ID domain base (npc.hpp:START_NPC_NUM).
    private const uint DomainStart = 110_000_000;

    private uint _nextBlockStart = DomainStart;

    public Task<ActorIdBlock> LeaseBlockAsync(string requesterId, uint blockSize)
    {
        if (blockSize == 0) throw new ArgumentOutOfRangeException(nameof(blockSize), "A leased actor-ID block must have a positive size.");

        var start = _nextBlockStart;
        // Only the theoretical case of the domain itself overflowing uint.MaxValue is a hard
        // failure here - matching the existing WorldActorIdAllocator.Allocate's own overflow-check
        // convention. At realistic monster/NPC/warp counts this is never reached.
        if ((ulong)start + blockSize > uint.MaxValue + 1UL)
            throw new InvalidOperationException("The global actor-ID domain is exhausted.");

        var end = start + blockSize;
        _nextBlockStart = end;

        WorldTelemetry.ActorIdBlockLeases.Add(1, new KeyValuePair<string, object?>("world.actorid.requester", requesterId));
        return Task.FromResult(new ActorIdBlock(start, end));
    }
}
