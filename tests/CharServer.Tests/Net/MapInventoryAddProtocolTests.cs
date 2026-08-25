using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapInventoryAddProtocolTests
{
    [Fact]
    public void Request_ParsesExactEighteenByteLayout()
    {
        byte[] packet = [0x31, 0x2b, 7, 0, 0, 0, 100, 0, 0, 0, 0x78, 0x17, 0, 0, 1, 0, 0, 0];

        Assert.True(MapInventoryAddProtocol.TryParseRequest(packet, out var request));
        Assert.Equal(7u, request.AccountId);
        Assert.Equal(100u, request.CharId);
        Assert.Equal(6008, request.ItemId);
        Assert.Equal(1u, request.Amount);
        Assert.False(MapInventoryAddProtocol.TryParseRequest(packet[..^1], out _));
    }

    [Theory]
    [InlineData(true, 1)]
    [InlineData(false, 0)]
    public void Response_HasExplicitNineteenByteLayout(bool success, byte expectedResult)
    {
        var packet = MapInventoryAddProtocol.BuildResponse(100, 6008, 2, slotIndex: 3, success);

        Assert.Equal(19, packet.Length);
        Assert.Equal(new byte[] { 0x32, 0x2b, 100, 0, 0, 0, 0x78, 0x17, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, expectedResult }, packet);
    }
}
