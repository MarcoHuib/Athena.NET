using Athena.Net.MapServer.Generated.Progression;

namespace Athena.Net.MapServer.World;

// Why a requested base-stat increase may not proceed - the server derives which reason
// applies; a client (or any caller) never supplies or trusts a target stat value or Status
// Point balance directly, matching CharacterSkillService's SkillUpgradeRejectionReason
// convention.
public enum StatIncreaseRejectionReason
{
    UnknownStat,
    InvalidIncreaseAmount,
    MaxValueReached,
    InsufficientStatusPoints,
}

// Immutable outcome of CharacterStatService.ValidateIncrease. Exactly one of Valid/Rejected is
// meaningful, discriminated by IsValid - mirrors SkillUpgradeValidationResult's own contract.
public readonly record struct StatIncreaseValidationResult(
    bool IsValid,
    StatIncreaseRejectionReason? RejectionReason,
    ushort PreviousValue,
    ushort NewValue,
    uint StatusPointsSpent,
    uint RemainingStatusPoints)
{
    public static StatIncreaseValidationResult Valid(ushort previousValue, ushort newValue, uint statusPointsSpent, uint remainingStatusPoints) =>
        new(true, null, previousValue, newValue, statusPointsSpent, remainingStatusPoints);

    public static StatIncreaseValidationResult Rejected(StatIncreaseRejectionReason reason) =>
        new(false, reason, 0, 0, 0, 0);
}

// Static/pure domain rules for base-stat allocation (STR/AGI/VIT/INT/DEX/LUK) and Status Point
// spending. No constructor, no stored session references, no I/O - persistence/session
// orchestration lives strictly above this layer (CharacterGameplayStateSession.
// IncreaseStatAsync), exactly the way CharacterSkillService keeps its own pure ValidateUpgrade
// separate from the session's mutation lock. This service never branches on JobClass beyond
// reading GeneratedProgressionDefinition.MaxBaseStat - a future job's cap works without a
// runtime change here, same rationale as CharacterSkillService's own doc comment.
public static class CharacterStatService
{
    // Pinned Renewal status-point cost formula (src/map/pc.cpp, PC_STATUS_POINT_COST under
    // #ifdef RENEWAL_STAT): cost of raising a stat FROM `current` to `current + 1`. Athena.NET
    // targets the Renewal ruleset (see AGENTS.md/ai/map-server.md), so only this branch is
    // ported - the pre-Renewal '(1 + (low + 9) / 10)' formula is intentionally not
    // implemented. Integer division here is C#'s ordinary truncating int division, matching
    // pinned C++'s int32 arithmetic exactly.
    private static int CostForStep(int current) => current < 100 ? 2 + (current - 1) / 10 : 16 + 4 * ((current - 100) / 5);

    // Pinned pc_need_status_point(sd, type, val) for val > 0: cumulative cost of raising a
    // stat by `increaseAmount` steps starting at `currentValue`, i.e. the sum of CostForStep
    // over every intermediate value in [currentValue, currentValue + increaseAmount). Pinned
    // source also defines val<0 semantics (cost to have raised a lower stat back up to the
    // current value) but this project's ValidateIncrease never calls this with a negative
    // amount - see its own InvalidIncreaseAmount rejection - so that branch is not ported.
    internal static uint CumulativeCost(ushort currentValue, int increaseAmount)
    {
        checked
        {
            uint total = 0;
            var value = (int)currentValue;
            for (var step = 0; step < increaseAmount; step++)
            {
                total += (uint)CostForStep(value);
                value++;
            }
            return total;
        }
    }

    private static ushort MaxBaseStat(CharacterGameplayState gameplay) => GeneratedProgressionRegistry.Get(gameplay.JobClass).MaxBaseStat;

    private static ushort CurrentValue(CharacterGameplayState gameplay, CharacterBaseStat stat) => stat switch
    {
        CharacterBaseStat.Strength => gameplay.Strength,
        CharacterBaseStat.Agility => gameplay.Agility,
        CharacterBaseStat.Vitality => gameplay.Vitality,
        CharacterBaseStat.Intelligence => gameplay.Intelligence,
        CharacterBaseStat.Dexterity => gameplay.Dexterity,
        CharacterBaseStat.Luck => gameplay.Luck,
        _ => throw new ArgumentOutOfRangeException(nameof(stat), stat, "Unknown base stat."),
    };

    // Validates a requested increase of `increaseAmount` steps to `stat`, from server-side
    // truth only. Never accepts or trusts a caller-supplied target value or point balance -
    // both are always derived internally from gameplay. Pinned pc_statusup validates in this
    // exact order: type/increase sanity, then the max-parameter cap, then the actual Status
    // Point cost against the character's current balance (src/map/pc.cpp:8872-8920). Unlike
    // pinned pc_statusup, this does NOT silently clamp increaseAmount down to
    // pc_maxparameterincrease's affordable maximum - task section 10 requires an increase that
    // would exceed the stat max or the available points to be REJECTED outright, not
    // partially applied, so an out-of-range request never creates authoritative state.
    public static StatIncreaseValidationResult ValidateIncrease(
        CharacterGameplayState gameplay,
        CharacterBaseStat stat,
        int increaseAmount)
    {
        if (!Enum.IsDefined(stat)) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.UnknownStat);
        if (increaseAmount <= 0) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.InvalidIncreaseAmount);

        var current = CurrentValue(gameplay, stat);
        var max = MaxBaseStat(gameplay);
        if (current >= max) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.MaxValueReached);

        // An absurd client-supplied increaseAmount (e.g. int.MaxValue) must be REJECTED, not
        // wrap or throw - task section 10 requires rejecting integer overflow, not merely
        // tolerating it via an unhandled exception. long avoids the overflow this addition
        // would otherwise risk in int, so the comparison below is always well-defined.
        var newValueLong = (long)current + increaseAmount;
        if (newValueLong > max) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.MaxValueReached);
        var newValueInt = (int)newValueLong;

        var cost = CumulativeCost(current, increaseAmount);
        if (cost > gameplay.StatPoints) return StatIncreaseValidationResult.Rejected(StatIncreaseRejectionReason.InsufficientStatusPoints);

        var newValue = checked((ushort)newValueInt);
        var remaining = gameplay.StatPoints - cost;
        return StatIncreaseValidationResult.Valid(current, newValue, cost, remaining);
    }
}
