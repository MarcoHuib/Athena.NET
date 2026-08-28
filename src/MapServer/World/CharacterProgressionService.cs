using Athena.Net.MapServer.Gameplay.Rates;
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

public sealed class CharacterProgressionService(
    CharacterGameplayStateSession stateSession,
    GameplayRateOptions? rates = null)
{
    private const ulong MaximumExperience = long.MaxValue;
    private const ulong MaximumBaseLevelExperience = 99_999_999;
    private const ulong MaximumJobLevelExperience = 999_999_999;
    private readonly GameplayRateOptions _rates = rates ?? new GameplayRateOptions();

    public async Task<CharacterProgressionResult?> AddExperienceAsync(
        long baseExperience,
        long jobExperience,
        ExperienceAwardSource source,
        CancellationToken cancellationToken)
    {
        if (baseExperience < 0 || jobExperience < 0)
            throw new ArgumentOutOfRangeException(nameof(baseExperience), "Experience awards must be non-negative.");

        var rate = source switch
        {
            ExperienceAwardSource.Battle => (_rates.BaseExperience, _rates.JobExperience),
            ExperienceAwardSource.Quest => (_rates.QuestExperience, _rates.QuestExperience),
            _ => throw new ArgumentOutOfRangeException(nameof(source)),
        };
        var ratedBase = GameplayRateOptions.Apply((ulong)baseExperience, rate.Item1);
        var ratedJob = GameplayRateOptions.Apply((ulong)jobExperience, rate.Item2);
        var before = stateSession.State;
        var calculation = Calculate(before, ratedBase, ratedJob);
        if (calculation.After == before) return calculation;
        var persisted = await stateSession.MutateAsync(_ => calculation.After, cancellationToken);
        return persisted is null ? null : calculation with { After = persisted };
    }

    // Script getexp is the only pre-existing consumer, so this compatibility overload
    // deliberately retains quest-rate semantics.
    public Task<CharacterProgressionResult?> AddExperienceAsync(long baseExperience, long jobExperience, CancellationToken cancellationToken) =>
        AddExperienceAsync(baseExperience, jobExperience, ExperienceAwardSource.Quest, cancellationToken);

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
