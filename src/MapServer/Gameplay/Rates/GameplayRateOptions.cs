namespace Athena.Net.MapServer.Gameplay.Rates;

// Immutable server policy loaded once at startup. Values use rAthena's percentage
// convention (100 = 1x). Drop settings are deliberately modeled independently even
// though Athena's generic monster-drop/MVP-reward runtime does not exist yet.
public sealed record GameplayRateOptions
{
    public const uint DefaultRate = 100;

    public uint BaseExperience { get; init; } = DefaultRate;
    public uint JobExperience { get; init; } = DefaultRate;
    public uint QuestExperience { get; init; } = DefaultRate;
    public uint MvpExperience { get; init; } = DefaultRate;

    public uint ItemCommon { get; init; } = DefaultRate;
    public uint ItemHeal { get; init; } = DefaultRate;
    public uint ItemUse { get; init; } = DefaultRate;
    public uint ItemEquip { get; init; } = DefaultRate;
    public uint ItemCard { get; init; } = DefaultRate;
    public uint ItemCommonBoss { get; init; } = DefaultRate;
    public uint ItemHealBoss { get; init; } = DefaultRate;
    public uint ItemUseBoss { get; init; } = DefaultRate;
    public uint ItemEquipBoss { get; init; } = DefaultRate;
    public uint ItemCardBoss { get; init; } = DefaultRate;
    public uint ItemCommonMvp { get; init; } = DefaultRate;
    public uint ItemHealMvp { get; init; } = DefaultRate;
    public uint ItemUseMvp { get; init; } = DefaultRate;
    public uint ItemEquipMvp { get; init; } = DefaultRate;
    public uint ItemCardMvp { get; init; } = DefaultRate;
    public uint ItemMvp { get; init; } = DefaultRate;

    // Positive rAthena percentage multiplication truncates toward zero. UInt128 keeps
    // the multiplication exact and avoids floating-point precision and overflow.
    public static ulong Apply(ulong raw, uint rate)
    {
        var rated = (UInt128)raw * rate / 100u;
        return rated > long.MaxValue ? long.MaxValue : (ulong)rated;
    }
}

public enum ExperienceAwardSource
{
    Battle,
    Quest,
}
