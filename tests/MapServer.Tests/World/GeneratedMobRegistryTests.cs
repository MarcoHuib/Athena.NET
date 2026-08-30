using System.Text.RegularExpressions;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.World;

namespace MapServer.Tests.World;

public sealed class GeneratedMobRegistryTests
{
    private static string RepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

    [Fact]
    public void Registry_IdSetExactlyEqualsPinnedMobDb()
    {
        var yaml = File.ReadAllText(Path.Combine(RepositoryRoot(), "legacy/rathena/db/re/mob_db.yml"));
        var pinnedIds = Regex.Matches(yaml, @"(?m)^  - Id: (?<id>\d+)\s*$")
            .Select(match => int.Parse(match.Groups["id"].Value))
            .ToArray();

        Assert.Equal(2675, pinnedIds.Length);
        Assert.Equal(pinnedIds.Length, pinnedIds.Distinct().Count());
        Assert.Equal(pinnedIds.Order(), GeneratedMobRegistry.Ids.Order());
        Assert.Equal(2675, GeneratedMobRegistry.Count);
    }

    [Theory]
    [InlineData(1002, "PORING")]
    [InlineData(1007, "FABRE")]
    [InlineData(1063, "LUNATIC")]
    [InlineData(22192, "SPIRIT_G_LAND_S")]
    [InlineData(22239, "SPIRIT_C_WIND_SL")]
    public void Registry_ResolvesKnownAndModernIds(int id, string aegisName)
    {
        Assert.True(GeneratedMobRegistry.TryGet(id, out var mob));
        Assert.Equal(id, mob.Id);
        Assert.Equal(aegisName, mob.AegisName);
    }

    [Fact]
    public void Registry_UnknownIdFailsCleanly()
    {
        Assert.False(GeneratedMobRegistry.TryGet(int.MaxValue, out var mob));
        Assert.Null(mob);
    }

    [Fact]
    public void TutorialAndLosslessFoundationFactsRemainUnchanged()
    {
        Assert.Equal(8, GeneratedMobs.Poring.Drops?.Count);
        Assert.Equal("Poring", GeneratedMobs.Poring.JapaneseName);
        Assert.True(GeneratedMobs.Poring.Mode.HasFlag(MobMode.Looter));
        Assert.True(GeneratedMobs.GPoring.Mode.HasFlag(MobMode.FixedItemDrop));

        var boss = GeneratedMobRegistry.All.First(mob => mob.Class == MobClass.Boss);
        Assert.True(boss.EffectiveMode.HasFlag(MobMode.Detector));
        Assert.True(boss.EffectiveMode.HasFlag(MobMode.StatusImmune));
        Assert.True(boss.EffectiveMode.HasFlag(MobMode.KnockBackImmune));
    }

    [Fact]
    public void EveryPinnedMobSpawnReferenceResolvesToGeneratedDefinition()
    {
        var npcRoot = Path.Combine(RepositoryRoot(), "legacy/rathena/npc");
        // Deliberately a NARROWER proxy grammar than the canonical MobDataCompiler.SpawnLine parser
        // (numeric MobId only, mandatory ",x,y") - this test is a standalone smoke check that every
        // discovered numeric MobId resolves through GeneratedMobRegistry, not a second copy of the
        // canonical spawn-line grammar; it intentionally excludes both boss_monster (a separate MVP
        // concern) and the bare-map-name/AegisName-token forms the canonical parser DOES cover (see
        // WorldDataImporter.Tests.MobSpawnGenerationTests for the authoritative 10,068-declaration
        // coverage, including those forms) - this regex's own mandatory ",x,y" requirement means its
        // count is UNCHANGED at 9,844 even after the canonical parser's bare-map-name fix, since none
        // of the 224 newly-recovered declarations use the ",x,y" form this proxy regex requires.
        var pattern = new Regex(@"^(?<map>[A-Za-z0-9_]+),(?<x>-?\d+),(?<y>-?\d+)(?:,(?<xs>\d+),(?<ys>\d+))?\t+monster\t+[^\t]+\t+(?<id>\d+),(?<count>\d+)(?:,(?<delay1>\d+))?(?:,(?<delay2>\d+))?", RegexOptions.Compiled);
        var unresolved = new List<string>();
        var discovered = 0;
        foreach (var path in Directory.EnumerateFiles(npcRoot, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        foreach (var (line, index) in File.ReadLines(path).Select((value, index) => (value, index)))
        {
            var match = pattern.Match(line);
            if (!match.Success) continue;
            discovered++;
            var id = int.Parse(match.Groups["id"].Value);
            if (!GeneratedMobRegistry.TryGet(id, out _))
                unresolved.Add($"{Path.GetRelativePath(RepositoryRoot(), path)}:{index + 1} -> {id}");
        }

        Assert.Equal(9844, discovered);
        Assert.True(unresolved.Count == 0, "Unresolved mob spawn references:\n" + string.Join('\n', unresolved));
    }
}
