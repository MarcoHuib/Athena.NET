using System.Buffers.Binary;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

internal static class IroCharacterProgressionPackets
{
    internal static IReadOnlyList<byte[]> Build(CharacterProgressionResult result, bool baseAwarded, bool jobAwarded)
    {
        var packets = new List<byte[]>();
        if (result.BaseLevelsGained > 0)
        {
            packets.Add(Parameter(9, result.After.StatPoints));
            packets.Add(Parameter(11, result.After.BaseLevel));
        }
        if (baseAwarded)
        {
            packets.Add(LongParameter(1, result.After.BaseExperience));
            packets.Add(LongParameter(22, result.NextBaseExperience));
        }
        if (result.BaseLevelsGained > 0)
        {
            packets.Add(Parameter(6, result.After.MaxHp));
            packets.Add(Parameter(5, result.After.CurrentHp));
            packets.Add(Parameter(8, result.After.MaxSp));
            packets.Add(Parameter(7, result.After.CurrentSp));
        }
        if (result.JobLevelsGained > 0) packets.Add(Parameter(55, result.After.JobLevel));
        if (jobAwarded)
        {
            packets.Add(LongParameter(2, result.After.JobExperience));
            packets.Add(LongParameter(23, result.NextJobExperience));
        }
        if (result.JobLevelsGained > 0) packets.Add(Parameter(12, result.After.SkillPoints));
        return packets;
    }

    internal static byte[] Parameter(ushort id, uint value)
    {
        var packet = new byte[8];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcParameterChange);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), id);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), value);
        return packet;
    }

    internal static byte[] LongParameter(ushort id, ulong value)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcLongLongParameterChange);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), id);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(4), value);
        return packet;
    }
}
