using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class IroCharacterProgressionPacketsTests
{
    [Theory]
    [InlineData(1, 150UL, "CB-0A-01-00-96-00-00-00-00-00-00-00")]
    [InlineData(2, 9UL, "CB-0A-02-00-09-00-00-00-00-00-00-00")]
    [InlineData(22, 548UL, "CB-0A-16-00-24-02-00-00-00-00-00-00")]
    [InlineData(23, 18UL, "CB-0A-17-00-12-00-00-00-00-00-00-00")]
    public void LongParameter_MatchesCaptureBytes(ushort parameter, ulong value, string expected) =>
        Assert.Equal(Convert.FromHexString(expected.Replace("-", "")), IroCharacterProgressionPackets.LongParameter(parameter, value));

    [Theory]
    [InlineData(1, "CC-0A-3B-AF-5F-00-96-00-00-00-00-00-00-00-01-00-00-00")]
    [InlineData(2, "CC-0A-3B-AF-5F-00-96-00-00-00-00-00-00-00-02-00-00-00")]
    public void ExperienceGain_MatchesCaptainCapture(ushort parameter, string expected) =>
        Assert.Equal(Convert.FromHexString(expected.Replace("-", "")), IroCharacterProgressionPackets.ExperienceGain(0x005faf3b, 150, parameter));

    [Theory]
    [InlineData(11, 2u, "B0-00-0B-00-02-00-00-00")]
    [InlineData(55, 2u, "B0-00-37-00-02-00-00-00")]
    [InlineData(9, 51u, "B0-00-09-00-33-00-00-00")]
    [InlineData(12, 1u, "B0-00-0C-00-01-00-00-00")]
    public void Parameter_UsesCaptureSupportedLayout(ushort parameter, uint value, string expected) =>
        Assert.Equal(Convert.FromHexString(expected.Replace("-", "")), IroCharacterProgressionPackets.Parameter(parameter, value));

    [Theory]
    [InlineData(0u, "9B-01-3B-AF-5F-00-00-00-00-00")]
    [InlineData(1u, "9B-01-3B-AF-5F-00-01-00-00-00")]
    public void LevelUpEffect_UsesCaptureSupportedTypes(uint effect, string expected) =>
        Assert.Equal(Convert.FromHexString(expected.Replace("-", "")), IroCharacterProgressionPackets.LevelUpEffect(0x005faf3b, effect));

    [Fact]
    public void Build_UsesCaptureBackedProgressionOrder()
    {
        var before = State();
        var after = before with { JobLevel = 2, BaseExperience = 150, JobExperience = 9, SkillPoints = 1 };
        var packets = IroCharacterProgressionPackets.Build(0x005faf3b, new(before, after, 0, 1, 548, 18, 150, 150));

        Assert.Equal([0x0acb, 0x0acc, 0x00b0, 0x00b0, 0x0acb, 0x0acb, 0x019b, 0x0acb, 0x0acc],
            packets.Select(packet => (int)BitConverter.ToUInt16(packet)).ToArray());
        Assert.Equal((ushort)12, BitConverter.ToUInt16(packets[2], 2));
        Assert.Equal((ushort)55, BitConverter.ToUInt16(packets[3], 2));
    }

    private static CharacterGameplayState State() => new(9, 0, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
}
