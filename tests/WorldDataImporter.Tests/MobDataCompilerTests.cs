using System.Text.RegularExpressions;
using Athena.WorldCompiler.Generation;

public sealed class MobDataCompilerTests
{
    // Sanitized excerpt mirroring the real pinned db/re/mob_db.yml layout for
    // mob IDs 1002 (ordinary Poring) and 2401 (G_PORING), so tests prove the
    // compiler resolves 2401 specifically and never substitutes 1002.
    private const string MobDbFixture = """
        Body:
          - Id: 1002
            AegisName: PORING
            Name: Poring
            Level: 1
            Hp: 50
            BaseExp: 2
            JobExp: 1
            Attack: 7
            Attack2: 13
            Defense: 0
            MagicDefense: 5
            Str: 1
            Agi: 2
            Vit: 2
            Int: 1
            Dex: 6
            Luk: 15
            AttackRange: 1
            SkillRange: 5
            ChaseRange: 5
            Size: Small
            Race: Plant
            Element: Water
            ElementLevel: 1
            WalkSpeed: 300
            AttackDelay: 1936
            AttackMotion: 1000
            ClientAttackMotion: 480
            DamageMotion: 480
          - Id: 2401
            AegisName: G_PORING
            Name: Poring
            Level: 1
            Hp: 55
            Attack: 1
            Attack2: 1
            Defense: 2
            MagicDefense: 5
            Str: 6
            Int: 0
            Dex: 6
            Luk: 5
            AttackRange: 1
            SkillRange: 10
            ChaseRange: 12
            Size: Medium
            Race: Plant
            Element: Water
            ElementLevel: 1
            WalkSpeed: 400
            AttackDelay: 1872
            AttackMotion: 672
            ClientAttackMotion: 288
            DamageMotion: 480
            Ai: 02
            Modes:
              FixedItemDrop: true
          - Id: 2402
            AegisName: POPORING_IMMUNE_M
            Name: Poporing
            Level: 30
            Hp: 524
            BaseExp: 99
            JobExp: 112
        """;

    private const string SpawnFixture = "int_land,0,0\tmonster\tPoring\t2401,40,5000\n" +
        "int_land01,0,0\tmonster\tPoring\t2401,40,5000\n" +
        "int_land02,0,0\tmonster\tPoring\t2401,40,5000\n" +
        "int_land03,0,0\tmonster\tPoring\t2401,40,5000\n" +
        "int_land04,0,0\tmonster\tPoring\t2401,40,5000\n";

