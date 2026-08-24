using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

public readonly record struct CharacterProgressionResult(
    CharacterGameplayState Before,
    CharacterGameplayState After,
    ushort BaseLevelsGained,
    ushort JobLevelsGained,
    ulong NextBaseExperience,
    ulong NextJobExperience);

public sealed class CharacterProgressionService(CharacterGameplayStateSession stateSession)
{
    private const ulong MaximumBaseLevelExperience = 99_999_999;
    private const ulong MaximumJobLevelExperience = 999_999_999;

    public async Task<CharacterProgressionResult?> AddExperienceAsync(long baseExperience, long jobExperience, CancellationToken cancellationToken)
    {
        if (baseExperience < 0 || jobExperience < 0) throw new ArgumentOutOfRangeException(nameof(baseExperience), "This progression slice supports non-negative getexp awards.");
        var before = stateSession.State;
        var calculation = Calculate(before, (ulong)baseExperience, (ulong)jobExperience);
        if (calculation.After == before) return calculation;
        var persisted = await stateSession.MutateAsync(_ => calculation.After, cancellationToken);
        return persisted is null ? null : calculation with { After = persisted };
    }

    internal static CharacterProgressionResult Calculate(CharacterGameplayState state, ulong baseAward, ulong jobAward)
    {
        if (state.JobClass != GeneratedNoviceProgression.JobClass)
            throw new NotSupportedException($"Progression data for job class {state.JobClass} is not generated.");
        if (state.BaseLevel is 0 or > GeneratedNoviceProgression.MaxBaseLevel || state.JobLevel is 0 or > GeneratedNoviceProgression.MaxJobLevel)
            throw new InvalidOperationException("Character levels are outside the generated Novice progression range.");

        var baseLevel = state.BaseLevel;
        var jobLevel = state.JobLevel;
        var baseExperience = SaturatingAdd(state.BaseExperience, baseAward);
        var jobExperience = SaturatingAdd(state.JobExperience, jobAward);
        var statPoints = state.StatPoints;
        var skillPoints = state.SkillPoints;
        ushort baseLevelsGained = 0;
        ushort jobLevelsGained = 0;

        while (baseLevel < GeneratedNoviceProgression.MaxBaseLevel)
        {
            var required = GeneratedNoviceProgression.BaseExperienceToNext[baseLevel];
            if (baseExperience < required) break;
            baseExperience -= required;
            statPoints = checked(statPoints + (uint)(GeneratedNoviceProgression.CumulativeStatPoints[baseLevel + 1] - GeneratedNoviceProgression.CumulativeStatPoints[baseLevel]));
            baseLevel++;
            baseLevelsGained++;
        }
        if (baseLevel == GeneratedNoviceProgression.MaxBaseLevel) baseExperience = Math.Min(baseExperience, MaximumBaseLevelExperience);

        while (jobLevel < GeneratedNoviceProgression.MaxJobLevel)
        {
            var required = GeneratedNoviceProgression.JobExperienceToNext[jobLevel];
            if (jobExperience < required) break;
            jobExperience -= required;
            jobLevel++;
            jobLevelsGained++;
            skillPoints = checked(skillPoints + 1);
        }
        if (jobLevel == GeneratedNoviceProgression.MaxJobLevel) jobExperience = Math.Min(jobExperience, MaximumJobLevelExperience);

        var after = state with
        {
            BaseLevel = baseLevel,
            JobLevel = jobLevel,
            BaseExperience = baseExperience,
            JobExperience = jobExperience,
            StatPoints = statPoints,
            SkillPoints = skillPoints,
        };
        if (baseLevelsGained > 0)
        {
            var maxHp = CalculateMaximum(GeneratedNoviceProgression.BaseHp[baseLevel], state.Vitality + GeneratedNoviceProgression.JobVitalityBonus[state.JobLevel]);
            var maxSp = CalculateMaximum(GeneratedNoviceProgression.BaseSp[baseLevel], state.Intelligence + GeneratedNoviceProgression.JobIntelligenceBonus[state.JobLevel]);
            after = after with { MaxHp = maxHp, MaxSp = maxSp, CurrentHp = maxHp, CurrentSp = maxSp };
        }

        if (jobLevelsGained > 0)
        {
            var maxHp = CalculateMaximum(GeneratedNoviceProgression.BaseHp[baseLevel], state.Vitality + GeneratedNoviceProgression.JobVitalityBonus[jobLevel]);
            var maxSp = CalculateMaximum(GeneratedNoviceProgression.BaseSp[baseLevel], state.Intelligence + GeneratedNoviceProgression.JobIntelligenceBonus[jobLevel]);
            after = after with { MaxHp = maxHp, MaxSp = maxSp, CurrentHp = Math.Min(after.CurrentHp, maxHp), CurrentSp = Math.Min(after.CurrentSp, maxSp) };
        }

        return new(state, after, baseLevelsGained, jobLevelsGained,
            baseLevel < GeneratedNoviceProgression.MaxBaseLevel ? GeneratedNoviceProgression.BaseExperienceToNext[baseLevel] : MaximumBaseLevelExperience,
            jobLevel < GeneratedNoviceProgression.MaxJobLevel ? GeneratedNoviceProgression.JobExperienceToNext[jobLevel] : MaximumJobLevelExperience);
    }

    private static ulong SaturatingAdd(ulong current, ulong award) => ulong.MaxValue - current < award ? ulong.MaxValue : current + award;
    private static uint CalculateMaximum(uint baseValue, ulong stat) => checked((uint)(baseValue * (100UL + stat) / 100UL));
}
