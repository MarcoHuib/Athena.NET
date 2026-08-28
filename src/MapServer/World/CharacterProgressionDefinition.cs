using Athena.Net.MapServer.Generated.Jobs;

namespace Athena.Net.MapServer.World;

// Immutable generated-data boundary consumed by the progression domain. Arrays are
// one-based by level and are generated directly from pinned rAthena YAML. JobClass is the
// strongly-typed generated enum; conversion from the ushort wire/persistence contract
// happens only at the GeneratedProgressionRegistry.Get(ushort) boundary, never here.
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
    IReadOnlyList<uint> JobLuckBonus);
