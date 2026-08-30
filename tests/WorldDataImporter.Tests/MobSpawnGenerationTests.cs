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

        var mobDbYaml = File.ReadAllText(Path.Combine(Root, "db/re/mob_db.yml"));
        var mobDefinitions = MobDataCompiler.ReadAllMobDefinitions(mobDbYaml);
        PinnedMobIds = mobDefinitions.Select(mob => mob.Id).ToHashSet();
        var aegisNameToId = MobDataCompiler.BuildAegisNameLookup(mobDefinitions);

        var npcRoot = Path.Combine(Root, "npc");
        var scanned = new List<MobDataCompiler.MobSpawnData>();
        foreach (var path in Directory.EnumerateFiles(npcRoot, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var relative = CanonicalSourceFile(path);
            scanned.AddRange(MobDataCompiler.ReadAllMobSpawns(File.ReadAllText(path), relative, aegisNameToId));
        }
        Scanned = scanned;

        var analyzed = RepositoryDomainAnalyzers.Analyze(Root, new HashSet<string> { "mob-spawns", "maps" });
        AnalyzerMobSpawnIds = analyzed.Where(entity => entity.Domain == "mob-spawns").Select(entity => entity.Id).ToHashSet(StringComparer.Ordinal);
        ValidMaps = analyzed.Where(entity => entity.Domain == "maps").Select(entity => entity.Name).ToHashSet(StringComparer.Ordinal);
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

    // ReadAllMobSpawns (the generate-mob-spawns CLI's own scan) must find EXACTLY the same 10,068
    // identities as RepositoryDomainAnalyzers.AnalyzeMobSpawns (task section 22/41: "compare stable
    // identities, not just counts") - both derive from the SAME ReadMobSpawns/SpawnLine parser
    // (task section 23's "strong preference: one shared parser"), so this proves the all-declarations
    // scan path and the name-filtered analyzer path never silently diverge.
    // ReadAllMobSpawns' successfully-parsed identity set is a SUBSET of (not equal to) the analyzer's
    // own mob-spawns domain identity set: RepositoryDomainAnalyzers.AnalyzeMobSpawns now uses
    // MobDataCompiler.TryReadAllMobSpawns (line-isolated, candidate-line-aware), which additionally
    // reports 171 real, currently-unmatched-by-SpawnLine "ordinary monster declaration" lines as
    // their own explicit mob-spawn:parse-failure diagnostic entities (see
    // ai/follow-up/mob-spawn-map-token-gap.md) - REAL declarations that ReadAllMobSpawns/SpawnLine
    // still cannot parse at all (a known, separate follow-up gap, not something introduced by this
    // test). Both entity id schemes share the SAME "mob-spawn:{file}:{line}" shape for the
    // successfully-parsed subset, so ReadAllMobSpawns' ids must be a subset of the analyzer's -
    // proving the generator's own scan path never silently diverges from what the analyzer
    // successfully resolves, while still allowing the analyzer to see MORE (as explicit diagnostics,
    // never silent) than the generator currently can.
    [Fact]
    public void ReadAllMobSpawns_IdentitySet_IsASubsetOfTheAnalyzerMobSpawnDomain()
    {
        // Analyzer ids are ROOT-relative (RepositoryDomainAnalyzers.Relative -> "npc/..."), while
        // generated WorldSourceInfo.File is repo-canonical ("legacy/rathena/npc/..." -
        // CanonicalSourceFile, matching every other generated-source-provenance field in this
        // project). Both identify the exact same declaration; strip the shared "legacy/rathena/"
        // prefix here so the comparison is apples-to-apples rather than a spurious path-convention
        // mismatch.
        var scannedIds = fixture.Scanned.Select(spawn => $"mob-spawn:{spawn.SourceFile.Replace("legacy/rathena/", string.Empty, StringComparison.Ordinal)}:{spawn.SourceLine}").ToHashSet(StringComparer.Ordinal);

        Assert.True(scannedIds.IsSubsetOf(fixture.AnalyzerMobSpawnIds), "ReadAllMobSpawns' identity set must be a subset of the analyzer's mob-spawns domain.");
        // Exactly 171 analyzer entities exist that ReadAllMobSpawns could not itself parse (the
        // known map-token character-class gap, ai/follow-up/mob-spawn-map-token-gap.md) - locked
        // here so a future fix to either side is a deliberate, visible test update.
        Assert.Equal(171, fixture.AnalyzerMobSpawnIds.Count - scannedIds.Count);
    }

    // Count corrected from the original 9,844 to 10,068 by a genuine parser bug fix (this branch):
    // pinned npc_parse_mob's own w1 sscanf success condition is `w1count >= 1`
    // (src/map/npc.cpp:5233) - a bare "<map>\tmonster\t..." declaration with NO ",x,y" coordinates
    // at all is valid pinned syntax (x/y/xs/ys stay at their memset-zero spawn_data default), but
    // the PRIOR SpawnLine regex required ",x,y" unconditionally and silently dropped every such
    // declaration. 224 real ordinary `monster` declarations use this bare form (verified by direct
    // scan of the pinned tree; 12 more real bare-form declarations are `boss_monster`, out of this
    // project's ordinary-monster scope entirely - see ai/world-data.md's explicit non-goals) -
    // 9844 + 224 = 10068. 15 of those 224 use a real AegisName mob token instead of a numeric MobId
    // (e.g. npc/re/mobs/dungeons/sp_rudus.txt:26 `GIANT_CAPUT`) - the ONLY real AegisName-token
    // declarations anywhere in the pinned tree, all previously unreachable by any parser in this
    // project.
    [Fact]
    public void ReadAllMobSpawns_FindsExactly10068Declarations() => Assert.Equal(10068, fixture.Scanned.Count);

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
    // exactly matching the generate-mob-spawns CLI's own printed summary (10065 valid / 3 invalid -
    // the 3 invalid are still exactly the evt_zombie declarations; the bare-map-name fix's 224
    // recovered declarations all target already-valid maps).
    [Fact]
    public void ScanAllOrdinarySpawns_ValidAndInvalidMapDependencyCounts()
    {
        var valid = fixture.Scanned.Count(spawn => fixture.ValidMaps.Contains(spawn.Map));
        var invalid = fixture.Scanned.Count - valid;

        Assert.Equal(10065, valid);
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

    // Task section 27/28: SpawnName-only (the overwhelming default real form - no real pinned
    // declaration anywhere uses the optional level field, see below), verified against a real
    // declaration whose SpawnName genuinely diverges from the mob's own db Name - real pinned Mob Id
    // 1182 (AegisName THIEF_MUSHROOM) has db Name "Thief Mushroom", but this specific spawn
    // declaration's own w3 name token is "Toadstool" (1,369 real pinned declarations diverge like
    // this, verified by direct scan cross-referencing every spawn's name token against
    // db/re/mob_db.yml's own Name field for the same MobId).
    [Fact]
    public void ReadAllMobSpawns_SpawnName_PreservedVerbatimIndependentOfMobDefinitionName()
    {
        var spawn = fixture.Scanned.Single(spawn => spawn.SourceFile == "legacy/rathena/npc/custom/etc/penal_servitude.txt" && spawn.SourceLine == 186);
        Assert.Equal(1182, spawn.MobId);
        Assert.Equal("Toadstool", spawn.SpawnName);
        Assert.Null(spawn.DeclaredLevel);
    }

    // Task section 15/16/28: zero real pinned ordinary-monster declarations use the optional
    // ",<mob level>" w3 field anywhere in the tree (exhaustively verified) - locked here so a future
    // pinned revision that adds one is caught as a test failure (the count changing away from 0)
    // rather than silently assumed to remain zero forever.
    [Fact]
    public void ReadAllMobSpawns_NoRealPinnedDeclarationUsesTheOptionalLevelFieldToday()
    {
        Assert.DoesNotContain(fixture.Scanned, spawn => spawn.DeclaredLevel is not null);
    }

    // Task section 16/17/28: numeric MobId token (the default/overwhelming real form).
    [Fact]
    public void ReadAllMobSpawns_NumericMobIdToken_ResolvesDirectly()
    {
        var spawn = fixture.Scanned.Single(spawn => spawn.SourceFile == "legacy/rathena/npc/re/mobs/int_land.txt" && spawn.SourceLine == 11);
        Assert.Equal(2401, spawn.MobId);
    }

    // Task section 16/28: real AegisName mob token - pinned npc_parse_mob tries a numeric parse
    // first, then falls back to mobdb_search_aegisname (npc.cpp:5258-5275). This exact declaration
    // is one of only 15 real pinned occurrences anywhere in the tree (a genuine parser gap fix in
    // this branch: see ReadAllMobSpawns_FindsExactly10068Declarations' own doc comment - the prior
    // SpawnLine regex required ",x,y" unconditionally and never reached these bare-map-name lines
    // at all, so this exact form was previously unreachable regardless of AegisName support).
    [Fact]
    public void ReadAllMobSpawns_RealAegisNameMobToken_ResolvesToTheCorrectMobId()
    {
        var spawn = fixture.Scanned.Single(spawn => spawn.SourceFile == "legacy/rathena/npc/re/mobs/dungeons/sp_rudus.txt" && spawn.SourceLine == 26);
        Assert.Equal("sp_rudus4", spawn.Map);
        Assert.Equal("Giant Caput", spawn.SpawnName);
        Assert.Equal(0, spawn.X); Assert.Equal(0, spawn.Y); Assert.Equal(0, spawn.Xs); Assert.Equal(0, spawn.Ys);
        // GIANT_CAPUT's real pinned mob Id, cross-checked against db/re/mob_db.yml directly.
        var expectedId = MobDataCompiler.ReadAllMobDefinitions(File.ReadAllText(Path.Combine(fixture.Root, "db/re/mob_db.yml")))
            .Single(mob => string.Equals(mob.AegisName, "GIANT_CAPUT", StringComparison.OrdinalIgnoreCase)).Id;
        Assert.Equal(expectedId, spawn.MobId);
    }

    // Task section 17: an unknown non-numeric mob token fails closed with a clear diagnostic,
    // never silently skipped.
    [Fact]
    public void ReadMobSpawns_UnknownAegisNameToken_ThrowsWithSourceLocation()
    {
        const string Text = "some_map\tmonster\tGhost\tTHIS_TOKEN_DOES_NOT_EXIST,10,5000\n";
        var ex = Assert.Throws<ArgumentException>(() => MobDataCompiler.ReadAllMobSpawns(Text, "synthetic.txt", new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)));
        Assert.Contains("THIS_TOKEN_DOES_NOT_EXIST", ex.Message, StringComparison.Ordinal);
        Assert.Contains("synthetic.txt:1", ex.Message, StringComparison.Ordinal);
    }

    // Task section 17: a non-numeric mob token with NO resolver supplied at all also fails closed
    // (there is no valid interpretation of an AegisName token without a database to resolve it).
    [Fact]
    public void ReadMobSpawns_NonNumericTokenWithNoResolverSupplied_ThrowsClosed()
    {
        const string Text = "some_map\tmonster\tGhost\tSOME_AEGIS_NAME,10,5000\n";
        Assert.Throws<ArgumentException>(() => MobDataCompiler.ReadAllMobSpawns(Text, "synthetic.txt"));
    }
}

