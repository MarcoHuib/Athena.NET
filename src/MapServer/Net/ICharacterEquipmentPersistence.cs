namespace Athena.Net.MapServer.Net;

// Scoped to the right-hand weapon slot only (pinned EQP_HAND_R = 0x000002,
// mmo.hpp:340) - the only equip slot this vertical slice's combat/appearance
// path needs. Extend with further slots (EQP_HEAD_TOP, armor, etc.) only when
// a traced use case needs them, mirroring ItemDefinition's shallow-extension
// convention.
public sealed record CharacterEquipmentSnapshot(int? RightHandItemId, byte RightHandRefine);

public interface ICharacterEquipmentPersistence
{
    Task<CharacterEquipmentSnapshot?> GetEquipmentAsync(uint accountId, uint characterId, CancellationToken cancellationToken);
}
