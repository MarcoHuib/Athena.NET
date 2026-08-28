using System.Buffers.Binary;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Fixed-length composite mutation: opcode.W accountId.L expectedGameplayState[70] skillId.W
// expectedCurrentLevel.B = 79 bytes request; opcode.W result.B charId.L newGameplayState[70]
// newSkillLevel.B = 78 bytes response, ALWAYS this exact length (mirrors
// MapCharacterGameplayStateProtocol.BuildResponse's own convention: the state/level bytes are
// simply zero-filled when result != 0, never omitted, so this opcode needs no variable-length
// registration).
//
// Deliberately does NOT carry a MapServer-computed MaxLevel: CharServer only re-validates
// version/points/expected-current-level against its OWN fresh DB state (see
// ai/map-server.md - "MapServer is the gameplay-rule authority; CharServer is the persistence/
// concurrency authority"). expectedGameplayState and expectedCurrentLevel are MapServer-internal,
// already-validated values from CharacterSkillService.ValidateUpgrade - NOT raw client input; this
// is the internal MapServer<->CharServer boundary, not the (not-yet-implemented) client-facing
// skill-up request.
internal static class MapSkillLearnProtocol
{
    internal const int RequestLength = 2 + 4 + MapCharacterGameplayStateProtocol.StateLength + 2 + 1;
    internal const int ResponseHeaderLength = 7;
    internal const int ResponseLength = ResponseHeaderLength + MapCharacterGameplayStateProtocol.StateLength + 1;

    internal static byte[] BuildRequest(uint accountId, CharacterGameplayState expected, ushort skillId, byte expectedCurrentLevel)
    {
        var packet = new byte[RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSkillLearnRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        MapCharacterGameplayStateProtocol.WriteState(packet.AsSpan(6, MapCharacterGameplayStateProtocol.StateLength), expected);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(76), skillId);
        packet[78] = expectedCurrentLevel;
        return packet;
    }

    internal static bool TryParseResponse(ReadOnlySpan<byte> packet, out byte result, out uint charId, out CharacterSkillLearnResult? learnResult, ushort requestedSkillId)
    {
        result = 1; charId = 0; learnResult = null;
        if (packet.Length != ResponseLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapSkillLearnResponse) return false;
        result = packet[2];
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[3..]);
        if (result == 0)
        {
            var state = MapCharacterGameplayStateProtocol.ReadState(packet.Slice(ResponseHeaderLength, MapCharacterGameplayStateProtocol.StateLength));
            var newLevel = packet[ResponseHeaderLength + MapCharacterGameplayStateProtocol.StateLength];
            learnResult = new CharacterSkillLearnResult(state, requestedSkillId, newLevel);
        }
        return true;
    }

    // Test-only: MapServer never sends this response in production (CharServer does).
    internal static byte[] BuildResponse(byte result, uint charId, CharacterGameplayState? newState, byte newSkillLevel)
    {
        var packet = new byte[ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapSkillLearnResponse);
        packet[2] = result;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), charId);
        if (result == 0 && newState is not null)
        {
            MapCharacterGameplayStateProtocol.WriteState(packet.AsSpan(ResponseHeaderLength, MapCharacterGameplayStateProtocol.StateLength), newState);
            packet[ResponseHeaderLength + MapCharacterGameplayStateProtocol.StateLength] = newSkillLevel;
        }
        return packet;
    }
}
