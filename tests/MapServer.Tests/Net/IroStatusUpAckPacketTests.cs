using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Byte-level tests for the verified stock-iRO base-stat-allocation success ack (statsonly.pcapng
// - see ai/iro-2026-wire.md for the full capture evidence trace).
public sealed class IroStatusUpAckPacketTests
{
    public static IEnumerable<object[]> CapturedAcks =>
    [
        [(ushort)13, (byte)3, new byte[] { 0xBC, 0x00, 0x0D, 0x00, 0x01, 0x03 }],
        [(ushort)14, (byte)3, new byte[] { 0xBC, 0x00, 0x0E, 0x00, 0x01, 0x03 }],
        [(ushort)15, (byte)3, new byte[] { 0xBC, 0x00, 0x0F, 0x00, 0x01, 0x03 }],
        [(ushort)16, (byte)3, new byte[] { 0xBC, 0x00, 0x10, 0x00, 0x01, 0x03 }],
        [(ushort)17, (byte)3, new byte[] { 0xBC, 0x00, 0x11, 0x00, 0x01, 0x03 }],
        [(ushort)18, (byte)3, new byte[] { 0xBC, 0x00, 0x12, 0x00, 0x01, 0x03 }],
    ];

    [Theory]
    [MemberData(nameof(CapturedAcks))]
    public void BuildSuccess_MatchesCapturedBytesExactly(ushort statusId, byte newValue, byte[] expected)
    {
        Assert.Equal(expected, IroStatusUpAckPacket.BuildSuccess(statusId, newValue));
    }

    [Fact]
    public void BuildSuccess_ProducesExactlySixBytes()
    {
        Assert.Equal(6, IroStatusUpAckPacket.BuildSuccess(13, 99).Length);
    }

    [Fact]
    public void BuildSuccess_UsesNewValue_NotSomeOtherField()
    {
        var packet = IroStatusUpAckPacket.BuildSuccess(13, 42);
        Assert.Equal((short)0x00bc, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((ushort)13, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal((byte)1, packet[4]); // Result = success
        Assert.Equal((byte)42, packet[5]);
    }
}
