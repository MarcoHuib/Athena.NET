using Athena.WorldCompiler.Generation;

namespace WorldDataImporter.Tests;

// Shared expensive fixture (task tests re-scan/re-analyze the ENTIRE real pinned
// legacy/rathena/npc tree, ~10K declarations across ~200 files, plus a full map_cache.dat merge) -
// computed exactly ONCE for the whole test class via a lazily-initialized static (xUnit creates a
// new test-class instance per [Fact], so IClassFixture<T> would be the usual sharing mechanism, but
// T's public surface can't expose the internal MobSpawnData type from a public test class - a
// static cache sidesteps that accessibility conflict while keeping the same "computed once" intent)
// rather than once per [Fact], which would otherwise multiply an already-heavy real-tree scan by
// the number of assertions.
internal sealed class MobSpawnGenerationFixture
{
    public string Root { get; }
    public IReadOnlyList<MobDataCompiler.MobSpawnData> Scanned { get; }
    public IReadOnlySet<string> AnalyzerMobSpawnIds { get; }
    public IReadOnlySet<string> ValidMaps { get; }
    public IReadOnlySet<int> PinnedMobIds { get; }

    public MobSpawnGenerationFixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
        Root = Path.Combine(repositoryRoot, "legacy/rathena");

        var npcRoot = Path.Combine(Root, "npc");
        var scanned = new List<MobDataCompiler.MobSpawnData>();
        foreach (var path in Directory.EnumerateFiles(npcRoot, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = CanonicalSourceFile(path);
            scanned.AddRange(MobDataCompiler.ReadAllMobSpawns(File.ReadAllText(path), relative));
        }
        Scanned = scanned;

        var analyzed = RepositoryDomainAnalyzers.Analyze(Root, new HashSet<string> { "mob-spawns", "maps" });
        AnalyzerMobSpawnIds = analyzed.Where(entity => entity.Domain == "mob-spawns").Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal);
        ValidMaps = analyzed.Where(entity => entity.Domain == "maps").Select(entity => entity.Name).ToHashSet(StringComparer.Ordinal);

        var mobDbYaml = File.ReadAllText(Path.Combine(Root, "db/re/mob_db.yml"));
        PinnedMobIds = MobDataCompiler.ReadAllMobDefinitions(mobDbYaml).Select(mob => mob.Id).ToHashSet();
    }

    private static string CanonicalSourceFile(string path)
    {
        var normalized = path.Replace('\\', '/');
        var legacy = normalized.IndexOf("legacy/rathena/", StringComparison.Ordinal);
        return legacy >= 0 ? normalized[legacy..] : normalized;
    }
}

// Hard regression coverage for the generate-mob-spawns pipeline (ai/world-data.md's "Generated
// mob spawns" section, task: "generate all pinned rAthena mob spawns into Athena.NET production
// world data"). Runs against the REAL pinned legacy/rathena tree, not a synthetic fixture -
// deliberately heavier than most WorldDataImporter.Tests, matching the existing precedent
// (CompilerTests.RealAcademyWorld_GenerationIsDeterministicAndMatchesCompiledAcademyTree) for
// proving generated production data against the genuine source of truth rather than a stand-in.
//
// This project cannot reference the already-generated MapServer assembly (WorldDataImporter.csproj
// deliberately compiles in only two isolated map-cache files - see its own doc comment), so these
// tests re-derive the same scan/parse the generate-mob-spawns CLI command performs
// (MobDataCompiler.ReadAllMobSpawns over every pinned npc/**/*.txt file) rather than reading the
// compiled GeneratedMobSpawnRegistry - the compiled-registry-shaped coverage
// (GetForMap/TryGetMap/Count/DeathEvent round-trip) lives in MapServer.Tests instead, where a real
// project reference to the generated output already exists.
public sealed class MobSpawnGenerationTests
{
    private static readonly Lazy<MobSpawnGenerationFixture> LazyFixture = new(() => new MobSpawnGenerationFixture());
    private static MobSpawnGenerationFixture fixture => LazyFixture.Value;

    // ReadAllMobSpawns (the generate-mob-spawns CLI's own scan) must find EXACTLY the same 9,844
    // identities as RepositoryDomainAnalyzers.AnalyzeMobSpawns (task section 22/41: "compare stable
    // identities, not just counts") - both derive from the SAME ReadMobSpawns/SpawnLine parser
    // (task section 23's "strong preference: one shared parser"), so this proves the all-declarations
    // scan path and the name-filtered analyzer path never silently diverge.
    [Fact]
    public void ReadAllMobSpawns_IdentitySet_ExactlyMatchesAnalyzerMobSpawnDomain()
    {
        // Analyzer ids are ROOT-relative (RepositoryDomainAnalyzers.Relative -> "npc/..."), while
        // generated WorldSourceInfo.File is repo-canonical ("legacy/rathena/npc/..." -
        // CanonicalSourceFile, matching every other generated-source-provenance field in this
        // project). Both identify the exact same declaration; strip the shared "legacy/rathena/"
        // prefix here so the comparison is apples-to-apples rather than a spurious path-convention
        // mismatch.
        var scannedIds = fixture.Scanned.Select(spawn => $"mob-spawn:{spawn.SourceFile.Replace("legacy/rathena/", string.Empty, StringComparison.Ordinal)}:{spawn.SourceLine}").ToHashSet(StringComparer.Ordinal);

        Assert.Equal(fixture.AnalyzerMobSpawnIds.Count, scannedIds.Count);
        Assert.Equal(fixture.AnalyzerMobSpawnIds, scannedIds);
    }

