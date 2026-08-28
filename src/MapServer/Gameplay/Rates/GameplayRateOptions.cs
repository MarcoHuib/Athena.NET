namespace Athena.Net.MapServer.Gameplay.Rates;

// Immutable server policy loaded once at startup. Values use rAthena's percentage
// convention (100 = 1x). Global rates always have a value (default 100 = 1x).
// Source-specific and category-specific overrides are nullable: null means
// "inherit the relevant global rate", NOT an independent default of 100. See
// GameplayRateResolver for the single place that turns a global + optional
// override into one effective rate - overrides REPLACE the global, they never
// stack/multiply with it.
public sealed record GameplayRateOptions
{
    public const uint DefaultRate = 100;

    // Global EXP/drop rates. Always have a value.
    public uint BaseExpRate { get; init; } = DefaultRate;
    public uint JobExpRate { get; init; } = DefaultRate;
    public uint ItemDropRate { get; init; } = DefaultRate;

    // Optional EXP overrides. Null = inherit the corresponding global rate above.
    public uint? QuestBaseExpRate { get; init; }
    public uint? QuestJobExpRate { get; init; }
    public uint? MvpBaseExpRate { get; init; }
    public uint? MvpJobExpRate { get; init; }

    // Optional drop-rate overrides. Null = inherit ItemDropRate. There is no
    // Quest item-drop-rate override: quest ITEM COLLECTION drop chance is owned
    // entirely by generated QuestDropRule.Rate / QuestDropResolver, and quest
    // COMPLETION item rewards (getitem) are fixed quantities - neither is a
    // server-wide-rate-scaled monster drop-table roll, so no
    // "quest_item_drop_rate" concept exists here.
    public uint? CardDropRate { get; init; }
    public uint? BossItemDropRate { get; init; }
    public uint? MvpItemDropRate { get; init; }

    // Optional rAthena-style item-category overrides (normal-drop-table items,
    // categorized by item type). Null = inherit ItemDropRate (or the more
    // specific Boss/Mvp override above, when the category itself is unset).
    public uint? ItemRateCommon { get; init; }
    public uint? ItemRateHeal { get; init; }
    public uint? ItemRateUse { get; init; }
    public uint? ItemRateEquip { get; init; }
    public uint? ItemRateCard { get; init; }
    public uint? ItemRateCommonBoss { get; init; }
    public uint? ItemRateHealBoss { get; init; }
    public uint? ItemRateUseBoss { get; init; }
    public uint? ItemRateEquipBoss { get; init; }
    public uint? ItemRateCardBoss { get; init; }
    public uint? ItemRateCommonMvp { get; init; }
    public uint? ItemRateHealMvp { get; init; }
    public uint? ItemRateUseMvp { get; init; }
    public uint? ItemRateEquipMvp { get; init; }
    public uint? ItemRateCardMvp { get; init; }

    // Direct-reward MVP item rate - distinct from the ItemRate*Mvp family above,
    // which are normal drop-table items dropped BY an MVP monster, categorized by
    // item type. ItemRateMvp is the rate for the MVP's own direct reward item.
    public uint? ItemRateMvp { get; init; }

    // Positive rAthena percentage multiplication truncates toward zero. UInt128 keeps
    // the multiplication exact and avoids floating-point precision and overflow.
    public static ulong Apply(ulong raw, uint rate)
    {
        var rated = (UInt128)raw * rate / 100u;
        return rated > long.MaxValue ? long.MaxValue : (ulong)rated;
    }
}
