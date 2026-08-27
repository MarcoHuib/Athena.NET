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
    public void Response_HasExplicitTwentyEightByteLayout(bool success, byte expectedResult)
    {
        var packet = MapInventoryAddProtocol.BuildResponse(
            charId: 100, itemId: 6008, newAmount: 2, durableId: 3, equip: 0x000002,
            identified: true, refine: 4, favorite: 1, bound: 0, isNewRow: true, success);

        Assert.Equal(28, packet.Length);
        Assert.Equal(
            new byte[] { 0x32, 0x2b, 100, 0, 0, 0, 0x78, 0x17, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 2, 0, 0, 0, 1, 4, 1, 0, 1, expectedResult },
            packet);
    }

    [Fact]
    public void Response_IsNewRowFalse_WritesZeroInIsNewRowByte()
    {
        var packet = MapInventoryAddProtocol.BuildResponse(
            charId: 100, itemId: 6008, newAmount: 6, durableId: 2, equip: 0,
            identified: false, refine: 0, favorite: 0, bound: 0, isNewRow: false, success: true);

        Assert.Equal((byte)0, packet[26]);
        Assert.Equal((byte)1, packet[27]);
    }
}
