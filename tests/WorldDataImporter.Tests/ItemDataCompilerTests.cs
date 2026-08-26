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
            Attack: 17
            WeaponLevel: 1
          - Id: 1202
            AegisName: Sword
            Name: Sword
            Type: Weapon
            Attack: 10
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
    public void ReadItemDefinition_WeaponReadsAttackAndWeaponLevel()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1201);
        Assert.True(item.Stackable == false);
        Assert.Equal(17, item.Attack);
        Assert.Equal(1, item.WeaponLevel);
    }

    [Fact]
    public void ReadItemDefinition_WeaponWithoutExplicitWeaponLevel_DefaultsToOne()
    {
        // Pinned item_db_equip.yml header: "WeaponLevel  Weapon level. (Default: 1 for Weapons)".
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1202);
        Assert.Equal(10, item.Attack);
        Assert.Equal(1, item.WeaponLevel);
    }

    [Fact]
    public void ReadItemDefinition_NonWeaponHasNullAttackAndWeaponLevel()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 6008);
        Assert.Null(item.Attack);
        Assert.Null(item.WeaponLevel);
    }

    [Fact]
    public void Generate_Weapon_EmitsWeaponItemDefinitionWithAttackFields()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1201);
        var generated = ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "Knife", "db/re/item_db_equip.yml", 7);

        Assert.Contains("WeaponItemDefinition", generated);
        Assert.Contains("Attack: 17", generated);
        Assert.Contains("WeaponLevel: 1", generated);
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
        Assert.Contains("EtcItemDefinition", first);
    }
}
