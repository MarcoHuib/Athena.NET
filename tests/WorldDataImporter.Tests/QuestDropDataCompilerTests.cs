using Athena.WorldCompiler.Generation;

public sealed class QuestDropDataCompilerTests
{
    private const string QuestDbFixture = """
        Body:
          - Id: 21001
            Title: Escape the Wreck
          - Id: 21002
            Title: The first battle
          - Id: 21008
            Title: The first battle
            Drops:
              - Mob: G_PORING
                Item: Wood
                Rate: 10000
          - Id: 21016
            Title: Undead War-break
            TimeLimit: 4h
        """;

    private const string MobDbFixture = """
        Body:
          - Id: 1002
            AegisName: PORING
            Name: Poring
          - Id: 2401
            AegisName: G_PORING
            Name: Poring
        """;

    private const string ItemDbFixture = """
        Body:
          - Id: 6008
            AegisName: Wood
            Name: Wood
            Type: Etc
        """;

    [Fact]
    public void ReadSingleDrop_ResolvesMobAndItemIdsFromAegisNames()
    {
        var drop = QuestDropDataCompiler.ReadSingleDrop(QuestDbFixture, 21008, MobDbFixture, ItemDbFixture);

        Assert.Equal(21008u, drop.QuestId);
        Assert.Equal(2401, drop.MobId); // Not 1002 - resolved via AegisName "G_PORING", not the smaller/first numeric id.
        Assert.Equal(6008, drop.ItemId);
    }

    [Fact]
    public void ReadSingleDrop_CountDefaultsToOne_WhenPinnedYamlOmitsIt()
    {
        // quest.cpp QuestDatabase::parseBodyNode: "if (!targetExists) target->count = 1;"
        var drop = QuestDropDataCompiler.ReadSingleDrop(QuestDbFixture, 21008, MobDbFixture, ItemDbFixture);
        Assert.Equal(1, drop.Count);
    }

    [Fact]
    public void ReadSingleDrop_RateIsPreservedOutOf10000()
    {
        var drop = QuestDropDataCompiler.ReadSingleDrop(QuestDbFixture, 21008, MobDbFixture, ItemDbFixture);
        Assert.Equal(10000, drop.Rate);
    }

    [Fact]
    public void ReadSingleDrop_QuestWithoutDropsBlock_Throws()
    {
        Assert.Throws<ArgumentException>(() => QuestDropDataCompiler.ReadSingleDrop(QuestDbFixture, 21001, MobDbFixture, ItemDbFixture));
    }

    [Fact]
    public void ReadSingleDrop_QuestWithTargetsBlock_ThrowsInsteadOfSilentlyIgnoringKillCount()
    {
        const string withTargets = """
            Body:
              - Id: 21099
                Title: Fake kill quest
                Targets:
                  - Mob: G_PORING
                    Count: 2
                Drops:
                  - Mob: G_PORING
                    Item: Wood
                    Rate: 10000
            """;
        Assert.Throws<ArgumentException>(() => QuestDropDataCompiler.ReadSingleDrop(withTargets, 21099, MobDbFixture, ItemDbFixture));
    }

    [Fact]
    public void Generate_IsDeterministic_AndDoesNotEncodeKillCounter()
    {
        var drop = QuestDropDataCompiler.ReadSingleDrop(QuestDbFixture, 21008, MobDbFixture, ItemDbFixture);
        var first = QuestDropDataCompiler.Generate(drop, "abc123", "db/re/quest_db.yml", 42);
        var second = QuestDropDataCompiler.Generate(drop, "abc123", "db/re/quest_db.yml", 42);

        Assert.Equal(first, second);
        Assert.Contains("QuestId: 21008", first);
        Assert.Contains("MobId: 2401", first);
        Assert.Contains("ItemId: 6008", first);
        Assert.DoesNotContain("Count1", first);
        Assert.DoesNotContain("QuestTargetRule", first); // No kill-count-objective type generated.
    }
}
