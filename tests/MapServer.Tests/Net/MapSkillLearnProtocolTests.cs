using System.Buffers.Binary;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapSkillLearnProtocolTests
{
    private static CharacterGameplayState State() => new(9, 10, 0, 2, 2, 0, 0, 40, 11, 40, 11, 48, 1, 1, 1, 1, 1, 1, 1);

    [Fact]
    public void BuildRequest_WritesAccountExpectedStateSkillIdAndCurrentLevel()
    {
        var packet = MapSkillLearnProtocol.BuildRequest(7, State(), skillId: 1, expectedCurrentLevel: 0);

        Assert.Equal(MapSkillLearnProtocol.RequestLength, packet.Length);
        Assert.Equal(PacketConstants.MapSkillLearnRequest, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(7U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        var state = MapCharacterGameplayStateProtocol.ReadState(packet.AsSpan(6, MapCharacterGameplayStateProtocol.StateLength));
        Assert.Equal(State(), state);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(76)));
        Assert.Equal((byte)0, packet[78]);
    }

    [Fact]
    public void TryParseResponse_Success_AttachesRequestedSkillIdToResult()
    {
        var newState = State() with { Version = 11, SkillPoints = 0 };
        var packet = MapSkillLearnProtocol.BuildResponse(0, 9, newState, newSkillLevel: 1);

        Assert.True(MapSkillLearnProtocol.TryParseResponse(packet, out var result, out var charId, out var learnResult, requestedSkillId: 1));
        Assert.Equal((byte)0, result);
        Assert.Equal(9U, charId);
        Assert.NotNull(learnResult);
        Assert.Equal((ushort)1, learnResult!.SkillId);
        Assert.Equal((byte)1, learnResult.NewSkillLevel);
        Assert.Equal(newState, learnResult.GameplayState);
    }

    [Fact]
    public void TryParseResponse_Failure_ReturnsNullLearnResult()
    {
        var packet = MapSkillLearnProtocol.BuildResponse(2, 9, null, 0);

        Assert.True(MapSkillLearnProtocol.TryParseResponse(packet, out var result, out var charId, out var learnResult, requestedSkillId: 1));
        Assert.Equal((byte)2, result);
        Assert.Equal(9U, charId);
        Assert.Null(learnResult);
    }

    [Fact]
    public void TryParseResponse_WrongOpcodeOrLength_ReturnsFalse()
    {
        var wrongOpcode = new byte[MapSkillLearnProtocol.ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(wrongOpcode, PacketConstants.MapGameplayStateGetResponse);
        Assert.False(MapSkillLearnProtocol.TryParseResponse(wrongOpcode, out _, out _, out _, requestedSkillId: 1));

        var wrongLength = new byte[MapSkillLearnProtocol.ResponseLength - 1];
        Assert.False(MapSkillLearnProtocol.TryParseResponse(wrongLength, out _, out _, out _, requestedSkillId: 1));
    }
}
