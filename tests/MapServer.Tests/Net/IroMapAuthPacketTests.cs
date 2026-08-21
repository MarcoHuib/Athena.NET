using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroMapAuthPacketTests
{
    private const uint AccountId = 0x11223344;
    private const uint CharId = 0x55667788;
    private const uint LoginId1 = 0x99aabbcc;

    [Fact]
    public void TryParse_AcceptsSanitized1001BytePacketAndReadsProvenHeaderFields()
    {
        var packet = BuildPacket();

        var success = IroMapAuthPacket.TryParse(packet, out var parsed);

        Assert.True(success);
        Assert.Equal(AccountId, parsed.AccountId);
        Assert.Equal(CharId, parsed.CharId);
        Assert.Equal(LoginId1, parsed.LoginId1);
    }

    [Theory]
    [InlineData(999)]
    [InlineData(1000)]
    public void TryParse_RejectsIncompletePacketsWithoutReadingOutOfBounds(int length)
    {
        var packet = BuildPacket()[..length];

        Assert.False(IroMapAuthPacket.TryParse(packet, out _));
    }

    [Fact]
    public void TryParse_RejectsWrongPacketId()
    {
        var packet = BuildPacket();
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 0xffff);

        Assert.False(IroMapAuthPacket.TryParse(packet, out _));
    }

    private static byte[] BuildPacket()
    {
        var packet = new byte[PacketConstants.IroCzMapAuthLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzMapAuth);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), AccountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), CharId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), LoginId1);
        return packet;
    }
}