// Synthetic-fixture coverage for the level-override field (task section 15/27): zero real pinned
// declarations exercise this field (ReadAllMobSpawns_NoRealPinnedDeclarationUsesTheOptionalLevelField
// Today locks this), so every case here is deliberately synthetic - labeled as such in each test
// name/comment, matching task 27's "where possible" real-data preference (not possible here).
// Verified directly against pinned npc_parse_mob (npc.cpp:5218-5317): mob_lv's sentinel is -1
// (omitted); a present value of exactly 0 or > MAX_LEVEL (275) is a hard parse-time rejection;
// only a present value in (0, MAX_LEVEL] actually overrides mob.level (EffectiveLevelOverride);
// any other negative value (synthetically, e.g. -5) is accepted at parse time (fails neither the
// "==0" nor the ">MAX_LEVEL" checks) but never overrides mob.level - a genuine pinned quirk, not an
// approximation.
public sealed class MobSpawnLevelOverrideSyntheticTests
{
    private static MobDataCompiler.MobSpawnData ParseSingle(string levelField) =>
        MobDataCompiler.ReadAllMobSpawns($"some_map,0,0\tmonster\tGhost{levelField}\t1002,10,5000\n", "synthetic.txt").Single();

    [Fact]
    public void SpawnName_WithDeclaredLevel_PositiveInRange_IsPreservedAndBecomesTheEffectiveOverride()
    {
        var spawn = ParseSingle(",50");
        Assert.Equal("Ghost", spawn.SpawnName);
        Assert.Equal(50, spawn.DeclaredLevel);
    }

