namespace Athena.Net.MapServer.Gameplay.Rates;

// Where a candidate item drop/reward originates. Monster/Boss/Mvp are normal
// drop-table rolls off a killed monster of increasing rarity tier; Quest/Script/
// Event are non-monster-kill drop sources (e.g. a generated quest-drop rule).
public enum DropSource
{
    Monster,
    Boss,
    Mvp,
    Quest,
    Script,
    Event,
}

// rAthena-style item-type category for a normal drop-table entry. "Use" is the
// usable/consumable category EXCLUDING the separate Heal category.
public enum ItemCategory
{
    Common,
    Heal,
    Use,
    Equip,
    Card,
}

// Distinguishes a normal drop-table roll (an item a monster happens to drop,
// categorized by ItemCategory) from an MVP's own direct reward item - these are
// two different rate families (item_rate_*_mvp vs item_rate_mvp) and must never
// be conflated.
public enum RewardKind
{
    NormalDrop,
    MvpReward,
}

// Full context for one drop-rate resolution. Constructed by whatever code is
// about to roll/scale a drop chance and passed into GameplayRateResolver to get
// back the single effective rate for that specific drop.
public readonly record struct DropContext(DropSource Source, RewardKind Kind, ItemCategory? Category = null);
