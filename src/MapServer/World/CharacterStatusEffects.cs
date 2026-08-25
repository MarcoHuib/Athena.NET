namespace Athena.Net.MapServer.World;

// Generic temporary-status foundation. Deliberately not the complete Ragnarok status
// system: it models "a status ID with up to three integer values and an expiration
// instant", matches pinned rAthena sc_start(type, tick, val1[, val2, val3, val4])
// semantics closely enough for Blessing/Increase AGI, and is designed to grow to more
// statuses later without a rewrite. Temporary statuses live only in MapServer runtime
// memory (never persisted to CharacterGameplayState) and use TimeProvider so tests can
// control expiration deterministically without real timers or one Task.Delay per status.
public readonly record struct ActiveStatus(ushort StatusId, int Val1, int Val2, int Val3, DateTimeOffset ExpiresAt);

// Effective (base + active statuses) character stats. Derived on demand from base
// persisted stats plus currently active (non-expired) statuses; never itself persisted.
public readonly record struct EffectiveCharacterStats(
    ushort Strength,
    ushort Agility,
    ushort Vitality,
    ushort Intelligence,
    ushort Dexterity,
    ushort Luck,
    int MoveSpeedHaste,
    int AttackSpeedBonus);

// Per-session mutable status state. Each MapClientSession owns its own instance, so
// concurrent sessions never share mutable status state. Expiration is computed lazily
// on every read (ActiveStatuses / recalculation) against TimeProvider.GetUtcNow() -
// there is no timer/Task.Delay per active status.
public sealed class CharacterStatusEffectState(TimeProvider timeProvider)
{
    private readonly Dictionary<ushort, ActiveStatus> _statuses = [];
    private readonly TimeProvider _timeProvider = timeProvider;

    public static class StatusIds
    {
        // Pinned legacy/rathena/src/map/status.hpp enum sc_type (SC_STONE = 0 origin).
        public const ushort Blessing = 30;
        public const ushort IncreaseAgi = 32;
    }

    // Pinned rAthena sc_start(type, tick, val1) re-applies an active status by
    // overwriting its stored values and duration outright (status_change_start does
    // not stack/extend on top of an existing instance for these status types); it does
    // not average or add to the previous instance. Matches that: the newest call wins.
    //
    // val2/val3 default to 0, matching sc_start's own 3-argument default (no val2/val3
    // supplied). Pinned status_change_start then computes type-specific defaults for
    // some statuses before storing them (e.g. SC_BLESSING's default-value switch sets
    // val2 = val1 for a BL_PC target - MapServer's status state models player targets
    // exclusively, so that specialization is applied unconditionally here rather than
    // duplicated at every call site).
    public void Start(ushort statusId, int durationMilliseconds, int val1, int val2 = 0, int val3 = 0)
    {
        if (durationMilliseconds <= 0) throw new ArgumentOutOfRangeException(nameof(durationMilliseconds), "Status duration must be positive.");
        if (statusId == StatusIds.Blessing) val2 = val1;
        var expiresAt = _timeProvider.GetUtcNow().AddMilliseconds(durationMilliseconds);
        _statuses[statusId] = new ActiveStatus(statusId, val1, val2, val3, expiresAt);
    }

    public bool TryGet(ushort statusId, out ActiveStatus status)
    {
        if (_statuses.TryGetValue(statusId, out status) && status.ExpiresAt > _timeProvider.GetUtcNow()) return true;
        status = default;
        return false;
    }

    // Removes every status whose expiration has passed. Called opportunistically by
    // recalculation; there is no background timer driving this.
    public void PruneExpired()
    {
        var now = _timeProvider.GetUtcNow();
        foreach (var (id, status) in _statuses)
            if (status.ExpiresAt <= now) _statuses.Remove(id);
    }

    public IReadOnlyCollection<ActiveStatus> ActiveStatuses
    {
        get
        {
            PruneExpired();
            return _statuses.Values.ToArray();
        }
    }

    // Derives effective stats from persisted base stats plus every currently active
    // status. Pinned legacy/rathena/src/map/status.cpp semantics, verified against the
    // exact SC_BLESSING/SC_INCREASEAGI val1/val2 usage in status_calc_str/agi/int/dex/hit
    // and status_calc_speed/status_calc_aspd_rate:
    //   - SC_BLESSING val2 (== val1 for a PC target; status_change_start's default-value
    //     switch sets val2 = val1 for BL_PC) adds +val2 to STR, INT, and DEX.
    //   - SC_INCREASEAGI does not modify the AGI stat itself (no val2 assignment exists
    //     in status_change_start's default-value switch for SC_INCREASEAGI, so val2
    //     stays the sc_start(type, tick, val1) default of 0, and status_calc_agi only
    //     adds val2); it instead grants a flat +25 move-speed haste value
    //     (status_calc_speed) and a +val1 attack-speed bonus (status_calc_aspd_rate).
    public EffectiveCharacterStats Recalculate(CharacterGameplayState baseState)
    {
        PruneExpired();
        ushort strength = baseState.Strength, agility = baseState.Agility, vitality = baseState.Vitality,
            intelligence = baseState.Intelligence, dexterity = baseState.Dexterity, luck = baseState.Luck;
        var moveSpeedHaste = 0;
        var attackSpeedBonus = 0;

        if (TryGet(StatusIds.Blessing, out var blessing))
        {
            strength = (ushort)(strength + blessing.Val2);
            intelligence = (ushort)(intelligence + blessing.Val2);
            dexterity = (ushort)(dexterity + blessing.Val2);
        }

        if (TryGet(StatusIds.IncreaseAgi, out var increaseAgi))
        {
            moveSpeedHaste = Math.Max(moveSpeedHaste, 25);
            attackSpeedBonus += increaseAgi.Val1;
        }

        return new EffectiveCharacterStats(strength, agility, vitality, intelligence, dexterity, luck, moveSpeedHaste, attackSpeedBonus);
    }
}
