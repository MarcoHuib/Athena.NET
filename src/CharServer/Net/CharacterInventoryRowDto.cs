namespace Athena.Net.CharServer.Net;

// One persisted CharInventory row, projected for the wire (see
// Athena.Net.MapServer.Net.CharacterInventoryItem for the MapServer-side counterpart and its
// field-by-field pinned rationale). SlotIndex is assigned by the caller (stable load order),
// not stored on this DTO - see MapServerSession.HandleInventoryListGetAsync.
internal sealed record CharacterInventoryRowDto(int ItemId, uint Amount, uint Equip, bool Identified, byte Refine, byte Favorite, byte Bound);
