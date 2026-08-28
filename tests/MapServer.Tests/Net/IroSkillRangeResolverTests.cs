using Athena.Net.MapServer.Generated.Skills;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Unit tests for IroSkillRangeResolver against REAL generated skill data (never hand-built
// GeneratedSkillDefinition fixtures for the range-flag cases) - proves the pinned skill_get_range2
// (legacy/rathena/src/map/skill.cpp:324-365) modifiers are implemented generically, driven purely
// by GeneratedSkillDefinition.RangeFlags, with no per-skill-name special case anywhere in the
// resolver.
public sealed class IroSkillRangeResolverTests
{
    [Fact]
    public void NoRangeFlags_NegativeRange_ResolvesToAbsoluteValue()
    {
        var smBash = GeneratedSkillRegistry.GetById(5); // Range -1 at every level, no RangeFlags
        var result = IroSkillRangeResolver.Resolve(smBash, currentLevel: 1, CharacterSkillSnapshot.Empty);
        Assert.Equal((short)1, result);
    }

    [Fact]
    public void NoRangeFlags_PositiveRange_PassesThroughUnchanged()
    {
        var smProvoke = GeneratedSkillRegistry.GetById(6); // Range 9 at every level
        var result = IroSkillRangeResolver.Resolve(smProvoke, currentLevel: 1, CharacterSkillSnapshot.Empty);
        Assert.Equal((short)9, result);
    }

    [Fact]
    public void UnlearnedSkill_CurrentLevelZero_ResolvesFromZeroRawRange()
    {
        var nvBasic = GeneratedSkillRegistry.GetById(1); // no Range field at all
        var result = IroSkillRangeResolver.Resolve(nvBasic, currentLevel: 0, CharacterSkillSnapshot.Empty);
        Assert.Equal((short)0, result);
    }

    // AC_DOUBLE (46): Flags.AlterRangeVulture, Range -9. Additive: abs(-9) + AC_VULTURE(44) level.
    [Theory]
    [InlineData((byte)0, (short)9)]
    [InlineData((byte)5, (short)14)]
    [InlineData((byte)10, (short)19)]
    public void AlterRangeVulture_AddsCasterOwnAcVultureLevel(byte vultureLevel, short expected)
    {
        var acDouble = GeneratedSkillRegistry.GetById(46);
        Assert.True(acDouble.RangeFlags.AlterRangeVulture);
        var skills = vultureLevel == 0
            ? CharacterSkillSnapshot.Empty
            : CharacterSkillSnapshot.FromLogin([(44, vultureLevel, CharSkillFlag.Permanent)]);
        var result = IroSkillRangeResolver.Resolve(acDouble, currentLevel: 1, skills);
        Assert.Equal(expected, result);
    }

    // GS_PIERCINGSHOT (514): Flags.AlterRangeSnakeEye, Range -9. Additive: abs(-9) + GS_SNAKEEYE(510) level.
    [Theory]
    [InlineData((byte)0, (short)9)]
    [InlineData((byte)7, (short)16)]
    public void AlterRangeSnakeEye_AddsCasterOwnGsSnakeEyeLevel(byte snakeEyeLevel, short expected)
    {
        var piercingShot = GeneratedSkillRegistry.GetById(514);
        Assert.True(piercingShot.RangeFlags.AlterRangeSnakeEye);
        var skills = snakeEyeLevel == 0
            ? CharacterSkillSnapshot.Empty
            : CharacterSkillSnapshot.FromLogin([(510, snakeEyeLevel, CharSkillFlag.Permanent)]);
        var result = IroSkillRangeResolver.Resolve(piercingShot, currentLevel: 1, skills);
        Assert.Equal(expected, result);
    }

    // WL_WHITEIMPRISON (2201): Flags.AlterRangeRadius, Range 11 (positive). Additive: 11 + WL_RADIUS(2208) level.
    [Theory]
    [InlineData((byte)0, (short)11)]
    [InlineData((byte)3, (short)14)]
    public void AlterRangeRadius_AddsCasterOwnWlRadiusLevel(byte radiusLevel, short expected)
    {
        var whiteImprison = GeneratedSkillRegistry.GetById(2201);
        Assert.True(whiteImprison.RangeFlags.AlterRangeRadius);
        var skills = radiusLevel == 0
            ? CharacterSkillSnapshot.Empty
            : CharacterSkillSnapshot.FromLogin([(2208, radiusLevel, CharSkillFlag.Permanent)]);
        var result = IroSkillRangeResolver.Resolve(whiteImprison, currentLevel: 1, skills);
        Assert.Equal(expected, result);
    }

    // RA_CLUSTERBOMB (2239): Flags.AlterRangeResearchTrap, Range 3 (positive). Additive via pinned
    // non-linear rt_range table indexed by RA_RESEARCHTRAP(2248) level: {0,1,1,2,2,3,3,4,4,5,5}.
    [Theory]
    [InlineData((byte)0, (short)3)]
    [InlineData((byte)1, (short)4)]
    [InlineData((byte)2, (short)4)]
    [InlineData((byte)10, (short)8)]
    public void AlterRangeResearchTrap_AddsNonLinearRaResearchTrapTable(byte researchTrapLevel, short expected)
    {
        var clusterBomb = GeneratedSkillRegistry.GetById(2239);
        Assert.True(clusterBomb.RangeFlags.AlterRangeResearchTrap);
        var skills = researchTrapLevel == 0
            ? CharacterSkillSnapshot.Empty
            : CharacterSkillSnapshot.FromLogin([(2248, researchTrapLevel, CharSkillFlag.Permanent)]);
        var result = IroSkillRangeResolver.Resolve(clusterBomb, currentLevel: 1, skills);
        Assert.Equal(expected, result);
    }

    // NJ_KIRIKAGE (530): Flags.AlterRangeShadowJump. REPLACES the range outright with
    // NJ_SHADOWJUMP's (529) own per-level range at the caster's learned NJ_SHADOWJUMP level - not
    // additive, and not absolute-valuing KIRIKAGE's own range first.
    [Theory]
    [InlineData((byte)0, (short)0)]  // no NJ_SHADOWJUMP learned -> 0
    [InlineData((byte)1, (short)6)]  // NJ_SHADOWJUMP level 1 -> its own Range[0] = 6
    [InlineData((byte)5, (short)10)] // NJ_SHADOWJUMP level 5 -> its own Range[4] = 10
    public void AlterRangeShadowJump_ReplacesWithNjShadowJumpOwnRangeAtCasterLevel(byte shadowJumpLevel, short expected)
    {
        var kirikage = GeneratedSkillRegistry.GetById(530);
        Assert.True(kirikage.RangeFlags.AlterRangeShadowJump);
        var skills = shadowJumpLevel == 0
            ? CharacterSkillSnapshot.Empty
            : CharacterSkillSnapshot.FromLogin([(529, shadowJumpLevel, CharSkillFlag.Permanent)]);
        var result = IroSkillRangeResolver.Resolve(kirikage, currentLevel: 1, skills);
        Assert.Equal(expected, result);
    }
}
