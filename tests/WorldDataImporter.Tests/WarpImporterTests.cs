using System.Text.Json;

public sealed class WarpImporterTests
{
    [Fact]
    public void Import_ParsesStaticGeometryResolvesDuplicateAndClassifiesDynamic()
    {
        using var fixture = new ImportFixture(
            "// comment\n" +
            "map_a,10,20,0\twarp\t#ordinary\t1,1,map_b,30,40\n" +
            "map_c,5,6,0\twarp\t#zero\t0,0,map_c,7,8\n" +
            "map_d,9,10,0\tduplicate(#ordinary)\t#copy\tWARPNPC\n" +
            "map_e,11,12,0\tscript\t#dynamic\tWARPNPC,2,3,{\n" +
            "bad\twarp\t#bad\tinvalid\n");

        var result = WarpImporter.Import(new[] { fixture.Directory });

        Assert.Equal(3, result.Summary.StaticWarps);
        Assert.Equal(1, result.Summary.ResolvedDuplicates);
        Assert.Equal(1, result.Summary.DynamicWarps);
        Assert.Equal(1, result.Summary.Unsupported);
        var dynamicWarp = Assert.Single(result.DynamicWarps);
        Assert.Equal("#dynamic", dynamicWarp.Name);
        Assert.Equal("map_e", dynamicWarp.SourceMap);
        Assert.Equal(new WarpRadius(2, 3), dynamicWarp.Radius);
        var ordinary = Assert.Single(result.StaticWarps, warp => warp.Name == "#ordinary");
        Assert.Equal((ushort)1, ordinary.RadiusX);
        Assert.Equal((ushort)1, ordinary.RadiusY);
        Assert.Equal("map_b", ordinary.DestinationMap);
        var copy = Assert.Single(result.StaticWarps, warp => warp.Name == "#copy");
        Assert.Equal("map_d", copy.SourceMap);
        Assert.Equal("map_b", copy.DestinationMap);
    }

    [Fact]
    public void Import_IsDeterministicForSameInput()
    {
        using var fixture = new ImportFixture(
            "z_map,1,2,0\twarp\t#z\t2,3,a_map,4,5\n" +
            "a_map,6,7,0\twarp\t#a\t1,1,z_map,8,9\n");

        var first = JsonSerializer.Serialize(WarpImporter.Import(new[] { fixture.Directory }));
        var second = JsonSerializer.Serialize(WarpImporter.Import(new[] { fixture.Directory }));

        Assert.Equal(first, second);
    }

    private sealed class ImportFixture : IDisposable
    {
        public ImportFixture(string content)
        {
            Directory = Path.Combine(Path.GetTempPath(), $"athena-warp-import-{Guid.NewGuid():N}");
            System.IO.Directory.CreateDirectory(Directory);
            File.WriteAllText(Path.Combine(Directory, "warps.txt"), content);
        }

        public string Directory { get; }

        public void Dispose()
        {
            System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
