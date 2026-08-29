using System.Buffers.Binary;
using System.Text;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapAuthOkProtocolTests
{
    [Fact]
    public void BuildAuthOkPacket_CarriesSelectedAuthoritativeCharacterName()
    {
        var node = new MapAuthNode(100, 200, 300, 400, 1, "int_land03", 73, 100, 3, 0, 0, 0, false, "ServerOwnedName",
            HairStyle: 7, HairColor: 8, ClothesColor: 9, BodyStyle: 1, WeaponAppearance: 1201,
            ShieldAppearance: 2101, HeadBottomAppearance: 501, HeadTopAppearance: 502,
            HeadMidAppearance: 503, RobeAppearance: 504, Option: 5, Karma: 1, Manner: -2);

        var packet = MapServerSession.BuildAuthOkPacket(node);

        Assert.Equal(108, packet.Length);
        Assert.Equal(PacketConstants.MapAuthOk, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(node.CharId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(25)));
        Assert.Equal("ServerOwnedName", Encoding.ASCII.GetString(packet.AsSpan(53, PacketConstants.NameLength)).TrimEnd('\0'));
        Assert.Equal((ushort)7, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(77)));
        Assert.Equal(1201u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(85)));
        Assert.Equal(2101u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(89)));
        Assert.Equal((short)-2, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(106)));
    }
}
