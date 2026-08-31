using Athena.Net.World.Contracts;

namespace Athena.Net.World.Tests;

public sealed class WorldPartitionResolverTests
{
    [Theory]
    [InlineData("prontera", "prontera-region")]
    [InlineData("prontera.gat", "prontera-region")]
    [InlineData("prt_fild08d", "prontera-region")]
    [InlineData("izlude", "world-rest")]
    public void DevelopmentTopology_ResolvesExpectedOwner(string mapId, string partitionId)
    {
        var resolver = WorldPartitionResolver.CreateDevelopment(["prontera", "prt_fild08d", "izlude"]);
        Assert.Equal(partitionId, resolver.ResolvePartition(mapId));
    }

    [Fact]
    public void DevelopmentTopology_ResolvesEveryCurrentlyServedMapExactlyOnce()
    {
        string[] served = ["int_land", "int_land01", "int_land02", "int_land03", "int_land04", "iz_int", "iz_int01", "iz_int02", "iz_int03", "iz_int04", "izlude_d", "prt_fild08d", "prontera"];
        var resolver = WorldPartitionResolver.CreateDevelopment(served);
        Assert.All(served, map => Assert.False(string.IsNullOrWhiteSpace(resolver.ResolvePartition(map))));
    }

    [Fact]
    public void AmbiguousOwnership_FailsValidation() => Assert.Throws<InvalidOperationException>(() =>
        new WorldPartitionResolver([new("a", ["prontera"]), new("b", ["prontera"])], ["prontera"]));

    [Fact]
    public void MissingOwnership_FailsValidation() => Assert.Throws<InvalidOperationException>(() =>
        new WorldPartitionResolver([new("a", ["prontera"])], ["izlude"]));

    [Fact]
    public void DevelopmentActorRanges_DoNotOverlap()
    {
        WorldPartitionActorRanges.Validate(WorldPartitionActorRanges.Development);
        var prontera = new PartitionWorldActorIdAllocator(WorldPartitionActorRanges.Development[0]);
        var rest = new PartitionWorldActorIdAllocator(WorldPartitionActorRanges.Development[1]);
        Assert.NotEqual(prontera.Allocate(), rest.Allocate());
    }

    [Fact]
    public void OverlappingActorRanges_FailValidation() => Assert.Throws<InvalidOperationException>(() =>
        WorldPartitionActorRanges.Validate([new("a", 110_000_000, 120_000_000), new("b", 119_000_000, 129_000_000)]));
}
