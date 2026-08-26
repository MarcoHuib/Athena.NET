using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

// Pinned enum equip_index (pc.hpp:73-96) - the EQI_* slot array pc_equipitem/pc_unequipitem
// use to detect slot conflicts and swap out an already-equipped item. Only the slots this
// domain model can currently produce items for are modeled (EQI_HEAD_LOW/MID/TOP, EQI_ARMOR,
// EQI_HAND_L/R, EQI_SHOES, EQI_GARMENT, EQI_ACC_L/R, EQI_AMMO) - costume/shadow slots are
// omitted, matching ItemDefinition's own shallow-extension convention; extend only when a
// traced item needs them.
public enum EquipSlot
{
    AccLeft,
    AccRight,
    Shoes,
    Garment,
    HeadLow,
    HeadMid,
    HeadTop,
    Armor,
    HandLeft,
    HandRight,
    Ammo,
}

public static class EquipSlots
{
    // Pinned equip_bitmask[EQI_MAX] (pc.cpp:693-709), restricted to the slots EquipSlot models.
    public static readonly IReadOnlyDictionary<EquipSlot, uint> Bitmask = new Dictionary<EquipSlot, uint>
    {
        [EquipSlot.AccLeft] = 0x000080,
        [EquipSlot.AccRight] = 0x000008,
        [EquipSlot.Shoes] = 0x000040,
        [EquipSlot.Garment] = 0x000004,
        [EquipSlot.HeadLow] = 0x000001,
        [EquipSlot.HeadMid] = 0x000200,
        [EquipSlot.HeadTop] = 0x000100,
        [EquipSlot.Armor] = 0x000010,
        [EquipSlot.HandLeft] = 0x000020,
        [EquipSlot.HandRight] = 0x000002,
        [EquipSlot.Ammo] = 0x008000,
    };

    // Pinned EQP_ARMS = EQP_HAND_R|EQP_HAND_L (pc.hpp:1132).
    public const uint Arms = 0x000002 | 0x000020;
}

public enum EquipMutationResult
{
    // Pinned enum clif_equipitemack_flag, PACKETVER_RE_NUM >= 20121107 branch (clif.hpp:522-533):
    // OK=0, FAILLEVEL=1, FAIL=2. This slice does not model item level/sex/broken/restricted-map
    // eligibility (ItemDefinition has no Elv/Sex/Attribute fields yet) - FailLevel is therefore
    // never produced by this service today; it exists so a future level-gate implementation has
    // the correct ack value to report, rather than needing a new enum member later.
    Success,
    FailLevel,
    Fail,
}

public readonly record struct EquipOutcome(EquipMutationResult Result, uint WearLocation);
public readonly record struct UnequipOutcome(bool Success, uint WearLocation);

