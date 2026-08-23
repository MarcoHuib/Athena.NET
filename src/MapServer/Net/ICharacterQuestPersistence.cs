using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

public interface ICharacterQuestPersistence
{
    Task<CharacterQuestStatus?> GetQuestStateAsync(uint accountId, uint charId, uint questId, CancellationToken cancellationToken);
    Task<bool> SetQuestStateAsync(uint accountId, uint charId, uint questId, CharacterQuestStatus state, CancellationToken cancellationToken);
}
