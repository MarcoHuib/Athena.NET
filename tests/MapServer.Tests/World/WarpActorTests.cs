using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class WarpActorTests
{
    [Fact]
    public void ImportedStaticWarp_ProducesStableLogicalActor()
    {
        var actors = WorldMapRegistry.Tutorial
            .GetVisibleWarpActors("iz_int", 18, 26)
            .Where(actor => actor.Name == "#room_out")
            .ToArray();

        var actor = Assert.Single(actors);
        Assert.True(actor.ActorId >= 110_000_000);
        Assert.Equal((ushort)45, WarpActor.ClassId);
        Assert.Equal((ushort)45, actor.SpriteClass);
        Assert.Equal((byte)6, WarpActor.ObjectType);
        Assert.Equal((ushort)27, actor.X);
        Assert.Equal((ushort)30, actor.Y);
        Assert.Equal((byte)1, actor.RadiusX);
        Assert.Equal((byte)1, actor.RadiusY);
    }

    [Fact]
    public void ActorAllocator_IsThreadSafeAndCollisionFree()
    {
        var allocator = new WorldActorIdAllocator();
        var ids = new uint[1_000];

        Parallel.For(0, ids.Length, index => ids[index] = allocator.Allocate());

        Assert.Equal(ids.Length, ids.Distinct().Count());
        Assert.All(ids, id => Assert.True(id >= 110_000_000));
    }

}