    [Fact]
    public void DeclaredLevel_Omitted_IsNull()
    {
        var spawn = ParseSingle(string.Empty);
        Assert.Null(spawn.DeclaredLevel);
    }

    [Fact]
    public void DeclaredLevel_ExactlyZero_IsRejectedAtGenerationTime()
    {
        Assert.Throws<ArgumentException>(() => ParseSingle(",0"));
    }

    [Fact]
    public void DeclaredLevel_AboveMaxPinnedLevel_IsRejectedAtGenerationTime()
    {
        Assert.Throws<ArgumentException>(() => ParseSingle(",276"));
    }

    [Fact]
    public void DeclaredLevel_ExactlyMaxPinnedLevel_IsAcceptedAndEffective()
    {
        var spawn = ParseSingle(",275");
        Assert.Equal(275, spawn.DeclaredLevel);
    }

    // A negative value other than the omitted sentinel is accepted (fails neither pinned rejection
    // check) but is stored as-is - it never becomes an effective override (see
    // MobSpawnDefinition.EffectiveLevelOverride, src/MapServer/World/WorldEntityDefinition.cs).
    [Fact]
    public void DeclaredLevel_NegativeOtherThanOmittedSentinel_IsPreservedButNeverEffective()
    {
        var spawn = ParseSingle(",-5");
        Assert.Equal(-5, spawn.DeclaredLevel);
    }
}
