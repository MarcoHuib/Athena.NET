using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

// DurableId is the row's stable CharInventory.Id - CharServer's own real, never-changing
// primary key. This result deliberately does NOT include a SlotIndex: CharServer has no
// runtime-slot concept at all (see CharacterInventoryItem's own doc comment), so ONLY the
// caller - which owns the current CharacterInventorySnapshot for this session - can correctly
// decide the resulting runtime slot: IsNewRow=true means "assign the first free slot" (via
// CharacterInventorySnapshot.WithNewItem, reusing a hole if one exists); IsNewRow=false means
// "preserve whatever slot this DurableId already occupies" (via
// CharacterInventorySnapshot.WithUpdatedItem). Item is null when Success is false - callers
// must not synchronize the client or treat the drop as granted when Success is false.
public readonly record struct InventoryAddResult(bool Success, uint NewAmount, uint DurableId, bool IsNewRow, InventoryAddResultItem? Item);

// The persisted row's own authoritative fields this add produced/updated (not yet placed into a
// runtime slot - see InventoryAddResult's own doc comment for why SlotIndex isn't here).
public readonly record struct InventoryAddResultItem(int ItemId, uint Amount, uint Equip, bool Identified, byte Refine, byte Favorite, byte Bound);

// Generic "add a stackable item to this character's real persistent
// inventory" capability, following the same success rule already used by
// CharacterHealService/CharacterProgressionService: calculate the proposed
// mutation, persist it through the authoritative CharServer boundary, and
// only report success once that boundary acknowledges it. A failed/stale
// write must never be reported as success (no fake success - see task scope
// rules): callers must not synchronize the client or treat the drop as
// granted when Success is false.
public sealed class CharacterInventorySession(uint accountId, uint charId, ICharacterInventoryPersistence persistence)
{
    public async Task<InventoryAddResult> AddItemAsync(ItemDefinition item, uint amount, CancellationToken cancellationToken)
    {
        if (!item.Stackable && amount > 1) throw new ArgumentException($"Item '{item.AegisName}' is not stackable; amount must be 1.", nameof(amount));
        var result = await persistence.AddStackableItemAsync(accountId, charId, item.Id, amount, cancellationToken);
        if (!result.Success) return new InventoryAddResult(false, 0, 0, false, null);

        var row = new InventoryAddResultItem(item.Id, result.NewAmount, result.Equip, result.Identified, result.Refine, result.Favorite, result.Bound);
        return new InventoryAddResult(true, result.NewAmount, result.DurableId, result.IsNewRow, row);
    }

    // Pinned pc_delitem (pc.cpp:6103-6128) - consumes `amount` from an already-resolved
    // authoritative row (never a runtime slot or an item id: the caller must have already
    // resolved this row's DurableId from its own CharacterInventorySnapshot). See
    // InventoryConsumePersistenceResult's own doc comment for RowDeleted's meaning and
    // CharacterInventorySnapshot's own row-removal helper for how a caller applies a deleted
    // row to its runtime snapshot.
    public Task<InventoryConsumePersistenceResult> ConsumeItemAsync(uint durableId, uint amount, CancellationToken cancellationToken) =>
        persistence.ConsumeItemAsync(accountId, charId, durableId, amount, cancellationToken);
}
