namespace Athena.Net.MapServer.Gameplay.Rules;

// Result of one basic-attack damage calculation. Pure/deterministic: no mutation,
// no I/O. Whether the hit was lethal is answered later by MobInstance.ApplyDamage
// (which owns the authoritative HP mutation), not here - this type only reports
// what damage the attack calculates.
public readonly record struct BasicAttackDamageResult(uint Damage, bool IsMiss);
