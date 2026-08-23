using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapQuestStateProtocolTests
{
    [Fact]
    public void Request_ParsesExactFifteenByteLayout()
    {
        byte[] packet = [0x29, 0x2b, 7, 0, 0, 0, 9, 0, 0, 0, 0x09, 0x52, 0, 0, 1];

        Assert.True(MapQuestStateProtocol.TryParseRequest(packet, out var request));
        Assert.Equal((uint)7, request.AccountId);
        Assert.Equal((uint)9, request.CharId);
        Assert.Equal((uint)21001, request.QuestId);
        Assert.Equal((byte)1, request.Operation);
        Assert.False(MapQuestStateProtocol.TryParseRequest(packet[..^1], out _));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Response_HasExplicitTwelveByteLayout(bool success, byte expectedResult)
    {
        var packet = MapQuestStateProtocol.BuildResponse(9, 21001, 1, success);

        Assert.Equal(12, packet.Length);
        Assert.Equal(new byte[] { 0x2a, 0x2b, 9, 0, 0, 0, 0x09, 0x52, 0, 0, 1, expectedResult }, packet);
    }
}
