using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Gameplay.Rules;

// Every authoritative input a basic (no-skill) melee attack calculation needs,
// already resolved server-side by the caller (MonsterCombatCoordinator, itself fed
// by MapClientSession's equipment/stat resolution) - never client-supplied gameplay
// state. EquippedWeapon is null for a genuinely confirmed-unarmed right hand
// (EquippedWeaponResolution.Unarmed), never "unknown"/"not looked up yet" - see
// EquippedWeaponResolver's own doc comment for that invariant.
public readonly record struct BasicAttackContext(
    EffectiveCharacterStats Attacker,
    ushort AttackerBaseLevel,
    WeaponItemDefinition? EquippedWeapon,
    MobDefinition Target);
