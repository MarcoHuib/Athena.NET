using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

public interface ICharacterGameplayStatePersistence
{
    Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken);
    Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken);
}

public sealed class CharacterGameplayStateSession
{
    private readonly ICharacterGameplayStatePersistence _persistence;
    private readonly uint _accountId;
    private readonly SemaphoreSlim _mutationLock = new(1, 1);

    public CharacterGameplayStateSession(uint accountId, CharacterGameplayState initialState, ICharacterGameplayStatePersistence persistence)
    {
        _accountId = accountId;
        State = initialState;
        _persistence = persistence;
    }

    public CharacterGameplayState State { get; private set; }

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
}
