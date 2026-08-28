using Athena.Net.MapServer.Generated.Skills;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterSkillServiceTests
{
    // Small, isolated fixture tree - a generic 2-skill chain with an explicit BaseLevel/JobLevel/
    // prerequisite gate on the second entry, independent of any real generated job's exact shape.
    private static readonly GeneratedSkillTreeEntry FirstSkill = new(SkillId: 1, MaxLevel: 9, BaseLevel: 0, JobLevel: 0, Prerequisites: [], ExcludeFromInheritance: false);
    private static readonly GeneratedSkillTreeEntry GatedSkill = new(SkillId: 2, MaxLevel: 10, BaseLevel: 5, JobLevel: 3, Prerequisites: [new SkillPrerequisite(1, 4)], ExcludeFromInheritance: false);
    private static readonly GeneratedSkillTreeDefinition FixtureTree = new(JobClass: 0, InheritedFrom: [], DeclaredSkills: [FirstSkill, GatedSkill], EffectiveSkills: [FirstSkill, GatedSkill]);

    private static CharacterGameplayState State(ushort baseLevel = 10, ushort jobLevel = 10, uint skillPoints = 1) => new(
        CharacterId: 9, Version: 0, JobClass: 0, BaseLevel: baseLevel, JobLevel: jobLevel,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 11, MaxHp: 40, MaxSp: 11,
        StatPoints: 0, SkillPoints: skillPoints, Strength: 1, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1);

    [Fact]
    public void MissingPersistedRow_ResolvesToLevelZero()
    {
        var effective = CharacterSkillService.CalculateEffectiveState(State(), CharacterSkillSnapshot.Empty, FixtureTree, out _);
        Assert.Equal((byte)0, effective.Single(s => s.SkillId == 1).CurrentLevel);
    }

    [Fact]
    public void PersistedSkillOutsideEffectiveTree_IsExcludedFromResult_NotAnError()
    {
        // NV_FIRSTAID (142) is a real canonical skill, but not a member of FixtureTree - simulating
        // a legitimate historical skill from a prior job (job-changing is out of scope, but the
        // data model must not treat this as corrupted data).
        var skills = CharacterSkillSnapshot.FromLogin([(142, 1)]);
        var effective = CharacterSkillService.CalculateEffectiveState(State(), skills, FixtureTree, out var inconsistent);
        Assert.DoesNotContain(effective, s => s.SkillId == 142);
        Assert.Empty(inconsistent);
    }

    [Fact]
    public void PersistedLevelExceedingTreeMaxLevel_IsSurfacedAsInconsistency_NotThrown()
    {
        var skills = CharacterSkillSnapshot.FromLogin([(1, 9)]).WithLearnedSkill(1, 9); // legit max
        // Simulate a MaxLevel-lowering regeneration: persisted level (9) now exceeds a smaller tree entry's max.
        var shrunkEntry = FirstSkill with { MaxLevel = 5 };
        var shrunkTree = FixtureTree with { EffectiveSkills = [shrunkEntry, GatedSkill] };
        var effective = CharacterSkillService.CalculateEffectiveState(State(), skills, shrunkTree, out var inconsistent);
        Assert.Contains((ushort)1, inconsistent);
        Assert.Equal((byte)5, effective.Single(s => s.SkillId == 1).CurrentLevel); // clamped for display
    }

    [Fact]
    public void UnknownSkillId_CannotBeUpgraded()
    {
        var result = CharacterSkillService.ValidateUpgrade(State(), CharacterSkillSnapshot.Empty, FixtureTree, requestedSkillId: 60000);
        Assert.False(result.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.UnknownSkill, result.RejectionReason);
    }

    [Fact]
    public void SkillOutsideEffectiveTree_CannotBeUpgraded()
    {
        // A real canonical skill (SM_SWORD, id 2 - wait, FixtureTree's own id 2 IS in tree; use a
        // real canonical id genuinely absent from FixtureTree, e.g. NV_FIRSTAID (142).
        var result = CharacterSkillService.ValidateUpgrade(State(), CharacterSkillSnapshot.Empty, FixtureTree, requestedSkillId: 142);
        Assert.False(result.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.NotInEffectiveTree, result.RejectionReason);
    }

    [Fact]
    public void NoSkillPoints_CannotUpgrade()
    {
        var result = CharacterSkillService.ValidateUpgrade(State(skillPoints: 0), CharacterSkillSnapshot.Empty, FixtureTree, requestedSkillId: 1);
        Assert.False(result.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.NoSkillPoints, result.RejectionReason);
    }

    [Fact]
    public void CurrentLevelEqualsMaxLevel_CannotUpgrade()
    {
        var skills = CharacterSkillSnapshot.FromLogin([(1, 9)]); // FirstSkill.MaxLevel == 9
        var result = CharacterSkillService.ValidateUpgrade(State(), skills, FixtureTree, requestedSkillId: 1);
        Assert.False(result.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.MaxLevelReached, result.RejectionReason);
    }

    [Fact]
    public void BaseLevelRequirementNotMet_CannotUpgrade()
    {
        var skills = CharacterSkillSnapshot.FromLogin([(1, 4)]);
        var result = CharacterSkillService.ValidateUpgrade(State(baseLevel: 4, jobLevel: 10), skills, FixtureTree, requestedSkillId: 2);
        Assert.False(result.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.BaseLevelNotMet, result.RejectionReason);
    }

    [Fact]
    public void JobLevelRequirementNotMet_CannotUpgrade()
    {
        var skills = CharacterSkillSnapshot.FromLogin([(1, 4)]);
        var result = CharacterSkillService.ValidateUpgrade(State(baseLevel: 10, jobLevel: 2), skills, FixtureTree, requestedSkillId: 2);
        Assert.False(result.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.JobLevelNotMet, result.RejectionReason);
    }

    [Fact]
    public void MissingPrerequisite_CannotUpgrade()
    {
        var result = CharacterSkillService.ValidateUpgrade(State(), CharacterSkillSnapshot.Empty, FixtureTree, requestedSkillId: 2);
        Assert.False(result.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.PrerequisiteNotMet, result.RejectionReason);
    }

    [Fact]
    public void PrerequisiteBelowRequiredLevel_CannotUpgrade()
    {
        var skills = CharacterSkillSnapshot.FromLogin([(1, 3)]); // GatedSkill requires SkillId 1 >= level 4
        var result = CharacterSkillService.ValidateUpgrade(State(), skills, FixtureTree, requestedSkillId: 2);
        Assert.False(result.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.PrerequisiteNotMet, result.RejectionReason);
    }

    [Fact]
    public void PrerequisiteSatisfied_CanUpgrade()
    {
        var skills = CharacterSkillSnapshot.FromLogin([(1, 4)]);
        var result = CharacterSkillService.ValidateUpgrade(State(), skills, FixtureTree, requestedSkillId: 2);
        Assert.True(result.IsValid);
        Assert.Equal((byte)1, result.NewSkillLevel);
    }

    [Fact]
    public void ValidUpgrade_ComputesExactSkillPointAndLevelDelta()
    {
        var skills = CharacterSkillSnapshot.FromLogin([(1, 3)]);
        var result = CharacterSkillService.ValidateUpgrade(State(skillPoints: 5), skills, FixtureTree, requestedSkillId: 1);
        Assert.True(result.IsValid);
        Assert.Equal((byte)4, result.NewSkillLevel);
        Assert.Equal(4U, result.NewSkillPoints);
        Assert.Equal((ushort)9, result.MaxLevel);
    }

    [Fact]
    public void NotNormallyLearnableSkill_CannotUpgrade()
    {
        // NV_FIRSTAID (142) is source-backed NormallyLearnable=false (skill_db.yml Flags.IsQuest).
        var entry = new GeneratedSkillTreeEntry(SkillId: 142, MaxLevel: 1, BaseLevel: 0, JobLevel: 0, Prerequisites: [], ExcludeFromInheritance: false);
        var tree = new GeneratedSkillTreeDefinition(JobClass: 0, InheritedFrom: [], DeclaredSkills: [entry], EffectiveSkills: [entry]);
        var result = CharacterSkillService.ValidateUpgrade(State(), CharacterSkillSnapshot.Empty, tree, requestedSkillId: 142);
        Assert.False(result.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.NotNormallyLearnable, result.RejectionReason);
    }

    // Proves genericity against a REAL generated job (Swordman) other than Novice, per the
    // no-Novice-special-casing requirement - the exact same service functions, unchanged.
    [Fact]
    public void RealGeneratedSwordmanTree_TwoHandSwordRequiresOneHandSwordLevelOne()
    {
        var tree = GeneratedSkillTreeRegistry.Get(Athena.Net.MapServer.Generated.Jobs.JobClass.Swordman);
        var swordmanState = State(baseLevel: 10, jobLevel: 10, skillPoints: 1) with { JobClass = (ushort)Athena.Net.MapServer.Generated.Jobs.JobClass.Swordman };

        var withoutSword = CharacterSkillService.ValidateUpgrade(swordmanState, CharacterSkillSnapshot.Empty, tree, requestedSkillId: 3 /* SM_TWOHAND */);
        Assert.False(withoutSword.IsValid);
        Assert.Equal(SkillUpgradeRejectionReason.PrerequisiteNotMet, withoutSword.RejectionReason);

        var withSword = CharacterSkillSnapshot.FromLogin([(2, 1)]); // SM_SWORD level 1
        var canUpgrade = CharacterSkillService.ValidateUpgrade(swordmanState, withSword, tree, requestedSkillId: 3);
        Assert.True(canUpgrade.IsValid);
        Assert.Equal((byte)1, canUpgrade.NewSkillLevel);
    }
}
