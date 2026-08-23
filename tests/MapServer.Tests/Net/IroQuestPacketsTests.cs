using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroQuestPacketsTests
{
    [Fact]
    public void AddActive21001_MatchesCapturedHeaderAndFixedLength()
    {
        var packet = IroQuestPackets.BuildAddActive(21001);
        Assert.Equal(155, packet.Length);
        Assert.Equal((short)0x0b0c, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(21001u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal((byte)1, packet[6]);
        Assert.All(packet[7..], value => Assert.Equal((byte)0, value));
    }

    [Fact]
    public void Complete21001_MatchesCapturedDeletePacket()
    {
        Assert.Equal(new byte[] { 0xb4, 0x02, 0x09, 0x52, 0x00, 0x00 }, IroQuestPackets.BuildRemove(21001));
    }

    [Fact]
    public void InvalidQuestId_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IroQuestPackets.BuildAddActive(0));
    }
}
