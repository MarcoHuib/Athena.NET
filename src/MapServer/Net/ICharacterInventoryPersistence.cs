namespace Athena.Net.MapServer.Net;

// Authoritative inventory mutation boundary. MapServer never touches
// CharInventory rows directly (no EF Core/MSSQL access from MapServer, per
// the same architecture principle CharacterGameplayStateSession and
// ICharacterQuestPersistence already follow: CharServer is the durable
// owner). AddStackableItemAsync finds-or-creates the character's stack for
// `itemId` and returns the new authoritative total amount on success.
public interface ICharacterInventoryPersistence
{
    Task<(bool Success, uint NewAmount)> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken);
}
