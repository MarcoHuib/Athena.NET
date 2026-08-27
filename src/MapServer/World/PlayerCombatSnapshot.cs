namespace Athena.Net.MapServer.World;

// One synchronized snapshot of a live player's combat-relevant state, exposed by
// MapClientSession for the world monster-tick orchestrator (MapTcpServer) to pass into the
// monster combat domain service. Deliberately narrow: only what pinned mob_ai_sub_hard /
// unit_walktobl / battle_calc_base_damage actually need to decide "unlock, chase, or attack" and
// to compute mob-on-player damage - never the full MapClientSession surface. Captured as ONE
// value under the session's own synchronization gate (mirroring MobPosition's own "one atomic
// read" rationale) so the domain service never observes torn state (e.g. X updated but Map still
// stale) across a concurrent player movement/teleport happening on the same session.
public readonly record struct PlayerCombatSnapshot(
    uint AccountId, string Map, ushort X, ushort Y, bool IsAlive, ushort BaseLevel, ushort Vitality);
