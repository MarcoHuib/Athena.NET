namespace Athena.Net.MapServer.Net;

// Authoritative inventory mutation boundary. MapServer never touches
// CharInventory rows directly (no EF Core/MSSQL access from MapServer, per
// the same architecture principle CharacterGameplayStateSession and
// ICharacterQuestPersistence already follow: CharServer is the durable
// owner). AddStackableItemAsync finds-or-creates the character's stack for
// `itemId` and returns the new authoritative total amount on success, plus
// the SERVER-side inventory array position (0-based, matching pinned
// rAthena's sd->inventory.u.items_inventory[n] - CharServer derives it from
// the ONE authoritative stable row ordering shared with the inventory-list
// read and equip-update handlers - equipped and unequipped rows share that
// same namespace; neither Athena's schema nor real rAthena's own `inventory`
// SQL table persists a slot column at all). Callers building a client-facing
// packet must still apply the pinned client_index() transform (n + 2,
// clif.cpp:122-124) themselves.
//
// Equip/Identified/Refine/Favorite/Bound are the persisted row's own
// authoritative field values (CharServer is the only side that knows them -
// e.g. Identify=1 is set at insert time) so a caller can reconstruct the exact
// CharacterInventoryItem this add produced/updated without inventing or
// assuming any field.
public interface ICharacterInventoryPersistence
{
    Task<InventoryAddPersistenceResult> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken);
}

public readonly record struct InventoryAddPersistenceResult(
    bool Success, uint NewAmount, uint SlotIndex,
    uint Equip, bool Identified, byte Refine, byte Favorite, byte Bound)
{
    public static InventoryAddPersistenceResult Failed() => new(false, 0, 0, 0, false, 0, 0, 0);
}
