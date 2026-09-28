namespace Athena.Net.MapServer.Net;

// Pinned clif_damage's own AREA broadcast (clif.cpp:5292-5297: "clif_send(&p, sizeof(p), &dst,
// AREA)") applies identically to a PLAYER's own attack on a monster: the combat ACTION (hit landed/
// missed, damage number, animation timing) is visible to every nearby observer, never the
// attacking client's own socket only - exactly the same rule MonsterAttackActionOutcome already
// applies for a monster's own attack (see that record's own doc comment). This transient,
// immutable value carries everything IroMonsterCombatPackets.BuildNotifyAct3 needs so
// MapTcpServer's local fan-out can project the SAME already-authoritative (World
// ApplyMonsterDamageAsync-applied) hit to every session whose own visibility already covers the
// monster, without MapClientSession ever inspecting or iterating sibling sessions itself. It is
// never authoritative state and is never used to decide anything: World's damage result is the
// sole source of `Damage`; this DTO only carries enough of an already-resolved hit to build one
// wire packet and one diagnostic log line. `Lethal` is diagnostic-only (the "PLAYER ATTACK
// FANOUT" log line) - it never changes visibility or packet shape, which are identical for a
// lethal and a non-lethal hit (the death vanish itself is a completely separate, already-existing
// fan-out - see MapTcpServer.FanOutEntryAsync).
public readonly record struct PlayerAttackActionOutcome(
    uint AttackerActorId, uint MobActorId, string Map, uint Damage, uint SrcSpeed, uint DstSpeed, bool Lethal);
