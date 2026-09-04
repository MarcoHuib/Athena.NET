using System.Reflection;
using System.Text.Json;
using Athena.Net.World.Contracts;

namespace Athena.Net.World.Tests;

public sealed class WorldPartitionResolverTests
{
    // WorldPartitionResolver must be a purely generic ownership/glob resolver, with zero
    // hardcoded knowledge of concrete Ragnarok maps or deployment topology - that policy belongs
    // entirely to configuration (conf/world_partitions.json) and callers, never to this type.
    // Proven two ways: (1) structurally - the compiled type has no public factory/static topology
    // method at all (the old CreateDevelopment, which hardcoded "prontera"/"prt_fild*"/"world-rest",
    // is gone; the ONLY way to construct a resolver is via its plain constructor, which takes
    // definitions as data); (2) behaviorally - a topology built entirely from placeholder names the
    // resolver has never seen before resolves exactly like any other, proving nothing here special-
    // cases any particular map name.
    [Fact]
    public void Resolver_HasNoHardcodedTopologyFactory_AndResolvesArbitraryMapNamesFromDataAlone()
    {
        var resolverType = typeof(WorldPartitionResolver);
        var constructors = resolverType.GetConstructors(BindingFlags.Public | BindingFlags.Instance);
        var staticFactoryMethods = resolverType.GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(m => m.DeclaringType == resolverType)
            .ToArray();

        Assert.Single(constructors);
        Assert.Equal(["definitions", "servedMaps"], constructors[0].GetParameters().Select(p => p.Name));
        Assert.Empty(staticFactoryMethods); // No CreateDevelopment or equivalent baked-in topology.

        var resolver = new WorldPartitionResolver(
            [new("alpha-region", ["zzz-map-one", "zzz-map-two*"]), new("beta-region", ["*"], ["zzz-map-one", "zzz-map-two*"])],
            ["zzz-map-one", "zzz-map-two-a", "zzz-unrelated"]);
        Assert.Equal("alpha-region", resolver.ResolvePartition("zzz-map-one"));
        Assert.Equal("alpha-region", resolver.ResolvePartition("zzz-map-two-a"));
        Assert.Equal("beta-region", resolver.ResolvePartition("zzz-unrelated"));
    }

