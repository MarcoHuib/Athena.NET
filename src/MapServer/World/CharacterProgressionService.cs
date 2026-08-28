using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

public readonly record struct CharacterProgressionResult(
    CharacterGameplayState Before,
    CharacterGameplayState After,
    ushort BaseLevelsGained,
    ushort JobLevelsGained,
    ulong NextBaseExperience,
    ulong NextJobExperience,
    ulong BaseExperienceAwarded = 0,
    ulong JobExperienceAwarded = 0);

// Owns ONLY Base/Job EXP state, thresholds, Base/Job Level, stat/skill points,
// HP/SP recalculation, max levels, and the EXP remainder/overcarry policy, plus
// the one atomic persisted state transition. It receives already-rated final
// Base/Job EXP values and does not know or care whether the reward came from a
// monster, a script, an MVP, or an event - rate selection happens strictly
// upstream, in Athena.Net.MapServer.Gameplay.Rates (GameplayRateResolver /
// ExperienceRewardService), before this service is ever called.
public sealed class CharacterProgressionService(CharacterGameplayStateSession stateSession)
{
    private const ulong MaximumExperience = long.MaxValue;
    private const ulong MaximumBaseLevelExperience = 99_999_999;
    private const ulong MaximumJobLevelExperience = 999_999_999;

    public async Task<CharacterProgressionResult?> AddExperienceAsync(
        ulong baseExperience,
        ulong jobExperience,
        CancellationToken cancellationToken)
    {
        var before = stateSession.State;
        var calculation = Calculate(before, baseExperience, jobExperience);
        if (calculation.After == before) return calculation;
        var persisted = await stateSession.MutateAsync(_ => calculation.After, cancellationToken);
        return persisted is null ? null : calculation with { After = persisted };
    }

    internal static CharacterProgressionResult Calculate(CharacterGameplayState state, ulong baseAward, ulong jobAward)
    {
        var definition = GeneratedProgressionRegistry.Get(state.JobClass);
        if (state.BaseLevel is 0 || state.BaseLevel > definition.MaxBaseLevel ||
            state.JobLevel is 0 || state.JobLevel > definition.MaxJobLevel)
            throw new InvalidOperationException("Character levels are outside the generated progression range.");

        var baseLevel = state.BaseLevel;
        var jobLevel = state.JobLevel;
        var effectiveBaseAward = baseLevel == definition.MaxBaseLevel
            ? Math.Min(baseAward, MaximumBaseLevelExperience - Math.Min(state.BaseExperience, MaximumBaseLevelExperience))
            : baseAward;
        var effectiveJobAward = jobLevel == definition.MaxJobLevel
            ? Math.Min(jobAward, MaximumJobLevelExperience - Math.Min(state.JobExperience, MaximumJobLevelExperience))
            : jobAward;
        var baseExperience = SaturatingAdd(state.BaseExperience, effectiveBaseAward);
        var jobExperience = SaturatingAdd(state.JobExperience, effectiveJobAward);
        var statPoints = state.StatPoints;
        var skillPoints = state.SkillPoints;
        ushort baseLevelsGained = 0;
        ushort jobLevelsGained = 0;

        // Pinned exp.conf defaults multi_level_up to no. pc_checkbaselevelup crosses one
        // threshold and caps overcarry to that crossed threshold minus one.
        if (baseLevel < definition.MaxBaseLevel)
        {
            var required = definition.BaseExperienceToNext[baseLevel];
            if (baseExperience >= required)
            {
                baseExperience -= required;
                if (baseExperience > required - 1) baseExperience = required - 1;
                statPoints = checked(statPoints + definition.CumulativeStatPoints[baseLevel + 1] - definition.CumulativeStatPoints[baseLevel]);
                baseLevel++;
                baseLevelsGained = 1;
            }
        }
        if (baseLevel == definition.MaxBaseLevel) baseExperience = Math.Min(baseExperience, MaximumBaseLevelExperience);

        if (jobLevel < definition.MaxJobLevel)
        {
            var required = definition.JobExperienceToNext[jobLevel];
            if (jobExperience >= required)
            {
                jobExperience -= required;
                if (jobExperience > required - 1) jobExperience = required - 1;
                jobLevel++;
                jobLevelsGained = 1;
                skillPoints = checked(skillPoints + 1);
            }
        }
        if (jobLevel == definition.MaxJobLevel) jobExperience = Math.Min(jobExperience, MaximumJobLevelExperience);

        var after = state with
        {
            BaseLevel = baseLevel,
            JobLevel = jobLevel,
            BaseExperience = baseExperience,
            JobExperience = jobExperience,
            StatPoints = statPoints,
            SkillPoints = skillPoints,
        };

        if (baseLevelsGained > 0 || jobLevelsGained > 0)
        {
            var maxHp = CalculateMaximum(definition.BaseHp[baseLevel], state.Vitality + definition.JobVitalityBonus[jobLevel]);
            var maxSp = CalculateMaximum(definition.BaseSp[baseLevel], state.Intelligence + definition.JobIntelligenceBonus[jobLevel]);
            after = baseLevelsGained > 0
                ? after with { MaxHp = maxHp, MaxSp = maxSp, CurrentHp = maxHp, CurrentSp = maxSp }
                : after with { MaxHp = maxHp, MaxSp = maxSp, CurrentHp = Math.Min(after.CurrentHp, maxHp), CurrentSp = Math.Min(after.CurrentSp, maxSp) };
        }

        return new(state, after, baseLevelsGained, jobLevelsGained,
            baseLevel < definition.MaxBaseLevel ? definition.BaseExperienceToNext[baseLevel] : MaximumBaseLevelExperience,
            jobLevel < definition.MaxJobLevel ? definition.JobExperienceToNext[jobLevel] : MaximumJobLevelExperience,
            effectiveBaseAward,
            effectiveJobAward);
    }

    private static ulong SaturatingAdd(ulong current, ulong award) =>
        MaximumExperience - Math.Min(current, MaximumExperience) < award ? MaximumExperience : current + award;

    private static uint CalculateMaximum(uint baseValue, ulong stat) =>
        checked((uint)(baseValue * (100UL + stat) / 100UL));
}
