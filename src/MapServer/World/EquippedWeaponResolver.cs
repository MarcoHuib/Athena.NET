using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

// Resolves an authoritative CharacterEquipmentSnapshot's right-hand item id against the
// generated item registry into a WeaponItemDefinition (and its source-backed WeaponType) -
// the one path any weapon-aware combat/appearance code must go through, so there is exactly
// one place that turns "an item id is equipped" into "this is the equipped weapon."
public enum EquippedWeaponResolution
{
    // RightHandItemId is null - a real, confirmed empty right hand (see
    // CharacterEquipmentReadResult's own doc comment: a successful read always distinguishes
    // this from a failed/unavailable read upstream). The only normal unarmed state.
    Unarmed,

    // RightHandItemId resolved to a WeaponItemDefinition.
    Weapon,

    // RightHandItemId does not exist in the generated item registry at all. Never silently
    // treated as unarmed - an equipped item id that isn't in the pinned item_db is a data/
    // generation gap, not a legitimate empty hand.
    UnknownItem,

    // RightHandItemId resolved to a registered item that is NOT a WeaponItemDefinition (e.g.
    // an EtcItemDefinition). Pinned rAthena's own EQP_HAND_R semantics only ever put weapons
    // (IT_WEAPON) in the right hand (pc_equipitem gates weapon-only fields behind
    // `pos & EQP_HAND_R`, and item_db's Equip location groups for non-weapon Etc/Usable types
    // never include EQP_HAND_R) - so a non-weapon item resolving into this slot is treated as
    // an invariant violation, not a legitimate case to unwrap.
    NonWeaponInWeaponSlot,
}

public readonly record struct EquippedWeaponResult(EquippedWeaponResolution Resolution, WeaponItemDefinition? Weapon)
{
    public static EquippedWeaponResult Unarmed() => new(EquippedWeaponResolution.Unarmed, null);
    public static EquippedWeaponResult ForWeapon(WeaponItemDefinition weapon) => new(EquippedWeaponResolution.Weapon, weapon);
    public static EquippedWeaponResult UnknownItem() => new(EquippedWeaponResolution.UnknownItem, null);
    public static EquippedWeaponResult NonWeaponInWeaponSlot() => new(EquippedWeaponResolution.NonWeaponInWeaponSlot, null);
}

public static class EquippedWeaponResolver
{
    public static EquippedWeaponResult Resolve(CharacterEquipmentSnapshot equipment, IReadOnlyDictionary<int, ItemDefinition> items)
    {
        if (equipment.RightHandItemId is not { } itemId) return EquippedWeaponResult.Unarmed();

        if (!items.TryGetValue(itemId, out var item)) return EquippedWeaponResult.UnknownItem();

        if (item is not WeaponItemDefinition weapon) return EquippedWeaponResult.NonWeaponInWeaponSlot();

        return EquippedWeaponResult.ForWeapon(weapon);
    }
}
