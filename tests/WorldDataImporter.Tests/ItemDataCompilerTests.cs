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
            Locations:
              Right_Hand: true
            Attack: 17
            Range: 1
            WeaponLevel: 1
          - Id: 1202
            AegisName: Sword
            Name: Sword
            Type: Weapon
            SubType: 1hSword
            Locations:
              Right_Hand: true
            Attack: 10
          - Id: 501
            AegisName: Red_Potion
            Name: Red Potion
            Type: Healing
          - Id: 4001
            AegisName: Poring_Card
            Name: Poring Card
            Type: Card
          - Id: 1203
            AegisName: Mystery_Weapon
            Name: Mystery Weapon
            Type: Weapon
            SubType: Nonexistent
            Locations:
              Right_Hand: true
            Attack: 5
          - Id: 1301
            AegisName: Novice_Knife
            Name: Novice Knife
            Type: Weapon
            SubType: Dagger
            Locations:
              Right_Hand: true
            AliasName: Knife
            Attack: 17
            WeaponLevel: 1
          - Id: 2301
            AegisName: Cotton_Shirt
            Name: Cotton Shirt
            Type: Armor
            Locations:
              Armor: true
          - Id: 12325
            AegisName: N_Magnifier
            Name: Novice Magnifier
            Type: DelayConsume
          - Id: 23484
            AegisName: Firstaid_Box_5
            Name: First Aid Box (5)
            Type: Usable
          - Id: 22542
            AegisName: Center_Potion_B
            Name: "[Not For Sale] Concentration Potion"
            Type: Usable
            Script: |
              sc_start SC_ASPDPOTION0,1800000,4;
          - Id: 23486
            AegisName: Firstaid_Box_15
            Name: First Aid Box (15)
            Type: Usable
            Script: |
              getitem 11518,10;
              getitem 11614,20;
          - Id: 23487
            AegisName: Mixed_Script_Box
            Name: Mixed Script Box
            Type: Usable
            Script: |
              getitem 11518,10;
              sc_start SC_ASPDPOTION0,1800000,4;
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

    // Pinned item_db_equip.yml header: "Range  Weapon's attack range. (Default: 0)". Read
    // generically for every Type: Weapon row - no mob/item-id-specific range check exists
    // anywhere in ItemDataCompiler (Range is read the same way for every weapon, exactly like
    // Attack/WeaponLevel).
    [Fact]
    public void ReadItemDefinition_Knife1201_RangeIsOneFromPinnedSource()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1201);
        Assert.Equal(1, item.Range);
    }

    [Fact]
    public void ReadItemDefinition_WeaponWithoutExplicitRange_DefaultsToZero()
    {
        // Sword (1202) has no Range column in the fixture - pinned default is 0.
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1202);
        Assert.Equal(0, item.Range);
    }

    [Fact]
    public void ReadItemDefinition_NonWeaponHasNullRange()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 6008);
        Assert.Null(item.Range);
    }

    [Fact]
    public void Generate_Weapon_EmitsRangeField()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1201);
        var generated = ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "Knife", "db/re/item_db_equip.yml", 7);

        Assert.Contains("Range: 1", generated);
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
        Assert.Contains("ClientViewId: 1201", generated);
        Assert.Contains("EquipLocation: 0x000002", generated);
    }

    [Fact]
    public void ReadItemDefinition_WeaponWithoutAliasName_ClientViewIdEqualsOwnId()
    {
        // Pinned map_session_data::update_look / client_nameid() (pc.cpp:623-647,
        // clif.cpp:144-151): falls back to the item's own nameid when it has no
        // AliasName-resolved view_id. Verified against stock-iRO capture
        // (kill-poring-heal-jobup, frame 210): Knife 1201's LOOK_WEAPON wire value is 1201.
        var knife = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1201);
        Assert.Equal(1201, knife.ClientViewId);
    }

    [Fact]
    public void ReadItemDefinition_WeaponWithAliasName_ClientViewIdResolvesToAliasedItemId()
    {
        var novice = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1301);
        Assert.Equal(1201, novice.ClientViewId); // AliasName: Knife -> Id 1201
    }

    [Fact]
    public void ReadItemDefinition_NonWeaponAlsoGetsClientViewId()
    {
        var wood = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 6008);
        Assert.Equal(6008, wood.ClientViewId); // no AliasName -> own id, applies to every item type
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
    public void ReadItemDefinition_WeaponLocations_ResolvesToRightHandBitmask()
    {
        // Pinned EQP_HAND_R = 0x000002 (mmo.hpp:340).
        var knife = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 1201);
        Assert.Equal(0x000002u, knife.EquipLocation);
    }

    [Fact]
    public void ReadItemDefinition_ArmorLocations_ResolvesToArmorBitmask()
    {
        // Pinned EQP_ARMOR = 0x000010 (mmo.hpp:342).
        var shirt = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 2301);
        Assert.Equal("Armor", shirt.Type);
        Assert.Equal(0x000010u, shirt.EquipLocation);
        Assert.Null(shirt.Attack);
        Assert.Null(shirt.WeaponType);
    }

    [Fact]
    public void Generate_Armor_EmitsArmorItemDefinitionWithEquipLocation()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 2301);
        var generated = ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "CottonShirt", "db/re/item_db_equip.yml", 9);

        Assert.Contains("ArmorItemDefinition", generated);
        Assert.Contains("EquipLocation: 0x000010", generated);
        Assert.DoesNotContain("Attack:", generated);
    }

    [Fact]
    public void ReadItemDefinition_UsableItemHasNoWeaponOrEquipFields()
    {
        var firstAidBox = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 23484);
        Assert.Equal("Usable", firstAidBox.Type);
        Assert.Null(firstAidBox.EquipLocation);
        Assert.Null(firstAidBox.Attack);
    }

    [Fact]
    public void Generate_Usable_EmitsUsableItemDefinition()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 23484);
        var generated = ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "FirstAidBox", "db/re/item_db_usable.yml", 11);

        Assert.Contains("UsableItemDefinition", generated);
    }

    [Fact]
    public void Generate_UnsupportedType_ThrowsRatherThanCollapsingIntoEtc()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 4001);
        Assert.Throws<NotSupportedException>(() => ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "PoringCard", "db/re/item_db_card.yml", 3));
    }

    [Fact]
    public void Generate_Healing_EmitsHealingItemDefinition_DistinctFromUsable()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 501);
        var generated = ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "RedPotion", "db/re/item_db_usable.yml", 3);

        Assert.Contains("HealingItemDefinition", generated);
        Assert.DoesNotContain("UsableItemDefinition", generated);
    }

    [Fact]
    public void Generate_DelayConsume_EmitsDelayConsumeItemDefinition_DistinctFromUsable()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 12325);
        var generated = ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "NoviceMagnifier", "db/re/item_db_usable.yml", 3);

        Assert.Contains("DelayConsumeItemDefinition", generated);
        Assert.DoesNotContain("UsableItemDefinition", generated);
    }

    [Fact]
    public void ReadItemDefinition_UsableWithNoScript_HasNullGrants()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 23484);
        Assert.Null(item.Grants);
    }

    [Fact]
    public void ReadItemDefinition_UsableWithGetItemScript_ParsesExactGrantList()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 23486);

        Assert.NotNull(item.Grants);
        Assert.Equal(2, item.Grants!.Count);
        Assert.Equal((11518, 10u), item.Grants[0]);
        Assert.Equal((11614, 20u), item.Grants[1]);
    }

    [Fact]
    public void Generate_UsableWithGrants_EmitsGrantsArray()
    {
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 23486);
        var generated = ItemDataCompiler.Generate(item, "abc123", "AcademyItems", "FirstAidBox", "db/re/item_db_usable.yml", 11);

        Assert.Contains("Grants: [new ItemGrantDefinition(11518, 10), new ItemGrantDefinition(11614, 20)]", generated);
    }

    [Fact]
    public void ReadItemDefinition_UsableWithNonGetItemScript_HasNullGrants_NotAnError()
    {
        // A Usable item whose script is some other, currently-unmodeled effect (not a getitem
        // container) must not throw - it simply has no Grants, matching how Healing's itemheal
        // effect is left unmodeled without failing generation.
        var item = ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 22542);
        Assert.Null(item.Grants);
    }

    [Fact]
    public void ReadItemDefinition_UsableWithMixedGetItemAndOtherScript_ThrowsRatherThanPartiallyParsing()
    {
        // Once a script commits to looking like a container (first statement IS getitem), every
        // remaining statement must also be getitem - a trailing non-getitem statement must fail
        // generation loudly rather than silently representing only the getitem prefix.
        Assert.Throws<NotSupportedException>(() => ItemDataCompiler.ReadItemDefinition(ItemDbFixture, 23487));
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
