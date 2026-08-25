using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

// SlotIndex is the SERVER-side inventory array position (0-based), matching
// pinned rAthena's sd->inventory.u.items_inventory[n] - the caller building a
// wire packet must apply the pinned client_index() transform (n + 2,
// clif.cpp:122-124) before placing it into ZC_ITEM_PICKUP_ACK.Index.
public readonly record struct InventoryAddResult(bool Success, uint NewAmount, uint SlotIndex);

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
        var (success, newAmount, slotIndex) = await persistence.AddStackableItemAsync(accountId, charId, item.Id, amount, cancellationToken);
        return new InventoryAddResult(success, newAmount, slotIndex);
    }
}
