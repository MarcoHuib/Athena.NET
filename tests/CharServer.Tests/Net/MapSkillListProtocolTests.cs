using System.Buffers.Binary;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapSkillListProtocolTests
{
    [Fact]
    public void TryParseGet_ValidPacket_ReadsAccountAndCharId()
    {
        var packet = new byte[MapSkillListProtocol.GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSkillListGetRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), 100);

        Assert.True(MapSkillListProtocol.TryParseGet(packet, out var accountId, out var charId));
        Assert.Equal(7U, accountId);
        Assert.Equal(100U, charId);
    }

    [Fact]
    public void TryParseGet_WrongOpcodeOrLength_ReturnsFalse()
    {
        var wrongOpcode = new byte[MapSkillListProtocol.GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(wrongOpcode, PacketConstants.MapGameplayStateGetRequest);
        Assert.False(MapSkillListProtocol.TryParseGet(wrongOpcode, out _, out _));

        var wrongLength = new byte[MapSkillListProtocol.GetRequestLength - 1];
        Assert.False(MapSkillListProtocol.TryParseGet(wrongLength, out _, out _));
    }

    [Fact]
    public void BuildResponse_MultipleRows_WritesLengthPrefixAndAllSkills()
    {
        var rows = new List<CharacterSkillRowDto>
        {
            new(SkillId: 1, Level: 3),  // NV_BASIC
            new(SkillId: 142, Level: 1), // NV_FIRSTAID
        };
        var response = MapSkillListProtocol.BuildResponse(0, 100, rows);

        Assert.Equal(MapSkillListProtocol.ResponseHeaderLength + 2 * MapSkillListProtocol.SkillLength, response.Length);
        Assert.Equal(PacketConstants.MapSkillListGetResponse, BinaryPrimitives.ReadInt16LittleEndian(response));
        Assert.Equal((ushort)response.Length, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(2)));
        Assert.Equal((byte)0, response[4]);
        Assert.Equal(100U, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(5)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(9)));

        var firstRow = response.AsSpan(MapSkillListProtocol.ResponseHeaderLength, MapSkillListProtocol.SkillLength);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(firstRow));
        Assert.Equal((byte)3, firstRow[2]);

        var secondRow = response.AsSpan(MapSkillListProtocol.ResponseHeaderLength + MapSkillListProtocol.SkillLength, MapSkillListProtocol.SkillLength);
        Assert.Equal((ushort)142, BinaryPrimitives.ReadUInt16LittleEndian(secondRow));
        Assert.Equal((byte)1, secondRow[2]);
    }

    [Fact]
    public void BuildResponse_FailureResult_EmitsHeaderOnlyNoSkills()
    {
        var response = MapSkillListProtocol.BuildResponse(1, 100, null);

        Assert.Equal(MapSkillListProtocol.ResponseHeaderLength, response.Length);
        Assert.Equal((byte)1, response[4]);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(9)));
    }

    [Fact]
    public void BuildResponse_NoLearnedSkills_EmitsHeaderOnlyNoSkills()
    {
        var response = MapSkillListProtocol.BuildResponse(0, 100, new List<CharacterSkillRowDto>());

        Assert.Equal(MapSkillListProtocol.ResponseHeaderLength, response.Length);
        Assert.Equal((byte)0, response[4]);
    }
}
