using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapSavePointProtocolTests
{
    [Fact]
    public void RequestParsingAndAcknowledgementAreDeterministic()
    {
        var packet = new byte[MapSavePointProtocol.RequestLength];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSavePointRequest);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), 10);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), 20);
        System.Text.Encoding.ASCII.GetBytes("izlude_d").CopyTo(packet.AsSpan(10));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(26), 128);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(28), 142);

        Assert.True(MapSavePointProtocol.TryParseRequest(packet, out var request));
        Assert.Equal(new MapSavePointRequest(10, 20, "izlude_d", 128, 142), request);
        Assert.False(MapSavePointProtocol.TryParseRequest(packet[..^1], out _));
        Assert.Equal(7, MapSavePointProtocol.BuildResponse(20, true).Length);
    }
}
