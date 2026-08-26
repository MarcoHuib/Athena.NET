namespace Athena.Net.MapServer.Net;

// Scoped to the right-hand weapon slot only (pinned EQP_HAND_R = 0x000002,
// mmo.hpp:340) - the only equip slot this vertical slice's combat/appearance
// path needs. Extend with further slots (EQP_HEAD_TOP, armor, etc.) only when
// a traced use case needs them, mirroring ItemDefinition's shallow-extension
// convention.
//
// RightHandItemId == null means "confirmed no right-hand item equipped" - this
// type is only ever constructed when the underlying read actually succeeded
// (see CharacterEquipmentReadResult), so a null item id here is never
// ambiguous with a failed/unavailable read.
public sealed record CharacterEquipmentSnapshot(int? RightHandItemId, byte RightHandRefine);

// A failed/unavailable equipment read (DB error, disconnected CharServer, malformed
// response, multiple conflicting equipped rows) must never be represented the same
// way as "successfully confirmed unarmed" - collapsing both into a nullable
// CharacterEquipmentSnapshot would let future combat/appearance code silently treat
// an unknown equipment state as unarmed. Succeeded=false always carries Snapshot=null;
// Succeeded=true always carries a non-null Snapshot (RightHandItemId may itself be
// null inside it - that is the authoritative "unarmed" case).
public readonly record struct CharacterEquipmentReadResult(bool Succeeded, CharacterEquipmentSnapshot? Snapshot)
{
    public static CharacterEquipmentReadResult Success(CharacterEquipmentSnapshot snapshot) => new(true, snapshot);
    public static CharacterEquipmentReadResult Failed() => new(false, null);
}

public interface ICharacterEquipmentPersistence
{
    Task<CharacterEquipmentReadResult> GetEquipmentAsync(uint accountId, uint characterId, CancellationToken cancellationToken);
}
