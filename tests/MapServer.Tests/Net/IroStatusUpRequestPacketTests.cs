using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Byte-level tests for the verified stock-iRO base-stat-allocation request (statsonly.pcapng -
// see ai/iro-2026-wire.md for the full capture evidence trace).
public sealed class IroStatusUpRequestPacketTests
{
    // Exact captured 2->3 upgrade requests (frames 157/279/304/321/335/353).
    public static IEnumerable<object[]> CapturedRequests =>
    [
        [new byte[] { 0xBB, 0x00, 0x0D, 0x00, 0x01, 0x1E }, CharacterBaseStat.Strength],
        [new byte[] { 0xBB, 0x00, 0x0E, 0x00, 0x01, 0x80 }, CharacterBaseStat.Agility],
        [new byte[] { 0xBB, 0x00, 0x0F, 0x00, 0x01, 0x4F }, CharacterBaseStat.Vitality],
        [new byte[] { 0xBB, 0x00, 0x10, 0x00, 0x01, 0xCB }, CharacterBaseStat.Intelligence],
        [new byte[] { 0xBB, 0x00, 0x11, 0x00, 0x01, 0xC0 }, CharacterBaseStat.Dexterity],
        [new byte[] { 0xBB, 0x00, 0x12, 0x00, 0x01, 0xB8 }, CharacterBaseStat.Luck],
    ];

    [Theory]
    [MemberData(nameof(CapturedRequests))]
    public void TryParse_CapturedRequest_ParsesExactStatAndAmount(byte[] captured, CharacterBaseStat expectedStat)
    {
        Assert.True(IroStatusUpRequestPacket.TryParse(captured, out var value));
        Assert.Equal(expectedStat, value.Stat);
        Assert.Equal((byte)1, value.Amount);
    }

    // The six captured requests carry six DIFFERENT trailing-byte values (0x1E/0x80/0x4F/0xCB/
    // 0xC0/0xB8) - conclusive proof this byte is not a fixed constant. This test proves the
    // parser treats it purely structurally: the same STR request parses identically for
    // gameplay purposes (Stat/Amount) regardless of the trailing byte's value.
    [Theory]
    [InlineData((byte)0x00)]
    [InlineData((byte)0x1E)]
    [InlineData((byte)0xFF)]
    [InlineData((byte)0x42)]
    public void TryParse_OpaqueTrailingByte_NeverAffectsParsedStatOrAmount(byte trailingByte)
    {
        byte[] request = [0xBB, 0x00, 0x0D, 0x00, 0x01, trailingByte];
        Assert.True(IroStatusUpRequestPacket.TryParse(request, out var value));
        Assert.Equal(CharacterBaseStat.Strength, value.Stat);
        Assert.Equal((byte)1, value.Amount);
        Assert.Equal(trailingByte, value.OpaqueTrailingByte);
    }

    [Fact]
    public void TryParse_WrongOpcode_Rejects()
    {
        byte[] wrongOpcode = [0xBC, 0x00, 0x0D, 0x00, 0x01, 0x1E];
        Assert.False(IroStatusUpRequestPacket.TryParse(wrongOpcode, out _));
    }

    [Fact]
    public void TryParse_TruncatedPacket_Rejects()
    {
        byte[] truncated = [0xBB, 0x00, 0x0D, 0x00, 0x01];
        Assert.False(IroStatusUpRequestPacket.TryParse(truncated, out _));
    }

    [Fact]
    public void TryParse_OversizedPacket_Rejects()
    {
        byte[] oversized = [0xBB, 0x00, 0x0D, 0x00, 0x01, 0x1E, 0x00];
        Assert.False(IroStatusUpRequestPacket.TryParse(oversized, out _));
    }

    [Fact]
    public void TryParse_EmptyPacket_Rejects()
    {
        Assert.False(IroStatusUpRequestPacket.TryParse(ReadOnlySpan<byte>.Empty, out _));
    }

    // An unrecognized StatusId (e.g. a fourth-job trait stat this project never wires - POW=25
    // in pinned _sp, or any other unmapped value) must still parse STRUCTURALLY (the packet is
    // well-formed) but resolve Stat to null - never silently aliasing onto a different
    // CharacterBaseStat member. The caller (MapClientSession) is responsible for dropping a
    // null-Stat request before it ever reaches CharacterStatService.
    [Fact]
    public void TryParse_UnrecognizedStatusId_ParsesStructurally_ButResolvesNullStat()
    {
        byte[] unknownStatus = [0xBB, 0x00, 0x19, 0x00, 0x01, 0x1E]; // StatusId 25 (SP_POW), not wired
        Assert.True(IroStatusUpRequestPacket.TryParse(unknownStatus, out var value));
        Assert.Null(value.Stat);
    }

    [Theory]
    [InlineData(CharacterBaseStat.Strength, (ushort)13)]
    [InlineData(CharacterBaseStat.Agility, (ushort)14)]
    [InlineData(CharacterBaseStat.Vitality, (ushort)15)]
    [InlineData(CharacterBaseStat.Intelligence, (ushort)16)]
    [InlineData(CharacterBaseStat.Dexterity, (ushort)17)]
    [InlineData(CharacterBaseStat.Luck, (ushort)18)]
    public void WireStatusId_MatchesCapturedStatusIds(CharacterBaseStat stat, ushort expectedStatusId)
    {
        Assert.Equal(expectedStatusId, IroStatusUpRequestPacket.WireStatusId(stat));
    }
}
