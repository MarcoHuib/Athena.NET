using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroUseItemRequestPacketTests
{
    // Live-captured bytes (see ai/map-server.md "Item-use request"):
    // A7 00 04 00 80 84 1E 00 D2
    private static readonly byte[] LiveCapturedPacket = [0xa7, 0x00, 0x04, 0x00, 0x80, 0x84, 0x1e, 0x00, 0xd2];

    [Fact]
    public void TryParse_LiveCapturedBytes_ResolvesExactFields()
    {
        Assert.True(IroUseItemRequestPacket.TryParse(LiveCapturedPacket, out var request));
        Assert.Equal((ushort)4, request.ClientIndex);
        Assert.Equal(2_000_000u, request.AccountId);
        Assert.Equal((byte)0xd2, request.OpaqueTrailingByte);
    }

    [Fact]
    public void TryParse_WrongLength_Rejected()
    {
        Assert.False(IroUseItemRequestPacket.TryParse(LiveCapturedPacket[..^1], out _));
        Assert.False(IroUseItemRequestPacket.TryParse([.. LiveCapturedPacket, 0], out _));
    }

    [Fact]
    public void TryParse_WrongOpcode_Rejected()
    {
        var wrongOpcode = (byte[])LiveCapturedPacket.Clone();
        wrongOpcode[0] = 0xff;
        Assert.False(IroUseItemRequestPacket.TryParse(wrongOpcode, out _));
    }
}