// Runtime equip/unequip mutation - the pinned PACKETVER 20220406 equivalent of
// pc_equipitem/pc_unequipitem (pc.cpp:12015-12240, 12398-12500), scoped to the structural
// slot-conflict/EQP_ARMS logic those functions implement. Explicitly NOT modeled (see each
// method's own doc comment): EquipLevelMin/Max, Sex, broken-item, and restricted-map
// eligibility checks - ItemDefinition has no fields for these yet, so this service can only
// validate EquipLocation and already-equipped/slot-conflict state, never silently claim to
// enforce the deferred checks.
//
// CharServer remains the durable owner of CharInventory.Equip - every mutation persists via
// ICharacterInventoryListPersistence.SetItemEquipAsync BEFORE this service reports success;
// MapServer's own CharacterInventorySnapshot/CharacterEquipmentSnapshot are only rebuilt after
// a confirmed persisted write, never mutated in place speculatively.
public sealed class CharacterEquipmentMutationService(
    uint accountId,
    uint characterId,
    ICharacterInventoryListPersistence persistence)
{
    // Pinned pc_equipitem (pc.cpp:12015-12172), restricted to this service's modeled checks:
    //   1. already-equipped items.equip != 0 -> ITEM_EQUIP_ACK_FAIL (pc.cpp:12063-12070)
    //   2. requested position must intersect the item's EquipLocation -> ITEM_EQUIP_ACK_FAIL
    //   3. EQP_ARMS dual-wield: if the item's own EquipLocation is exactly EQP_HAND_R and the
    //      caller requested EQP_ARMS (both hands), pick whichever hand is free, preferring
    //      HAND_L when both are free (pc.cpp:12108-12115, RENEWAL branch)
    //   4. slot conflict: any currently-equipped item occupying one of the resolved position's
    //      EQI slots is unequipped first (pc.cpp:12147-12157)
    // Returns null if the requested slotIndex is not a real, currently-unequipped row in
    // `inventory` resolving to a WeaponItemDefinition/ArmorItemDefinition (via
    // IEquippableItemDefinition) - the caller (MapClientSession) turns that into
    // ITEM_EQUIP_ACK_FAIL without a persistence round-trip, matching pc_equipitem's own
    // `if (!(id = sd->inventory_data[n])) return false;` early-out.
    public async Task<(EquipOutcome? Outcome, CharacterInventorySnapshot? Updated)> EquipAsync(
        CharacterInventorySnapshot inventory,
        uint slotIndex,
        uint requestedPosition,
        IReadOnlyDictionary<int, ItemDefinition> items,
        CancellationToken cancellationToken)
    {
        var target = inventory.Items.FirstOrDefault(i => i.SlotIndex == slotIndex);
        if (target is null) return (null, null);
        if (!items.TryGetValue(target.ItemId, out var definition) || definition is not IEquippableItemDefinition equippable)
            return (new EquipOutcome(EquipMutationResult.Fail, 0), null);

        if (target.Equip != 0)
            return (new EquipOutcome(EquipMutationResult.Fail, 0), null);

        var pos = equippable.EquipLocation;
        if ((pos & requestedPosition) == 0)
            return (new EquipOutcome(EquipMutationResult.Fail, 0), null);

        if (pos == EquipSlots.Arms && equippable.EquipLocation == EquipSlots.Bitmask[EquipSlot.HandRight])
        {
            // Dual-wield-capable weapon (pinned pc.cpp:12108-12115): item's own possible
            // location is exactly EQP_HAND_R, but EQP_ARMS is offered as an alternative when
            // the caller requests both hands.
            pos = requestedPosition & EquipSlots.Arms;
            if (pos == EquipSlots.Arms)
            {
                var rightTaken = inventory.Items.Any(i => (i.Equip & EquipSlots.Bitmask[EquipSlot.HandRight]) != 0);
                var leftTaken = inventory.Items.Any(i => (i.Equip & EquipSlots.Bitmask[EquipSlot.HandLeft]) != 0);
                pos = (rightTaken && !leftTaken) ? EquipSlots.Bitmask[EquipSlot.HandLeft] : EquipSlots.Bitmask[EquipSlot.HandRight];
            }
        }

        // Slot conflict: unequip whatever currently occupies any EQI slot `pos` maps to
        // (pinned pc.cpp:12147-12157 - `if(sd->equip_index[i] >= 0) pc_unequipitem(...)`).
        var working = inventory;
        foreach (var slot in EquipSlots.Bitmask)
        {
            if ((pos & slot.Value) == 0) continue;
            var occupying = working.Items.FirstOrDefault(i => i.SlotIndex != slotIndex && (i.Equip & slot.Value) != 0);
            if (occupying is null) continue;

            var unequipped = await persistence.SetItemEquipAsync(accountId, characterId, occupying.SlotIndex, 0, cancellationToken);
            if (!unequipped) return (new EquipOutcome(EquipMutationResult.Fail, 0), null);
            working = ReplaceItem(working, occupying with { Equip = 0 });
        }

        var persisted = await persistence.SetItemEquipAsync(accountId, characterId, slotIndex, pos, cancellationToken);
        if (!persisted) return (new EquipOutcome(EquipMutationResult.Fail, 0), null);

        var updated = ReplaceItem(working, target with { Equip = pos });
        return (new EquipOutcome(EquipMutationResult.Success, pos), updated);
    }

    // Pinned pc_unequipitem (pc.cpp:12398-12495), restricted to this service's modeled checks:
    //   - not currently equipped (items.equip == 0) -> failure (pc.cpp:12407-12410)
    // Ammo-unequip-on-weapon-change and status-effect-cancel side effects
    // (SC_DANCING/SC_EDP/SC_SHIELDSPELL) are out of scope - this slice has no status-effect
    // integration for equipment changes yet.
    public async Task<(UnequipOutcome? Outcome, CharacterInventorySnapshot? Updated)> UnequipAsync(
        CharacterInventorySnapshot inventory,
        uint slotIndex,
        CancellationToken cancellationToken)
    {
        var target = inventory.Items.FirstOrDefault(i => i.SlotIndex == slotIndex);
        if (target is null || target.Equip == 0)
            return (new UnequipOutcome(false, 0), null);

        var pos = target.Equip;
        var persisted = await persistence.SetItemEquipAsync(accountId, characterId, slotIndex, 0, cancellationToken);
        if (!persisted) return (new UnequipOutcome(false, 0), null);

        var updated = ReplaceItem(inventory, target with { Equip = 0 });
        return (new UnequipOutcome(true, pos), updated);
    }

    private static CharacterInventorySnapshot ReplaceItem(CharacterInventorySnapshot inventory, CharacterInventoryItem replacement)
    {
        var items = inventory.Items.Select(i => i.SlotIndex == replacement.SlotIndex ? replacement : i).ToList();
        return new CharacterInventorySnapshot(items);
    }
}
