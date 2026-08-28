using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// A failed/unavailable skill read (DB error, disconnected CharServer, malformed response) must
// never be represented the same way as "successfully confirmed no learned skills" - collapsing
// both into a nullable snapshot would let character initialization silently treat an unknown
// state as empty. Mirrors CharacterInventoryReadResult exactly.
public readonly record struct CharacterSkillReadResult(bool Succeeded, CharacterSkillSnapshot? Snapshot)
{
    public static CharacterSkillReadResult Success(CharacterSkillSnapshot snapshot) => new(true, snapshot);
    public static CharacterSkillReadResult Failed() => new(false, null);
}

// The composite outcome of a successful skill-learn mutation: BOTH the new authoritative
// CharacterGameplayState (bumped Version, decremented SkillPoints) and the new skill level, always
// together - never as two independently-updated partial results. See
// CharacterGameplayStateSession.LearnSkillAsync for why both must be adopted atomically under the
// same lock.
public sealed record CharacterSkillLearnResult(CharacterGameplayState GameplayState, ushort SkillId, byte NewSkillLevel);

public interface ICharacterSkillPersistence
{
    Task<CharacterSkillReadResult> GetSkillsAsync(uint accountId, uint characterId, CancellationToken cancellationToken);

    // Persists exactly one skill-level increment (expectedCurrentLevel -> expectedCurrentLevel+1)
    // atomically with a SkillPoints decrement and a GameplayStateVersion bump, all in one CharServer
    // MSSQL transaction. `expectedGameplayState` and `expectedCurrentLevel` are MapServer-internal,
    // already-validated values (from CharacterSkillService.ValidateUpgrade) - NOT raw client input;
    // this call is the internal MapServer<->CharServer boundary, not the (not-yet-implemented)
    // client-facing skill-up request. Returns null on any failure (stale GameplayStateVersion,
    // stale expectedCurrentLevel, no skill points, DB error, disconnected CharServer) - callers
    // must never assume success and must never report success to the client before this returns
    // non-null.
    Task<CharacterSkillLearnResult?> LearnSkillAsync(
        uint accountId,
        CharacterGameplayState expectedGameplayState,
        ushort skillId,
        byte expectedCurrentLevel,
        CancellationToken cancellationToken);
}
