using System.Buffers.Binary;
using System.Text;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapAuthOkProtocolTests
{
    [Fact]
    public void BuildAuthOkPacket_CarriesSelectedAuthoritativeCharacterName()
    {
        var node = new MapAuthNode(100, 200, 300, 400, 1, "int_land03", 73, 100, 3, 0, 0, 0, false, "ServerOwnedName");

        var packet = MapServerSession.BuildAuthOkPacket(node);

        Assert.Equal(77, packet.Length);
        Assert.Equal(PacketConstants.MapAuthOk, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(node.CharId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(25)));
        Assert.Equal("ServerOwnedName", Encoding.ASCII.GetString(packet.AsSpan(53, PacketConstants.NameLength)).TrimEnd('\0'));
    }
}