    [Fact]
    public void ReadMobDefinition_ResolvesGPoring2401_NotOrdinaryPoring1002()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(2401, mob.Id);
        Assert.Equal("G_PORING", mob.AegisName);
        Assert.NotEqual("PORING", mob.AegisName);
    }

    // Source-data regression coverage for the "prove G_PORING is allowed to move" requirement:
    // pinned mob_db.yml's Id 2401 block has Ai: 02 and a Modes: block that ONLY sets
    // FixedItemDrop=true (a bit this project's MobMode does not model) - the CanMove capability
    // comes entirely from the Ai=02 preset (MONSTER_TYPE_02=0x83=MD_CANMOVE|MD_LOOTER|MD_CANATTACK,
    // mob.hpp:153), NOT from an explicit Modes: entry. This proves the compiler actually resolves
    // that preset rather than only ever reading an explicit Modes: override - a mob whose Modes:
    // block never mentions CanMove at all must still correctly report MobMode.CanMove.
    [Fact]
    public void ReadMobDefinition_GPoring2401_ModeIsCanMoveAndCanAttack_DerivedFromAiPreset_NotFromModesBlock()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        // Pinned Ai=02 resolves to MONSTER_TYPE_02=0x83=MD_CANMOVE|MD_LOOTER|MD_CANATTACK
        // (mob.hpp:153), and this fixture's own Modes: block additionally sets FixedItemDrop - all
        // four bits are now retained (MobModeData models the complete pinned bitmask), not only the
        // two bits MapServer's runtime currently executes (CanMove/CanAttack) - see MobMode's own
        // doc comment for the representation-vs-runtime-execution distinction this asserts.
        Assert.Equal(
            MobDataCompiler.MobModeData.CanMove | MobDataCompiler.MobModeData.Looter | MobDataCompiler.MobModeData.CanAttack | MobDataCompiler.MobModeData.FixedItemDrop,
            mob.Mode);
    }

    [Fact]
    public void ReadMobDefinition_UnknownAiPreset_DefaultsToNoModeledCapability()
    {
        const string fixtureWithUnknownAi = """
            Body:
              - Id: 9999
                AegisName: TEST_UNKNOWN_AI
                Name: Test
                Ai: XX
            """;

        var mob = MobDataCompiler.ReadMobDefinition(fixtureWithUnknownAi, 9999);

        // Pinned MobDatabase::parseBodyNode defaults an unrecognized Ai value to MONSTER_TYPE_06=0
        // (mob.cpp:5456-5458) rather than failing the whole block - reproduced here the same way.
        Assert.Equal(MobDataCompiler.MobModeData.None, mob.Mode);
    }

    [Fact]
    public void ReadMobDefinition_NoAiField_DefaultsToNoModeledCapability()
    {
        const string fixtureWithNoAi = """
            Body:
              - Id: 9998
                AegisName: TEST_NO_AI
                Name: Test
            """;

        var mob = MobDataCompiler.ReadMobDefinition(fixtureWithNoAi, 9998);

        Assert.Equal(MobDataCompiler.MobModeData.None, mob.Mode);
    }

    [Fact]
    public void ReadMobDefinition_ExplicitModesOverridesAiPreset()
    {
        // A Modes: entry naming a bit this project models (CanMove) must correctly OR/AND-NOT it
        // on top of the Ai preset - proving the override mechanism itself, independent of which
        // preset supplied the base value.
        const string fixtureWithExplicitNoRandomWalk = """
            Body:
              - Id: 9997
                AegisName: TEST_STATIONARY
                Name: Test
                Ai: 02
                Modes:
                  NoRandomWalk: true
            """;

        var mob = MobDataCompiler.ReadMobDefinition(fixtureWithExplicitNoRandomWalk, 9997);

        // Ai=02=0x83 resolves MD_CANMOVE|MD_LOOTER|MD_CANATTACK - this test's own point is that
        // NoRandomWalk was correctly OR'd on top of the FULL Ai=02 preset (Looter included), not
        // that any preset bit was somehow suppressed.
        Assert.Equal(
            MobDataCompiler.MobModeData.CanMove | MobDataCompiler.MobModeData.Looter | MobDataCompiler.MobModeData.CanAttack | MobDataCompiler.MobModeData.NoRandomWalk,
            mob.Mode);
    }

    [Fact]
    public void ReadMobDefinition_GPoring2401_WalkSpeedRemains400FromSource()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(400, mob.WalkSpeed);
    }

    [Fact]
    public void GenerateMobDefinition_EmitsSourceBackedMobModeExpression_NoHardcodedMobIdCheck()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "abc123", "AcademyMobs", "GPoring", "db/re/mob_db.yml", 42);

        // Emission order follows ModeBitOrder (pinned bit-value ascending), not source/enum
        // declaration order coincidence - FixedItemDrop (0x1000000) sorts after CanAttack (0x80).
        Assert.Contains("Mode: MobMode.CanMove | MobMode.Looter | MobMode.CanAttack | MobMode.FixedItemDrop,", generated);
        // The generated source must never special-case this mob's numeric Id - the Mode value is
        // computed once from pinned Ai/Modes: data and emitted as a plain expression.
        Assert.DoesNotContain("2401 ==", generated);
        Assert.DoesNotContain("== 2401", generated);
    }

    // ===== AttackMotion / DamageMotion (distinct from AttackDelay - see MobDefinition's own doc
    // comment for why these three timings must never be conflated) =====

    [Fact]
    public void ReadMobDefinition_GPoring2401_AttackMotionMatchesPinnedValue()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(672, mob.AttackMotion);
    }

    [Fact]
    public void ReadMobDefinition_GPoring2401_DamageMotionMatchesPinnedValue()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(480, mob.DamageMotion);
    }

    // AttackMotion/DamageMotion must never be confused with AttackDelay, even though all three are
    // plain millisecond integers on the same mob_db row - G_PORING's own pinned values are all
    // DIFFERENT (1872/672/480), so any accidental field mix-up in the compiler would be caught here.
    [Fact]
    public void ReadMobDefinition_GPoring2401_AttackDelayAttackMotionDamageMotion_AreAllDistinctValues()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(1872, mob.AttackDelay);
        Assert.Equal(672, mob.AttackMotion);
        Assert.Equal(480, mob.DamageMotion);
        Assert.NotEqual(mob.AttackDelay, mob.AttackMotion);
        Assert.NotEqual(mob.AttackMotion, mob.DamageMotion);
        Assert.NotEqual(mob.AttackDelay, mob.DamageMotion);
    }

    [Fact]
    public void ReadMobDefinition_OrdinaryPoring1002_HasDifferentAttackMotionAndDamageMotionThanGPoring()
    {
        var ordinary = MobDataCompiler.ReadMobDefinition(MobDbFixture, 1002);
        var gPoring = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(1000, ordinary.AttackMotion);
        Assert.Equal(480, ordinary.DamageMotion); // Same DamageMotion as G_PORING by coincidence in this fixture...
        Assert.NotEqual(ordinary.AttackMotion, gPoring.AttackMotion); // ...but AttackMotion genuinely differs, proving per-mob resolution.
    }

    [Fact]
    public void ReadMobDefinition_MissingAttackMotionAndDamageMotion_DefaultToZero()
    {
        // Mob Id 2402 in the fixture has neither field - pinned mob.cpp's own default-constructor
        // rationale (mob.cpp:4946-4963) applies identically to these two fields as it already does
        // to AttackDelay: genuinely 0/unset when the pinned block omits them, never inherited from
        // another mob or silently approximated.
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2402);

        Assert.Equal(0, mob.AttackMotion);
        Assert.Equal(0, mob.DamageMotion);
    }

    [Fact]
    public void GenerateMobDefinition_EmitsAttackMotionAndDamageMotion_AsSeparateFieldsFromAttackDelay()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "abc123", "AcademyMobs", "GPoring", "db/re/mob_db.yml", 42);

        Assert.Contains("AttackDelay: 1872,", generated);
        Assert.Contains("AttackMotion: 672,", generated);
        Assert.Contains("DamageMotion: 480,", generated);
    }

    [Fact]
    public void ReadMobDefinition_ReadsCombatStatsFromGeneratedData()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(55u, mob.Hp);
        Assert.Equal(1, mob.Attack);
        Assert.Equal(1, mob.Attack2);
        Assert.Equal(2, mob.Defense);
        Assert.Equal(5, mob.MagicDefense);
        Assert.Equal(6, mob.Str);
        Assert.Equal(6, mob.Dex);
        Assert.Equal(5, mob.Luk);
        Assert.Equal(1, mob.Level);
    }

    [Fact]
    public void ReadMobDefinition_MissingSixCoreStatsDefaultToOne_NotZero()
    {
        // Pinned mob_db.yml doc comment: "Str/Agi/Vit/Int/Dex/Luk ... (Default: 1)".
        // G_PORING's block omits Agi and Vit entirely (unlike Int, which is
        // explicitly 0) - the constructor default of 1 must survive, not a
        // blanket "absent field = 0" rule.
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(1, mob.Agi);
        Assert.Equal(1, mob.Vit);
        Assert.Equal(0, mob.Int); // Explicitly present as 0 in the pinned block - not defaulted.
    }

    [Fact]
    public void ReadMobDefinition_MissingExpFieldsDefaultToZero_NoExpAwarded()
    {
        // G_PORING has no BaseExp/JobExp fields at all in the pinned source.
        // This must resolve to 0, never inherit ordinary Poring's nonzero
        // values merely because CharacterProgressionService exists.
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(0, mob.BaseExp);
        Assert.Equal(0, mob.JobExp);
    }

    [Fact]
    public void ReadMobDefinition_OrdinaryPoring1002_HasDifferentStatsAndNonzeroExp()
    {
        var ordinary = MobDataCompiler.ReadMobDefinition(MobDbFixture, 1002);

        Assert.Equal("PORING", ordinary.AegisName);
        Assert.Equal(50u, ordinary.Hp);
        Assert.Equal(2, ordinary.BaseExp);
        Assert.Equal(1, ordinary.JobExp);
        Assert.NotEqual(ordinary.Hp, MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401).Hp);
    }

    [Fact]
    public void ReadMobSpawns_FindsAllFiveDeclarations_PreservesCountAndRespawnDelay()
    {
        var spawns = MobDataCompiler.ReadMobSpawns(SpawnFixture, "npc/re/mobs/int_land.txt", "Poring");

        Assert.Equal(5, spawns.Count);
        Assert.All(spawns, spawn =>
        {
            Assert.Equal(2401, spawn.MobId);
            Assert.Equal(40, spawn.Count);
            Assert.Equal(5000, spawn.RespawnDelay);
        });
        Assert.Equal(["int_land", "int_land01", "int_land02", "int_land03", "int_land04"], spawns.Select(s => s.Map));
    }

    [Fact]
    public void ReadMobSpawns_PreservesSourceLineNumbers()
    {
        var spawns = MobDataCompiler.ReadMobSpawns(SpawnFixture, "npc/re/mobs/int_land.txt", "Poring");

        Assert.Equal([1, 2, 3, 4, 5], spawns.Select(s => s.SourceLine));
    }

    [Fact]
    public void ReadMobSpawns_XYZeroWithNoXsYs_ParsesAsMapWideRandomDeclaration()
    {
        var spawns = MobDataCompiler.ReadMobSpawns(SpawnFixture, "npc/re/mobs/int_land.txt", "Poring");

        Assert.All(spawns, spawn =>
        {
            Assert.Equal(0, spawn.X);
            Assert.Equal(0, spawn.Y);
            Assert.Equal(0, spawn.Xs);
            Assert.Equal(0, spawn.Ys);
        });
    }

    [Fact]
    public void ReadMobSpawns_ExplicitCenterAndAreaFields_ArePreserved()
    {
        const string rectangularFixture = "prontera,150,180,10,12\tmonster\tPoring\t1002,5,5000\n";
        var spawns = MobDataCompiler.ReadMobSpawns(rectangularFixture, "x.txt", "Poring");

        var spawn = Assert.Single(spawns);
        Assert.Equal(150, spawn.X);
        Assert.Equal(180, spawn.Y);
        Assert.Equal(10, spawn.Xs);
        Assert.Equal(12, spawn.Ys);
    }

    [Fact]
    public void ReadMobSpawns_ExcludedMapIsFiltered()
    {
        var spawns = MobDataCompiler.ReadMobSpawns(SpawnFixture, "npc/re/mobs/int_land.txt", "Poring", new HashSet<string> { "int_land" });

        Assert.Equal(4, spawns.Count);
        Assert.DoesNotContain(spawns, s => s.Map == "int_land");
    }

    [Fact]
    public void ReadMobSpawns_NameMismatchIsIgnored()
    {
        const string unrelated = "int_land05,0,0\tmonster\tPecopeco\t1234,1,5000\n";
        Assert.Throws<ArgumentException>(() => MobDataCompiler.ReadMobSpawns(unrelated, "x.txt", "Poring"));
    }

    [Fact]
    public void GenerateMobDefinition_IsDeterministic()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);
        var first = MobDataCompiler.GenerateMobDefinition(mob, "abc123", "AcademyMobs", "GPoring", "db/re/mob_db.yml", 42);
        var second = MobDataCompiler.GenerateMobDefinition(mob, "abc123", "AcademyMobs", "GPoring", "db/re/mob_db.yml", 42);

        Assert.Equal(first, second);
        Assert.Contains("Id: 2401", first);
        Assert.Contains("AegisName: \"G_PORING\"", first);
        Assert.DoesNotContain("PoringDefinition", first); // No monster-specific class name.
    }

    [Fact]
    public void GenerateMobSpawns_IsDeterministicAndCarriesProvenance()
    {
        var spawns = MobDataCompiler.ReadMobSpawns(SpawnFixture, "npc/re/mobs/int_land.txt", "Poring");
        var first = MobDataCompiler.GenerateMobSpawns(spawns, "AcademyMobs.GPoring", "abc123", "AcademyMobSpawns", "GPoringSpawns");
        var second = MobDataCompiler.GenerateMobSpawns(spawns, "AcademyMobs.GPoring", "abc123", "AcademyMobSpawns", "GPoringSpawns");

        Assert.Equal(first, second);
        Assert.Contains("abc123", first);
        Assert.Contains("int_land04", first);
        Assert.Contains("X: 0, Y: 0, Xs: 0, Ys: 0", first);
    }

    // ===== Expanded static-field schema coverage (Size/Race/Element/Class/scalars) =====

    // Real pinned db/re/mob_db.yml Id 1086 (Golden Thief Bug, e985006171d2eb320ee512a653f4c83aea3d81b6),
    // reproduced verbatim except for the still-unmodeled list-shaped MvpDrops:/Drops: blocks (which
    // this project intentionally does not scalar-parse - see the Drops component's own doc comment
    // in RepositoryDomainAnalyzers.AnalyzeMobs). Chosen because it is a real MVP that exercises
    // MvpExp, Size: Large (-> MobSize.Big), Race: Insect, Element: Fire, a non-default ElementLevel,
    // ClientAttackMotion/DamageTaken values that genuinely differ from their pinned defaults, and
    // Class: Boss all in one authentic pinned block - a genuine "lossless round trip against real
    // rAthena data" regression, not a synthetic fixture.
    private const string GoldenThiefBugFixture = """
        Body:
          - Id: 1086
            AegisName: GOLDEN_BUG
            Name: Golden Thief Bug
            Level: 65
            Hp: 222750
            BaseExp: 102060
            JobExp: 77760
            MvpExp: 51030
            Attack: 952
            Attack2: 843
            Defense: 159
            MagicDefense: 81
            Str: 71
            Agi: 77
            Vit: 80
            Int: 62
            Dex: 140
            Luk: 76
            AttackRange: 1
            SkillRange: 10
            ChaseRange: 12
            Size: Large
            Race: Insect
            Element: Fire
            ElementLevel: 2
            WalkSpeed: 100
            AttackDelay: 768
            AttackMotion: 768
            ClientAttackMotion: 720
            DamageMotion: 480
            DamageTaken: 10
            Ai: 07
            Class: Boss
            Modes:
              Mvp: true
        """;

    // Same real pinned Golden Thief Bug record, this time INCLUDING the real pinned MvpDrops:/
    // Drops: blocks (db/re/mob_db.yml:4222-4246) that GoldenThiefBugFixture above deliberately
    // omits (that fixture predates this project's Drops/MvpDrops representation and several other
    // tests already depend on its exact shape) - a separate constant rather than editing the
    // shared one, to avoid changing what those other tests exercise.
    private const string GoldenThiefBugWithDropsFixture = """
        Body:
          - Id: 1086
            AegisName: GOLDEN_BUG
            Name: Golden Thief Bug
            Level: 65
            Hp: 222750
            BaseExp: 102060
            JobExp: 77760
            MvpExp: 51030
            Attack: 952
            Attack2: 843
            Defense: 159
            MagicDefense: 81
            Str: 71
            Agi: 77
            Vit: 80
            Int: 62
            Dex: 140
            Luk: 76
            AttackRange: 1
            SkillRange: 10
            ChaseRange: 12
            Size: Large
            Race: Insect
            Element: Fire
            ElementLevel: 2
            WalkSpeed: 100
            AttackDelay: 768
            AttackMotion: 768
            ClientAttackMotion: 720
            DamageMotion: 480
            DamageTaken: 10
            Ai: 07
            Class: Boss
            Modes:
              Mvp: true
            MvpDrops:
              - Item: Gold_Ring
                Rate: 2000
              - Item: Ora_Ora
                Rate: 1000
              - Item: Bs_Making_S
                Rate: 5000
            Drops:
              - Item: Gold
                Rate: 500
              - Item: Golden_Mace
                Rate: 75
              - Item: Golden_Gear
                Rate: 125
              - Item: Golden_Bell
                Rate: 250
              - Item: Emperium
                Rate: 150
              - Item: Elunium
                Rate: 1000
              - Item: Shadowdecon
                Rate: 50
              - Item: Golden_Bug_Card
                Rate: 1
                StealProtected: true
        """;

    [Fact]
    public void ReadMobDefinition_RealPinnedGoldenThiefBug_ReadsEveryExpandedStaticField()
    {
        var mob = MobDataCompiler.ReadMobDefinition(GoldenThiefBugFixture, 1086);

        Assert.Equal("GOLDEN_BUG", mob.AegisName);
        Assert.Equal(51030, mob.MvpExp);
        Assert.Equal(10, mob.SkillRange);
        Assert.Equal(12, mob.ChaseRange);
        Assert.Equal(MobDataCompiler.MobSizeData.Big, mob.Size); // Pinned "Large" -> SZ_BIG.
        Assert.Equal(MobDataCompiler.MobRaceData.Insect, mob.Race);
        Assert.Equal(MobDataCompiler.MobElementData.Fire, mob.Element);
        Assert.Equal(2, mob.ElementLevel);
        Assert.Equal(720, mob.ClientAttackMotion); // Explicit pinned value, not the AttackMotion-derived default.
        Assert.Equal(10, mob.DamageTaken);
        Assert.Equal(MobDataCompiler.MobClassData.Boss, mob.Class);
        Assert.Equal(0, mob.Resistance); // Absent from this block - genuinely defaults to 0.
        Assert.Equal(0, mob.MagicResistance);
        Assert.Equal(1u, mob.Sp); // Absent - defaults to 1 per pinned constructor.
        // Absent JapaneseName falls back to this SAME block's own Name (mob.cpp:5028-5040's
        // `!exists` branch), never null - see ReadMobDefinition's own doc comment.
        Assert.Equal("Golden Thief Bug", mob.JapaneseName);
        Assert.Null(mob.Title);
        Assert.Equal(0, mob.GroupId);
    }

    [Fact]
    public void GenerateMobDefinition_RealPinnedGoldenThiefBug_RoundTripsLosslesslyThroughGeneratedSource()
    {
        var mob = MobDataCompiler.ReadMobDefinition(GoldenThiefBugFixture, 1086);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "e985006171d2eb320ee512a653f4c83aea3d81b6", "GeneratedMobs", "GoldenThiefBug", "db/re/mob_db.yml", 4187);

        Assert.Contains("MvpExp: 51030", generated);
        Assert.Contains("SkillRange: 10", generated);
        Assert.Contains("ChaseRange: 12", generated);
        Assert.Contains("Size: MobSize.Big", generated);
        Assert.Contains("Race: MobRace.Insect", generated);
        Assert.Contains("Element: MobElement.Fire", generated);
        Assert.Contains("ElementLevel: 2", generated);
        Assert.Contains("ClientAttackMotion: 720", generated);
        Assert.Contains("DamageTaken: 10", generated);
        Assert.Contains("Class: MobClass.Boss", generated);
        Assert.Contains("JapaneseName: \"Golden Thief Bug\"", generated);
        Assert.Contains("Title: null", generated);
    }

    // ClientAttackMotion's pinned "absent -> falls back to this same mob's own resolved
    // AttackMotion" default (mob.cpp:5391-5397) is genuinely derived, not a fixed constant - proven
    // with a dedicated fixture that sets a distinctive AttackMotion and omits ClientAttackMotion
    // entirely (unlike G_PORING's fixture block above, which sets both explicitly to DIFFERENT
    // values and would not actually exercise this fallback path).
    [Fact]
    public void ReadMobDefinition_MissingClientAttackMotion_DefaultsToThisMobsOwnAttackMotion()
    {
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    AttackMotion: 555\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        Assert.Equal(555, mob.AttackMotion);
        Assert.Equal(555, mob.ClientAttackMotion);
    }

    [Fact]
    public void ReadMobDefinition_MissingDamageTaken_DefaultsTo100()
    {
        // Pinned constructor default (mob.cpp:4966: this->damagetaken = 100;), matching the doc
        // comment "(Default: 100)" exactly - not 0.
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 2401);

        Assert.Equal(100, mob.DamageTaken);
    }

    // ===== Enum-coverage: every valid pinned Size:/Race:/Element:/Class: string value resolves to
    // the matching modeled enum member, exercising the full CHK_RACE/CHK_ELEMENT/Size_*/CLASS_*
    // valid ranges this project models - not merely the handful of values incidentally present in
    // the G_PORING/Golden Thief Bug fixtures above. =====

    // xUnit [Theory]/[InlineData] requires the test method's parameter types to be at least as
    // accessible as the method itself (a public [Theory] cannot take an `internal` enum parameter),
    // and MobDataCompiler's Mob*Data enums are deliberately `internal` (matching MobModeData's own
    // existing accessibility) - so each enum-coverage case below is a small [Fact] loop instead of a
    // [Theory], not a scope reduction in what is actually asserted.
    [Fact]
    public void ReadMobDefinition_EveryValidSize_ResolvesToMatchingEnumMember()
    {
        AssertSize("Small", MobDataCompiler.MobSizeData.Small);
        AssertSize("Medium", MobDataCompiler.MobSizeData.Medium);
        AssertSize("Large", MobDataCompiler.MobSizeData.Big);
        AssertSize("large", MobDataCompiler.MobSizeData.Big); // script_get_constant/search_str uses strcasecmp - case-insensitive.

        static void AssertSize(string pinnedValue, MobDataCompiler.MobSizeData expected)
        {
            var fixture = $"Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Size: {pinnedValue}\n";
            Assert.Equal(expected, MobDataCompiler.ReadMobDefinition(fixture, 1).Size);
        }
    }

    [Fact]
    public void ReadMobDefinition_UnknownSize_DefaultsToSmall()
    {
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Size: NotReal\n";
        Assert.Equal(MobDataCompiler.MobSizeData.Small, MobDataCompiler.ReadMobDefinition(fixture, 1).Size);
    }

    [Fact]
    public void ReadMobDefinition_EveryValidRace_ResolvesToMatchingEnumMember()
    {
        AssertRace("Formless", MobDataCompiler.MobRaceData.Formless);
        AssertRace("Undead", MobDataCompiler.MobRaceData.Undead);
        AssertRace("Brute", MobDataCompiler.MobRaceData.Brute);
        AssertRace("Plant", MobDataCompiler.MobRaceData.Plant);
        AssertRace("Insect", MobDataCompiler.MobRaceData.Insect);
        AssertRace("Fish", MobDataCompiler.MobRaceData.Fish);
        AssertRace("Demon", MobDataCompiler.MobRaceData.Demon);
        AssertRace("Demihuman", MobDataCompiler.MobRaceData.DemiHuman); // Real pinned spelling (single-word "Demihuman", not "DemiHuman").
        AssertRace("Angel", MobDataCompiler.MobRaceData.Angel);
        AssertRace("Dragon", MobDataCompiler.MobRaceData.Dragon);
        AssertRace("Player_Human", MobDataCompiler.MobRaceData.PlayerHuman);
        AssertRace("Player_Doram", MobDataCompiler.MobRaceData.PlayerDoram);

        static void AssertRace(string pinnedValue, MobDataCompiler.MobRaceData expected)
        {
            var fixture = $"Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Race: {pinnedValue}\n";
            Assert.Equal(expected, MobDataCompiler.ReadMobDefinition(fixture, 1).Race);
        }
    }

    [Fact]
    public void ReadMobDefinition_UnknownRace_DefaultsToFormless()
    {
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Race: NotReal\n";
        Assert.Equal(MobDataCompiler.MobRaceData.Formless, MobDataCompiler.ReadMobDefinition(fixture, 1).Race);
    }

    [Fact]
    public void ReadMobDefinition_EveryValidElement_ResolvesToMatchingEnumMember()
    {
        AssertElement("Neutral", MobDataCompiler.MobElementData.Neutral);
        AssertElement("Water", MobDataCompiler.MobElementData.Water);
        AssertElement("Earth", MobDataCompiler.MobElementData.Earth);
        AssertElement("Fire", MobDataCompiler.MobElementData.Fire);
        AssertElement("Wind", MobDataCompiler.MobElementData.Wind);
        AssertElement("Poison", MobDataCompiler.MobElementData.Poison);
        AssertElement("Holy", MobDataCompiler.MobElementData.Holy);
        AssertElement("Dark", MobDataCompiler.MobElementData.Dark);
        AssertElement("Ghost", MobDataCompiler.MobElementData.Ghost);
        AssertElement("Undead", MobDataCompiler.MobElementData.Undead);

        static void AssertElement(string pinnedValue, MobDataCompiler.MobElementData expected)
        {
            var fixture = $"Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Element: {pinnedValue}\n";
            Assert.Equal(expected, MobDataCompiler.ReadMobDefinition(fixture, 1).Element);
        }
    }

    [Fact]
    public void ReadMobDefinition_UnknownElement_DefaultsToNeutral()
    {
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Element: NotReal\n";
        Assert.Equal(MobDataCompiler.MobElementData.Neutral, MobDataCompiler.ReadMobDefinition(fixture, 1).Element);
    }

    [Fact]
    public void ReadMobDefinition_EveryValidClass_ResolvesToMatchingEnumMember()
    {
        AssertClass("Normal", MobDataCompiler.MobClassData.Normal);
        AssertClass("Boss", MobDataCompiler.MobClassData.Boss);
        AssertClass("Guardian", MobDataCompiler.MobClassData.Guardian);
        AssertClass("Battlefield", MobDataCompiler.MobClassData.Battlefield);
        AssertClass("Event", MobDataCompiler.MobClassData.Event);

        static void AssertClass(string pinnedValue, MobDataCompiler.MobClassData expected)
        {
            var fixture = $"Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Class: {pinnedValue}\n";
            Assert.Equal(expected, MobDataCompiler.ReadMobDefinition(fixture, 1).Class);
        }
    }

    [Fact]
    public void ReadMobDefinition_UnknownClass_DefaultsToNormal()
    {
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Class: NotReal\n";
        Assert.Equal(MobDataCompiler.MobClassData.Normal, MobDataCompiler.ReadMobDefinition(fixture, 1).Class);
    }

    // ===== Optional string fields (JapaneseName/Title) - genuinely Optional per the pinned doc
    // comment, unlike AegisName/Name which pinned source always populates. =====

    [Fact]
    public void ReadMobDefinition_JapaneseNameAndTitle_ArePreservedWhenPresent()
    {
        // Real pinned values: db/re/mob_db.yml Id 1013 (Snake) JapaneseName, and the "<Red Pepper>"
        // Title convention used by several real pinned costume/festival mobs.
        const string fixture = """
            Body:
              - Id: 1
                AegisName: T
                Name: Test
                JapaneseName: Snake
                Title: "<Red Pepper>"
            """;
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        Assert.Equal("Snake", mob.JapaneseName);
        Assert.Equal("<Red Pepper>", mob.Title);
    }

    // Pinned MobDatabase::parseBodyNode (mob.cpp:5028-5040): JapaneseName absent on a mob_id seen
    // for the first time (`!exists`) falls back to `mob->name` - never left null/blank. Verified
    // against a real pinned mob whose block genuinely omits JapaneseName (Poring, Id 1002 -
    // db/re/mob_db.yml:135-165 has no `JapaneseName:` line at all), not only the synthetic fixture
    // above.
    [Fact]
    public void ReadMobDefinition_RealPinnedPoring_MissingJapaneseName_FallsBackToName()
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/re/mob_db.yml"));
        var mob = MobDataCompiler.ReadMobDefinition(yaml, 1002);

        Assert.Equal("Poring", mob.Name);
        Assert.Equal("Poring", mob.JapaneseName);
    }

    [Fact]
    public void GenerateMobDefinition_MissingJapaneseName_EmitsNameAsTheFallbackValue()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 1002);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "abc123", "GeneratedMobs", "Poring", "db/re/mob_db.yml", 1);

        Assert.Equal("Poring", mob.JapaneseName);
        Assert.Contains("JapaneseName: \"Poring\"", generated);
    }

    [Fact]
    public void GenerateMobDefinition_Title_IsUnwrappedFromYamlQuotingAndEmittedAsCSharpStringLiteral()
    {
        // Real pinned convention (db/re/mob_db.yml Id 100200/100201): Title: values are
        // YAML-double-quoted whenever they contain characters the YAML scanner treats specially
        // (here, '<'/'>') - MobDataCompiler must unwrap that YAML quoting, not embed it verbatim
        // into the generated C# string literal.
        const string fixture = """
            Body:
              - Id: 1
                AegisName: T
                Name: Test
                Title: "<Red Pepper>"
            """;
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "abc123", "GeneratedMobs", "Test", "db/re/mob_db.yml", 1);

        Assert.Equal("<Red Pepper>", mob.Title);
        Assert.Contains("Title: \"<Red Pepper>\"", generated);
    }

    [Fact]
    public void GenerateMobDefinition_TitleContainingABackslashOrQuote_IsEscapedForCSharpStringLiterals()
    {
        // No real pinned mob_db.yml Title: value contains an embedded quote/backslash today, but
        // MobDataCompiler's escaping must still be correct if one ever did - proven directly against
        // EscapeForCSharpString's contract via a synthetic value (bare, not YAML-quoted, so this
        // exercises escaping in isolation from the YAML-unwrap behavior proven above).
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Title: Say \"Hi\"\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "abc123", "GeneratedMobs", "Test", "db/re/mob_db.yml", 1);

        Assert.Equal("Say \"Hi\"", mob.Title);
        Assert.Contains("Title: \"Say \\\"Hi\\\"\"", generated);
    }

    // ===== Real schema-drift guard: scans the ACTUAL pinned legacy/rathena/db/re/mob_db.yml for
    // every top-level key that genuinely occurs in a real record (not merely the file's own header
    // comment, which could itself drift from the real data, or a hand-copied C# list, which a
    // future field addition could silently bypass by never being updated). Each discovered key must
    // be explicitly classified as Modeled (MobDataCompiler.ReadMobDefinition parses it into a
    // scalar/enum MobDefinitionData field), DedicatedComponent (RaceGroups/Drops/MvpDrops/Modes -
    // list-shaped blocks with their own dedicated representation and analyzer component rather than
    // a flat scalar), or ExplicitlyIgnoredWithReason (none exist today - kept as an empty set so a
    // reviewer must add both the classification AND the reason if one is ever needed). A genuinely
    // new pinned top-level key with none of these three classifications fails the test - this is
    // the actual fail-closed guard the earlier hand-duplicated list could not provide. =====

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "Athena.NET.sln"))) directory = directory.Parent;
        return directory?.FullName ?? throw new DirectoryNotFoundException("Athena.NET repository root was not found.");
    }

    private static readonly Regex TopLevelKeyPattern = new(@"(?m)^    (?<key>[A-Za-z][A-Za-z0-9_]*):", RegexOptions.Compiled);

    [Fact]
    public void PinnedMobDbSchema_EveryTopLevelKeyActuallyPresentInRealData_IsExplicitlyClassified()
    {
        var path = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/re/mob_db.yml");
        var yaml = File.ReadAllText(path);

        // Every distinct 4-space-indented "Key:" that actually occurs anywhere in a real record
        // (scans the whole file body, not just one mob - a field could in principle be used by only
        // a handful of the 2000+ records).
        var discoveredKeys = TopLevelKeyPattern.Matches(yaml).Select(match => match.Groups["key"].Value).Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(discoveredKeys); // Guards against a silently-empty/mis-pathed scan passing vacuously.

        var modeled = new HashSet<string>(StringComparer.Ordinal)
        {
            "Id", "AegisName", "Name", "JapaneseName", "Level", "Hp", "Sp", "BaseExp", "JobExp",
            "MvpExp", "Attack", "Attack2", "Defense", "MagicDefense", "Resistance", "MagicResistance",
            "Str", "Agi", "Vit", "Int", "Dex", "Luk", "AttackRange", "SkillRange", "ChaseRange",
            "Size", "Race", "Element", "ElementLevel", "WalkSpeed", "AttackDelay", "AttackMotion",
            "ClientAttackMotion", "DamageMotion", "DamageTaken", "GroupId", "Title", "Ai", "Class",
        };
        // List-shaped blocks with their own dedicated representation (MobDefinitionData.RaceGroups/
        // Drops/MvpDrops lists, MobDefinitionData.Mode via the Modes: block) and dedicated analyzer
        // component (RepositoryDomainAnalyzers.AnalyzeMobs' RaceGroups/Drops/MvpDrops/ModeData/
        // ModeRuntime components) - never a flat MobSupportedKeys scalar entry.
        var dedicatedComponent = new HashSet<string>(StringComparer.Ordinal) { "Modes", "RaceGroups", "Drops", "MvpDrops" };
        // No pinned top-level mob_db.yml key is currently excluded without representation - if one
        // is ever added here, it must carry its own reason (see this test's own header comment).
        var explicitlyIgnoredWithReason = new Dictionary<string, string>(StringComparer.Ordinal);

        var unclassified = discoveredKeys.Where(key => !modeled.Contains(key) && !dedicatedComponent.Contains(key) && !explicitlyIgnoredWithReason.ContainsKey(key)).ToArray();
        Assert.Empty(unclassified);
    }

    // Companion to the header-body scan: also fails closed on a genuinely new nested Modes: entry
    // name (the complete pinned MD_* bitmask this project already models in full - see MobModeData's
    // own doc comment - so this should only ever fail if a FUTURE rAthena revision adds a brand new
    // MD_* bit beyond the 26 pinned ones this project currently knows about).
    private static readonly Regex ModesEntryPattern = new(@"(?m)^      (?<key>[A-Za-z][A-Za-z0-9_]*):\s*(?:true|false)\s*$", RegexOptions.Compiled);

    // Same column-0-comment tolerance as MobDataCompiler's own ModesBlock regex (see that field's
    // doc comment in MobDataCompiler.cs for the real-data rationale: 14 real pinned mobs have their
    // Modes: block interrupted by a column-0 `#...` line) - using a STRICTER indent-only scan here
    // would make this schema-drift test itself under-count real entries and silently pass instead
    // of genuinely verifying full coverage.
    private static readonly Regex ModesBlockTolerant = new(@"(?m)^    Modes:\n((?:(?:      .+|#.*|)\n?)*)", RegexOptions.Compiled);

    [Fact]
    public void PinnedMobDbSchema_EveryModesEntryNameActuallyPresentInRealData_IsRecognizedByModeBitsByName()
    {
        var path = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/re/mob_db.yml");
        var yaml = File.ReadAllText(path);

        var recognizedModeNames = new HashSet<string>(StringComparer.Ordinal)
        {
            "CanMove", "Looter", "Aggressive", "Assist", "CastSensorIdle", "NoRandomWalk", "NoCast", "CanAttack",
            "CastSensorChase", "ChangeChase", "Angry", "ChangeTargetMelee", "ChangeTargetChase", "TargetWeak",
            "RandomTarget", "IgnoreMelee", "IgnoreMagic", "IgnoreRanged", "Mvp", "IgnoreMisc", "KnockBackImmune",
            "TeleportBlock", "FixedItemDrop", "Detector", "StatusImmune", "SkillImmune",
        };
        Assert.Equal(26, recognizedModeNames.Count);

        var discoveredModeNames = new List<string>();
        foreach (Match modesBlock in ModesBlockTolerant.Matches(yaml))
        {
            discoveredModeNames.AddRange(ModesEntryPattern.Matches(modesBlock.Groups[1].Value).Select(m => m.Groups["key"].Value));
        }
        var distinctDiscovered = discoveredModeNames.Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(distinctDiscovered); // Guards against a silently-empty scan passing vacuously.

        Assert.Empty(distinctDiscovered.Except(recognizedModeNames, StringComparer.Ordinal));
    }

    // ===== Nested drop-entry schema drift: both Drops: and MvpDrops: list entries must have every
    // field they actually use, in real pinned data, explicitly classified - fails closed on a
    // genuinely new drop-entry field the same way the top-level scan does. =====

    private static Regex DropsBlockTolerant(string sectionName) => new($@"(?m)^    {Regex.Escape(sectionName)}:\n((?:(?:      .+|#.*|)\n?)*)", RegexOptions.Compiled);
    private static readonly Regex DropEntryFieldName = new(@"(?m)^\s*(?<field>[A-Za-z][A-Za-z0-9_]*):", RegexOptions.Compiled);

    [Fact]
    public void PinnedMobDbSchema_EveryDropsEntryFieldActuallyPresentInRealData_IsExplicitlyClassified()
    {
        AssertDropSectionSchema("Drops", knownIndexField: true);
    }

    [Fact]
    public void PinnedMobDbSchema_EveryMvpDropsEntryFieldActuallyPresentInRealData_IsExplicitlyClassified()
    {
        AssertDropSectionSchema("MvpDrops", knownIndexField: true);
    }

    // knownIndexField is always true for both sections today (both Drops: and MvpDrops: entries use
    // Index: in real pinned data - see ReadDrops' own doc comment on why Index carries real
    // overwrite/append semantics, not merely "unused, ignorable") - kept as an explicit parameter
    // rather than a hardcoded assumption so a future divergence between the two sections' actual
    // field usage is visible in the test signature, not silently assumed identical.
    private static void AssertDropSectionSchema(string sectionName, bool knownIndexField)
    {
        var path = Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/re/mob_db.yml");
        var yaml = File.ReadAllText(path);

        // Modeled (MobDataCompiler.ReadDrops parses these into MobDropEntryData/effective-list
        // resolution) vs the one genuinely pinned-documented field this project does NOT retain on
        // the FINAL MobDropEntry as its own persistent field (Index only affects WHICH slot/whether
        // an entry survives into the effective list - see ReadDrops - it has no meaning once the
        // effective list is resolved, so it is consumed during resolution rather than stored).
        var modeled = new HashSet<string>(StringComparer.Ordinal) { "Item", "Rate", "StealProtected", "RandomOptionGroup" };
        var consumedDuringResolution = new HashSet<string>(StringComparer.Ordinal);
        if (knownIndexField) consumedDuringResolution.Add("Index");

        var discoveredFields = new List<string>();
        foreach (Match sectionBlock in DropsBlockTolerant(sectionName).Matches(yaml))
        {
            foreach (Match entry in Regex.Matches(sectionBlock.Groups[1].Value, @"(?m)^\s*-\s*Item:\s*\S+\s*\n(?<rest>(?:(?!\s*-\s*Item:).*\n?)*)"))
            {
                discoveredFields.AddRange(DropEntryFieldName.Matches(entry.Groups["rest"].Value).Select(m => m.Groups["field"].Value));
            }
        }
        var distinctDiscovered = discoveredFields.Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(distinctDiscovered); // Guards against a silently-empty scan passing vacuously.

        var unclassified = distinctDiscovered.Where(field => !modeled.Contains(field) && !consumedDuringResolution.Contains(field)).ToArray();
        Assert.Empty(unclassified);
    }

    // ===== Drops/MvpDrops Index: pinned overwrite/append/skip semantics (MobDatabase::
    // parseDropNode, mob.cpp:4844-4923). Real pinned data uses Index: on essentially every drop
    // entry (1,301 real occurrences) - it is not a theoretical/db-import-only mechanism. =====

    [Fact]
    public void ReadMobDefinition_DropsWithNoIndex_Appends()
    {
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Drops:\n      - Item: A\n        Rate: 100\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        var entry = Assert.Single(mob.Drops);
        Assert.Equal("A", entry.Item);
        Assert.Equal(100, entry.Rate);
    }

    [Fact]
    public void ReadMobDefinition_DropsWithExplicitAppendIndex_AppendsAtThatSlot()
    {
        // Index == the effective list's current count (0, since this is the first entry) is an
        // explicit append, identical in effect to omitting Index entirely - pinned source's own
        // "Trying to add the next entry (just manually assigned the index)" comment.
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Drops:\n      - Item: A\n        Index: 0\n        Rate: 100\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        var entry = Assert.Single(mob.Drops);
        Assert.Equal("A", entry.Item);
    }

    [Fact]
    public void ReadMobDefinition_DropsIndexLessThanCurrentCount_OverwritesInPlace()
    {
        // Entry B declares Index: 0 - the slot A (implicitly index 0, via plain append) already
        // occupies - pinned source OVERWRITES that slot rather than moving/appending: the final
        // effective table has exactly ONE entry (B), not two.
        const string fixture = """
            Body:
              - Id: 1
                AegisName: T
                Name: T
                Drops:
                  - Item: A
                    Rate: 100
                  - Item: B
                    Index: 0
                    Rate: 200
            """;
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        var entry = Assert.Single(mob.Drops);
        Assert.Equal("B", entry.Item);
        Assert.Equal(200, entry.Rate);
    }

    [Fact]
    public void ReadMobDefinition_DropsIndexOverwritesOnlyTheTargetedSlot_SiblingEntriesUnaffected()
    {
        const string fixture = """
            Body:
              - Id: 1
                AegisName: T
                Name: T
                Drops:
                  - Item: A
                    Rate: 100
                  - Item: B
                    Rate: 200
                  - Item: C
                    Index: 0
                    Rate: 999
            """;
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        Assert.Equal(2, mob.Drops.Count);
        Assert.Equal("C", mob.Drops[0].Item); // Overwritten.
        Assert.Equal(999, mob.Drops[0].Rate);
        Assert.Equal("B", mob.Drops[1].Item); // Untouched - stays at its own slot.
        Assert.Equal(200, mob.Drops[1].Rate);
    }

    [Fact]
    public void ReadMobDefinition_DropsIndexGreaterThanCurrentCount_IsSkipped()
    {
        // Index: 5 with only one prior entry (count=1) is a genuine gap - pinned source's own
        // "TODO: warning" branch skips it entirely rather than padding/inserting.
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Drops:\n      - Item: A\n        Rate: 100\n      - Item: B\n        Index: 5\n        Rate: 200\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        var entry = Assert.Single(mob.Drops);
        Assert.Equal("A", entry.Item);
    }

    [Fact]
    public void ReadMobDefinition_DropsIndexAtOrAboveMaxMobDrop_IsSkipped()
    {
        // MAX_MOB_DROP is 10 (mob.hpp:27) - Index: 10 is out of bounds (valid range 0-9).
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Drops:\n      - Item: A\n        Index: 10\n        Rate: 100\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        Assert.Empty(mob.Drops);
    }

    [Fact]
    public void ReadMobDefinition_MoreThan10DropsWithNoIndex_StopsAtMaxMobDrop()
    {
        var lines = new System.Text.StringBuilder("Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Drops:\n");
        for (var i = 0; i < 12; i++) lines.Append("      - Item: Item").Append(i).Append("\n        Rate: 100\n");
        var mob = MobDataCompiler.ReadMobDefinition(lines.ToString(), 1);

        Assert.Equal(10, mob.Drops.Count); // MAX_MOB_DROP, not all 12 declared entries.
        Assert.Equal("Item0", mob.Drops[0].Item);
        Assert.Equal("Item9", mob.Drops[9].Item);
    }

    [Fact]
    public void ReadMobDefinition_MvpDropsIndexAtOrAboveMaxMvpDrop_IsSkipped()
    {
        // MAX_MVP_DROP is 3 (mob.hpp:31), distinct from MAX_MOB_DROP - Index: 3 is out of bounds
        // for MvpDrops even though it would be valid for a normal Drops: block.
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    MvpDrops:\n      - Item: A\n        Index: 3\n        Rate: 100\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        Assert.Empty(mob.MvpDrops);
    }

    [Fact]
    public void ReadMobDefinition_MoreThan3MvpDropsWithNoIndex_StopsAtMaxMvpDrop()
    {
        const string fixture = """
            Body:
              - Id: 1
                AegisName: T
                Name: T
                MvpDrops:
                  - Item: A
                    Rate: 100
                  - Item: B
                    Rate: 100
                  - Item: C
                    Rate: 100
                  - Item: D
                    Rate: 100
            """;
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        Assert.Equal(3, mob.MvpDrops.Count);
        Assert.DoesNotContain(mob.MvpDrops, entry => entry.Item == "D");
    }

    // Real pinned Poring (Id 1002) declares Index: 0 through Index: 7 on its own 8 base Drops:
    // entries (db/re/mob_db.yml:164-188) - every entry is a plain sequential append via Index, so
    // the effective table must equal all 8 declared entries in declaration order. This is also a
    // regression proof for the block-capture indentation fix: Poring's real Drops: block is
    // interrupted by a column-0 `#       RandomOptionGroup: 30L` comment
    // (db/re/mob_db.yml:171) between its 2nd and 3rd entries - an earlier version of
    // DropsBlockRegex (6-space-indent-only) silently truncated the block there, so this mob's real
    // generated data previously carried only 2 of its true 8 drop entries.
    [Fact]
    public void ReadMobDefinition_RealPinnedPoring_Has8DropsDespiteInterruptingCommentLine()
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/re/mob_db.yml"));
        var mob = MobDataCompiler.ReadMobDefinition(yaml, 1002);

        Assert.Equal(8, mob.Drops.Count);
        Assert.Equal("Jellopy", mob.Drops[0].Item);
        Assert.Equal("Knife_", mob.Drops[1].Item);
        Assert.Equal("Sticky_Mucus", mob.Drops[2].Item); // Immediately after the interrupting comment.
        Assert.Equal("Apple", mob.Drops[3].Item);
        Assert.Equal("Wing_Of_Fly", mob.Drops[4].Item);
        Assert.Equal("Apple", mob.Drops[5].Item);
        Assert.Equal("Unripe_Apple", mob.Drops[6].Item);
        var card = mob.Drops[7];
        Assert.Equal("Poring_Card", card.Item);
        Assert.Equal(20, card.Rate);
        Assert.True(card.StealProtected);
    }

    // ===== Effective mode: SourceMode (Ai preset + Modes: overrides, i.e. .Mode) vs EffectiveMode
    // (SourceMode + Class-derived bits pinned MobDatabase::loadingFinished() ORs on afterward,
    // mob.cpp:5536-5551). MobDataCompiler.ResolveEffectiveMode is the WorldDataImporter-side mirror
    // of Athena.Net.MapServer.World.MobModeResolver.ClassDerivedBits. =====

    [Fact]
    public void ResolveEffectiveMode_ClassBoss_AddsDetectorStatusImmuneKnockBackImmune()
    {
        var effective = MobDataCompiler.ResolveEffectiveMode(MobDataCompiler.MobModeData.CanMove, MobDataCompiler.MobClassData.Boss);

        Assert.True(effective.HasFlag(MobDataCompiler.MobModeData.CanMove)); // Source bit preserved.
        Assert.True(effective.HasFlag(MobDataCompiler.MobModeData.Detector));
        Assert.True(effective.HasFlag(MobDataCompiler.MobModeData.StatusImmune));
        Assert.True(effective.HasFlag(MobDataCompiler.MobModeData.KnockBackImmune));
        Assert.False(effective.HasFlag(MobDataCompiler.MobModeData.SkillImmune)); // Not a Boss bit.
    }

    [Fact]
    public void ResolveEffectiveMode_ClassGuardian_AddsOnlyStatusImmune()
    {
        var effective = MobDataCompiler.ResolveEffectiveMode(MobDataCompiler.MobModeData.None, MobDataCompiler.MobClassData.Guardian);

        Assert.Equal(MobDataCompiler.MobModeData.StatusImmune, effective);
    }

    [Fact]
    public void ResolveEffectiveMode_ClassBattlefield_AddsStatusImmuneAndSkillImmune()
    {
        var effective = MobDataCompiler.ResolveEffectiveMode(MobDataCompiler.MobModeData.None, MobDataCompiler.MobClassData.Battlefield);

        Assert.Equal(MobDataCompiler.MobModeData.StatusImmune | MobDataCompiler.MobModeData.SkillImmune, effective);
    }

    [Fact]
    public void ResolveEffectiveMode_ClassEvent_AddsFixedItemDrop()
    {
        var effective = MobDataCompiler.ResolveEffectiveMode(MobDataCompiler.MobModeData.None, MobDataCompiler.MobClassData.Event);

        Assert.Equal(MobDataCompiler.MobModeData.FixedItemDrop, effective);
    }

    [Fact]
    public void ResolveEffectiveMode_ClassNormal_AddsNothing()
    {
        var effective = MobDataCompiler.ResolveEffectiveMode(MobDataCompiler.MobModeData.CanMove | MobDataCompiler.MobModeData.CanAttack, MobDataCompiler.MobClassData.Normal);

        Assert.Equal(MobDataCompiler.MobModeData.CanMove | MobDataCompiler.MobModeData.CanAttack, effective);
    }

    // Real pinned Golden Thief Bug (Id 1086, Class: Boss) - its source Modes: block only ever sets
    // Mvp: true explicitly; Detector/StatusImmune/KnockBackImmune are NEVER mentioned in the YAML
    // at all, yet a real rAthena server grants them purely from Class: Boss. Proves source-mode
    // fidelity is preserved (mob.Mode does NOT contain those bits) while effective-mode resolution
    // still correctly derives them.
    [Fact]
    public void ResolveEffectiveMode_RealPinnedGoldenThiefBug_SourceModeExcludesClassBitsEffectiveModeIncludesThem()
    {
        var mob = MobDataCompiler.ReadMobDefinition(GoldenThiefBugFixture, 1086);

        Assert.True(mob.Mode.HasFlag(MobDataCompiler.MobModeData.Mvp)); // Explicit Modes: Mvp: true.
        Assert.False(mob.Mode.HasFlag(MobDataCompiler.MobModeData.Detector)); // Never in the YAML.
        Assert.False(mob.Mode.HasFlag(MobDataCompiler.MobModeData.StatusImmune));
        Assert.False(mob.Mode.HasFlag(MobDataCompiler.MobModeData.KnockBackImmune));

        var effective = MobDataCompiler.ResolveEffectiveMode(mob.Mode, mob.Class);
        Assert.True(effective.HasFlag(MobDataCompiler.MobModeData.Mvp)); // Source bit still present.
        Assert.True(effective.HasFlag(MobDataCompiler.MobModeData.Detector)); // Class-derived.
        Assert.True(effective.HasFlag(MobDataCompiler.MobModeData.StatusImmune));
        Assert.True(effective.HasFlag(MobDataCompiler.MobModeData.KnockBackImmune));
    }

    // ===== RaceGroups: real pinned round-trip (Id 1016, Archer Skeleton - db/re/mob_db.yml). =====

    [Fact]
    public void ReadMobDefinition_RealPinnedArcherSkeleton_PreservesRaceGroupsAndDropsWithStealProtected()
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/re/mob_db.yml"));
        var mob = MobDataCompiler.ReadMobDefinition(yaml, 1016);

        Assert.Equal("ARCHER_SKELETON", mob.AegisName);
        var raceGroup = Assert.Single(mob.RaceGroups);
        Assert.Equal("Clocktower", raceGroup.Name);
        Assert.True(raceGroup.Value);

        Assert.NotEmpty(mob.Drops);
        var cardDrop = mob.Drops.Single(drop => drop.Item == "Archer_Skeleton_Card");
        Assert.Equal(1, cardDrop.Rate);
        Assert.True(cardDrop.StealProtected);
        // Every other entry in this real block omits StealProtected - must default to false, not
        // silently inherit the previous entry's value.
        var otherDrop = mob.Drops.First(drop => drop.Item != "Archer_Skeleton_Card");
        Assert.False(otherDrop.StealProtected);
    }

    [Fact]
    public void GenerateMobDefinition_RealPinnedArcherSkeleton_RoundTripsRaceGroupsAndDropsThroughGeneratedSource()
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/re/mob_db.yml"));
        var mob = MobDataCompiler.ReadMobDefinition(yaml, 1016);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "e985006171d2eb320ee512a653f4c83aea3d81b6", "GeneratedMobs", "ArcherSkeleton", "db/re/mob_db.yml", 765);

        Assert.Contains("RaceGroups: [new MobRaceGroupEntry(\"Clocktower\", true)]", generated);
        Assert.Contains("new MobDropEntry(\"Archer_Skeleton_Card\", 1, true, null)", generated);
        Assert.Contains("MvpDrops: null", generated);
    }

    // ===== MvpDrops: real pinned round-trip (Id 1086, Golden Thief Bug - already the fixture used
    // by the Size/Race/Element/Class MVP round-trip tests above). =====

    [Fact]
    public void ReadMobDefinition_RealPinnedGoldenThiefBug_PreservesMvpDropsAndDrops()
    {
        var mob = MobDataCompiler.ReadMobDefinition(GoldenThiefBugWithDropsFixture, 1086);

        Assert.Equal(3, mob.MvpDrops.Count);
        Assert.Equal("Gold_Ring", mob.MvpDrops[0].Item);
        Assert.Equal(2000, mob.MvpDrops[0].Rate);
        Assert.Equal("Ora_Ora", mob.MvpDrops[1].Item);
        Assert.Equal("Bs_Making_S", mob.MvpDrops[2].Item);

        Assert.Equal(8, mob.Drops.Count);
        var cardDrop = mob.Drops.Single(drop => drop.Item == "Golden_Bug_Card");
        Assert.Equal(1, cardDrop.Rate);
        Assert.True(cardDrop.StealProtected);
    }

    [Fact]
    public void GenerateMobDefinition_RealPinnedGoldenThiefBug_RoundTripsMvpDropsAndDropsThroughGeneratedSource()
    {
        var mob = MobDataCompiler.ReadMobDefinition(GoldenThiefBugWithDropsFixture, 1086);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "e985006171d2eb320ee512a653f4c83aea3d81b6", "GeneratedMobs", "GoldenThiefBug", "db/re/mob_db.yml", 4187);

        Assert.Contains("MvpDrops: [new MobDropEntry(\"Gold_Ring\", 2000, false, null), new MobDropEntry(\"Ora_Ora\", 1000, false, null), new MobDropEntry(\"Bs_Making_S\", 5000, false, null)]", generated);
        Assert.Contains("new MobDropEntry(\"Golden_Bug_Card\", 1, true, null)", generated);
    }

    // ===== RaceGroups/Drops/MvpDrops absent - must be null, never an empty (but present) list, on
    // the generated record. =====

    [Fact]
    public void ReadMobDefinition_NoRaceGroupsDropsOrMvpDrops_AllThreeAreEmpty()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 1002);

        Assert.Empty(mob.RaceGroups);
        Assert.Empty(mob.Drops);
        Assert.Empty(mob.MvpDrops);
    }

    [Fact]
    public void GenerateMobDefinition_NoRaceGroupsDropsOrMvpDrops_EmitsNullForAllThree()
    {
        var mob = MobDataCompiler.ReadMobDefinition(MobDbFixture, 1002);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "abc123", "GeneratedMobs", "Poring", "db/re/mob_db.yml", 1);

        Assert.Contains("RaceGroups: null", generated);
        Assert.Contains("Drops: null", generated);
        Assert.Contains("MvpDrops: null", generated);
    }

    // ===== Full pinned mode bitmask preservation (not merely the 5 runtime-executed bits). =====

    [Fact]
    public void ReadMobDefinition_ModesEntry_False_RemovesBitFromAiPreset()
    {
        // Ai=02=0x83=MD_CANMOVE|MD_LOOTER|MD_CANATTACK; an explicit Modes: Looter: false must
        // AND-NOT that bit back off, proving the override direction (not merely that True adds
        // bits already present).
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Ai: 02\n    Modes:\n      Looter: false\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        Assert.Equal(MobDataCompiler.MobModeData.CanMove | MobDataCompiler.MobModeData.CanAttack, mob.Mode);
    }

    // Pinned mmo.hpp:242-272 declares 26 named MD_* enum members (MD_NONE=0 excluded, since it is
    // not a toggleable bit; the MD_MASK #define excluded, since it is not an enum member at all) -
    // 5 of those are exercised elsewhere (CanMove/NoRandomWalk/CanAttack/ChangeTargetMelee/
    // ChangeTargetChase, the runtime-executed subset - see e.g.
    // ReadMobDefinition_GPoring2401_ModeIsCanMoveAndCanAttack_DerivedFromAiPreset_NotFromModesBlock),
    // the remaining 21 are exercised individually below - proving the modeled named-bit count is
    // genuinely 26, not merely "a lot". Two bit POSITIONS (0x0000100, 0x0800000) are pinned "FREE"/
    // unused - correctly zero named members, not a gap in this project's own modeling.
    [Fact]
    public void MobModeData_NamedMemberCount_Is26()
    {
        var namedMembers = Enum.GetValues<MobDataCompiler.MobModeData>().Where(value => value != MobDataCompiler.MobModeData.None).ToArray();
        Assert.Equal(26, namedMembers.Length);
    }

    [Fact]
    public void ReadMobDefinition_EveryPinnedModeBitName_SetsExactlyThatBit()
    {
        // Exercises 21 of the 26 pinned MD_* names this project models (see MobModeData's own doc
        // comment and MobModeData_NamedMemberCount_Is26 above) individually via a fresh Ai-less
        // block (Ai absent -> base mode None), so each assertion proves that name maps to exactly
        // its own bit, not merely "some bit changed". The other 5 (the runtime-executed subset) are
        // exercised by their own dedicated tests elsewhere in this file.
        AssertBit("Looter", MobDataCompiler.MobModeData.Looter);
        AssertBit("Aggressive", MobDataCompiler.MobModeData.Aggressive);
        AssertBit("Assist", MobDataCompiler.MobModeData.Assist);
        AssertBit("CastSensorIdle", MobDataCompiler.MobModeData.CastSensorIdle);
        AssertBit("NoCast", MobDataCompiler.MobModeData.NoCast);
        AssertBit("CastSensorChase", MobDataCompiler.MobModeData.CastSensorChase);
        AssertBit("ChangeChase", MobDataCompiler.MobModeData.ChangeChase);
        AssertBit("Angry", MobDataCompiler.MobModeData.Angry);
        AssertBit("TargetWeak", MobDataCompiler.MobModeData.TargetWeak);
        AssertBit("RandomTarget", MobDataCompiler.MobModeData.RandomTarget);
        AssertBit("IgnoreMelee", MobDataCompiler.MobModeData.IgnoreMelee);
        AssertBit("IgnoreMagic", MobDataCompiler.MobModeData.IgnoreMagic);
        AssertBit("IgnoreRanged", MobDataCompiler.MobModeData.IgnoreRanged);
        AssertBit("Mvp", MobDataCompiler.MobModeData.Mvp);
        AssertBit("IgnoreMisc", MobDataCompiler.MobModeData.IgnoreMisc);
        AssertBit("KnockBackImmune", MobDataCompiler.MobModeData.KnockBackImmune);
        AssertBit("TeleportBlock", MobDataCompiler.MobModeData.TeleportBlock);
        AssertBit("FixedItemDrop", MobDataCompiler.MobModeData.FixedItemDrop);
        AssertBit("Detector", MobDataCompiler.MobModeData.Detector);
        AssertBit("StatusImmune", MobDataCompiler.MobModeData.StatusImmune);
        AssertBit("SkillImmune", MobDataCompiler.MobModeData.SkillImmune);

        static void AssertBit(string modeName, MobDataCompiler.MobModeData expectedBit)
        {
            var fixture = $"Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Modes:\n      {modeName}: true\n";
            Assert.Equal(expectedBit, MobDataCompiler.ReadMobDefinition(fixture, 1).Mode);
        }
    }

    [Fact]
    public void GenerateMobDefinition_EveryPinnedModeBit_EmitsCorrespondingMobModeMember()
    {
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Modes:\n      Aggressive: true\n      Detector: true\n      SkillImmune: true\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);
        var generated = MobDataCompiler.GenerateMobDefinition(mob, "abc123", "GeneratedMobs", "Test", "db/re/mob_db.yml", 1);

        Assert.Contains("Mode: MobMode.Aggressive | MobMode.Detector | MobMode.SkillImmune,", generated);
    }

    [Fact]
    public void ReadAllMobDefinitions_RealPinnedDb_IsCompleteUniqueAndCarriesRealLines()
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/re/mob_db.yml"));
        var mobs = MobDataCompiler.ReadAllMobDefinitions(yaml);

        Assert.Equal(2675, mobs.Count);
        Assert.Equal(2675, mobs.Select(mob => mob.Id).Distinct().Count());
        Assert.All(mobs, mob => Assert.True(mob.SourceLine > 0));
        Assert.Equal(136, mobs.Single(mob => mob.Id == 1002).SourceLine);
    }

    [Fact]
    public void CreateGeneratedSymbols_SanitizesAndDeterministicallyDisambiguatesCollisions()
    {
        const string fixture = "Body:\n  - Id: 1\n    AegisName: SOME_NAME\n    Name: One\n  - Id: 2\n    AegisName: SOME-NAME\n    Name: Two\n  - Id: 3\n    AegisName: 123_MOB\n    Name: Three\n";
        var symbols = MobDataCompiler.CreateGeneratedSymbols(MobDataCompiler.ReadAllMobDefinitions(fixture));

        Assert.Equal(["SomeName", "SomeName_2", "Mob123Mob"], symbols.Select(item => item.Symbol));
        Assert.Equal(symbols, MobDataCompiler.CreateGeneratedSymbols(MobDataCompiler.ReadAllMobDefinitions(fixture)));
    }

    [Fact]
    public void CreateGeneratedSymbols_DuplicateIdsFailClosed()
    {
        var first = MobDataCompiler.ReadMobDefinition("Body:\n  - Id: 1\n    AegisName: ONE\n    Name: One\n", 1);
        var second = first with { AegisName = "TWO" };

        var error = Assert.Throws<ArgumentException>(() => MobDataCompiler.CreateGeneratedSymbols([first, second]));
        Assert.Contains("duplicate effective mob Id(s): 1", error.Message);
    }

    [Fact]
    public void OwnedGeneratedMobFile_RequiresOwnedNameAndHeader()
    {
        var directory = Directory.CreateTempSubdirectory("athena-mob-cleanup-");
        try
        {
            var owned = Path.Combine(directory.FullName, "GeneratedMobs.Monsters.1000-1999.cs");
            var unrelated = Path.Combine(directory.FullName, "GeneratedMobs.Monsters.notes.cs");
            var registry = Path.Combine(directory.FullName, "GeneratedMobs.Registry.cs");
            File.WriteAllText(owned, "// <auto-generated>\n// Generated by Athena.WorldCompiler.\n");
            File.WriteAllText(unrelated, "// user file\n");
            File.WriteAllText(registry, "// <auto-generated>\n// Generated by Athena.WorldCompiler.\n");

            Assert.True(MobDataCompiler.IsOwnedGeneratedMobFile(owned, "GeneratedMobs", "Monsters"));
            Assert.False(MobDataCompiler.IsOwnedGeneratedMobFile(unrelated, "GeneratedMobs", "Monsters"));
            Assert.True(MobDataCompiler.IsOwnedGeneratedMobFile(registry, "GeneratedMobs", "Monsters"));
        }
        finally
        {
            directory.Delete(recursive: true);
        }
    }

    [Fact]
    public void FullPinnedMobGeneration_IsByteDeterministic()
    {
        var yaml = File.ReadAllText(Path.Combine(FindRepositoryRoot(), "legacy/rathena/db/re/mob_db.yml"));
        var symbols = MobDataCompiler.CreateGeneratedSymbols(MobDataCompiler.ReadAllMobDefinitions(yaml));

        var firstRegistry = MobDataCompiler.GenerateMobRegistry(symbols, "pin", "legacy/rathena/db/re/mob_db.yml");
        var secondRegistry = MobDataCompiler.GenerateMobRegistry(symbols, "pin", "legacy/rathena/db/re/mob_db.yml");
        Assert.Equal(firstRegistry, secondRegistry);

        foreach (var bucket in symbols.GroupBy(item => item.Mob.Id / 1000))
        {
            var rows = bucket.Select(item => (item.Mob, item.Symbol)).ToArray();
            Assert.Equal(
                MobDataCompiler.GenerateMobDefinitions(rows, "pin", "GeneratedMobs", "legacy/rathena/db/re/mob_db.yml", 0),
                MobDataCompiler.GenerateMobDefinitions(rows, "pin", "GeneratedMobs", "legacy/rathena/db/re/mob_db.yml", 0));
        }
    }

    [Fact]
    public void ReadMobDefinition_UnrecognizedModeName_IsSkippedNotThrown()
    {
        // Pinned source's own "Unknown monster mode %s, skipping" fallback (mob.cpp:5501-5504) -
        // never a thrown error for one bad entry, and the recognized sibling entry still applies.
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Modes:\n      SomeFutureMode: true\n      CanMove: true\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        Assert.Equal(MobDataCompiler.MobModeData.CanMove, mob.Mode);
    }
}
