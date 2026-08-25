namespace Athena.Net.MapServer.Net;

// Authoritative inventory mutation boundary. MapServer never touches
// CharInventory rows directly (no EF Core/MSSQL access from MapServer, per
// the same architecture principle CharacterGameplayStateSession and
// ICharacterQuestPersistence already follow: CharServer is the durable
// owner). AddStackableItemAsync finds-or-creates the character's stack for
// `itemId` and returns the new authoritative total amount on success, plus
// the SERVER-side inventory array position (0-based, matching pinned
// rAthena's sd->inventory.u.items_inventory[n] - CharServer derives it from
// stable row-insertion order among the character's own inventory rows,
// since neither Athena's schema nor real rAthena's own `inventory` SQL
// table persists a slot column at all). Callers building a client-facing
// packet must still apply the pinned client_index() transform (n + 2,
// clif.cpp:122-124) themselves.
public interface ICharacterInventoryPersistence
{
    Task<(bool Success, uint NewAmount, uint SlotIndex)> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken);
}
