using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

public interface ICharacterGameplayStatePersistence
{
    Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken);
    Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken);
}

// A skill-learn mutation is a COMPOSITE change of CharacterGameplayState (SkillPoints, Version)
// and CharacterSkillSnapshot (the learned skill's level) that must be replaced together, under the
// SAME per-character mutation lock this session already uses for MutateAsync - never a second,
// independently-locked session for skills (see ai/map-server.md for the rationale: two locks would
// let one mutation act on a stale SkillPoints/Version pair while the other is mid-flight).
public sealed class CharacterGameplayStateSession
{
    private readonly ICharacterGameplayStatePersistence _persistence;
    private readonly ICharacterSkillPersistence? _skillPersistence;
    private readonly uint _accountId;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public CharacterGameplayStateSession(uint accountId, CharacterGameplayState initialState, ICharacterGameplayStatePersistence persistence)
        : this(accountId, initialState, persistence, CharacterSkillSnapshot.Empty, null)
    {
    }

    public CharacterGameplayStateSession(
        uint accountId,
        CharacterGameplayState initialState,
        ICharacterGameplayStatePersistence persistence,
        CharacterSkillSnapshot initialSkills,
        ICharacterSkillPersistence? skillPersistence)
    {
        _accountId = accountId;
        State = initialState;
        _persistence = persistence;
        Skills = initialSkills;
        _skillPersistence = skillPersistence;
    }

    public CharacterGameplayState State { get; private set; }
    public CharacterSkillSnapshot Skills { get; private set; }

    public async Task<CharacterGameplayState?> MutateAsync(Func<CharacterGameplayState, CharacterGameplayState> mutation, CancellationToken cancellationToken)
    {
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            var expected = State;
            var candidate = mutation(expected) with { CharacterId = expected.CharacterId, Version = expected.Version };
            var persisted = await _persistence.UpdateAsync(_accountId, expected, candidate, cancellationToken);
            if (persisted is not null) State = persisted;
            return persisted;
        }
        finally { _mutationLock.Release(); }
    }

    // Validates and, if valid, atomically persists a one-level skill-point spend, then replaces
    // BOTH State and Skills together before releasing the lock - see this type's own doc comment.
    // Returns null on validation rejection OR persistence failure; in both cases neither State nor
    // Skills is mutated. CharacterSkillService remains fully static/pure - this is the one place
    // that calls into it, exactly the way CharacterProgressionService.AddExperienceAsync calls its
    // own pure Calculate.
    public async Task<CharacterSkillLearnResult?> LearnSkillAsync(
        GeneratedSkillTreeDefinition tree,
        ushort requestedSkillId,
        CancellationToken cancellationToken)
    {
        if (_skillPersistence is null) throw new InvalidOperationException("This session was not constructed with skill persistence.");
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            var validation = CharacterSkillService.ValidateUpgrade(State, Skills, tree, requestedSkillId);
            if (!validation.IsValid) return null;
            var expectedCurrentLevel = Skills.CurrentLevel(requestedSkillId);
            var result = await _skillPersistence.LearnSkillAsync(_accountId, State, requestedSkillId, expectedCurrentLevel, cancellationToken);
            if (result is null) return null;
            State = result.GameplayState;
            Skills = Skills.WithLearnedSkill(requestedSkillId, result.NewSkillLevel);
            return result;
        }
        finally { _mutationLock.Release(); }
    }

    // Validates and, if valid, atomically persists a base-stat increase (STR/AGI/VIT/INT/DEX/
    // LUK). Unlike LearnSkillAsync this mutates ONLY fields already inside
    // CharacterGameplayState (the target stat, StatPoints, Version), so it reuses the plain
    // ICharacterGameplayStatePersistence.UpdateAsync optimistic-concurrency path through this
    // session's existing MutateAsync rather than a second persistence interface - see
    // ai/map-server.md section 15 ("reuse existing gameplay-state persistence where possible").
    // Returns null on validation rejection OR persistence failure (including a stale
    // GameplayStateVersion); in both cases State is left unchanged and no Status Points are
    // spent. CharacterStatService remains fully static/pure - this is the one place that calls
    // into it, exactly the way LearnSkillAsync is the one place that calls CharacterSkillService.
    public async Task<CharacterStatIncreaseResult?> IncreaseStatAsync(
        CharacterBaseStat stat,
        int increaseAmount,
        CancellationToken cancellationToken)
    {
        await _mutationLock.WaitAsync(cancellationToken);
        try
        {
            var before = State;
            var validation = CharacterStatService.ValidateIncrease(before, stat, increaseAmount);
            if (!validation.IsValid) return null;
            var candidate = ApplyStat(before, stat, validation.NewValue) with { StatPoints = validation.RemainingStatusPoints };
            var persisted = await _persistence.UpdateAsync(_accountId, before, candidate, cancellationToken);
            if (persisted is null) return null;
            State = persisted;
            return new CharacterStatIncreaseResult(before, persisted, stat, validation.PreviousValue, validation.NewValue, validation.StatusPointsSpent);
        }
        finally { _mutationLock.Release(); }
    }

    private static CharacterGameplayState ApplyStat(CharacterGameplayState state, CharacterBaseStat stat, ushort newValue) => stat switch
    {
        CharacterBaseStat.Strength => state with { Strength = newValue },
        CharacterBaseStat.Agility => state with { Agility = newValue },
        CharacterBaseStat.Vitality => state with { Vitality = newValue },
        CharacterBaseStat.Intelligence => state with { Intelligence = newValue },
        CharacterBaseStat.Dexterity => state with { Dexterity = newValue },
        CharacterBaseStat.Luck => state with { Luck = newValue },
        _ => throw new ArgumentOutOfRangeException(nameof(stat), stat, "Unknown base stat."),
    };
}

// Immutable committed outcome of CharacterGameplayStateSession.IncreaseStatAsync. Before/After
// are the full POST-COMMIT gameplay state on either side of the mutation - mirrors
// CharacterProgressionResult's own Before/After convention. Deliberately carries no
// packet-specific fields (see CharacterStatService's own doc comment on keeping protocol
// constants out of the pure gameplay layer) - a future wire boundary projects a response from
// this result, never the reverse.
public sealed record CharacterStatIncreaseResult(
    CharacterGameplayState Before,
    CharacterGameplayState After,
    CharacterBaseStat Stat,
    ushort PreviousValue,
    ushort NewValue,
    uint StatusPointsSpent)
{
    public bool ValueChanged => NewValue != PreviousValue;
    public uint StatusPointsChanged => StatusPointsSpent;
}
