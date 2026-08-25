namespace Athena.Net.MapServer.World;

// Traced from pinned rAthena status_calc_speed (status.cpp:8018-8223) for the ONLY subset this
// codebase currently models: a PC with no active haste/slow status except Increase AGI's flat
// move-speed value (already computed as EffectiveCharacterStats.MoveSpeedHaste by
// CharacterStatusEffectState.Recalculate - see that type's own doc comment tracing SC_INCREASEAGI's
// "GetMoveHasteValue1()" +25 to speed_rate, status.cpp:8146-8147). Not derived from Increase AGI
// merely because that status happens to touch movement speed: MoveSpeedHaste IS, by construction,
// exactly the accumulated haste `val` status_calc_speed itself computes before applying it to
// speed_rate - this calculator performs the remaining, genuinely separate steps:
//   speed_rate = 100 - val, floored at 40                    (status.cpp:8203-8204)
//   speed = baseSpeed * speed_rate / 100                     (status.cpp:8212-8213, "if (speed_rate != 100)")
//   speed = clamp(speed, MIN_WALK_SPEED=20, MAX_WALK_SPEED=1000)   (status.cpp:8223, mmo.hpp:95-96)
// baseSpeed is DEFAULT_WALK_SPEED=150 (mmo.hpp:93) for every currently supported character - Athena
// has no per-character stored base walk speed field, matching the "no invented state" rule the same
// way BasicAttackCalculator treats POW=0 as the real default rather than an assumption.
//
// Explicitly NOT modeled (would require status/item/skill state this codebase does not have):
// mounts, carts, Berserk/Run/other haste statuses, slow statuses (Curse, Decrease AGI, etc.),
// permanent item-based speed bonuses. A character with any of those active would need a broader
// calculator; using this one for such a character would silently under-count their real speed.
public static class MovementSpeedCalculator
{
    public const int DefaultWalkSpeedMs = 150;
    private const int MinWalkSpeedMs = 20;
    private const int MaxWalkSpeedMs = 1000;

    public static int CellDurationMs(int moveSpeedHaste)
    {
        var speedRate = Math.Max(100 - moveSpeedHaste, 40);
        var speed = DefaultWalkSpeedMs * speedRate / 100;
        return Math.Clamp(speed, MinWalkSpeedMs, MaxWalkSpeedMs);
    }
}
