using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapQuestStateProtocolTests
{
    [Fact]
    public void Request_HasExplicitFifteenByteLayout()
    {
        var packet = MapQuestStateProtocol.BuildRequest(7, 9, 21001, CharacterQuestStatus.Active);

        Assert.Equal(15, packet.Length);
        Assert.Equal([0x29, 0x2b, 7, 0, 0, 0, 9, 0, 0, 0, 0x09, 0x52, 0, 0, 1], packet);
    }

    [Fact]
    public void Response_ParsesExplicitTwelveByteSuccessAndFailureLayouts()
    {
        byte[] success = [0x2a, 0x2b, 9, 0, 0, 0, 0x09, 0x52, 0, 0, 1, 1];
        byte[] failure = [0x2a, 0x2b, 9, 0, 0, 0, 0x09, 0x52, 0, 0, 0, 0];

        Assert.True(MapQuestStateProtocol.TryParseResponse(success, out var charId, out var questId, out var state));
        Assert.Equal((uint)9, charId);
        Assert.Equal((uint)21001, questId);
        Assert.Equal(CharacterQuestStatus.Active, state);
        Assert.True(MapQuestStateProtocol.TryParseResponse(failure, out _, out _, out state));
        Assert.Null(state);
        Assert.False(MapQuestStateProtocol.TryParseResponse([.. success, 0], out _, out _, out _));
    }
}
