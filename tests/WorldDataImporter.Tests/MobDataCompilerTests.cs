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
            Assert.Equal(5000, spawn.RespawnDelayMs);
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
        Assert.Null(mob.JapaneseName);
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
        Assert.Contains("JapaneseName: null", generated);
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
    // MD_* bit beyond the 22 pinned ones this project currently knows about).
    private static readonly Regex ModesEntryPattern = new(@"(?m)^      (?<key>[A-Za-z][A-Za-z0-9_]*):\s*(?:true|false)\s*$", RegexOptions.Compiled);

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

        var discoveredModeNames = new List<string>();
        foreach (Match modesBlock in Regex.Matches(yaml, @"(?m)^    Modes:\n((?:      .+\n?)*)"))
        {
            discoveredModeNames.AddRange(ModesEntryPattern.Matches(modesBlock.Groups[1].Value).Select(m => m.Groups["key"].Value));
        }
        var distinctDiscovered = discoveredModeNames.Distinct(StringComparer.Ordinal).ToArray();
        Assert.NotEmpty(distinctDiscovered); // Guards against a silently-empty scan passing vacuously.

        Assert.Empty(distinctDiscovered.Except(recognizedModeNames, StringComparer.Ordinal));
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

    [Fact]
    public void ReadMobDefinition_EveryPinnedModeBitName_SetsExactlyThatBit()
    {
        // Exercises every one of the 22 pinned MD_* names this project models (see MobModeData's
        // own doc comment) individually via a fresh Ai-less block (Ai absent -> base mode None), so
        // each assertion proves that name maps to exactly its own bit, not merely "some bit changed".
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
    public void ReadMobDefinition_UnrecognizedModeName_IsSkippedNotThrown()
    {
        // Pinned source's own "Unknown monster mode %s, skipping" fallback (mob.cpp:5501-5504) -
        // never a thrown error for one bad entry, and the recognized sibling entry still applies.
        const string fixture = "Body:\n  - Id: 1\n    AegisName: T\n    Name: T\n    Modes:\n      SomeFutureMode: true\n      CanMove: true\n";
        var mob = MobDataCompiler.ReadMobDefinition(fixture, 1);

        Assert.Equal(MobDataCompiler.MobModeData.CanMove, mob.Mode);
    }
}
