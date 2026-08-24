using System.Buffers.Binary;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

internal static class MapCharacterGameplayStateProtocol
{
    internal const int GetRequestLength = 10;
    internal const int StateLength = 70;
    internal const int UpdateRequestLength = 146;
    internal const int ResponseLength = 77;

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
        WriteState(packet.AsSpan(76, StateLength), updated);
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
        BinaryPrimitives.WriteUInt16LittleEndian(target[12..], state.JobClass);
        BinaryPrimitives.WriteUInt16LittleEndian(target[14..], state.BaseLevel);
        BinaryPrimitives.WriteUInt16LittleEndian(target[16..], state.JobLevel);
        BinaryPrimitives.WriteUInt64LittleEndian(target[18..], state.BaseExperience);
        BinaryPrimitives.WriteUInt64LittleEndian(target[26..], state.JobExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(target[34..], state.CurrentHp);
        BinaryPrimitives.WriteUInt32LittleEndian(target[38..], state.CurrentSp);
        BinaryPrimitives.WriteUInt32LittleEndian(target[42..], state.MaxHp);
        BinaryPrimitives.WriteUInt32LittleEndian(target[46..], state.MaxSp);
        BinaryPrimitives.WriteUInt32LittleEndian(target[50..], state.StatPoints);
        BinaryPrimitives.WriteUInt32LittleEndian(target[54..], state.SkillPoints);
        BinaryPrimitives.WriteUInt16LittleEndian(target[58..], state.Strength);
        BinaryPrimitives.WriteUInt16LittleEndian(target[60..], state.Agility);
        BinaryPrimitives.WriteUInt16LittleEndian(target[62..], state.Vitality);
        BinaryPrimitives.WriteUInt16LittleEndian(target[64..], state.Intelligence);
        BinaryPrimitives.WriteUInt16LittleEndian(target[66..], state.Dexterity);
        BinaryPrimitives.WriteUInt16LittleEndian(target[68..], state.Luck);
    }

    internal static CharacterGameplayState ReadState(ReadOnlySpan<byte> source) => new(
        BinaryPrimitives.ReadUInt32LittleEndian(source), BinaryPrimitives.ReadUInt64LittleEndian(source[4..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[12..]), BinaryPrimitives.ReadUInt16LittleEndian(source[14..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[16..]), BinaryPrimitives.ReadUInt64LittleEndian(source[18..]),
        BinaryPrimitives.ReadUInt64LittleEndian(source[26..]), BinaryPrimitives.ReadUInt32LittleEndian(source[34..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[38..]), BinaryPrimitives.ReadUInt32LittleEndian(source[42..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[46..]), BinaryPrimitives.ReadUInt32LittleEndian(source[50..]),
        BinaryPrimitives.ReadUInt32LittleEndian(source[54..]), BinaryPrimitives.ReadUInt16LittleEndian(source[58..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[60..]), BinaryPrimitives.ReadUInt16LittleEndian(source[62..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[64..]), BinaryPrimitives.ReadUInt16LittleEndian(source[66..]),
        BinaryPrimitives.ReadUInt16LittleEndian(source[68..]));
}
