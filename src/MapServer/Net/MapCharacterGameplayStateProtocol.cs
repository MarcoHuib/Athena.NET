using System.Buffers.Binary;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

internal static class MapCharacterGameplayStateProtocol
{
    internal const int GetRequestLength = 10;
    internal const int StateLength = 68;
    internal const int UpdateRequestLength = 142;
    internal const int ResponseLength = 75;

    internal static byte[] BuildGetRequest(uint accountId, uint charId)
    {
        var packet = new byte[GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapGameplayStateGetRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), charId);
        return packet;
    }

    internal static byte[] BuildUpdateRequest(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated)
    {
        var packet = new byte[UpdateRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapGameplayStateUpdateRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        WriteState(packet.AsSpan(6, StateLength), expected);
        WriteState(packet.AsSpan(74, StateLength), updated);
        return packet;
    }

    internal static bool TryParseResponse(ReadOnlySpan<byte> packet, short type, out byte result, out uint charId, out CharacterGameplayState? state)
    {
        result = 0; charId = 0; state = null;
        if (packet.Length != ResponseLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != type) return false;
        result = packet[2]; charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[3..]);
        if (result == 0) state = ReadState(packet.Slice(7, StateLength));
        return true;
    }

    internal static void WriteState(Span<byte> target, CharacterGameplayState state)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(target, state.CharacterId);
        BinaryPrimitives.WriteUInt64LittleEndian(target[4..], state.Version);
        BinaryPrimitives.WriteUInt16LittleEndian(target[12..], state.BaseLevel);
        BinaryPrimitives.WriteUInt16LittleEndian(target[14..], state.JobLevel);
        BinaryPrimitives.WriteUInt64LittleEndian(target[16..], state.BaseExperience);
        BinaryPrimitives.WriteUInt64LittleEndian(target[24..], state.JobExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(target[32..], state.CurrentHp);
        BinaryPrimitives.WriteUInt32LittleEndian(target[36..], state.CurrentSp);
        BinaryPrimitives.WriteUInt32LittleEndian(target[40..], state.MaxHp);
        BinaryPrimitives.WriteUInt32LittleEndian(target[44..], state.MaxSp);
        BinaryPrimitives.WriteUInt32LittleEndian(target[48..], state.StatPoints);
        BinaryPrimitives.WriteUInt32LittleEndian(target[52..], state.SkillPoints);
        BinaryPrimitives.WriteUInt16LittleEndian(target[56..], state.Strength);
        BinaryPrimitives.WriteUInt16LittleEndian(target[58..], state.Agility);
        BinaryPrimitives.WriteUInt16LittleEndian(target[60..], state.Vitality);
        BinaryPrimitives.WriteUInt16LittleEndian(target[62..], state.Intelligence);
        BinaryPrimitives.WriteUInt16LittleEndian(target[64..], state.Dexterity);
        BinaryPrimitives.WriteUInt16LittleEndian(target[66..], state.Luck);
    }

    internal static CharacterGameplayState ReadState(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(source), BinaryPrimitives.ReadUInt64LittleEndian(source[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[12..]), BinaryPrimitives.ReadUInt16LittleEndian(source[14..]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[16..]), BinaryPrimitives.ReadUInt64LittleEndian(source[24..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[32..]), BinaryPrimitives.ReadUInt32LittleEndian(source[36..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[40..]), BinaryPrimitives.ReadUInt32LittleEndian(source[44..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[48..]), BinaryPrimitives.ReadUInt32LittleEndian(source[52..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[56..]), BinaryPrimitives.ReadUInt16LittleEndian(source[58..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[60..]), BinaryPrimitives.ReadUInt16LittleEndian(source[62..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[64..]), BinaryPrimitives.ReadUInt16LittleEndian(source[66..]));
}
