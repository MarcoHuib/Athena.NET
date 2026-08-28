namespace Athena.Net.MapServer.World;

// Immutable generated-data boundary consumed by the progression domain. Arrays are
// one-based by level and are generated directly from pinned rAthena YAML.
public sealed record CharacterProgressionDefinition(
    ushort JobClass,
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
