using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using System.Text.Json;

// Priority 8/18 (ai/world-data.md): cross-domain dependencies.json coverage (NPC + mob-spawn +
// quest + shop + item dependencies all folding into ONE deterministic global dependency graph,
// not just the NPC/warp scan's own Dependencies) plus a small multi-domain determinism check.
// Separate file from RepositoryCompatibilityAnalyzerTests.cs/RepositoryDomainAnalyzersTests.cs to
// avoid colliding with concurrent edits to those files.
public sealed class RepositoryCompatibilityAnalyzerDomainTests
{
    private sealed class RepoFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "athena-repo-domain-" + Guid.NewGuid().ToString("N"));
        public string Output => Path.Combine(Root, "reports");
        public RepoFixture() => Directory.CreateDirectory(Root);
        public void Write(string relativePath, string text)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text.Replace("\r\n", "\n"));
        }
        public void WriteBytes(string relativePath, byte[] bytes)
        {
            var path = Path.Combine(Root, relativePath);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, bytes);
        }
        public void Dispose() => Directory.Delete(Root, true);
    }

    private const string ItemDbHeader = "Header:\n  Type: ITEM_DB\n  Version: 1\nBody:\n";

    // A minimal single-map (1x1, one zero-valued raw GAT cell byte) db/map_cache.dat -
    // RathenaMapCacheFormat.ReadAll requires the decompressed payload to be exactly width*height
    // bytes. Without a map_cache.dat present, AnalyzeMaps returns no maps at all, which starves
    // AnalyzeMobSpawns of any resolvable map and silently drops every mob-spawn dependency - this
    // fixture must include one for the mob-spawn -> map dependency assertions below to mean anything.
    private static byte[] BuildMinimalMapCache(string mapName)
    {
        using var compressedStream = new MemoryStream();
        using (var zlib = new ZLibStream(compressedStream, CompressionMode.Compress, leaveOpen: true)) zlib.Write([0], 0, 1);
        var compressed = compressedStream.ToArray();
        var record = new byte[12 + 2 + 2 + 4 + compressed.Length];
        Encoding.ASCII.GetBytes(mapName).CopyTo(record, 0);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(12, 2), 1);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16, 4), compressed.Length);
        compressed.CopyTo(record, 20);
        var buffer = new byte[8 + record.Length];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)buffer.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), 1);
        record.CopyTo(buffer, 8);
        return buffer;
    }

    private static void WriteMultiDomainFixture(RepoFixture fixture)
    {
        // NPC event with a real cross-domain dependency (setquest).
        fixture.Write("npc/re/cities/sample.txt", """
            prontera,10,10,4	script	QuestGiver	100,{
            mes "hello";
            setquest 21008;
            close;
            }
            prontera,30,30,0	shop	Test Shop	100,909:-1
            prontera,0,0	monster	Poring	1002,5,5000
            """);
        fixture.WriteBytes("db/map_cache.dat", BuildMinimalMapCache("prontera"));
        fixture.Write("db/re/mob_db.yml", $$"""
            Header:
              Type: MOB_DB
              Version: 1
            Body:
              - Id: 1002
                AegisName: PORING
                Name: Poring
                Level: 1
                Hp: 50
                Attack: 7
                Attack2: 12
                Defense: 0
                MagicDefense: 0
                Str: 1
                Agi: 3
                Vit: 2
                Int: 2
                Dex: 3
                Luk: 3
                AttackRange: 1
                WalkSpeed: 200
                AttackDelay: 1000
                AttackMotion: 1000
                DamageMotion: 480
                BaseExp: 2
                JobExp: 1
                Ai: 01_DE_TWFOLLOW
            """);
        fixture.Write("db/re/item_db_etc.yml", ItemDbHeader + """
              - Id: 909
                AegisName: Jellopy
                Name: Jellopy
                Type: Etc
            """);
        fixture.Write("db/re/quest_db.yml", """
            Header:
              Type: QUEST_DB
              Version: 1
            Body:
              - Id: 21008
                Title: The first battle
                Drops:
                  - Mob: PORING
                    Item: Jellopy
                    Rate: 10000
            """);
    }

    [Fact]
    public async Task DependenciesJson_IncludesNpcAndMobSpawnAndQuestAndShopAndItemDependencies()
    {
        using var fixture = new RepoFixture();
        WriteMultiDomainFixture(fixture);

        var options = new AnalysisOptions(fixture.Root, fixture.Output);
        var result = RepositoryCompatibilityAnalyzer.Analyze(options);
        await RepositoryCompatibilityAnalyzer.WriteAsync(options, result);

        // NPC event dependency (setquest 21008).
        Assert.Contains(result.Dependencies, item => item.Dependencies.Contains("quest:21008"));
        // mob-spawn -> map/mob dependency.
        Assert.Contains(result.Dependencies, item => item.Entity.StartsWith("mob-spawn:", StringComparison.Ordinal)
            && item.Dependencies.Contains("map:prontera") && item.Dependencies.Contains("mob:1002"));
        // quest -> mob/item dependency (from the DropRule component).
        Assert.Contains(result.Dependencies, item => item.Entity == "quest:21008"
            && item.Dependencies.Contains("mob:1002") && item.Dependencies.Contains("item:909"));
        // shop -> item dependency.
        Assert.Contains(result.Dependencies, item => item.Entity.StartsWith("shop:", StringComparison.Ordinal) && item.Dependencies.Contains("item:909"));

        var json = await File.ReadAllTextAsync(Path.Combine(fixture.Output, "dependencies.json"));
        using var document = JsonDocument.Parse(json);
        Assert.True(document.RootElement.GetArrayLength() > 0);

        // The combined array itself must be deterministically ordered by Entity id.
        var ids = result.Dependencies.Select(item => item.Entity).ToArray();
        Assert.Equal(ids.Order(StringComparer.Ordinal).ToArray(), ids);
    }

    [Fact]
    public void MultiDomainFixture_AnalyzedTwice_ProducesByteIdenticalSerializedOutput()
    {
        using var fixture = new RepoFixture();
        WriteMultiDomainFixture(fixture);
        var options = new AnalysisOptions(fixture.Root, fixture.Output);

        var first = RepositoryCompatibilityAnalyzer.Analyze(options);
        var second = RepositoryCompatibilityAnalyzer.Analyze(options);

        Assert.Equal(DeterministicJson.Serialize(first), DeterministicJson.Serialize(second));
        Assert.Equal(DeterministicJson.Serialize(first.DomainEntities), DeterministicJson.Serialize(second.DomainEntities));
    }
}
