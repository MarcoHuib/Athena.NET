using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroMovementPacketTests
{
    [Theory]
    [InlineData("5F030501E079", 20, 30, 0x79)]
    [InlineData("5F03064170B7", 25, 23, 0xB7)]
    [InlineData("5F030681E01E", 26, 30, 0x1E)]
    [InlineData("5F030E81C0CF", 58, 28, 0xCF)]
    [InlineData("5F0313466040", 77, 102, 0x40)]
    public void TryParseRequest_DecodesCaptureProvenDestination(
        string hex,
        ushort expectedX,
        ushort expectedY,
        byte expectedOpaque)
    {
        var parsed = IroMovementPackets.TryParseRequest(Convert.FromHexString(hex), out var request);

        Assert.True(parsed);
        Assert.Equal(expectedX, request.TargetX);
        Assert.Equal(expectedY, request.TargetY);
        Assert.Equal(expectedOpaque, request.OpaqueExtra);
    }

    [Fact]
    public void TryParseRequest_RejectsWrongIdOrLength()
    {
        Assert.False(IroMovementPackets.TryParseRequest(new byte[5], out _));
        Assert.False(IroMovementPackets.TryParseRequest(new byte[7], out _));
        Assert.False(IroMovementPackets.TryParseRequest(
            new byte[] { 0x60, 0x03, 0x05, 0x01, 0xe0, 0x79 },
            out _));
    }

    [Fact]
    public void BuildResponse_SerializesCaptureEquivalentMovementLayout()
    {
        const uint tick = 0x0e35acf9;

        var packet = IroMovementPackets.BuildResponse(tick, 22, 37, 20, 30);

        Assert.Equal(12, packet.Length);
        Assert.Equal(PacketConstants.ZcNotifyPlayerMove, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(tick, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2, 4)));
        Assert.Equal(new byte[] { 0x05, 0x82, 0x50, 0x50, 0x1e, 0x88 }, packet[6..]);
    }
}
