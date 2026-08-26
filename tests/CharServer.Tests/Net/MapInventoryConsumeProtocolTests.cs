using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapInventoryConsumeProtocolTests
{
    [Fact]
    public void Request_ParsesExactEighteenByteLayout()
    {
        // opcode 0x2b37 LE, accountId=2000000(0x1E8480), charId=9, durableId=2, amount=1
        byte[] packet = [0x37, 0x2b, 0x80, 0x84, 0x1e, 0x00, 9, 0, 0, 0, 2, 0, 0, 0, 1, 0, 0, 0];

        Assert.True(MapInventoryConsumeProtocol.TryParseRequest(packet, out var accountId, out var charId, out var durableId, out var amount));
        Assert.Equal(2_000_000u, accountId);
        Assert.Equal(9u, charId);
        Assert.Equal(2u, durableId);
        Assert.Equal(1u, amount);
        Assert.False(MapInventoryConsumeProtocol.TryParseRequest(packet[..^1], out _, out _, out _, out _));
    }

    [Fact]
    public void Response_RowDeleted_HasExplicitSixteenByteLayout()
    {
        var packet = MapInventoryConsumeProtocol.BuildResponse(success: true, charId: 9, durableId: 2, newAmount: 0, rowDeleted: true);

        Assert.Equal(16, packet.Length);
        Assert.Equal(new byte[] { 0x38, 0x2b, 1, 9, 0, 0, 0, 2, 0, 0, 0, 0, 0, 0, 0, 1 }, packet);
    }

    [Fact]
    public void Response_AmountDecremented_HasExplicitSixteenByteLayout()
    {
        var packet = MapInventoryConsumeProtocol.BuildResponse(success: true, charId: 9, durableId: 2, newAmount: 4, rowDeleted: false);

        Assert.Equal(16, packet.Length);
        Assert.Equal(new byte[] { 0x38, 0x2b, 1, 9, 0, 0, 0, 2, 0, 0, 0, 4, 0, 0, 0, 0 }, packet);
    }

    [Fact]
    public void Response_Failure_HasResultByteZero()
    {
        var packet = MapInventoryConsumeProtocol.BuildResponse(success: false, charId: 9, durableId: 2, newAmount: 0, rowDeleted: false);

        Assert.Equal((byte)0, packet[2]);
    }
}
