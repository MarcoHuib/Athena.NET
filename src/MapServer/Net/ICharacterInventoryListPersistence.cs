namespace Athena.Net.MapServer.Net;

// One authoritative CharInventory row, as needed by the client-facing inventory/equip-list
// packets (0x0B09/0x0B39) and by CharacterEquipmentSnapshot's right-hand derivation. Fields
// mirror what clif_item_normal/clif_item_equip actually read from the persisted `item` struct
// (clif.cpp:2932-2960, 2987-3011) - Cards/Options are left unmodeled until a traced item needs
// them (the tutorial starter rows carry none), matching the shallow-extension convention used
// throughout this domain model.
//
// SlotIndex is the stable server-side array position (0-based) this row would occupy in a real
// rAthena `sd->inventory.u.items_inventory[]` load pass - see CharacterInventorySession's own
// SlotIndex doc comment for the identical convention already used by the 0x0B41 pickup path.
// client_index() (clif.cpp:122) adds +2 to this at wire-serialization time; callers must never
// apply that offset themselves before this point.
public sealed record CharacterInventoryItem(
    uint SlotIndex,
    int ItemId,
    uint Amount,
    uint Equip,
    bool Identified,
    byte Refine,
    byte Favorite,
    byte Bound);

// Every persisted CharInventory row for one character, in stable load order (matches
// CharacterInventoryItem.SlotIndex). This is the ONE authoritative read - both the right-hand
// weapon-appearance/combat path (CharacterEquipmentSnapshot, derived from this) and the full
// client inventory/equip-list projection (0x0B09/0x0B39) originate from the same fetch, never
// two independent CharServer reads of the same persisted state.
public sealed record CharacterInventorySnapshot(IReadOnlyList<CharacterInventoryItem> Items);

// Pinned EQP_HAND_R = 0x000002 (mmo.hpp:340) - the only equip slot the weapon-appearance/combat
// path needs. RightHandItemId == null means "confirmed no right-hand item equipped" - derived
// from a successful CharacterInventorySnapshot read, so never ambiguous with a failed read (see
// CharacterInventoryReadResult).
public sealed record CharacterEquipmentSnapshot(int? RightHandItemId, byte RightHandRefine)
{
    internal static CharacterEquipmentSnapshot FromInventory(CharacterInventorySnapshot inventory)
    {
        // Pinned mmo.hpp:322: "equip; // location(s) where item is equipped (using enum
        // equip_pos for bitmasking)" - always tested via `equip & EQP_HAND_R`, never exact
        // equality (pc.cpp:1582/12173/12427), since a row's Equip can carry multiple
        // simultaneous position bits.
        var rightHand = inventory.Items.FirstOrDefault(item => (item.Equip & 0x000002) != 0);
        return rightHand is null
            ? new CharacterEquipmentSnapshot(null, 0)
            : new CharacterEquipmentSnapshot(rightHand.ItemId, rightHand.Refine);
    }
}

// A failed/unavailable inventory read (DB error, disconnected CharServer, malformed response)
// must never be represented the same way as "successfully confirmed empty/unarmed" -
// collapsing both into a nullable snapshot would let future combat/appearance code silently
// treat an unknown state as empty. Succeeded=false always carries Snapshot=null; Succeeded=true
// always carries a non-null Snapshot (Items may itself be empty - that is the authoritative
// "no inventory rows" case).
public readonly record struct CharacterInventoryReadResult(bool Succeeded, CharacterInventorySnapshot? Snapshot)
{
    public static CharacterInventoryReadResult Success(CharacterInventorySnapshot snapshot) => new(true, snapshot);
    public static CharacterInventoryReadResult Failed() => new(false, null);
}

public interface ICharacterInventoryListPersistence
{
    Task<CharacterInventoryReadResult> GetInventoryAsync(uint accountId, uint characterId, CancellationToken cancellationToken);

    // Persists a single CharInventory row's Equip bitmask by its stable SlotIndex - CharServer
    // remains the durable owner of CharInventory.Equip (see CharacterEquipmentMutationService).
    // Returns false on any failure (row not found for this character, DB error, disconnected
    // CharServer) - callers must never assume success and must never report success to the
    // client before this returns true.
    Task<bool> SetItemEquipAsync(uint accountId, uint characterId, uint slotIndex, uint equip, CancellationToken cancellationToken);
}
