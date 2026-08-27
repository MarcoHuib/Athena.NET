namespace Athena.Net.CharServer.Net;

// One persisted CharInventory row, projected for the wire (see
// Athena.Net.MapServer.Net.CharacterInventoryItem for the MapServer-side counterpart and its
// field-by-field pinned rationale). DurableId is CharInventory's own real primary key - the
// ONE stable identity a row keeps for its entire lifetime, completely independent of any
// runtime/session-local slot numbering. MapServer owns runtime SlotIndex entirely; CharServer
// never assigns, derives, or reasons about it - see ai/map-server.md "Durable row identity vs
// runtime SlotIndex" for the full architecture rationale.
internal sealed record CharacterInventoryRowDto(uint DurableId, int ItemId, uint Amount, uint Equip, bool Identified, byte Refine, byte Favorite, byte Bound);
