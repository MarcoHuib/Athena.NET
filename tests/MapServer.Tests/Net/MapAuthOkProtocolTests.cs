using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapAuthOkProtocolTests
{
    [Fact]
    public void TryParseAuthOk_RoundTripsAuthoritativeCharacterNameField()
    {
        var packet = new byte[MapAuthOkData.MinimumLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapAuthOk);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 100);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(8), 300);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(12), 400);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(25), 200);
        Encoding.ASCII.GetBytes("int_land03").CopyTo(packet.AsSpan(29));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(45), 73);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(47), 100);
        packet[49] = 3;
        packet[52] = 1;
        Encoding.ASCII.GetBytes("ServerOwnedName").CopyTo(packet.AsSpan(53));

        Assert.True(CharServerConnector.TryParseAuthOk(packet, out var parsed));
        Assert.Equal(100u, parsed.AccountId);
        Assert.Equal(200u, parsed.CharId);
        Assert.Equal("ServerOwnedName", parsed.CharacterName);
    }

    [Fact]
    public void TryParseAuthOk_RejectsLegacyShapeWithoutCharacterName()
    {
        var packet = new byte[53];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapAuthOk);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)packet.Length);

        Assert.False(CharServerConnector.TryParseAuthOk(packet, out _));
    }
}
