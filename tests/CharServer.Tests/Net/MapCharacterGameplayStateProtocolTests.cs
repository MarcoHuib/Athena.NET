using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapCharacterGameplayStateProtocolTests
{
    [Fact]
    public void UpdateRoundTripsAllPersistentFieldsAsOneMessage()
    {
        var expected=new CharacterGameplayStateDto(9,4,1,1,0,0,40,11,40,11,48,0,1,1,1,1,1,1);
        var updated=expected with{BaseLevel=2,BaseExperience=600,CurrentHp=45,MaxHp=45,StatPoints=51};
        var packet=new byte[MapCharacterGameplayStateProtocol.UpdateRequestLength];
        System.Buffers.Binary.BinaryPrimitives.WriteInt16LittleEndian(packet,PacketConstants.MapGameplayStateUpdateRequest);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2),7);
        MapCharacterGameplayStateProtocol.Write(packet.AsSpan(6,MapCharacterGameplayStateProtocol.StateLength),expected);
        MapCharacterGameplayStateProtocol.Write(packet.AsSpan(74,MapCharacterGameplayStateProtocol.StateLength),updated);
        Assert.True(MapCharacterGameplayStateProtocol.TryParseUpdate(packet,out var accountId,out var parsedExpected,out var parsedUpdated));
        Assert.Equal(7U,accountId); Assert.Equal(expected,parsedExpected); Assert.Equal(updated,parsedUpdated);
        var response=MapCharacterGameplayStateProtocol.BuildResponse(PacketConstants.MapGameplayStateUpdateResponse,0,9,updated with{Version=5});
        Assert.Equal(MapCharacterGameplayStateProtocol.ResponseLength,response.Length);
        Assert.Equal((byte)0,response[2]); Assert.Equal((ulong)5,System.Buffers.Binary.BinaryPrimitives.ReadUInt64LittleEndian(response.AsSpan(11,8)));
    }
}
