using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;

// Priority 2/3/4/7/18 (ai/world-data.md) focused coverage for RepositoryDomainAnalyzers, using
// small synthetic on-disk fixtures (a scratch directory shaped like a minimal pinned rAthena
// tree: db/map_cache.dat, db/re/*.yml, npc/*.txt) rather than the full pinned legacy/rathena
// tree - mirroring RepositoryCompatibilityAnalyzerTests' DirectoryFixture style and
// RathenaMapCacheLayersTests' synthetic map_cache.dat builder.
public sealed class RepositoryDomainAnalyzersTests
{
    private sealed class DomainFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "athena-domain-analysis-" + Guid.NewGuid().ToString("N"));
        public DomainFixture() => Directory.CreateDirectory(Root);
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

    // --- synthetic map_cache.dat builder, mirrored from RathenaMapCacheLayersTests ---
    private static byte[] ZlibCompress(byte[] raw)
    {
        using var output = new MemoryStream();
        using (var zlib = new ZLibStream(output, CompressionMode.Compress, leaveOpen: true)) zlib.Write(raw, 0, raw.Length);
        return output.ToArray();
    }
    private static byte[] BuildRecord(string name, short xs, short ys, byte[] rawCells)
    {
        var compressed = ZlibCompress(rawCells);
        var record = new byte[12 + 2 + 2 + 4 + compressed.Length];
        Encoding.ASCII.GetBytes(name).CopyTo(record, 0);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(12, 2), xs);
        BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(14, 2), ys);
        BinaryPrimitives.WriteInt32LittleEndian(record.AsSpan(16, 4), compressed.Length);
        compressed.CopyTo(record, 20);
        return record;
    }
    private static byte[] BuildMapCache(params (string Name, short Xs, short Ys, byte[] RawCells)[] maps)
    {
        var records = maps.Select(map => BuildRecord(map.Name, map.Xs, map.Ys, map.RawCells)).ToArray();
        var totalLength = 8 + records.Sum(record => record.Length);
        var buffer = new byte[totalLength];
        BinaryPrimitives.WriteUInt32LittleEndian(buffer.AsSpan(0, 4), (uint)totalLength);
        BinaryPrimitives.WriteUInt16LittleEndian(buffer.AsSpan(4, 2), (ushort)maps.Length);
        var offset = 8;
        foreach (var record in records) { record.CopyTo(buffer, offset); offset += record.Length; }
        return buffer;
    }
    // RathenaMapCacheFormat.ReadAll requires the decompressed payload to be EXACTLY width*height
    // bytes (one raw GAT cell-type byte per cell) - a 1x1 fixture map therefore needs exactly 1
    // raw cell byte, matching the corrected fixture in RathenaMapCacheLayersTests.
    private static readonly byte[] OneCell = [0];

    private const string ItemDbHeader = "Header:\n  Type: ITEM_DB\n  Version: 1\nBody:\n";

    // ---------------------------------------------------------------------
    // Priority 2: mob-spawn/mapflag map dependency resolution post Priority-1 fix.
    // ---------------------------------------------------------------------

    [Fact]
    public void MobSpawn_ReferencingAMapPresentOnlyInTheBaseCache_ResolvesEvenWithARenewalOverlayPresent()
    {
        // Regression guard for the fixed 8-map bug: prt_fild08 exists only in the BASE cache;
        // db/re/map_cache.dat (present, covering a wholly different map) must not cause the
        // mob-spawn's "dependency:map" blocker to fire.
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.WriteBytes("db/re/map_cache.dat", BuildMapCache(("prontera", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("npc/re/mobs/fields.txt", "prt_fild08,0,0\tmonster\tPoring\t1002,5,5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        var spawn = Assert.Single(entities, item => item.Domain == "mob-spawns");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, spawn.Status);
        Assert.DoesNotContain("dependency:map", spawn.Blockers);
        Assert.DoesNotContain("dependency:mob", spawn.Blockers);
        Assert.Contains("map:prt_fild08", spawn.Dependencies);
    }

    [Fact]
    public void MobSpawn_ReferencingAMapAbsentFromEveryLayer_ReportsGenuineDependencyMapBlocker()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prontera", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("npc/re/mobs/fields.txt", "nonexistent_map,0,0\tmonster\tPoring\t1002,5,5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        var spawn = Assert.Single(entities, item => item.Domain == "mob-spawns");
        Assert.Equal(DomainCompatibilityStatus.Unsupported, spawn.Status);
        Assert.Contains("dependency:map", spawn.Blockers);
    }

    // A synthetic fixture with no real npc/re/scripts_main.conf tree degrades gracefully - the
    // mob-spawns domain's own count/status/blockers are entirely unaffected (task section 32: never
    // redefine the existing domain metric), and Provenance simply reports "Disabled" (no config
    // graph resolved, so nothing is classified as Renewal-active) rather than throwing.
    [Fact]
    public void MobSpawn_SyntheticFixtureWithNoRealScriptsMainConf_DegradesGracefullyToDisabledProvenance()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("npc/re/mobs/fields.txt", "prt_fild08,0,0\tmonster\tPoring\t1002,5,5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        var spawn = Assert.Single(entities, item => item.Domain == "mob-spawns");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, spawn.Status);
        Assert.Equal("Disabled", spawn.Provenance);
    }

    // Hardening regression (post-PR #25/#26): a genuinely unknown AegisName token must never make a
    // source declaration silently disappear from analyzer coverage. AnalyzeMobSpawns previously
    // called MobDataCompiler.ReadMobSpawns once PER DISTINCT NAME in a file, wrapped in a blanket
    // `try { ... } catch { continue; }` - since ReadMobSpawns' own TryParseSpawnLine call runs on
    // EVERY line of the file before that line's mob name is even compared against the name being
    // looked up, one malformed line threw for every name lookup that reached it, and the blanket
    // catch silently dropped ALL of that file's mob-spawns domain coverage with zero diagnostic.
    [Fact]
    public void MobSpawn_UnknownAegisNameToken_NeverSilentlyDisappearsFromAnalyzerCoverage()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("npc/re/mobs/fields.txt", "prt_fild08\tmonster\tGhost\tTHIS_TOKEN_DOES_NOT_EXIST,10,5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        // The malformed declaration must be represented as its OWN diagnostic entity - never
        // silently absent from the domain entirely.
        var spawn = Assert.Single(entities, item => item.Domain == "mob-spawns");
        Assert.Equal(DomainCompatibilityStatus.Unsupported, spawn.Status);
        Assert.Contains("mob-spawn:parse-failure", spawn.Blockers);
        Assert.Contains("THIS_TOKEN_DOES_NOT_EXIST", spawn.Name, StringComparison.Ordinal);
    }

    // The poisoning half of the same regression: a malformed line elsewhere in a file must not
    // suppress OTHER, perfectly valid declarations in that same file (the old per-name/whole-file
    // rescan meant a bad line anywhere could break name resolution for every other name too,
    // depending on scan order) - both the good and the bad declaration must be represented.
    [Fact]
    public void MobSpawn_UnknownAegisNameToken_DoesNotSuppressOtherValidDeclarationsInTheSameFile()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("npc/re/mobs/fields.txt",
            "prt_fild08\tmonster\tGhost\tTHIS_TOKEN_DOES_NOT_EXIST,10,5000\n" +
            "prt_fild08,0,0\tmonster\tPoring\t1002,5,5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);
        var spawns = entities.Where(item => item.Domain == "mob-spawns").ToArray();

        Assert.Equal(2, spawns.Length);
        Assert.Contains(spawns, item => item.Status == DomainCompatibilityStatus.Unsupported && item.Blockers.Contains("mob-spawn:parse-failure"));
        Assert.Contains(spawns, item => item.Status == DomainCompatibilityStatus.FullyCompatible && item.Name == "Poring");
    }

    [Fact]
    public void MapFlag_ReferencingAMapPresentOnlyInTheBaseCache_HasNoFalseDependencyBlocker()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.WriteBytes("db/re/map_cache.dat", BuildMapCache(("prontera", (short)1, (short)1, OneCell)));
        fixture.Write("npc/re/mobs/fields.txt", "prt_fild08\tmapflag\tnoteleport\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        var flag = Assert.Single(entities, item => item.Domain == "mapflags");
        Assert.DoesNotContain("dependency:map", flag.Blockers);
    }

    // ---------------------------------------------------------------------
    // Priority 3/4: item StaticData/RuntimeBehavior independence + semantic capability ids.
    // ---------------------------------------------------------------------

    [Fact]
    public void Item_FullyRepresentedWithNoScript_BothComponentsFullyCompatible()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/item_db_etc.yml", ItemDbHeader + """
              - Id: 909
                AegisName: Jellopy
                Name: Jellopy
                Type: Etc
            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "items" });

        var item = Assert.Single(entities);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, item.Status);
        var staticData = item.Components.Single(c => c.Name == "StaticData");
        var runtime = item.Components.Single(c => c.Name == "RuntimeBehavior");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, staticData.Status);
        Assert.Empty(staticData.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, runtime.Status);
        Assert.Empty(runtime.Blockers!);
    }

    [Fact]
    public void Item_StaticFieldOnlyBlocker_DoesNotTaintRuntimeBehavior()
    {
        using var fixture = new DomainFixture();
        // Weight/Jobs/Slots are not in ItemSupportedKeys, so they surface as item-field:* blockers.
        fixture.Write("db/re/item_db_etc.yml", ItemDbHeader + """
              - Id: 1201
                AegisName: Knife
                Name: Knife
                Type: Weapon
                SubType: Dagger
                Attack: 17
                Weight: 400
                Locations:
                  Right_Hand: true
            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "items" });

        var item = Assert.Single(entities);
        var staticData = item.Components.Single(c => c.Name == "StaticData");
        var runtime = item.Components.Single(c => c.Name == "RuntimeBehavior");
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, staticData.Status);
        Assert.Contains("item-field:weight", staticData.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, runtime.Status);
        Assert.Empty(runtime.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, item.Status);
    }

    [Fact]
    public void Item_RuntimeScriptOnlyBlocker_StaticDataStaysFullyCompatible()
    {
        using var fixture = new DomainFixture();
        // A Usable item with an EquipScript is a nonsensical-but-parseable combination purely to
        // exercise the item-script:equip blocker in isolation from any static-field gap - every
        // top-level key here is in ItemSupportedKeys.
        fixture.Write("db/re/item_db_etc.yml", ItemDbHeader + """
              - Id: 501
                AegisName: Red_Potion
                Name: Red Potion
                Type: Usable
                EquipScript: |
                  bonus bStr,1;
            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "items" });

        var item = Assert.Single(entities);
        var staticData = item.Components.Single(c => c.Name == "StaticData");
        var runtime = item.Components.Single(c => c.Name == "RuntimeBehavior");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, staticData.Status);
        Assert.Empty(staticData.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, runtime.Status);
        Assert.Contains("item-script:equip", runtime.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, item.Status);
    }

    [Fact]
    public void Item_BothStaticAndRuntimeBlockers_BothComponentsIndependentlyPartial()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/item_db_etc.yml", ItemDbHeader + """
              - Id: 502
                AegisName: Orange_Potion
                Name: Orange Potion
                Type: Usable
                Weight: 100
                EquipScript: |
                  bonus bStr,1;
            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "items" });

        var item = Assert.Single(entities);
        var staticData = item.Components.Single(c => c.Name == "StaticData");
        var runtime = item.Components.Single(c => c.Name == "RuntimeBehavior");
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, staticData.Status);
        Assert.Contains("item-field:weight", staticData.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, runtime.Status);
        Assert.Contains("item-script:equip", runtime.Blockers!);
    }

    [Fact]
    public void Item_UnsupportedType_ClassifiesAsSemanticItemTypeCapability_NotGenericBucket()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/item_db_card.yml", ItemDbHeader + """
              - Id: 4001
                AegisName: Poring_Card
                Name: Poring Card
                Type: Card
            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "items" });

        var item = Assert.Single(entities);
        Assert.Equal(DomainCompatibilityStatus.Unsupported, item.Status);
        Assert.Contains("item-type:card", item.Blockers);
        Assert.DoesNotContain(item.Blockers, blocker => blocker.StartsWith("item-definition:", StringComparison.Ordinal));
    }

    [Fact]
    public void Item_UnsupportedWeaponSubType_ClassifiesAsSemanticItemSubtypeCapability()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/item_db_equip.yml", ItemDbHeader + """
              - Id: 1301
                AegisName: Weird_Weapon
                Name: Weird Weapon
                Type: Weapon
                SubType: NotARealSubType
                Attack: 10
                Locations:
                  Right_Hand: true
            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "items" });

        var item = Assert.Single(entities);
        // Kebab() only inserts a hyphen between a lowercase/digit and a following uppercase, so
        // consecutive capitals ("AR" in "NotARealSubType") do not split further - matching the
        // convention already used for every other capability id in this project.
        Assert.Contains("item-subtype:not-areal-sub-type", item.Blockers);
    }

    // ---------------------------------------------------------------------
    // Priority 7: quest semantics - drop-rule vs general quest-definition, multi-file item index.
    // ---------------------------------------------------------------------

    [Fact]
    public void Quest_SupportedSingleDropRule_DropRuleFullyCompatibleAndDefinitionPartial()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
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

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "quests" });

        var quest = Assert.Single(entities);
        var dropRule = quest.Components.Single(c => c.Name == "DropRule");
        var definition = quest.Components.Single(c => c.Name == "Definition");
        var targets = quest.Components.Single(c => c.Name == "Targets");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, dropRule.Status);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, targets.Status);
        // Not claiming Definition = FullyCompatible merely from drop-compiler success.
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, definition.Status);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, quest.Status);
        Assert.Contains("mob:1002", quest.Dependencies);
        Assert.Contains("item:909", quest.Dependencies);
    }

    [Fact]
    public void Quest_WithTargetsBlock_TargetsComponentUnsupported_DefinitionUnsupported()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("db/re/item_db_etc.yml", ItemDbHeader);
        fixture.Write("db/re/quest_db.yml", """
            Header:
              Type: QUEST_DB
              Version: 1
            Body:
              - Id: 21009
                Title: Kill Porings
                Targets:
                  - Mob: PORING
                    Count: 5
            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "quests" });

        var quest = Assert.Single(entities);
        var targets = quest.Components.Single(c => c.Name == "Targets");
        var definition = quest.Components.Single(c => c.Name == "Definition");
        Assert.Equal(DomainCompatibilityStatus.Unsupported, targets.Status);
        Assert.Contains("quest:targets", targets.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.Unsupported, definition.Status);
        Assert.Equal(DomainCompatibilityStatus.Unsupported, quest.Status);
    }

    [Fact]
    public void Quest_WithNoDropsRuleAtAll_DropRuleNotApplicable_DefinitionNotYetAnalyzed_NotUnsupported()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("db/re/item_db_etc.yml", ItemDbHeader);
        fixture.Write("db/re/quest_db.yml", """
            Header:
              Type: QUEST_DB
              Version: 1
            Body:
              - Id: 21010
                Title: Talk to the mayor
            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "quests" });

        var quest = Assert.Single(entities);
        var dropRule = quest.Components.Single(c => c.Name == "DropRule");
        var definition = quest.Components.Single(c => c.Name == "Definition");
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, dropRule.Status);
        // No general quest-definition converter exists - absence of a Drops rule must not be
        // conflated with a positively-known incompatibility.
        Assert.Equal(DomainCompatibilityStatus.NotYetAnalyzed, definition.Status);
        Assert.Equal(DomainCompatibilityStatus.NotYetAnalyzed, quest.Status);
    }

    [Fact]
    public void Quest_DropItemResolvesAcrossDifferentItemDbFiles_NotOnlyItemDbEtc()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        // Deliberately empty item_db_etc.yml - the drop item lives in item_db_equip.yml instead.
        fixture.Write("db/re/item_db_etc.yml", ItemDbHeader);
        fixture.Write("db/re/item_db_equip.yml", ItemDbHeader + """
              - Id: 1201
                AegisName: Knife
                Name: Knife
                Type: Weapon
                SubType: Dagger
                Attack: 17
                Locations:
                  Right_Hand: true
            """);
        fixture.Write("db/re/quest_db.yml", """
            Header:
              Type: QUEST_DB
              Version: 1
            Body:
              - Id: 21011
                Title: Bring me a knife
                Drops:
                  - Mob: PORING
                    Item: Knife
                    Rate: 10000
            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "quests" });

        var quest = Assert.Single(entities);
        var dropRule = quest.Components.Single(c => c.Name == "DropRule");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, dropRule.Status);
        Assert.Contains("item:1201", quest.Dependencies);
    }

    // ---------------------------------------------------------------------
    // Priority 5 (hardened): mob StaticData/ModeData/ModeRuntime/RaceGroups/Drops/MvpDrops
    // component independence.
    // ---------------------------------------------------------------------

    [Fact]
    public void Mob_FullyRepresented_AllComponentsFullyCompatibleOrNotApplicable()
    {
        using var fixture = new DomainFixture();
        // MobBlock's own Ai: value ("01_DE_TWFOLLOW") is not a recognized pinned preset, so this
        // mob's resolved Mode is None - ModeRuntime is therefore NotApplicable (no resolved bit to
        // execute), not FullyCompatible; ModeData is still FullyCompatible (no unrecognized Modes:
        // entry NAME - there is no Modes: block at all here).
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Status);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Components.Single(c => c.Name == "StaticData").Status);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Components.Single(c => c.Name == "ModeData").Status);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, mob.Components.Single(c => c.Name == "ModeRuntime").Status);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, mob.Components.Single(c => c.Name == "RaceGroups").Status);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, mob.Components.Single(c => c.Name == "Drops").Status);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, mob.Components.Single(c => c.Name == "MvpDrops").Status);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, mob.Components.Single(c => c.Name == "Skills").Status);
    }

    [Fact]
    public void Mob_UnsupportedStaticField_DoesNotTaintModesOrDrops()
    {
        using var fixture = new DomainFixture();
        // Size/Race are fully represented (MobDataCompiler reads them) - a genuinely unmodeled
        // top-level scalar field is used instead to prove StaticData independence.
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Size: Small\n    Race: Plant\n    SomeFutureField: 1\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var staticData = mob.Components.Single(c => c.Name == "StaticData");
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, staticData.Status);
        Assert.DoesNotContain("mob-field:size", staticData.Blockers!);
        Assert.DoesNotContain("mob-field:race", staticData.Blockers!);
        Assert.Contains("mob-field:some-future-field", staticData.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Components.Single(c => c.Name == "ModeData").Status);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, mob.Status);
    }

    [Fact]
    public void Mob_RaceGroupsBlock_IsRepresentedAsDataButRemainsARuntimeGap()
    {
        using var fixture = new DomainFixture();
        // RaceGroups is now fully REPRESENTED (MobDataCompiler.ReadRaceGroups round-trips it
        // losslessly) - it must never produce a "mob-field:race-groups" StaticData blocker, but it
        // still has no gameplay runtime consumer, so its own dedicated component stays Unsupported
        // with a distinct `mob-race-groups:runtime` capability id.
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    RaceGroups:\n      Goblin: true\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var staticData = mob.Components.Single(c => c.Name == "StaticData");
        Assert.DoesNotContain("mob-field:race-groups", staticData.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, staticData.Status);
        var raceGroups = mob.Components.Single(c => c.Name == "RaceGroups");
        Assert.Equal(DomainCompatibilityStatus.Unsupported, raceGroups.Status);
        Assert.Contains("mob-race-groups:runtime", raceGroups.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, mob.Status);
    }

    [Fact]
    public void Mob_UnrecognizedModeName_ReportsModeDataBlockerIndependentOfStaticData()
    {
        using var fixture = new DomainFixture();
        // Aggressive/CanMove are both real, fully-representable MD_* mode bits (MobModeData models
        // the complete pinned bitmask) - a genuinely unrecognized future mode name is used instead
        // to prove ModeData actually detects a real representation gap.
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Modes:\n      SomeFutureMode: true\n      CanMove: true\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var modeData = mob.Components.Single(c => c.Name == "ModeData");
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, modeData.Status);
        Assert.Contains("mob-field:mode-some-future-mode", modeData.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Components.Single(c => c.Name == "StaticData").Status);
    }

    [Fact]
    public void Mob_AggressiveMode_IsRepresentedButRuntimeUnsupported()
    {
        using var fixture = new DomainFixture();
        // Aggressive is a real, fully-REPRESENTABLE MD_* bit (ModeData: FullyCompatible) that no
        // MapServer runtime call site executes yet (ModeRuntime: PartiallyCompatible, since CanMove
        // in the same block IS executed) - proving the representation-vs-execution split directly.
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Modes:\n      Aggressive: true\n      CanMove: true\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var modeData = mob.Components.Single(c => c.Name == "ModeData");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, modeData.Status);
        Assert.Empty(modeData.Blockers!);
        var modeRuntime = mob.Components.Single(c => c.Name == "ModeRuntime");
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, modeRuntime.Status);
        Assert.Contains("mob-mode-runtime:aggressive", modeRuntime.Blockers!);
        Assert.DoesNotContain("mob-mode-runtime:can-move", modeRuntime.Blockers!);
    }

    [Fact]
    public void Mob_ClassBoss_ImpliesClassDerivedModeRuntimeBlockersEvenWithNoExplicitModesEntries()
    {
        using var fixture = new DomainFixture();
        // No Modes: block at all - Detector/StatusImmune/KnockBackImmune come ENTIRELY from
        // Class: Boss (pinned MobDatabase::loadingFinished(), mob.cpp:5536-5551), never an explicit
        // source Modes: entry. ModeRuntime must still report them as unexecuted, proving effective-
        // mode (not merely source Modes:) drives this component.
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Class: Boss\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var modeRuntime = mob.Components.Single(c => c.Name == "ModeRuntime");
        Assert.Contains("mob-mode-runtime:detector", modeRuntime.Blockers!);
        Assert.Contains("mob-mode-runtime:status-immune", modeRuntime.Blockers!);
        Assert.Contains("mob-mode-runtime:knock-back-immune", modeRuntime.Blockers!);
        // ModeData (source-representation) must stay unaffected - no Modes: block was declared at all.
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Components.Single(c => c.Name == "ModeData").Status);
        Assert.Empty(mob.Components.Single(c => c.Name == "ModeData").Blockers!);
    }

    [Fact]
    public void Mob_ClassEvent_ImpliesFixedItemDropModeRuntimeBlocker()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Class: Event\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var modeRuntime = mob.Components.Single(c => c.Name == "ModeRuntime");
        Assert.Contains("mob-mode-runtime:fixed-item-drop", modeRuntime.Blockers!);
    }

    [Fact]
    public void Mob_WithDropsBlock_DropsComponentUnsupportedButNotAStaticDataBlocker()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Drops:\n      - Item: Jellopy\n        Rate: 5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        Assert.DoesNotContain("mob-field:drops", mob.Components.Single(c => c.Name == "StaticData").Blockers!);
        var drops = mob.Components.Single(c => c.Name == "Drops");
        Assert.Equal(DomainCompatibilityStatus.Unsupported, drops.Status);
        Assert.Contains("mob-drops:runtime", drops.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, mob.Status);
    }

    [Fact]
    public void Mob_WithMvpDropsBlock_MvpDropsComponentUnsupportedButNotAStaticDataBlocker()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    MvpDrops:\n      - Item: Gold_Ring\n        Rate: 2000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        Assert.DoesNotContain("mob-field:mvp-drops", mob.Components.Single(c => c.Name == "StaticData").Blockers!);
        var mvpDrops = mob.Components.Single(c => c.Name == "MvpDrops");
        Assert.Equal(DomainCompatibilityStatus.Unsupported, mvpDrops.Status);
        Assert.Contains("mob-mvp-drops:runtime", mvpDrops.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, mob.Status);
    }

    // ---------------------------------------------------------------------
    // Priority 6: mob skill dependency/component.
    // ---------------------------------------------------------------------

    [Fact]
    public void Mob_WithMobSkillRow_SkillsComponentUnsupportedWithSkillCapabilityId()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1086, "GOLDEN_BUG", "Golden Thief Bug"));
        fixture.Write("db/re/mob_skill_db.txt",
            "// header\n1086,0,ANY,152,1,3000,0,3000,yes,target,0,0,0,0,0,0,0,5,0\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var skills = mob.Components.Single(c => c.Name == "Skills");
        Assert.Equal(DomainCompatibilityStatus.Unsupported, skills.Status);
        Assert.Contains("mob-skill:152", skills.Blockers!);
        Assert.Contains("mob-skill:runtime", skills.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, mob.Status);
    }

    [Fact]
    public void Mob_AbsentFromMobSkillDb_SkillsComponentStaysNotApplicable()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("db/re/mob_skill_db.txt", "// header\n1086,0,ANY,152,1,3000,0,3000,yes,target,0,0,0,0,0,0,0,5,0\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, mob.Components.Single(c => c.Name == "Skills").Status);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Status);
    }

    // ---------------------------------------------------------------------
    // Priority 10: MVP classification (Class: Boss + Modes.Mvp: true + MvpExp/MvpDrops).
    // ---------------------------------------------------------------------

    [Fact]
    public void Mob_WithClassBossAndModesMvpTrue_ProducesMvpDomainEntity()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1086, "GOLDEN_BUG", "Golden Thief Bug") + """
                MvpExp: 51030
                Class: Boss
                Modes:
                  Mvp: true
                MvpDrops:
                  - Item: Gold_Ring
                    Rate: 2000

            """);

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs", "mvp" });

        var mvp = Assert.Single(entities, item => item.Domain == "mvp");
        Assert.Equal("mvp:1086", mvp.Id);
        Assert.Equal(DomainCompatibilityStatus.Unsupported, mvp.Status);
        Assert.Contains("mvp:runtime-behavior", mvp.Blockers);
        Assert.Contains(mvp.Components, c => c.Name == "MvpBehavior" && c.Status == DomainCompatibilityStatus.Unsupported);
    }

    [Fact]
    public void Mob_WithoutClassBoss_DoesNotProduceMvpEntity()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs", "mvp" });

        Assert.DoesNotContain(entities, item => item.Domain == "mvp");
    }

    // ---------------------------------------------------------------------
    // Priority 15: map-world completeness aggregate.
    // ---------------------------------------------------------------------

    [Fact]
    public void MapWorld_FullyEvaluatedMap_ReportsFullyCompatibleWithCounts()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("npc/re/mobs/fields.txt", "prt_fild08,0,0\tmonster\tPoring\t1002,5,5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        var world = Assert.Single(entities, item => item.Domain == "map-world" && item.Map == "prt_fild08");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, world.Status);
        var spawns = world.Components.Single(c => c.Name == "MobSpawns");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, spawns.Status);
        Assert.Empty(spawns.Blockers!); // Priority 4: completeness counts are a structural Metric, never a formatted "x/y" string in Blockers.
        Assert.Equal(new DomainMetric(1, 1), spawns.Metric);
    }

    [Fact]
    public void MapWorld_PartiallyEvaluatedMap_ReportsPartialWithCounts()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        // One resolvable spawn, one referencing a genuinely missing map - a real partial mix.
        fixture.Write("npc/re/mobs/fields.txt",
            "prt_fild08,0,0\tmonster\tPoring\t1002,5,5000\n" +
            "nonexistent_map,0,0\tmonster\tPoring\t1002,5,5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        var world = Assert.Single(entities, item => item.Domain == "map-world" && item.Map == "prt_fild08");
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, world.Status); // prt_fild08's own spawn is fine; the other spawn belongs to a different (unresolvable) map.
        var missingWorld = entities.SingleOrDefault(item => item.Domain == "map-world" && item.Map == "nonexistent_map");
        Assert.Null(missingWorld); // map-world only enumerates maps that exist in the "maps" domain itself.
    }

    [Fact]
    public void MapWorld_NotSelectedWhenMapsDomainNotSelected()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("npc/re/mobs/fields.txt", "prt_fild08\tmapflag\tnoteleport\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mapflags" });

        Assert.DoesNotContain(entities, item => item.Domain == "map-world");
    }

    // ---------------------------------------------------------------------
    // Determinism: a small multi-domain fixture produces byte-identical repeated output.
    // ---------------------------------------------------------------------

    [Fact]
    public void MultiDomainFixture_AnalyzedTwice_ProducesByteIdenticalSerializedOutput()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("db/re/item_db_etc.yml", ItemDbHeader + """
              - Id: 909
                AegisName: Jellopy
                Name: Jellopy
                Type: Etc
            """);
        fixture.Write("npc/re/mobs/fields.txt", "prt_fild08,0,0\tmonster\tPoring\t1002,5,5000\n");
        fixture.Write("npc/re/mobs/flags.txt", "prt_fild08\tmapflag\tnoteleport\n");

        var first = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);
        var second = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        Assert.Equal(DeterministicJson.Serialize(first), DeterministicJson.Serialize(second));
    }

    // ---------------------------------------------------------------------
    // Priority 1 (post-full-dry-run): raw-line scanners must ignore commented-out declarations.
    // ---------------------------------------------------------------------

    [Fact]
    public void MapFlags_ActiveDeclaration_IsDiscovered()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("npc/custom/etc/penal_servitude.txt", "prt_fild08\tmapflag\tpvp\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        var flag = Assert.Single(entities, item => item.Domain == "mapflags");
        Assert.Equal("pvp", flag.Name);
        Assert.Equal("prt_fild08", flag.Map);
    }

    [Fact]
    public void MapFlags_CommentedOutDeclaration_IsIgnored()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("sec_in02", (short)1, (short)1, OneCell)));
        // Real pinned shape: legacy/rathena/npc/custom/etc/penal_servitude.txt.
        fixture.Write("npc/custom/etc/penal_servitude.txt",
            "//sec_in02\tmapflag\tpvp\n" +
            "//sec_in02\tmapflag\tpvp_noparty\n" +
            "//sec_in02\tmapflag\tgvg\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        Assert.DoesNotContain(entities, item => item.Domain == "mapflags");
    }

    [Fact]
    public void MapFlags_WhitespaceBeforeComment_IsAlsoIgnored()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("sec_in02", (short)1, (short)1, OneCell)));
        fixture.Write("npc/custom/etc/penal_servitude.txt", "   \t //sec_in02\tmapflag\tpvp\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        Assert.DoesNotContain(entities, item => item.Domain == "mapflags");
    }

    [Fact]
    public void MapFlags_CommentedDeclaration_NeverProducesDependencyMapBlocker()
    {
        // Even referencing a map name that genuinely does not exist anywhere: a commented-out line
        // must never surface AT ALL, let alone as a false "dependency:map" blocker.
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prontera", (short)1, (short)1, OneCell)));
        fixture.Write("npc/custom/etc/penal_servitude.txt", "//sec_in02\tmapflag\tpvp\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        Assert.DoesNotContain(entities, item => item.Domain == "mapflags");
    }

    [Fact]
    public void MapFlags_CommentedDeclarations_NeverContributeToDomainTotals()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("npc/custom/etc/penal_servitude.txt",
            "prt_fild08\tmapflag\tnoteleport\n" +
            "//sec_in02\tmapflag\tpvp\n" +
            "//sec_in02\tmapflag\tpvp_noparty\n" +
            "//sec_in02\tmapflag\tgvg\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);
        var summaries = RepositoryDomainAnalyzers.Summaries(entities);

        var mapflagsSummary = Assert.Single(summaries, item => item.Domain == "mapflags");
        Assert.Equal(1, mapflagsSummary.Total); // Only the one active declaration - the three commented ones never entered the totals.
    }

    [Fact]
    public void MapFlags_ActiveDeclarationAdjacentToCommentedOne_IsStillDiscovered()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("npc/custom/etc/penal_servitude.txt",
            "//prt_fild08\tmapflag\tpvp\n" +
            "prt_fild08\tmapflag\tnoteleport\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        var flag = Assert.Single(entities, item => item.Domain == "mapflags");
        Assert.Equal("noteleport", flag.Name);
    }

    // ---------------------------------------------------------------------
    // Priority 2 (post-full-dry-run): mob Drops must not double-classify as both a generic
    // StaticData "mob-field:drops" blocker AND the dedicated Drops component's blocker.
    // ---------------------------------------------------------------------

    [Fact]
    public void Mob_WithDropsBlock_OnlyReportsDropsRuntimeBlocker_NotAGenericStaticDataDropsField()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Drops:\n      - Item: Jellopy\n        Rate: 5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities, item => item.Domain == "mobs");
        var staticData = mob.Components.Single(c => c.Name == "StaticData");
        var drops = mob.Components.Single(c => c.Name == "Drops");
        Assert.DoesNotContain("mob-field:drops", staticData.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, staticData.Status); // A Drops block alone must not degrade StaticData.
        Assert.Equal(DomainCompatibilityStatus.Unsupported, drops.Status);
        Assert.Contains("mob-drops:runtime", drops.Blockers!);
        Assert.DoesNotContain("mob-field:drops", mob.Blockers);
        Assert.Single(mob.Blockers, blocker => blocker == "mob-drops:runtime"); // Exactly one blocker for this one source construct, never two.
    }

    // ---------------------------------------------------------------------
    // Priority 3 (post-full-dry-run): function entity ids must be source-qualified and unique.
    // ---------------------------------------------------------------------

    [Fact]
    public void Functions_SameNameInDifferentFiles_RemainSeparateEntitiesWithDistinctIds()
    {
        using var fixture = new DomainFixture();
        fixture.Write("npc/re/jobs/a.txt", "function\tscript\tChk\t{\n\tend;\n}\n");
        fixture.Write("npc/re/jobs/b.txt", "function\tscript\tChk\t{\n\tend;\n}\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "functions" });
        var functions = entities.Where(item => item.Domain == "functions").ToArray();

        Assert.Equal(2, functions.Length);
        var ids = functions.Select(item => item.Id).ToHashSet(StringComparer.Ordinal);
        Assert.Equal(2, ids.Count); // Distinct ids - no collapse merely because the display name matches.
        Assert.All(functions, item => Assert.Equal("Chk", item.Name)); // Display name is still the plain function name.
        Assert.Contains(ids, id => id.Contains("a.txt", StringComparison.Ordinal));
        Assert.Contains(ids, id => id.Contains("b.txt", StringComparison.Ordinal));
    }

    [Fact]
    public void Functions_SameNameInSameFileDifferentLines_RemainSeparateEntities()
    {
        using var fixture = new DomainFixture();
        fixture.Write("npc/re/jobs/a.txt",
            "function\tscript\tCatwarp\t{\n\tend;\n}\n" +
            "function\tscript\tCatwarp\t{\n\tend;\n}\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "functions" });
        var functions = entities.Where(item => item.Domain == "functions").ToArray();

        Assert.Equal(2, functions.Length);
        Assert.Equal(2, functions.Select(item => item.Id).Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void Functions_DependenciesJson_KeepsSameNamedFunctionsAsSeparateGraphNodes()
    {
        using var fixture = new DomainFixture();
        fixture.Write("npc/re/jobs/a.txt", "function\tscript\tJob_Change\t{\n\tend;\n}\n");
        fixture.Write("npc/re/jobs/b.txt", "function\tscript\tJob_Change\t{\n\tend;\n}\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "functions" });
        var functions = entities.Where(item => item.Domain == "functions").ToArray();

        // Simulates the dependencies.json fold: grouping by Id (not Name) must not merge these.
        var grouped = functions.GroupBy(item => item.Id, StringComparer.Ordinal).ToArray();
        Assert.Equal(2, grouped.Length);
    }

    // ---------------------------------------------------------------------
    // Priority 4 (post-full-dry-run): map-world completeness must be a structural Metric, never a
    // formatted "x/y" string smuggled into Blockers.
    // ---------------------------------------------------------------------

    [Fact]
    public void MapWorld_Components_NeverContainRatioStringsInBlockers()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("npc/re/mobs/fields.txt", "prt_fild08,0,0\tmonster\tPoring\t1002,5,5000\n");
        fixture.Write("npc/re/mobs/flags.txt", "prt_fild08\tmapflag\tnoteleport\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, null);

        var world = Assert.Single(entities, item => item.Domain == "map-world" && item.Map == "prt_fild08");
        foreach (var component in world.Components)
        {
            Assert.All(component.Blockers ?? [], blocker => Assert.DoesNotMatch(@"^\d+/\d+$", blocker));
        }
        var spawns = world.Components.Single(c => c.Name == "MobSpawns");
        var flags = world.Components.Single(c => c.Name == "MapFlags");
        Assert.Equal(new DomainMetric(1, 1), spawns.Metric);
        Assert.Equal(new DomainMetric(0, 1), flags.Metric); // Mapflags are always Unsupported today (no runtime) - 0 of 1 compatible, still a real count.
    }

    [Fact]
    public void MapWorld_Determinism_ByteIdenticalAcrossRepeatedRuns()
    {
        using var fixture = new DomainFixture();
        fixture.WriteBytes("db/map_cache.dat", BuildMapCache(("prt_fild08", (short)1, (short)1, OneCell)));
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));
        fixture.Write("npc/re/mobs/fields.txt", "prt_fild08,0,0\tmonster\tPoring\t1002,5,5000\n");

        var first = RepositoryDomainAnalyzers.Analyze(fixture.Root, null).Where(item => item.Domain == "map-world").ToArray();
        var second = RepositoryDomainAnalyzers.Analyze(fixture.Root, null).Where(item => item.Domain == "map-world").ToArray();

        Assert.Equal(DeterministicJson.Serialize(first), DeterministicJson.Serialize(second));
    }

    private static string MobBlock(int id, string aegisName, string name) => $"""
        Header:
          Type: MOB_DB
          Version: 1
        Body:
          - Id: {id}
            AegisName: {aegisName}
            Name: {name}
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

        """;
}

// Real-pinned-data Provenance/load-classification coverage for the mob-spawns domain (task section
// 20/26/35), against the genuine legacy/rathena tree (commit
// e985006171d2eb320ee512a653f4c83aea3d81b6) - mirrors MobSpawnGenerationTests' real-tree fixture
// style; the mob-spawns domain's own entity COUNT stays exactly 10,068 (task section 32), this class
// covers only the new Provenance field.
public sealed class RepositoryDomainAnalyzersMobSpawnProvenanceTests
{
    private static readonly Lazy<IReadOnlyList<DomainEntity>> LazyMobSpawns = new(() =>
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        var repositoryRoot = directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
        var root = Path.Combine(repositoryRoot, "legacy/rathena");
        return RepositoryDomainAnalyzers.Analyze(root, new HashSet<string> { "mob-spawns" });
    });
    private static IReadOnlyList<DomainEntity> MobSpawns => LazyMobSpawns.Value;

    [Fact]
    public void Count_RemainsExactly10068RegardlessOfProvenance()
    {
        Assert.Equal(10068, MobSpawns.Count);
    }

    // Real declaration from a file referenced (active) by npc/re/scripts_monsters.conf.
    [Fact]
    public void RealActiveRenewalFile_ReportsRenewalDefaultProvenance()
    {
        var entity = MobSpawns.First(item => item.SourceFile == "npc/re/mobs/towns.txt");
        Assert.Equal("RenewalDefault", entity.Provenance);
    }

    // Real declaration from a pinned-disabled event file (commented in npc/scripts_athena.conf).
    [Fact]
    public void RealDisabledEventFile_ReportsDisabledProvenance()
    {
        var entity = MobSpawns.First(item => item.SourceFile == "npc/events/christmas_2013.txt");
        Assert.Equal("Disabled", entity.Provenance);
    }

    // Real npc/pre-re/... declaration.
    [Fact]
    public void RealPreRenewalFile_ReportsPreRenewalSourceProvenance()
    {
        var entity = MobSpawns.First(item => item.SourceFile.Contains("/pre-re/", StringComparison.Ordinal));
        Assert.Equal("PreRenewalSource", entity.Provenance);
    }

    // Academy overlay: pinned-disabled yet Athena-overlay-active.
    [Fact]
    public void AcademyFile_ReportsAthenaOverlayProvenance()
    {
        var entity = MobSpawns.First(item => item.SourceFile == "npc/re/mobs/academy.txt");
        Assert.Equal("AthenaOverlay", entity.Provenance);
    }

    // evt_zombie: represented, Disabled (halloween_2008.txt is commented out).
    [Fact]
    public void EvtZombieDeclarations_ReportDisabledProvenance()
    {
        var evtZombie = MobSpawns.Where(item => item.Map == "evt_zombie").ToArray();
        Assert.Equal(3, evtZombie.Length);
        Assert.All(evtZombie, item => Assert.Equal("Disabled", item.Provenance));
    }
}
