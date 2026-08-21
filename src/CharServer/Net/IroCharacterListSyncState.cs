using Athena.Net.CharServer.Db.Entities;

namespace Athena.Net.CharServer.Net;

internal sealed class IroCharacterListSyncState
{
    private readonly IReadOnlyList<CharCharacter> _characters;

    public IroCharacterListSyncState(IReadOnlyList<CharCharacter> characters)
    {
        _characters = characters.OrderBy(character => character.CharNum).ToArray();
    }

    public int RequestsReceived { get; private set; }
    public bool IsComplete { get; private set; }

    public IReadOnlyList<byte[]> HandleRequest()
    {
        RequestsReceived++;
        if (IsComplete)
        {
            return Array.Empty<byte[]>();
        }

        IsComplete = true;
        return ClientSession.BuildIroCharacterListResponses(_characters);
    }
}
