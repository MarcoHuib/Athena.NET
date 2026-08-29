using Athena.Net.MapServer.Generated.Jobs;

namespace Athena.Net.MapServer.World;

// Immutable generated-data boundary consumed by the progression domain. Arrays are
// one-based by level and are generated directly from pinned rAthena YAML. JobClass is the
// strongly-typed generated enum; conversion from the ushort wire/persistence contract
// happens only at the GeneratedProgressionRegistry.Get(ushort) boundary, never here.
// MaxBaseStat is the pinned pc_maxparameter cap shared by STR/AGI/VIT/INT/DEX/LUK for this
// job. Source-backed via pc_jobid2mapid's JOBL_BABY/THIRD/UPPER/FOURTH classification
// (src/map/pc.cpp) resolved to the matching conf/battle/player.conf max_*_parameter value -
// see CharacterDataCompiler.ResolveJobParameterCategory/JobParameterCategoryMaxStat. Every
// generated job carries exactly one cap; per-job database overrides (job_stats.yml MaxStats)
// are not present anywhere in the pinned snapshot, so this project does not yet special-case
// them - see that method's own doc comment.
public sealed record CharacterProgressionDefinition(
    JobClass JobClass,
    ushort MaxBaseLevel,
    ushort MaxJobLevel,
    IReadOnlyList<ulong> BaseExperienceToNext,
    IReadOnlyList<ulong> JobExperienceToNext,
    IReadOnlyList<uint> BaseHp,
    IReadOnlyList<uint> BaseSp,
    IReadOnlyList<uint> CumulativeStatPoints,
    IReadOnlyList<uint> JobStrengthBonus,
    IReadOnlyList<uint> JobAgilityBonus,
    IReadOnlyList<uint> JobVitalityBonus,
    IReadOnlyList<uint> JobIntelligenceBonus,
    IReadOnlyList<uint> JobDexterityBonus,
    IReadOnlyList<uint> JobLuckBonus,
    ushort MaxBaseStat);
