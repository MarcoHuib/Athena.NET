namespace Athena.Net.MapServer.Gameplay.Rates;

// Takes a raw Base/Job EXP reward plus its source tag, calls the central
// GameplayRateResolver, and returns the final (already-rated) Base/Job EXP.
// This is the ONLY place a raw reward is turned into a rated reward - callers
// (monster kill handling, generated NPC/script getexp, future MVP/event
// handling) must go through this rather than computing their own rate, and
// CharacterProgressionService must only ever receive the final values this
// returns.
public static class ExperienceRewardService
{
    public static (ulong BaseExperience, ulong JobExperience) ResolveReward(
        GameplayRateOptions rates,
        long rawBaseExperience,
        long rawJobExperience,
        ExperienceSource source)
    {
        if (rawBaseExperience < 0 || rawJobExperience < 0)
            throw new ArgumentOutOfRangeException(nameof(rawBaseExperience), "Experience awards must be non-negative.");

        var (baseRate, jobRate) = GameplayRateResolver.ResolveExperienceRate(rates, source);
        return (
            GameplayRateOptions.Apply((ulong)rawBaseExperience, baseRate),
            GameplayRateOptions.Apply((ulong)rawJobExperience, jobRate));
    }
}
