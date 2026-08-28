using System.Buffers.Binary;
namespace Athena.Net.CharServer.Net;

internal static class MapSkillLearnProtocol
{
    internal const int RequestLength = 2 + 4 + MapCharacterGameplayStateProtocol.StateLength + 2 + 1;
    internal const int ResponseHeaderLength = 7;
    internal const int ResponseLength = ResponseHeaderLength + MapCharacterGameplayStateProtocol.StateLength + 1;

    internal static bool TryParseRequest(byte[] packet, out uint accountId, out CharacterGameplayStateDto expected, out ushort skillId, out byte expectedCurrentLevel)
    {
        accountId = 0; expected = null!; skillId = 0; expectedCurrentLevel = 0;
        if (packet.Length != RequestLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapSkillLearnRequest) return false;
        accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2));
        expected = MapCharacterGameplayStateProtocol.Read(packet.AsSpan(6, MapCharacterGameplayStateProtocol.StateLength));
        skillId = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(76));
        expectedCurrentLevel = packet[78];
        return true;
    }

    internal static byte[] BuildResponse(byte result, uint charId, CharacterGameplayStateDto? newState, byte newSkillLevel)
    {
        var packet = new byte[ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSkillLearnResponse);
        packet[2] = result;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), charId);
        if (result == 0 && newState is not null)
        {
            MapCharacterGameplayStateProtocol.Write(packet.AsSpan(ResponseHeaderLength, MapCharacterGameplayStateProtocol.StateLength), newState);
            packet[ResponseHeaderLength + MapCharacterGameplayStateProtocol.StateLength] = newSkillLevel;
        }
        return packet;
    }
}
