using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Byte-level tests for the verified stock-iRO skill-up response (iro-skill-up-nv-basic-0-to-1.pcapng,
// frame 3623 - see ai/iro-2026-wire.md for the full capture evidence trace).
public sealed class IroSkillLevelUpdatePacketsTests
{
    // Verified capture bytes: 33 0B 01 00 00 00 00 00 01 00 00 00 01 00 01 01 00
    private static readonly byte[] CapturedNvBasicResponse =
        [0x33, 0x0B, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01, 0x00, 0x01, 0x01, 0x00];

    [Fact]
    public void Build_NvBasicLevelOne_MatchesCapturedBytesExactly()
    {
        var entry = new IroSkillInfoEntry(
            SkillId: 1, Inf: 0, CurrentLevel: 1, SpCost: 0, Range: 1, Upgradable: true, SecondaryLevel: 1);

        var packet = IroSkillLevelUpdatePackets.Build(entry);

        Assert.Equal(CapturedNvBasicResponse, packet);
    }

    [Fact]
    public void Build_HasVerifiedOpcodeAndLength()
    {
        var entry = new IroSkillInfoEntry(1, 0, 1, 0, 1, true, 1);
        var packet = IroSkillLevelUpdatePackets.Build(entry);

        Assert.Equal(17, packet.Length);
        Assert.Equal((short)0x0b33, BinaryPrimitives.ReadInt16LittleEndian(packet));
    }

    [Fact]
    public void Build_FieldOffsetsMatchOneZcSkillInfoListEntry()
    {
        // Same field layout as one ZC_SKILLINFO_LIST3 (0x0B32) entry minus its 2-byte
        // totalLength header field - proves the two serializers stay layout-compatible.
        var entry = new IroSkillInfoEntry(SkillId: 5, Inf: 16, CurrentLevel: 3, SpCost: 12, Range: 9, Upgradable: false, SecondaryLevel: 3);
        var packet = IroSkillLevelUpdatePackets.Build(entry);

        Assert.Equal((ushort)5, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(16, BinaryPrimitives.ReadInt32LittleEndian(packet.AsSpan(4)));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(8)));
        Assert.Equal((ushort)12, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(10)));
        Assert.Equal((short)9, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(12)));
        Assert.Equal((byte)0, packet[14]);
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(15)));
    }
}
