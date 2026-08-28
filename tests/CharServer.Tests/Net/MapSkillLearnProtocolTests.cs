using System.Buffers.Binary;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapSkillLearnProtocolTests
{
    [Fact]
    public void RequestRoundTripsAccountExpectedStateSkillIdAndCurrentLevel()
    {
        var expected = new CharacterGameplayStateDto(9, 10, 0, 2, 2, 0, 0, 40, 11, 40, 11, 48, 1, 1, 1, 1, 1, 1, 1);
        var packet = new byte[MapSkillLearnProtocol.RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSkillLearnRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), 7);
        MapCharacterGameplayStateProtocol.Write(packet.AsSpan(6, MapCharacterGameplayStateProtocol.StateLength), expected);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(76), 1);
        packet[78] = 0;

        Assert.True(MapSkillLearnProtocol.TryParseRequest(packet, out var accountId, out var parsedExpected, out var skillId, out var expectedCurrentLevel));
        Assert.Equal(7U, accountId);
        Assert.Equal(expected, parsedExpected);
        Assert.Equal((ushort)1, skillId);
        Assert.Equal((byte)0, expectedCurrentLevel);
    }

    [Fact]
    public void TryParseRequest_WrongOpcodeOrLength_ReturnsFalse()
    {
        var wrongOpcode = new byte[MapSkillLearnProtocol.RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(wrongOpcode, PacketConstants.MapGameplayStateGetRequest);
        Assert.False(MapSkillLearnProtocol.TryParseRequest(wrongOpcode, out _, out _, out _, out _));

        var wrongLength = new byte[MapSkillLearnProtocol.RequestLength - 1];
        Assert.False(MapSkillLearnProtocol.TryParseRequest(wrongLength, out _, out _, out _, out _));
    }

    [Fact]
    public void BuildResponse_Success_WritesResultCharIdStateAndNewLevel()
    {
        var newState = new CharacterGameplayStateDto(9, 11, 0, 2, 2, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        var response = MapSkillLearnProtocol.BuildResponse(0, 9, newState, 1);

        Assert.Equal(MapSkillLearnProtocol.ResponseLength, response.Length);
        Assert.Equal(PacketConstants.MapSkillLearnResponse, BinaryPrimitives.ReadInt16LittleEndian(response));
        Assert.Equal((byte)0, response[2]);
        Assert.Equal(9U, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(3)));
        var roundTrippedState = MapCharacterGameplayStateProtocol.Read(response.AsSpan(MapSkillLearnProtocol.ResponseHeaderLength, MapCharacterGameplayStateProtocol.StateLength));
        Assert.Equal(newState, roundTrippedState);
        Assert.Equal((byte)1, response[MapSkillLearnProtocol.ResponseHeaderLength + MapCharacterGameplayStateProtocol.StateLength]);
    }

    [Fact]
    public void BuildResponse_Failure_AlwaysFixedLength_ZeroFilledStateAndLevel()
    {
        var response = MapSkillLearnProtocol.BuildResponse(2, 9, null, 0);

        Assert.Equal(MapSkillLearnProtocol.ResponseLength, response.Length);
        Assert.Equal((byte)2, response[2]);
        Assert.Equal(9U, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(3)));
        Assert.Equal((byte)0, response[MapSkillLearnProtocol.ResponseHeaderLength + MapCharacterGameplayStateProtocol.StateLength]);
    }
}
