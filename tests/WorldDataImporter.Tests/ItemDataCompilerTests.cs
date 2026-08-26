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
            SubType: Dagger
            Attack: 17
            WeaponLevel: 1
          - Id: 1202
            AegisName: Sword
            Name: Sword
            Type: Weapon
            SubType: 1hSword
            Attack: 10
          - Id: 501
            AegisName: Red_Potion
            Name: Red Potion
            Type: Healing
          - Id: 1203
            AegisName: Mystery_Weapon
            Name: Mystery Weapon
            Type: Weapon
            SubType: Nonexistent
            Attack: 5
          - Id: 1301
            AegisName: Novice_Knife
            Name: Novice Knife
            Type: Weapon
            SubType: Dagger
            AliasName: Knife
            Attack: 17
            WeaponLevel: 1
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
        Assert.Contains("WeaponType: WeaponType.Dagger", generated);
        Assert.Contains("WeaponViewId: 1201", generated);
    }

    [Fact]
    public void ReadItemDefinition_WeaponWithoutAliasName_WeaponViewIdEqualsOwnId()
    {
        // Pinned map_session_data::update_look (pc.cpp:623-647): falls back to the item's own
        // nameid when it has no AliasName-resolved view_id. Verified against stock-iRO capture
        // (kill-poring-heal-jobup, frame 210): Knife 1201's LOOK_WEAPON wire value is 1201.
        var knife = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1201);
        Assert.Equal(1201, knife.WeaponViewId);
    }

    [Fact]
    public void ReadItemDefinition_WeaponWithAliasName_WeaponViewIdResolvesToAliasedItemId()
    {
        var novice = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1301);
        Assert.Equal(1201, novice.WeaponViewId); // AliasName: Knife -> Id 1201
    }

    [Fact]
    public void ReadItemDefinition_WeaponSubTypeMapsToStronglyTypedWeaponType()
    {
        // Pinned enum weapon_type (map/pc.hpp:959): W_DAGGER = 1, W_1HSWORD = 2.
        var knife = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1201);
        Assert.Equal(WeaponType.Dagger, knife.WeaponType);
        Assert.Equal((byte)1, (byte)knife.WeaponType!.Value);

        var sword = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1202);
        Assert.Equal(WeaponType.OneHandSword, sword.WeaponType);
        Assert.Equal((byte)2, (byte)sword.WeaponType!.Value);
    }

    [Fact]
    public void ReadItemDefinition_UnrecognizedSubType_ThrowsRatherThanSilentlyDefaulting()
    {
        Assert.Throws<NotSupportedException>(() => ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1203));
    }

    [Fact]
    public void Generate_UnsupportedType_ThrowsRatherThanCollapsingIntoEtc()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 501);
        Assert.Throws<NotSupportedException>(() => ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "RedPotion", "db/re/item_db_usable.yml", 3));
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
