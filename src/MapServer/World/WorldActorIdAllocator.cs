namespace Athena.Net.MapServer.World;

public sealed class WorldActorIdAllocator
{
    // rAthena's NPC domain starts at 110,000,000 (npc.hpp:START_NPC_NUM). One
    // allocator instance is shared across every actor kind in a composed
    // world (NPCs, warps, monsters) - see MapServerWorld.Build() - so there
    // is exactly one ID namespace, matching rAthena's own single NPC/monster
    // domain rather than giving each content kind its own disjoint range.
    private long _lastId = 109_999_999;

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
