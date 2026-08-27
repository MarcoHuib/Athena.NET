namespace Athena.Net.MapServer.Gameplay.Rules.Renewal;

// Pinned player dmotion (status.cpp:4640-4645, PC base-status calculation):
//   i = 800 - base_status->agi*4;
//   base_status->dmotion = cap_value(i, 400, 800);
//   if (battle_config.pc_damage_delay_rate != 100)
//       base_status->dmotion = base_status->dmotion*battle_config.pc_damage_delay_rate/100;
// `player_damage_delay_rate` defaults to 100 (battle.cpp:8271), making that last multiplier a
// genuine no-op for the default configuration this project targets - not modeled here since
// nothing in this codebase exposes battle_config as a runtime-tunable value yet.
//
// This is the player-side dstSpeed for clif_damage when a MOB attacks a player
// (MonsterEngagementTickProcessor's own mob->player combat path) - the mirror of
// MobDefinition.DamageMotion, which serves the identical role when a PLAYER attacks a mob. Kept as
// its own small pure calculator (not inlined into a packet builder) so the formula is
// independently testable, matching this project's existing WeaponAttackCalculator/
// MobBasicAttackCalculator precedent of separating pure combat math from packet construction.
public static class PlayerDamageMotionCalculator
{
    public static int Calculate(int agility) => Math.Clamp(800 - agility * 4, 400, 800);
}
