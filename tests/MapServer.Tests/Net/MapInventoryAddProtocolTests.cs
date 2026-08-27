using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapInventoryAddProtocolTests
{
    [Fact]
    public void Request_HasExplicitEighteenByteLayout()
    {
        var packet = MapInventoryAddProtocol.BuildRequest(accountId: 7, charId: 100, itemId: 6008, amount: 1);

        Assert.Equal(18, packet.Length);
        // opcode 0x2b31 LE, accountId=7, charId=100, itemId=6008(0x1778), amount=1
        Assert.Equal([0x31, 0x2b, 7, 0, 0, 0, 100, 0, 0, 0, 0x78, 0x17, 0, 0, 1, 0, 0, 0], packet);
    }

    [Fact]
    public void Response_ParsesSuccessAndFailureLayouts()
    {
        // opcode 0x2b32 LE, charId=100, itemId=6008(0x1778), newAmount=2, durableId=3,
        // equip=0x000002(EQP_HAND_R), identified=1, refine=4, favorite=1, bound=0, isNewRow=1, success=1
        byte[] success = [0x32, 0x2b, 100, 0, 0, 0, 0x78, 0x17, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 2, 0, 0, 0, 1, 4, 1, 0, 1, 1];
        byte[] failure = [0x32, 0x2b, 100, 0, 0, 0, 0x78, 0x17, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0];

        Assert.True(MapInventoryAddProtocol.TryParseResponse(
            success, out var charId, out var itemId, out var newAmount, out var durableId,
            out var equip, out var identified, out var refine, out var favorite, out var bound, out var isNewRow, out var ok));
        Assert.Equal(100u, charId);
        Assert.Equal(6008, itemId);
        Assert.Equal(2u, newAmount);
        Assert.Equal(3u, durableId);
        Assert.Equal(0x000002u, equip);
        Assert.True(identified);
        Assert.Equal(4, refine);
        Assert.Equal(1, favorite);
        Assert.Equal(0, bound);
        Assert.True(isNewRow);
        Assert.True(ok);

        Assert.True(MapInventoryAddProtocol.TryParseResponse(
            failure, out _, out _, out var failedAmount, out _, out _, out _, out _, out _, out _, out _, out var failedOk));
        Assert.Equal(0u, failedAmount);
        Assert.False(failedOk);
    }

    [Fact]
    public void Response_WrongLengthOrOpcode_IsRejected()
    {
        byte[] success = [0x32, 0x2b, 100, 0, 0, 0, 0x78, 0x17, 0, 0, 2, 0, 0, 0, 3, 0, 0, 0, 2, 0, 0, 0, 1, 4, 1, 0, 1, 1];
        Assert.False(MapInventoryAddProtocol.TryParseResponse([.. success, 0], out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _));

        var wrongOpcode = (byte[])success.Clone();
        wrongOpcode[0] = 0xFF;
        Assert.False(MapInventoryAddProtocol.TryParseResponse(wrongOpcode, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _, out _));
    }
}
