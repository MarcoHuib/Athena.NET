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
}
