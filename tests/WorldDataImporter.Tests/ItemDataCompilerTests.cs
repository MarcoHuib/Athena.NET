using Athena.WorldCompiler.Generation;

public sealed class ItemDataCompilerTests
{
    private const string ItemDbFixture = """
        Body:
          - Id: 6008
            AegisName: Wood
            Name: Wood
            Type: Etc
            Weight: 10
          - Id: 1201
            AegisName: Knife
            Name: Knife
            Type: Weapon
        """;

    [Fact]
    public void ReadItemDefinition_ResolvesWoodById()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 6008);
        Assert.Equal("Wood", item.AegisName);
        Assert.Equal("Etc", item.Type);
    }

    [Fact]
    public void ReadItemDefinition_EtcItemIsStackable()
    {
        // item_data::isStackable (itemdb.cpp): every Type except
        // Weapon/Armor/PetEgg/PetArmor/ShadowGear is stackable.
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 6008);
        Assert.True(item.Stackable);
    }

    [Fact]
    public void ReadItemDefinition_WeaponIsNotStackable()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1201);
        Assert.False(item.Stackable);
    }

    [Fact]
    public void Generate_IsDeterministic()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 6008);
        var first = ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "Wood", "db/re/item_db_etc.yml", 42);
        var second = ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "Wood", "db/re/item_db_etc.yml", 42);

        Assert.Equal(first, second);
        Assert.Contains("Id: 6008", first);
        Assert.Contains("Stackable: true", first);
    }
}
