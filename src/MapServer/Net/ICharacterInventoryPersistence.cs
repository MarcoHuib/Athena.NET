namespace Athena.Net.MapServer.Net;

// Authoritative inventory mutation boundary. MapServer never touches
// CharInventory rows directly (no EF Core/MSSQL access from MapServer, per
// the same architecture principle CharacterGameplayStateSession and
// ICharacterQuestPersistence already follow: CharServer is the durable
// owner). AddStackableItemAsync finds-or-creates the character's stack for
// `itemId` and returns the new authoritative total amount on success, plus
// the row's stable DurableId (CharInventory.Id) - CharServer has no runtime
// inventory-slot concept at all and never computes or returns one; MapServer
// is solely responsible for assigning/maintaining runtime SlotIndex for the
// active session (see CharacterInventoryItem's own doc comment). Callers
// building a client-facing packet must still resolve the row's CURRENT
// runtime SlotIndex (via CharacterInventorySnapshot) and apply the pinned
// client_index() transform (n + 2, clif.cpp:122-124) themselves.
//
// Equip/Identified/Refine/Favorite/Bound are the persisted row's own
// authoritative field values (CharServer is the only side that knows them -
// e.g. Identify=1 is set at insert time) so a caller can reconstruct the exact
// CharacterInventoryItem this add produced/updated without inventing or
// assuming any field.
public interface ICharacterInventoryPersistence
{
    Task<InventoryAddPersistenceResult> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken);

    // Consumes `amount` from the row identified by its stable DurableId (pinned pc_delitem,
    // pc.cpp:6103-6128) - targets an already-resolved row directly, never a runtime slot or an
    // item id (the caller already resolved DurableId from its own authoritative
    // CharacterInventorySnapshot before calling this). RowDeleted mirrors CharServer's own
    // row-removal decision when the row's amount reaches zero - see
    // InventoryConsumePersistenceResult's own doc comment.
    Task<InventoryConsumePersistenceResult> ConsumeItemAsync(uint accountId, uint charId, uint durableId, uint amount, CancellationToken cancellationToken);
}

// IsNewRow tells the caller whether DurableId is a brand-new row (the caller must assign it a
// fresh runtime slot - reusing a hole if one exists, via CharacterInventorySnapshot.
// WithNewItem) or an existing stack whose amount changed (the caller must preserve that
// DurableId's CURRENT runtime slot unchanged, via CharacterInventorySnapshot.WithUpdatedItem).
public readonly record struct InventoryAddPersistenceResult(
    bool Success, uint NewAmount, uint DurableId,
    uint Equip, bool Identified, byte Refine, byte Favorite, byte Bound, bool IsNewRow)
{
    public static InventoryAddPersistenceResult Failed() => new(false, 0, 0, 0, false, 0, 0, 0, false);
}

// RowDeleted=true means the consumed row's Amount reached zero and CharServer removed it from
// durable storage entirely - the caller MUST remove that DurableId's row from its own runtime
// CharacterInventorySnapshot too (leaving a hole at its former SlotIndex, never compacting -
// see CharacterInventorySnapshot.WithoutDurableId), never leave a stale zero-amount row behind.
public readonly record struct InventoryConsumePersistenceResult(bool Success, uint NewAmount, bool RowDeleted)
{
    public static InventoryConsumePersistenceResult Failed() => new(false, 0, false);
}
