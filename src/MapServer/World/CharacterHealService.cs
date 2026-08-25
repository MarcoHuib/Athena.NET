using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.World;

public readonly record struct CharacterHealResult(CharacterGameplayState Before, CharacterGameplayState After)
{
    public bool HpChanged => Before.CurrentHp != After.CurrentHp;
    public bool SpChanged => Before.CurrentSp != After.CurrentSp;
}

// Generic `heal` runtime capability. Pinned legacy/rathena/src/map/script.cpp
// BUILDIN_FUNC(heal) calls status_heal(sd, hp, sp, 1), which clamps the resulting
// HP/SP to [0, MaxHp]/[0, MaxSp] (a heal past the maximum simply stops at the
// maximum; status_heal does not apply overflow elsewhere). This slice only supports
// non-negative heal amounts, matching every currently generated caller (e.g.
// Captain Carocc's `heal 9999,0`); status_heal's negative/damage path is out of scope.
public sealed class CharacterHealService(CharacterGameplayStateSession stateSession)
{
    public async Task<CharacterHealResult?> HealAsync(int hp, int sp, CancellationToken cancellationToken)
    {
        if (hp < 0 || sp < 0) throw new ArgumentOutOfRangeException(nameof(hp), "This heal slice supports non-negative amounts only.");
        var before = stateSession.State;
        var after = before with
        {
            CurrentHp = (uint)Math.Min((long)before.CurrentHp + hp, before.MaxHp),
            CurrentSp = (uint)Math.Min((long)before.CurrentSp + sp, before.MaxSp),
        };
        if (after == before) return new(before, before);
        var persisted = await stateSession.MutateAsync(_ => after, cancellationToken);
        return persisted is null ? null : new(before, persisted);
    }
}
