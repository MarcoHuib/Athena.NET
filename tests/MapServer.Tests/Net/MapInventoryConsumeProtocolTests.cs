using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapInventoryConsumeProtocolTests
{
    [Fact]
    public void Request_HasExplicitEighteenByteLayout()
    {
        var packet = MapInventoryConsumeProtocol.BuildRequest(accountId: 2_000_000, charId: 9, durableId: 2, amount: 1);

        Assert.Equal(18, packet.Length);
        Assert.Equal(new byte[] { 0x37, 0x2b, 0x80, 0x84, 0x1e, 0x00, 9, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0 }, packet);
    }

    [Fact]
    public void Response_ParsesRowDeletedLayout()
    {
        byte[] packet = [0x38, 0x2b, 1, 9, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 1];

        Assert.True(MapInventoryConsumeProtocol.TryParseResponse(packet, out var success, out var charId, out var durableId, out var newAmount, out var rowDeleted));
        Assert.True(success);
        Assert.Equal(9u, charId);
        Assert.Equal(2u, durableId);
        Assert.Equal(0u, newAmount);
        Assert.True(rowDeleted);
    }

    [Fact]
    public void Response_ParsesAmountDecrementedLayout()
    {
        byte[] packet = [0x38, 0x2b, 1, 9, 0, 0, 0, 2, 0, 0, 0, 4, 0, 0, 0, 0];

        Assert.True(MapInventoryConsumeProtocol.TryParseResponse(packet, out var success, out _, out _, out var newAmount, out var rowDeleted));
        Assert.True(success);
        Assert.Equal(4u, newAmount);
        Assert.False(rowDeleted);
    }

    [Fact]
    public void Response_WrongLengthOrOpcode_IsRejected()
    {
        byte[] packet = [0x38, 0x2b, 1, 9, 0, 0, 0, 2, 0, 0, 0, 4, 0, 0, 0, 0];
        Assert.False(MapInventoryConsumeProtocol.TryParseResponse([.. packet, 0], out _, out _, out _, out _, out _));

        var wrongOpcode = (byte[])packet.Clone();
        wrongOpcode[0] = 0xff;
        Assert.False(MapInventoryConsumeProtocol.TryParseResponse(wrongOpcode, out _, out _, out _, out _, out _));
    }
}
