using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// The explicitly MapServer-LOCAL half of a monster's runtime state, post substep-9 cutover:
// NextAttackAt cadence bookkeeping ONLY. CurrentHp/MaxHp are gone entirely - World is the sole
// authority for monster HP (see WorldMonsterInstance's own doc comment and ApplyMonsterDamageAsync);
// this store never mirrors it locally any more. `IncarnationId` is WorldMonsterIncarnationId (the
// real World wire type) since MonsterAttackCadenceStore is keyed by the full authoritative
// (MapId, SimulationEpoch, ActorId, IncarnationId) tuple (see MonsterCombatKey's own doc comment).
//
// This is a per-read SNAPSHOT (record, not a live reference) - callers that need a fresh value
// after a reschedule must re-read from the store again.
public sealed record MonsterAttackCadenceState(uint ActorId, WorldMonsterIncarnationId IncarnationId, DateTimeOffset? NextAttackAt);
