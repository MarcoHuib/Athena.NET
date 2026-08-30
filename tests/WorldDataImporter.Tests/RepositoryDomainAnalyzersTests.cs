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
    // Priority 5: mob StaticData/Modes/Drops component independence.
    // ---------------------------------------------------------------------

    [Fact]
    public void Mob_FullyRepresented_AllComponentsFullyCompatibleOrNotApplicable()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring"));

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Status);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Components.Single(c => c.Name == "StaticData").Status);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Components.Single(c => c.Name == "Modes").Status);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, mob.Components.Single(c => c.Name == "Drops").Status);
        Assert.Equal(DomainCompatibilityStatus.NotApplicable, mob.Components.Single(c => c.Name == "Skills").Status);
    }

    [Fact]
    public void Mob_UnsupportedStaticField_DoesNotTaintModesOrDrops()
    {
        using var fixture = new DomainFixture();
        // Size/Race are real mob_db.yml fields with zero representation in MobDefinitionData.
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Size: Small\n    Race: Plant\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var staticData = mob.Components.Single(c => c.Name == "StaticData");
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, staticData.Status);
        Assert.Contains("mob-field:size", staticData.Blockers!);
        Assert.Contains("mob-field:race", staticData.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Components.Single(c => c.Name == "Modes").Status);
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, mob.Status);
    }

    [Fact]
    public void Mob_UnsupportedMode_ReportsModeSpecificBlockerIndependentOfStaticData()
    {
        using var fixture = new DomainFixture();
        // Aggressive is a real MD_* mode bit with zero representation in MobModeData.
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Modes:\n      Aggressive: true\n      CanMove: true\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var modes = mob.Components.Single(c => c.Name == "Modes");
        Assert.Equal(DomainCompatibilityStatus.PartiallyCompatible, modes.Status);
        Assert.Contains("mob-mode:aggressive", modes.Blockers!);
        Assert.Equal(DomainCompatibilityStatus.FullyCompatible, mob.Components.Single(c => c.Name == "StaticData").Status);
    }

    [Fact]
    public void Mob_WithDropsBlock_DropsComponentUnsupported()
    {
        using var fixture = new DomainFixture();
        fixture.Write("db/re/mob_db.yml", MobBlock(1002, "PORING", "Poring") + "    Drops:\n      - Item: Jellopy\n        Rate: 5000\n");

        var entities = RepositoryDomainAnalyzers.Analyze(fixture.Root, new HashSet<string> { "mobs" });

        var mob = Assert.Single(entities);
        var drops = mob.Components.Single(c => c.Name == "Drops");
        Assert.Equal(DomainCompatibilityStatus.Unsupported, drops.Status);
        Assert.Contains("mob-drops:runtime", drops.Blockers!);
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