    [Fact]
    public void ReadAllMobSpawns_FindsExactly9844Declarations() => Assert.Equal(9844, fixture.Scanned.Count);

    // Exact invalid-dependency regression (task section 40): the three known evt_zombie
    // declarations are discovered (source coverage preserved) but their map dependency is
    // genuinely invalid - locking these EXACT stable identities so a pinned revision bump can only
    // change this deliberately, never silently.
    [Fact]
    public void EvtZombieDeclarations_AreDiscoveredButHaveInvalidMapDependency()
    {
        var evtZombieSpawns = fixture.Scanned.Where(spawn => spawn.Map == "evt_zombie").OrderBy(spawn => spawn.SourceLine).ToArray();

        Assert.Equal(3, evtZombieSpawns.Length);
        Assert.Equal([267, 268, 269], evtZombieSpawns.Select(spawn => spawn.SourceLine));
        Assert.All(evtZombieSpawns, spawn => Assert.Equal("legacy/rathena/npc/events/halloween_2008.txt", spawn.SourceFile));
        Assert.Equal([3000, 3001, 3002], evtZombieSpawns.Select(spawn => spawn.MobId));

        Assert.DoesNotContain("evt_zombie", fixture.ValidMaps);
    }

    // Registry-completeness counts (task section 12/38/41): valid vs invalid map dependencies,
    // exactly matching the generate-mob-spawns CLI's own printed summary (9841 valid / 3 invalid).
    [Fact]
    public void ScanAllOrdinarySpawns_ValidAndInvalidMapDependencyCounts()
    {
        var valid = fixture.Scanned.Count(spawn => fixture.ValidMaps.Contains(spawn.Map));
        var invalid = fixture.Scanned.Count - valid;

        Assert.Equal(9841, valid);
        Assert.Equal(3, invalid);
    }

    // Every discovered MobId resolves against the pinned mob_db.yml symbol table (task section 10) -
    // the same resolution generate-mob-spawns performs before emitting a single file, proven
    // directly against real pinned data rather than only via "generation succeeded" as an implicit
    // proxy.
    [Fact]
    public void ScanAllOrdinarySpawns_EveryMobIdResolvesAgainstPinnedMobDb()
    {
        var unresolved = fixture.Scanned.Select(spawn => spawn.MobId).Distinct().Where(id => !fixture.PinnedMobIds.Contains(id)).ToArray();
        Assert.Empty(unresolved);
    }

    // A DeathEvent-bearing real pinned declaration round-trips losslessly through the shared parser
    // (correction to this plan/task: event/size/AI fields are source-data-preserved even though no
    // runtime consumes them yet) - lhz_dun_n.txt:12 is one of the 44 real quoted death-event
    // declarations found by direct inventory of the pinned tree, with surrounding quotes stripped.
    [Fact]
    public void ReadAllMobSpawns_DeathEvent_RoundTripsVerbatimForRealPinnedDeclaration()
    {
        var spawn = fixture.Scanned.Single(spawn => spawn.SourceFile == "legacy/rathena/npc/re/mobs/dungeons/lhz_dun_n.txt" && spawn.SourceLine == 12);
        Assert.Equal("lhz_dun_n::OnRegularDead3208", spawn.DeathEvent);
    }

    // RespawnRandomDelay (pinned mob.delay2) is preserved losslessly and independently from
    // RespawnDelay (mob.delay1) - correction to this plan: two source values must never collapse
    // into one. int_land's G_PORING declares delay1=5000 with no delay2 at all (omitted -> 0).
    [Fact]
    public void ReadAllMobSpawns_RespawnRandomDelay_DefaultsToZeroWhenOmittedFromSource()
    {
        var spawn = fixture.Scanned.Single(spawn => spawn.SourceFile == "legacy/rathena/npc/re/mobs/int_land.txt" && spawn.SourceLine == 11);
        Assert.Equal(2401, spawn.MobId);
        Assert.Equal(5000, spawn.RespawnDelay);
        Assert.Equal(0, spawn.RespawnRandomDelay);
    }

    // Two DISTINCT delay1/delay2 pairs really occur together in the pinned tree (proves this is not
    // a hypothetical field): a real declaration declares a nonzero delay2, matching pinned
    // mob_delay_amount's `spawntime = delay1 + rnd()%delay2` random-variance semantics.
    [Fact]
    public void ReadAllMobSpawns_RespawnRandomDelay_PreservesRealNonZeroDelay2()
    {
        var withDelay2 = fixture.Scanned.Where(spawn => spawn.RespawnRandomDelay > 0).ToArray();
        Assert.NotEmpty(withDelay2);
        Assert.Contains(withDelay2, spawn => spawn.RespawnDelay == 3600000 && spawn.RespawnRandomDelay == 7200000);
    }
}
