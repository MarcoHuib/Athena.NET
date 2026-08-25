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
    }
}
