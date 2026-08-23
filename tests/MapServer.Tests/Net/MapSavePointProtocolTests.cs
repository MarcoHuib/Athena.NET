using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapSavePointProtocolTests
{
    [Fact]
    public void RequestAndResponseHaveStableExplicitLayouts()
    {
        var request = MapSavePointProtocol.BuildRequest(10, 20, "izlude_d", 128, 142);
        Assert.Equal(30, request.Length);
        Assert.Equal(PacketConstants.MapSavePointRequest, BinaryPrimitives.ReadInt16LittleEndian(request));
        Assert.Equal((uint)10, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(2)));
        Assert.Equal((uint)20, BinaryPrimitives.ReadUInt32LittleEndian(request.AsSpan(6)));
        Assert.Equal((ushort)128, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(26)));
        Assert.Equal((ushort)142, BinaryPrimitives.ReadUInt16LittleEndian(request.AsSpan(28)));

        var response = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(response, PacketConstants.MapSavePointResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(response.AsSpan(2), 20);
        Assert.True(MapSavePointProtocol.TryParseResponse(response, out var charId, out var success));
        Assert.Equal((uint)20, charId); Assert.True(success);
        Assert.False(MapSavePointProtocol.TryParseResponse(response[..^1], out _, out _));
    }
}
