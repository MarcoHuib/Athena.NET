using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class WarpActorTests
{
    [Fact]
    public void ImportedStaticWarp_ProducesStableLogicalActor()
    {
        var actors = WorldMapRegistry.Tutorial
            .GetVisibleWarpActors("iz_int03", 18, 26)
            .Where(actor => actor.Name == "#room_out03")
            .ToArray();

        var actor = Assert.Single(actors);
        Assert.True(actor.ActorId >= 110_000_000);
        Assert.Equal((ushort)45, WarpActor.ClassId);
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

    [Fact]
    public void DynamicScriptedWarp_CanProvideVisualWithoutStaticDestination()
    {
        var actor = Assert.Single(
            WorldMapRegistry.Tutorial.GetVisibleWarpActors("iz_int03", 58, 28),
            candidate => candidate.Name == "#ship_out03");

        Assert.Equal((ushort)56, actor.X);
        Assert.Equal((ushort)15, actor.Y);
        Assert.Equal((ushort)45, WarpActor.ClassId);
    }
}
