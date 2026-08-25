public sealed class WarpTriggerConversionTests
{
    [Fact]
    public void ShipOutDuplicates_GroupIntoOneDefinitionAndFivePlacements()
    {
        var repository = FindRepositoryRoot();
        var roots = new[] { Path.Combine(repository, "legacy/rathena/npc/re/warps/cities") };
        var filter = new ConversionFilter("izlude.txt", null, "#ship_out", "warp");

        var result = WorldEntityConverter.ConvertWarpTriggers(roots, filter);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal("#ship_out", definition.TemplateNpcName);
        Assert.Contains("Warp", definition.OnTouch.RequiredCapabilities);
        Assert.Contains("SavePoint", definition.OnTouch.RequiredCapabilities);
        Assert.Equal(5, result.Placements.Count);
        Assert.All(result.Placements, placement => Assert.Equal(definition.DefinitionId, placement.DefinitionId));

        var byMap = result.Placements.ToDictionary(p => p.Map);
        foreach (var map in new[] { "iz_int", "iz_int01", "iz_int02", "iz_int03", "iz_int04" })
        {
            Assert.True(byMap.ContainsKey(map));
            Assert.Equal((ushort)56, byMap[map].X);
            Assert.Equal((ushort)15, byMap[map].Y);
            Assert.Equal((ushort)1, byMap[map].RadiusX);
            Assert.Equal((ushort)1, byMap[map].RadiusY);
        }
    }

    [Fact]
    public void IntroToIzludeDuplicates_GroupIntoOneDefinitionAndFivePlacements()
    {
        var repository = FindRepositoryRoot();
        var roots = new[] { Path.Combine(repository, "legacy/rathena/npc/re/warps/cities") };
        var filter = new ConversionFilter("izlude.txt", null, "#intro_to_izlude", "warp");

        var result = WorldEntityConverter.ConvertWarpTriggers(roots, filter);

        var definition = Assert.Single(result.Definitions);
        Assert.Equal(5, result.Placements.Count);
        var maps = result.Placements.Select(p => p.Map).OrderBy(m => m, StringComparer.Ordinal).ToArray();
        Assert.Equal(["int_land", "int_land01", "int_land02", "int_land03", "int_land04"], maps);
        Assert.All(result.Placements, placement => Assert.Equal((ushort)2, placement.RadiusX));
    }

    [Fact]
    public void ConvertWarpTriggers_IsDeterministic()
    {
        var repository = FindRepositoryRoot();
        var roots = new[] { Path.Combine(repository, "legacy/rathena/npc/re/warps/cities") };
        var filter = new ConversionFilter("izlude.txt", null, "#ship_out", "warp");

        var first = WorldEntityConverter.ConvertWarpTriggers(roots, filter);
        var second = WorldEntityConverter.ConvertWarpTriggers(roots, filter);

        Assert.Equal(first.Definitions.Select(d => d.DefinitionId), second.Definitions.Select(d => d.DefinitionId));
        Assert.Equal(first.Placements.Select(p => p.PlacementId), second.Placements.Select(p => p.PlacementId));
    }

    [Fact]
    public void DeclarativeRoomWarps_AreNotFoundByConvertWarpTriggers()
    {
        var repository = FindRepositoryRoot();
        var roots = new[] { Path.Combine(repository, "legacy/rathena/npc/re/warps/cities") };
        var filter = new ConversionFilter("izlude.txt", null, "#room_out", "warp");

        var result = WorldEntityConverter.ConvertWarpTriggers(roots, filter);

        Assert.Empty(result.Definitions);
        Assert.Empty(result.Placements);
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }
}