    // JSON-loaded topology, from an EXPLICIT path this test resolves itself (TestWorldPartitionsPath
    // - see its own doc comment: WorldPartitionTopologyLoader never searches for this file itself),
    // resolves Prontera/prt_fild according to that config file's own content - the topology
    // decision lives entirely in the JSON, never in WorldPartitionResolver.
    [Theory]
    [InlineData("prontera", "prontera-region")]
    [InlineData("prontera.gat", "prontera-region")]
    [InlineData("prt_fild08d", "prontera-region")]
    [InlineData("izlude", "world-rest")]
    public void JsonConfiguredTopology_ResolvesExpectedOwner(string mapId, string partitionId)
    {
        var resolver = WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["prontera", "prt_fild08d", "izlude"]);
        Assert.Equal(partitionId, resolver.ResolvePartition(mapId));
    }

    [Fact]
    public void JsonConfiguredTopology_ResolvesEveryCurrentlyServedMapExactlyOnce()
    {
        string[] served = ["int_land", "int_land01", "int_land02", "int_land03", "int_land04", "iz_int", "iz_int01", "iz_int02", "iz_int03", "iz_int04", "izlude_d", "prt_fild08d", "prontera"];
        var resolver = WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), served);
        Assert.All(served, map => Assert.False(string.IsNullOrWhiteSpace(resolver.ResolvePartition(map))));
    }

    // WorldPartitionTopologyLoader.Load works from ANY explicitly supplied path - a temp file here,
    // proving it has no dependency whatsoever on a source-repository layout or a solution file
    // being discoverable (the old FindRepositoryRoot()-based helper this replaced is gone).
    [Fact]
    public void Load_FromExplicitlySuppliedTemporaryPath_Succeeds_WithNoRepositoryLayoutDependency()
    {
        var path = WriteTempTopology(new { only_region = new { includeMaps = new[] { "*" } } });
        try
        {
            var resolver = WorldPartitionTopologyLoader.Load(path, ["anything", "whatever-map"]);
            Assert.Equal("only_region", resolver.ResolvePartition("anything"));
            Assert.Equal("only_region", resolver.ResolvePartition("whatever-map"));
        }
        finally { File.Delete(path); }
    }

    // A missing configured path must fail fast and loudly, never silently fall back to a
    // hardcoded/default topology.
    [Fact]
    public void Load_MissingConfiguredPath_ThrowsRatherThanFallingBackToADefaultTopology()
    {
        var missingPath = Path.Combine(Path.GetTempPath(), $"world_partitions_missing_{Guid.NewGuid():N}.json");
        Assert.Throws<FileNotFoundException>(() => WorldPartitionTopologyLoader.Load(missingPath, []));
    }

    // An invalid (unparseable) topology file must also fail fast rather than silently producing an
    // empty or partial resolver.
    [Fact]
    public void Load_InvalidJson_ThrowsRatherThanSilentlyProducingAPartialTopology()
    {
        var path = Path.Combine(Path.GetTempPath(), $"world_partitions_invalid_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, "{ this is not valid json");
        try { Assert.ThrowsAny<JsonException>(() => WorldPartitionTopologyLoader.Load(path, [])); }
        finally { File.Delete(path); }
    }

    // An empty (but syntactically valid, e.g. "{}") topology document must fail fast too - zero
    // partitions is never a legitimate topology.
    [Fact]
    public void Load_EmptyTopologyDocument_ThrowsRatherThanSucceedingWithZeroPartitions()
    {
        var path = WriteTempTopology(new { });
        try { Assert.Throws<InvalidOperationException>(() => WorldPartitionTopologyLoader.Load(path, [])); }
        finally { File.Delete(path); }
    }

    // Changing the topology configuration changes ownership WITHOUT modifying any resolver code -
    // proven by loading two DIFFERENT topology files (one matching production's grouping, one
    // deliberately different) through the exact same WorldPartitionTopologyLoader.Load call and
    // observing different ownership results purely from the file content.
    [Fact]
    public void ChangingTopologyConfiguration_ChangesOwnership_WithoutResolverCodeChanges()
    {
        var originalGrouping = WriteTempTopology(new
        {
            region_a = new { includeMaps = new[] { "prontera", "prt_fild*" } },
            region_b = new { includeMaps = new[] { "*" }, excludeMaps = new[] { "prontera", "prt_fild*" } },
        });
        var regroupedTopology = WriteTempTopology(new
        {
            region_a = new { includeMaps = new[] { "izlude*" } },
            region_b = new { includeMaps = new[] { "*" }, excludeMaps = new[] { "izlude*" } },
        });
        try
        {
            var beforeResolver = WorldPartitionTopologyLoader.Load(originalGrouping, ["prontera", "izlude_a"]);
            Assert.Equal("region_a", beforeResolver.ResolvePartition("prontera"));
            Assert.Equal("region_b", beforeResolver.ResolvePartition("izlude_a"));

            var afterResolver = WorldPartitionTopologyLoader.Load(regroupedTopology, ["prontera", "izlude_a"]);
            Assert.Equal("region_b", afterResolver.ResolvePartition("prontera"));
            Assert.Equal("region_a", afterResolver.ResolvePartition("izlude_a"));
        }
        finally
        {
            File.Delete(originalGrouping);
            File.Delete(regroupedTopology);
        }
    }

    [Fact]
    public void AmbiguousOwnership_FailsValidation() => Assert.Throws<InvalidOperationException>(() =>
        new WorldPartitionResolver([new("a", ["prontera"]), new("b", ["prontera"])], ["prontera"]));

    [Fact]
    public void MissingOwnership_FailsValidation() => Assert.Throws<InvalidOperationException>(() =>
        new WorldPartitionResolver([new("a", ["prontera"])], ["izlude"]));

    [Fact]
    public void ZeroPartitions_FailsValidation() => Assert.Throws<InvalidOperationException>(() =>
        new WorldPartitionResolver([], ["prontera"]));

    [Fact]
    public void DuplicatePartitionIds_FailValidation() => Assert.Throws<InvalidOperationException>(() =>
        new WorldPartitionResolver([new("a", ["prontera"]), new("a", ["izlude"])], []));

    // Topology configuration carries NO actor-ID concept at all (see WorldPartitionTopology.cs's
    // own doc comment, and ActorIdBlockAuthority.cs's own doc comment for where global actor-ID
    // uniqueness is actually guaranteed instead - a single leased-block Orleans grain, never a
    // config-declared numeric range tied to a specific partition). Proven structurally: the real
    // production conf/world_partitions.json parses successfully through the plain
    // WorldPartitionDefinition shape, which has no ActorIdRange field for a stray/legacy
    // "actorIdRange" key in the JSON to even bind to.
    [Fact]
    public void ProductionTopology_CarriesNoActorIdRangeConcept()
    {
        var resolver = WorldPartitionTopologyLoader.Load(TestWorldPartitionsPath.Resolve(), ["prontera", "prt_fild08d", "izlude"]);
        Assert.Equal("prontera-region", resolver.ResolvePartition("prontera"));
        var actorIdRangeField = typeof(WorldPartitionDefinition).GetProperty("ActorIdRange");
        Assert.Null(actorIdRangeField);
    }

    private static string WriteTempTopology(object document)
    {
        var path = Path.Combine(Path.GetTempPath(), $"world_partitions_test_{Guid.NewGuid():N}.json");
        File.WriteAllText(path, JsonSerializer.Serialize(document));
        return path;
    }
}
