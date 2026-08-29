using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Byte-level tests for the verified stock-iRO skill-up request (iro-skill-up-nv-basic-0-to-1.pcapng,
// frame 3604 - see ai/iro-2026-wire.md for the full capture evidence trace).
public sealed class IroSkillLevelUpRequestPacketTests
{
    // Verified capture bytes: 12 01 01 00 1D -> opcode 0x0112, SkillId 1 (NV_BASIC),
    // opaque trailing byte 0x1D.
    private static readonly byte[] CapturedNvBasicRequest = [0x12, 0x01, 0x01, 0x00, 0x1D];

    [Fact]
    public void TryParse_CapturedNvBasicRequest_ParsesExactFields()
    {
        Assert.True(IroSkillLevelUpRequestPacket.TryParse(CapturedNvBasicRequest, out var value));
        Assert.Equal((ushort)1, value.SkillId);
        Assert.Equal((byte)0x1D, value.OpaqueTrailingByte);
    }

    [Fact]
    public void TryParse_WrongOpcode_Rejects()
    {
        byte[] wrongOpcode = [0x13, 0x01, 0x01, 0x00, 0x1D];
        Assert.False(IroSkillLevelUpRequestPacket.TryParse(wrongOpcode, out _));
    }

    [Fact]
    public void TryParse_TruncatedPacket_Rejects()
    {
        byte[] truncated = [0x12, 0x01, 0x01, 0x00];
        Assert.False(IroSkillLevelUpRequestPacket.TryParse(truncated, out _));
    }

    [Fact]
    public void TryParse_OversizedPacket_Rejects()
    {
        byte[] oversized = [0x12, 0x01, 0x01, 0x00, 0x1D, 0x00];
        Assert.False(IroSkillLevelUpRequestPacket.TryParse(oversized, out _));
    }

    [Fact]
    public void TryParse_EmptyPacket_Rejects()
    {
        Assert.False(IroSkillLevelUpRequestPacket.TryParse(ReadOnlySpan<byte>.Empty, out _));
    }

    // Parser must not special-case the SkillId value - it structurally parses any well-formed
    // packet regardless of whether the SkillId is a real, in-tree, or learnable skill (task
    // section 32: unknown SkillId is a domain-validation concern, never a packet-corruption one).
    [Fact]
    public void TryParse_UnknownSkillId_StillParsesStructurally()
    {
        byte[] unknownSkill = [0x12, 0x01, 0xFF, 0xFF, 0x1D];
        Assert.True(IroSkillLevelUpRequestPacket.TryParse(unknownSkill, out var value));
        Assert.Equal((ushort)65535, value.SkillId);
    }
}
