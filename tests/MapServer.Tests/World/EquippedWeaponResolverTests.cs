using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class EquippedWeaponResolverTests
{
    private static readonly WeaponItemDefinition Knife = new(
        1201, "Knife", "Knife", Stackable: false, ClientViewId: 1201, Attack: 17, WeaponLevel: 1, WeaponType.Dagger, EquipLocation: 0x000002,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    private static readonly EtcItemDefinition Wood = new(
        6008, "Wood", "Wood", Stackable: true, ClientViewId: 6008,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_etc.yml", 1));

    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items =
        new Dictionary<int, ItemDefinition> { [Knife.Id] = Knife, [Wood.Id] = Wood };

    [Fact]
    public void Resolve_NoRightHandItem_ReturnsUnarmed()
    {
        var result = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: null, RightHandRefine: 0), Items);

        Assert.Equal(EquippedWeaponResolution.Unarmed, result.Resolution);
        Assert.Null(result.Weapon);
    }

    [Fact]
    public void Resolve_KnifeEquipped_ReturnsWeaponWithDaggerType()
    {
        var result = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: 1201, RightHandRefine: 3), Items);

        Assert.Equal(EquippedWeaponResolution.Weapon, result.Resolution);
        Assert.Equal(Knife, result.Weapon);
        Assert.Equal(WeaponType.Dagger, result.Weapon!.WeaponType);
    }

    [Fact]
    public void Resolve_UnregisteredItemId_ReturnsUnknownItem_NotUnarmed()
    {
        var result = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: 999999, RightHandRefine: 0), Items);

        Assert.Equal(EquippedWeaponResolution.UnknownItem, result.Resolution);
        Assert.Null(result.Weapon);
    }

    [Fact]
    public void Resolve_NonWeaponItemInRightHand_ReturnsNonWeaponInWeaponSlot_NotUnarmed()
    {
        var result = EquippedWeaponResolver.Resolve(new CharacterEquipmentSnapshot(RightHandItemId: 6008, RightHandRefine: 0), Items);

        Assert.Equal(EquippedWeaponResolution.NonWeaponInWeaponSlot, result.Resolution);
        Assert.Null(result.Weapon);
    }
}
