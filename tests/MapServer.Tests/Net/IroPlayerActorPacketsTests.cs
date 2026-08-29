using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroPlayerActorPacketsTests
{
    [Fact]
    public void BuildStandEntry_ProjectsPronteraIdentityPositionAndDiverseAppearance()
    {
        // Capture fixture identity/name/job/position: FS Brimstone, frame 3296.
        var player = Make(6_266_792, 39_590_120, "FS Brimstone", 4256, 156, 41) with
        {
            Direction = 4, Sex = 1, BaseLevel = 175, HairStyle = 12, HairColor = 7,
            ClothesColor = 3, WeaponAppearance = 1201, ShieldAppearance = 2101,
            HeadBottomAppearance = 501, HeadTopAppearance = 502, HeadMidAppearance = 503,
            BodyStyle = 1,
        };

        var packet = IroPlayerActorPackets.BuildStandEntry(player);

        Assert.Equal(84 + Encoding.ASCII.GetByteCount(player.CharacterName), packet.Length);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(packet.Length, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(0, packet[4]);
        Assert.Equal(player.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)));
        Assert.Equal(player.CharacterId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(9)));
        Assert.Equal(player.JobClass, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(23)));
        Assert.Equal(1201u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(27)));
        Assert.Equal(2101u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(31)));
        Assert.Equal((156, 41, 4), DecodePosition(packet.AsSpan(63, 3)));
        Assert.Equal(player.CharacterName, Encoding.ASCII.GetString(packet.AsSpan(84)));
    }

    [Fact]
    public void BuildStandEntry_NameLengthTracksShortAndLongCaptureNames()
    {
        var shortPlayer = Make(6_019_804, 39_586_360, "iShimmer", 0, 147, 37);
        var longPlayer = Make(6_185_505, 39_400_535, "Closing the Deal", 4064, 164, 27);
        Assert.Equal(84 + 8, IroPlayerActorPackets.BuildStandEntry(shortPlayer).Length);
        Assert.Equal(84 + 16, IroPlayerActorPackets.BuildStandEntry(longPlayer).Length);
    }

    [Fact]
    public void BuildWalkEntry_MatchesAssinMosterCaptureCoordinatesAndDynamicLength()
    {
        var player = Make(6_222_682, 39_000_001, "assin_moster", 4011, 142, 75) with
        {
            Movement = new PlayerMovementPresence(142, 75, 149, 76, 0x12345678),
        };
        var packet = IroPlayerActorPackets.BuildWalkEntry(player);
        Assert.Equal(102, packet.Length); // capture: 90 + "assin_moster".Length
        Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(0x12345678u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(37)));
        Assert.Equal((142, 75, 149, 76, 8, 8), DecodeMovement(packet.AsSpan(67, 6)));
        Assert.Equal("assin_moster", Encoding.ASCII.GetString(packet.AsSpan(90)));
    }

    [Fact]
    public void SpawnEntry_UsesNewEntryShapeWithoutIdleStateByte()
    {
        var packet = IroPlayerActorPackets.BuildSpawnEntry(Make(7, 9, "NewPlayer", 0, 100, 100));
        Assert.Equal((short)0x09fe, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(83 + "NewPlayer".Length, packet.Length);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(68)));
        Assert.Equal("NewPlayer", Encoding.ASCII.GetString(packet.AsSpan(83)));
    }

    [Theory]
    [InlineData("9C005AF35E00020000")]
    [InlineData("9C005AF35E00000001")]
    public void Direction_MatchesExactPronteraFixtures(string expectedHex)
    {
        var expected = Convert.FromHexString(expectedHex);
        var packet = IroPlayerActorPackets.BuildDirection(6_222_682, expected[6], expected[8]);
        Assert.Equal(expected, packet);
    }

    [Fact]
    public void PlayerInfo_IsExact106ByteAuthoritativeProjection()
    {
        var player = Make(6_266_792, 39_590_120, "FS Brimstone", 4256, 156, 41) with
        {
            PartyName = "goodvibeslord", GuildName = "MidgardSanctuary", GuildPositionName = "Position 20",
        };
        var packet = IroPlayerActorPackets.BuildPlayerInfo(player);
        Assert.Equal(106, packet.Length);
        Assert.Equal((short)0x0a30, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(player.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal("FS Brimstone", ReadFixed(packet.AsSpan(6, 24)));
        Assert.Equal("goodvibeslord", ReadFixed(packet.AsSpan(30, 24)));
        Assert.Equal("MidgardSanctuary", ReadFixed(packet.AsSpan(54, 24)));
        Assert.Equal("Position 20", ReadFixed(packet.AsSpan(78, 24)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(102)));
    }

    [Fact]
    public void Vanish_IsExactCapturedPlayerOutOfSightLayout()
    {
        Assert.Equal(Convert.FromHexString("80005AF35E0000"), IroPlayerActorPackets.BuildVanish(6_222_682));
    }

    private static PlayerPresence Make(uint actorId, uint charId, string name, ushort job, ushort x, ushort y) =>
        new(actorId, charId, name, "prontera", x, y, 0, 0, null, job, 1, 1, 150,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    private static (int X, int Y, int Direction) DecodePosition(ReadOnlySpan<byte> data) =>
        ((data[0] << 2) | (data[1] >> 6), ((data[1] & 0x3f) << 4) | (data[2] >> 4), data[2] & 0xf);

    private static (int X1, int Y1, int X2, int Y2, int SubX, int SubY) DecodeMovement(ReadOnlySpan<byte> data) =>
        ((data[0] << 2) | (data[1] >> 6), ((data[1] & 0x3f) << 4) | (data[2] >> 4),
            ((data[2] & 0xf) << 6) | (data[3] >> 2), ((data[3] & 0x3) << 8) | data[4], data[5] >> 4, data[5] & 0xf);

    private static string ReadFixed(ReadOnlySpan<byte> value)
    {
        var end = value.IndexOf((byte)0);
        return Encoding.ASCII.GetString(end < 0 ? value : value[..end]);
    }
}
