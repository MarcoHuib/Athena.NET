using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class MovementPathProviderTests
{
    [Fact]
    public void UnverifiedGridLineProvider_IncludesStartAndDestinationInclusive()
    {
        var provider = new UnverifiedGridLineMovementPathProvider();
        var path = provider.ComputePath("iz_int01", 0, 0, 3, 0);

        Assert.Equal((ushort)0, path[0].X);
        Assert.Equal((ushort)3, path[^1].X);
    }

    [Fact]
    public void UnverifiedGridLineProvider_SameStartAndDestination_SingleCellPath()
    {
        var provider = new UnverifiedGridLineMovementPathProvider();
        var path = provider.ComputePath("iz_int01", 5, 5, 5, 5);

        Assert.Single(path);
        Assert.Equal(((ushort)5, (ushort)5), path[0]);
    }
}
