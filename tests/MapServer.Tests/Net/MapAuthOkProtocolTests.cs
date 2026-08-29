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

    // Exercises MapServer's own reader (CharServerConnector.TryParseAuthOk) against every public
    // appearance field the 77->108-byte internal protocol expansion added, using distinct
    // non-zero/non-default values per field so a swapped offset or wrong width would fail at least
    // one assertion. Complements CharServer.Tests' writer-side coverage of the same offsets -
    // together they prove both ends of the internal protocol agree, not just one side's own
    // self-consistency.
    [Fact]
    public void TryParseAuthOk_RoundTripsAllPublicAppearanceFields()
    {
        var packet = new byte[MapAuthOkData.MinimumLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapAuthOk);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)packet.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(25), 2);
        Encoding.ASCII.GetBytes("prontera").CopyTo(packet.AsSpan(29));
        Encoding.ASCII.GetBytes("Appearance").CopyTo(packet.AsSpan(53));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(77), 11);  // HairStyle
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(79), 12);  // HairColor
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(81), 13);  // ClothesColor
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(83), 14);  // BodyStyle
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(85), 1201); // WeaponAppearance
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(89), 1502); // ShieldAppearance
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(93), 15);  // HeadBottomAppearance
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(95), 16);  // HeadTopAppearance
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(97), 17);  // HeadMidAppearance
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(99), 18);  // RobeAppearance
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(101), 0x1234); // Option
        packet[105] = 1; // Karma
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(106), -7); // Manner

        Assert.True(CharServerConnector.TryParseAuthOk(packet, out var parsed));
        Assert.Equal((ushort)11, parsed.HairStyle);
        Assert.Equal((ushort)12, parsed.HairColor);
        Assert.Equal((ushort)13, parsed.ClothesColor);
        Assert.Equal((ushort)14, parsed.BodyStyle);
        Assert.Equal(1201u, parsed.WeaponAppearance);
        Assert.Equal(1502u, parsed.ShieldAppearance);
        Assert.Equal((ushort)15, parsed.HeadBottomAppearance);
        Assert.Equal((ushort)16, parsed.HeadTopAppearance);
        Assert.Equal((ushort)17, parsed.HeadMidAppearance);
        Assert.Equal((ushort)18, parsed.RobeAppearance);
        Assert.Equal((uint)0x1234, parsed.Option);
        Assert.Equal((byte)1, parsed.Karma);
        Assert.Equal((short)-7, parsed.Manner);
    }
}
