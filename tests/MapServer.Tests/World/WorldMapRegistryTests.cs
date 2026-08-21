using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class WorldMapRegistryTests
{
    [Theory]
    [InlineData("iz_int01")]
    [InlineData("iz_int02")]
    [InlineData("iz_int03")]
    [InlineData("iz_int04")]
    public void Tutorial_ForwardDoorMatchesAllRealInstanceVariants(string mapName)
    {
        var found = WorldMapRegistry.Tutorial.TryFindWarp(mapName, 26, 30, out var warp);

        Assert.True(found);
        Assert.Equal(mapName, warp.DestinationMap);
        Assert.Equal((ushort)51, warp.DestinationX);
        Assert.Equal((ushort)30, warp.DestinationY);
    }

    [Fact]
    public void Tutorial_MatchesInclusiveRectangularArea()
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", 26, 29, out _));
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", 28, 31, out _));
    }

    [Theory]
    [InlineData(26, 30)]
    [InlineData(28, 30)]
    [InlineData(27, 29)]
    [InlineData(27, 31)]
    public void Tutorial_MatchesEveryBoundary(ushort x, ushort y)
    {
        Assert.True(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", x, y, out _));
    }

    [Theory]
    [InlineData(25, 30)]
    [InlineData(29, 30)]
    [InlineData(27, 28)]
    [InlineData(27, 32)]
    public void Tutorial_RejectsOneTileOutsideEveryBoundary(ushort x, ushort y)
    {
        Assert.False(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", x, y, out _));
    }

    [Fact]
    public void Tutorial_TileOutsideAreaDoesNotMatch()
    {
        Assert.False(WorldMapRegistry.Tutorial.TryFindWarp("iz_int01", 25, 30, out _));
    }

    [Fact]
    public void Tutorial_SameCoordinateOnAnotherMapDoesNotMatch()
    {
        Assert.False(WorldMapRegistry.Tutorial.TryFindWarp("prontera", 26, 30, out _));
    }

    [Fact]
    public void Tutorial_ExactTargetOnWarpIntersects()
    {
        var found = WorldMapRegistry.Tutorial.TryFindFirstWarpAlongRoute(
            "iz_int01", 29, 30, 28, 30, out var intersection);

        Assert.True(found);
        Assert.Equal((ushort)28, intersection.X);
        Assert.Equal((ushort)30, intersection.Y);
    }

    [Fact]
    public void Tutorial_TargetBeyondWarpStillIntersectsFirstWarpCell()
    {
        var found = WorldMapRegistry.Tutorial.TryFindFirstWarpAlongRoute(
            "iz_int01", 22, 31, 29, 29, out var intersection);

        Assert.True(found);
        Assert.Equal((ushort)26, intersection.X);
        Assert.Equal((ushort)30, intersection.Y);
    }

    [Fact]
    public void Tutorial_RouteBesideWarpDoesNotIntersect()
    {
        Assert.False(WorldMapRegistry.Tutorial.TryFindFirstWarpAlongRoute(
            "iz_int01", 22, 28, 32, 28, out _));
    }

    [Fact]
    public void Route_SelectsFirstIntersectedWarpRatherThanRegistryOrder()
    {
        var later = new WarpDefinition("later", "test", 8, 5, 0, 0, "later", 1, 1, true, "test", 1);
        var earlier = new WarpDefinition("earlier", "test", 3, 5, 0, 0, "earlier", 1, 1, true, "test", 2);
        var registry = new WorldMapRegistry(new[] { later, earlier });

        var found = registry.TryFindFirstWarpAlongRoute("test", 0, 5, 10, 5, out var intersection);

        Assert.True(found);
        Assert.Same(earlier, intersection.Warp);
        Assert.Equal((ushort)3, intersection.X);
    }
}
