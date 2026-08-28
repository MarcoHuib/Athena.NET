using System.Buffers.Binary;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapSkillListProtocolTests
{
    [Fact]
    public void BuildGetRequest_WritesOpcodeAccountAndCharId()
    {
        var packet = MapSkillListProtocol.BuildGetRequest(7, 100);

        Assert.Equal(MapSkillListProtocol.GetRequestLength, packet.Length);
        Assert.Equal(PacketConstants.MapSkillListGetRequest, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(7U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal(100U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6)));
    }

    [Fact]
    public void TryParseResponse_Success_WithSkills_ReturnsSnapshot()
    {
        var skills = CharacterSkillSnapshot.FromLogin([(1, 3), (142, 1)]);
        var packet = MapSkillListProtocol.BuildResponse(0, 100, skills);

        Assert.True(MapSkillListProtocol.TryParseResponse(packet, out var result, out var charId, out var read));
        Assert.Equal((byte)0, result);
        Assert.Equal(100U, charId);
        Assert.True(read.Succeeded);
        Assert.Equal((byte)3, read.Snapshot!.CurrentLevel(1));
        Assert.Equal((byte)1, read.Snapshot.CurrentLevel(142));
    }

    [Fact]
    public void TryParseResponse_FailureResult_ReturnsFailedRead()
    {
        var packet = MapSkillListProtocol.BuildResponse(1, 100, null);

        Assert.True(MapSkillListProtocol.TryParseResponse(packet, out var result, out _, out var read));
        Assert.Equal((byte)1, result);
        Assert.False(read.Succeeded);
        Assert.Null(read.Snapshot);
    }

    [Fact]
    public void TryParseResponse_NoLearnedSkills_ReturnsSuccessWithEmptySnapshot()
    {
        var packet = MapSkillListProtocol.BuildResponse(0, 100, CharacterSkillSnapshot.Empty);

        Assert.True(MapSkillListProtocol.TryParseResponse(packet, out _, out _, out var read));
        Assert.True(read.Succeeded);
        Assert.Empty(read.Snapshot!.Learned);
    }

    [Fact]
    public void TryParseResponse_DeclaredLengthMismatch_ReturnsFalse()
    {
        var packet = MapSkillListProtocol.BuildResponse(0, 100, CharacterSkillSnapshot.Empty);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)(packet.Length + 1));

        Assert.False(MapSkillListProtocol.TryParseResponse(packet, out _, out _, out _));
    }

    [Fact]
    public void TryParseResponse_SkillCountMismatchWithPayload_ReturnsFalse()
    {
        var packet = MapSkillListProtocol.BuildResponse(0, 100, CharacterSkillSnapshot.FromLogin([(1, 1)]));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9), 5); // claims 5 skills but payload only has 1

        Assert.False(MapSkillListProtocol.TryParseResponse(packet, out _, out _, out _));
    }

    [Fact]
    public void TryParseResponse_DuplicateSkillIds_ReturnsFalse()
    {
        var packet = MapSkillListProtocol.BuildResponse(0, 100, CharacterSkillSnapshot.FromLogin([(1, 1)]));
        // Manually corrupt: append a second identical-SkillId row without going through FromLogin
        // (which would itself reject the duplicate) - simulates a malformed wire payload directly.
        var corrupted = new byte[packet.Length + MapSkillListProtocol.SkillLength];
        packet.CopyTo(corrupted, 0);
        BinaryPrimitives.WriteUInt16LittleEndian(corrupted.AsSpan(2), (ushort)corrupted.Length);
        BinaryPrimitives.WriteUInt16LittleEndian(corrupted.AsSpan(9), 2);
        BinaryPrimitives.WriteUInt16LittleEndian(corrupted.AsSpan(packet.Length), 1); // duplicate SkillId 1
        corrupted[packet.Length + 2] = 1;

        Assert.False(MapSkillListProtocol.TryParseResponse(corrupted, out _, out _, out _));
    }

    [Fact]
    public void TryParseResponse_TooShort_ReturnsFalse()
    {
        Assert.False(MapSkillListProtocol.TryParseResponse(new byte[MapSkillListProtocol.ResponseHeaderLength - 1], out _, out _, out _));
    }
}
