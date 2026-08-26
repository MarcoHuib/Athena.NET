namespace Athena.Net.CharServer.Net;

// Scoped to EQP_HAND_R (0x000002, mmo.hpp:340) only - see
// Athena.Net.MapServer.Net.CharacterEquipmentSnapshot for the rationale.
// HasRightHand distinguishes "no weapon equipped" from a real item id 0,
// since 0 is not a valid pinned item_db id.
internal sealed record CharacterEquipmentDto(bool HasRightHand, uint RightHandItemId, byte RightHandRefine);
