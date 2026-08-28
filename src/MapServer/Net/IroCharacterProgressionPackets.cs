using System.Buffers.Binary;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

internal static class IroCharacterProgressionPackets
{
    internal const ushort BaseExperienceParameter = 1;
    internal const ushort JobExperienceParameter = 2;
    internal const uint BaseLevelUpEffect = 0;
    internal const uint JobLevelUpEffect = 1;

    // Capture-backed presentation order from Full-izlude. Fields are derived from
    // the persisted result; no captured payload is replayed.
    internal static IReadOnlyList<byte[]> Build(uint actorId, CharacterProgressionResult result)
    {
        var packets = new List<byte[]>();
        if (result.BaseExperienceAwarded > 0)
        {
            packets.Add(LongParameter(BaseExperienceParameter, result.After.BaseExperience));
            packets.Add(ExperienceGain(actorId, result.BaseExperienceAwarded, BaseExperienceParameter));
        }
        if (result.BaseLevelsGained > 0)
        {
            packets.Add(Parameter(9, result.After.StatPoints));
            packets.Add(Parameter(11, result.After.BaseLevel));
            packets.Add(Parameter(6, result.After.MaxHp));
            packets.Add(Parameter(5, result.After.CurrentHp));
            packets.Add(Parameter(8, result.After.MaxSp));
            packets.Add(Parameter(7, result.After.CurrentSp));
        }
        if (result.JobLevelsGained > 0)
        {
            // The capture sends Skill Points before Job Level.
            packets.Add(Parameter(12, result.After.SkillPoints));
            packets.Add(Parameter(55, result.After.JobLevel));
        }
        if (result.JobExperienceAwarded > 0) packets.Add(LongParameter(23, result.NextJobExperience));
        if (result.BaseExperienceAwarded > 0) packets.Add(LongParameter(22, result.NextBaseExperience));
        if (result.BaseLevelsGained > 0) packets.Add(LevelUpEffect(actorId, BaseLevelUpEffect));
        if (result.JobLevelsGained > 0) packets.Add(LevelUpEffect(actorId, JobLevelUpEffect));
        if (result.JobExperienceAwarded > 0)
        {
            packets.Add(LongParameter(JobExperienceParameter, result.After.JobExperience));
            packets.Add(ExperienceGain(actorId, result.JobExperienceAwarded, JobExperienceParameter));
        }
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

    internal static byte[] ExperienceGain(uint actorId, ulong amount, ushort parameterId)
    {
        var packet = new byte[PacketConstants.ZcNotifyExperienceLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNotifyExperience);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.AsSpan(6), amount);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(14), parameterId);
        // Captured normal Base/Job flow uses gain type/flag 0.
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(16), 0);
        return packet;
    }

    internal static byte[] LevelUpEffect(uint actorId, uint effectType)
    {
        if (effectType is not (BaseLevelUpEffect or JobLevelUpEffect))
            throw new ArgumentOutOfRangeException(nameof(effectType));
        var packet = new byte[PacketConstants.ZcNotifyEffectLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNotifyEffect);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), effectType);
        return packet;
    }
}
