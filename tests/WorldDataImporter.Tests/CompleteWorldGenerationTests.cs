using Athena.Rathena.Data;

public sealed class CompleteWorldGenerationTests
{
    [Fact]
    public void PinnedEffectiveMaps_AndGeneratedIdentitySetMatchExactly()
    {
        var root = FindRepositoryRoot();
        var rathena = Path.Combine(root, "legacy/rathena");
        var effective = RathenaMapCacheLayers.Merge(File.ReadAllBytes(Path.Combine(rathena, "db/map_cache.dat")), File.ReadAllBytes(Path.Combine(rathena, "db/re/map_cache.dat")));
        var generated = ReadGeneratedMapNames(Path.Combine(root, "src/MapServer/Generated/World"));
        Assert.Equal(1296, effective.Count);
        Assert.Equal(effective.Select(item => item.Entry.Name).Order(StringComparer.Ordinal), generated.Order(StringComparer.Ordinal));
    }

    [Fact]
    public void PinnedDeclarativeWarps_AndGeneratedSourceIdentitySetMatchExactly()
    {
        var root = FindRepositoryRoot();
        var rathena = Path.Combine(root, "legacy/rathena/npc");
        var analyzed = WorldEntityConverter.ConvertDeclarativeWarps([rathena]);
        Assert.Empty(analyzed.Unsupported);
        var generated = ReadGeneratedWarpIdentities(Path.Combine(root, "src/MapServer/Generated/World"));
        Assert.Equal(4468, analyzed.Entities.Count);
        Assert.Equal(analyzed.Entities.Select(item => $"{item.Source.File}:{item.Source.Line}").Order(StringComparer.Ordinal), generated.Order(StringComparer.Ordinal));
    }

    [Theory]
    [InlineData("gl_cas02", "GlCas02")]
    [InlineData("gl_cas02_", "GlCas02")]
    [InlineData("1@tower", "Map1Tower")]
    public void SharedMapNaming_IsStable(string map, string expectedBase) => Assert.Equal(expectedBase, Athena.WorldCompiler.Generation.MapModuleNaming.PascalCase(map));

    private static HashSet<string> ReadGeneratedMapNames(string root) => Directory.EnumerateFiles(root, "*Map.cs", SearchOption.AllDirectories)
        .Where(IsMapOwned).SelectMany(File.ReadLines).Select(line => System.Text.RegularExpressions.Regex.Match(line, "GeneratedMapDefinition \\w+ = new\\(\\\"(?<map>[^\\\"]+)\\\""))
        .Where(match => match.Success).Select(match => match.Groups["map"].Value).ToHashSet(StringComparer.Ordinal);

    private static HashSet<string> ReadGeneratedWarpIdentities(string root) => Directory.EnumerateFiles(root, "*Warps.cs", SearchOption.AllDirectories)
        .Where(IsWarpOwned).SelectMany(File.ReadLines).Select(line => System.Text.RegularExpressions.Regex.Match(line, """new\(.*new\("rAthena", "[^"]+", "(?<file>legacy/rathena/[^"]+)", (?<line>\d+)\)\),"""))
        .Where(match => match.Success).Select(match => $"{match.Groups["file"].Value}:{match.Groups["line"].Value}").ToHashSet(StringComparer.Ordinal);

    private static bool IsMapOwned(string path) => File.ReadLines(path).Take(3).Any(line => line.Contains("map generator", StringComparison.Ordinal));
    private static bool IsWarpOwned(string path) => File.ReadLines(path).Take(3).Any(line => line.Contains("warp generator", StringComparison.Ordinal));
    private static string FindRepositoryRoot()
    {
        var current = new DirectoryInfo(AppContext.BaseDirectory);
        while (current is not null && !File.Exists(Path.Combine(current.FullName, "Athena.NET.sln"))) current = current.Parent;
        return current?.FullName ?? throw new InvalidOperationException("Repository root not found.");
    }
}
