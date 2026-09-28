using Athena.Rathena.Data;
using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

// The canonical-map alias table is explicit importer-owned DATA (canonical-map-families.json), validated and
// emitted deterministically; the generic runtime resolver contains no map names. These tests pin the
// validation rules (no chains/duplicates/wildcards, identical geometry, real effective maps), determinism,
// and that the committed generated file is exactly what the generator produces from the real data.
public sealed class CanonicalMapFamiliesTests
{
    private static CanonicalMapFamilies.MapGeometry Geometry(byte fill = 1, int width = 2, int height = 2) => new(width, height, Enumerable.Repeat(fill, width * height).ToArray());

    private static Dictionary<string, CanonicalMapFamilies.MapGeometry> Maps(params string[] names) =>
        names.ToDictionary(name => name, _ => Geometry(), StringComparer.Ordinal);

    private static CanonicalMapFamily Family(string canonical, params string[] aliases) => new(canonical, aliases, "test evidence");

    [Fact]
    public void Validate_AcceptsAFlatExplicitFamily()
    {
        CanonicalMapFamilies.Validate([Family("a", "a1", "a2")], Maps("a", "a1", "a2"));
    }

    [Theory]
    [InlineData("a", "a")]                 // self alias
    [InlineData("a", "a*")]                // wildcard
    [InlineData("a", "a?")]
    [InlineData("a", "a.gat")]             // extension
    [InlineData("a", "sub/a1")]            // path
    [InlineData("a", "A1")]                // not lower-case
    [InlineData("a", " a1")]               // whitespace
    [InlineData("a", "")]
    public void Validate_RejectsNonLiteralOrSelfAliases(string canonical, string alias)
    {
        var maps = Maps("a", "a1", "sub/a1", "A1", "a*", "a?", "a.gat", " a1", "");
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([Family(canonical, alias)], maps));
    }

    [Fact]
    public void Validate_RejectsChainsDuplicatesAndAMapThatIsBothCanonicalAndAlias()
    {
        var maps = Maps("a", "a1", "b", "b1");
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([Family("a", "a1"), Family("b", "a1")], maps));      // alias listed twice
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([Family("a", "b"), Family("b", "b1")], maps));       // chain a <- b <- b1
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([Family("a", "a1"), Family("a", "a2")], Maps("a", "a1", "a2"))); // duplicate canonical
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([Family("a")], maps));                               // empty family
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([], maps));
    }

    [Fact]
    public void Validate_RejectsMapsThatAreNotEffective_AndGeometryMismatches()
    {
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([Family("a", "a1")], Maps("a")));   // alias missing from the map cache
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([Family("a", "a1")], Maps("a1")));  // canonical missing

        var differentCells = Maps("a"); differentCells["a1"] = Geometry(fill: 2);
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([Family("a", "a1")], differentCells));
        var differentSize = Maps("a"); differentSize["a1"] = Geometry(width: 4, height: 1);
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Validate([Family("a", "a1")], differentSize));
    }

    [Fact]
    public void SameGeometryAlone_NeverCreatesAnAlias_OnlyListedNamesEmit()
    {
        // b has geometry identical to a and a "_b"-style name, but is not listed: it must not appear.
        var source = CanonicalMapFamilies.Emit([Family("a", "a_a")], "abc");
        Assert.Contains("(\"a_a\", \"a\")", source);
        Assert.DoesNotContain("a_b", source);
    }

    [Fact]
    public void Emit_IsDeterministicAndIndependentOfInputOrder()
    {
        var one = CanonicalMapFamilies.Emit([Family("z", "z2", "z1"), Family("a", "a1")], "abc");
        var two = CanonicalMapFamilies.Emit([Family("a", "a1"), Family("z", "z1", "z2")], "abc");
        Assert.Equal(one, two);
        Assert.True(one.IndexOf("(\"a1\"", StringComparison.Ordinal) < one.IndexOf("(\"z1\"", StringComparison.Ordinal));
    }

    [Fact]
    public void Parse_RequiresCanonicalAliasesAndEvidence()
    {
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Parse("{}"));
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Parse("""{"families":[{"aliases":["x"],"evidence":"e"}]}"""));
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Parse("""{"families":[{"canonical":"a","evidence":"e"}]}"""));
        Assert.Throws<ArgumentException>(() => CanonicalMapFamilies.Parse("""{"families":[{"canonical":"a","aliases":["x"]}]}"""));
        var parsed = CanonicalMapFamilies.Parse("""{"families":[{"canonical":"a","aliases":["x"],"evidence":"e"}]}""");
        Assert.Equal("a", Assert.Single(parsed).Canonical);
    }

    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

    [Fact]
    public void RealData_IsExactlyTheTwentyFourProvenAliases_ValidAgainstThePinnedMapCache_AndTheCommittedGeneratedFileIsCurrent()
    {
        var root = RepositoryRoot();
        var families = CanonicalMapFamilies.Parse(File.ReadAllText(Path.Combine(root, "tools", "WorldDataImporter", "canonical-map-families.json")));

        Assert.Equal(6, families.Count);
        Assert.Equal(
            new[]
            {
                "int_land01", "int_land02", "int_land03", "int_land04", "iz_ac01_a", "iz_ac01_b", "iz_ac01_c", "iz_ac01_d", "iz_ac02_a", "iz_ac02_b", "iz_ac02_c", "iz_ac02_d",
                "iz_int01", "iz_int02", "iz_int03", "iz_int04", "izlude_a", "izlude_b", "izlude_c", "izlude_d", "prt_fild08a", "prt_fild08b", "prt_fild08c", "prt_fild08d",
            },
            families.SelectMany(family => family.Aliases).Order(StringComparer.Ordinal));

        var legacy = Path.Combine(root, "legacy", "rathena", "db");
        var maps = RathenaMapCacheLayers.Merge(
            File.ReadAllBytes(Path.Combine(legacy, "map_cache.dat")),
            File.Exists(Path.Combine(legacy, "re", "map_cache.dat")) ? File.ReadAllBytes(Path.Combine(legacy, "re", "map_cache.dat")) : null,
            File.Exists(Path.Combine(legacy, "import", "map_cache.dat")) ? File.ReadAllBytes(Path.Combine(legacy, "import", "map_cache.dat")) : null)
            .ToDictionary(item => item.Entry.Name, item => new CanonicalMapFamilies.MapGeometry(item.Entry.Width, item.Entry.Height, item.Entry.RawCells), StringComparer.Ordinal);
        CanonicalMapFamilies.Validate(families, maps);

        // The committed generated file must be byte-for-byte what the generator emits (no hand edits, no drift).
        var generated = File.ReadAllText(Path.Combine(root, "src", "Shared", "MapIdentity", "Generated", "GeneratedCanonicalMapAliases.cs"));
        Assert.Equal(CanonicalMapFamilies.Emit(families, "e985006171d2eb320ee512a653f4c83aea3d81b6"), generated);
    }
}
