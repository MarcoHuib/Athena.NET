using System.Buffers.Binary;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroCharacterProgressionPacketsTests
{
    [Fact]
    public void UsesCaptureSupportedParameterLayouts()
    {
        var before = State();
        var after = before with { BaseLevel = 2, JobLevel = 2, BaseExperience = 0, JobExperience = 0, StatPoints = 51, SkillPoints = 1, MaxHp = 45, CurrentHp = 45, MaxSp = 12, CurrentSp = 12 };
        var packets = IroCharacterProgressionPackets.Build(new(before, after, 1, 1, 894, 18), true, true);

        Assert.All(packets.Where(packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == PacketConstants.ZcParameterChange), packet => Assert.Equal(8, packet.Length));
        Assert.All(packets.Where(packet => BinaryPrimitives.ReadInt16LittleEndian(packet) == PacketConstants.ZcLongLongParameterChange), packet => Assert.Equal(12, packet.Length));
        Assert.Contains(packets, packet => BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 11 && BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(4)) == 2);
        Assert.Contains(packets, packet => BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)) == 1 && BinaryPrimitives.ReadUInt64LittleEndian(packet.AsSpan(4)) == 0);
    }

    private static CharacterGameplayState State() => new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
}
