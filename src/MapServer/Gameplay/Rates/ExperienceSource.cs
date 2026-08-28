namespace Athena.Net.MapServer.Gameplay.Rates;

// Where a raw Base/Job EXP reward originated. Purely a tag consumed by
// GameplayRateResolver/ExperienceRewardService to pick the correct rate -
// CharacterProgressionService itself never sees or cares about this.
public enum ExperienceSource
{
    Monster,
    Quest,
    Script,
    Mvp,
    Event,
}
