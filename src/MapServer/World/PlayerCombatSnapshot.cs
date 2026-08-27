namespace Athena.Net.MapServer.World;

// One synchronized snapshot of a live player's combat-relevant state, exposed by
// MapClientSession for the world monster-tick orchestrator (MapTcpServer) to pass into the
// monster combat domain service. Deliberately narrow: only what pinned mob_ai_sub_hard /
// unit_walktobl / battle_calc_base_damage actually need to decide "unlock, chase, or attack" and
// to compute mob-on-player damage - never the full MapClientSession surface. Captured as ONE
// value under the session's own synchronization gate (mirroring MobPosition's own "one atomic
// read" rationale) so the domain service never observes torn state (e.g. X updated but Map still
// stale) across a concurrent player movement/teleport happening on the same session.
// `IsWalking` mirrors pinned unit_is_walking(target) (unit.cpp:3256) - unit_attack_timer_sub grants
// a +1 "chasing" range bonus to WHICHEVER side is attacking based on whether the TARGET is
// currently walking, checked identically for a PC attacker (sd) and a mob attacker (md) alike
// (unit.cpp:3253-3268: the range++ happens BEFORE the sd/md branch splits) - the existing
// player-attacks-mob path already applies this same bonus keyed on the MOB's own IsWalking
// (MapClientSession's basic-attack range check, `target.IsWalking ? 1 : 0`); this field is what
// lets the mob-attacks-player path apply the identical pinned rule keyed on the PLAYER's own
// current walking state, rather than assuming the two directions share one rule without tracing it.
// `Agility` is the player's EFFECTIVE (base + active status bonuses) AGI, needed by the RENEWAL
// player def2 formula (status.cpp:2649-2656: floor((BaseLevel+VIT)/2 + AGI/5)) - see
// MobBasicAttackCalculator's own doc comment for the full trace.
public readonly record struct PlayerCombatSnapshot(
    uint AccountId, string Map, ushort X, ushort Y, bool IsAlive, bool IsWalking, ushort BaseLevel, ushort Vitality, ushort Agility);
