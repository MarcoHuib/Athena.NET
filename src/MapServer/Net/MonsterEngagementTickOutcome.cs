namespace Athena.Net.MapServer.Net;

// Pinned clif_damage's own AREA broadcast (clif.cpp:5292-5297: "clif_send(&p, sizeof(p), &dst,
// AREA)") - the combat ACTION (hit landed/missed, damage number, animation timing) is visible to
// every nearby observer, never victim-only. Carries everything IroMonsterCombatPackets.
// BuildNotifyAct3 needs so MapTcpServer can build and fan out the SAME 0x08C8 to every session
// whose own visibility already covers `Map`/`Position`, without MonsterAttackCadenceExecutor
// (which never sends packets) needing to know the wire packet shape itself. `VictimAccountId` is
// who the already-applied HP mutation (see MapClientSession.ApplyIncomingMobBasicAttackAsync)
// belongs to - the SAME account's self-only SP_HP parameter update (`HpAfter`/`HpChanged`) is
// carried here too so MapClientSession.NotifyMonsterAttackOutcomeAsync can send it, for the VICTIM
// session only, immediately AFTER the action packet on the SAME fan-out call - matching pinned
// rAthena's own wire ordering (the action always precedes the HP sync) rather than writing the HP
// packet earlier, during ProcessAsync itself, which would put it on the wire BEFORE the action
// every other observer also receives.
public readonly record struct MonsterAttackActionOutcome(
    uint MobActorId, string Map, ushort MobX, ushort MobY, uint VictimAccountId, uint Damage, bool IsMiss, uint SrcSpeed, uint DstSpeed, uint HpAfter, bool HpChanged);

// Everything MonsterAttackCadenceExecutor.ProcessAsync observed as world-visible THIS tick, for
// MapTcpServer (the fan-out/orchestration owner) to broadcast to every relevant session. Movement
// is no longer reported here (post-cutover, ALL monster movement/position changes flow through the
// World feed's own MonsterFeedProjection - see that type's own doc comment) - this result now
// carries only AttackActions, the one thing that remains genuinely MapServer-local (cadence,
// damage calculation, quest drops).
public readonly record struct MonsterEngagementTickResult(IReadOnlyList<MonsterAttackActionOutcome> AttackActions)
{
    public static readonly MonsterEngagementTickResult Empty = new([]);
}
