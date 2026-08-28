using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.Generated.Skills;

namespace Athena.Net.MapServer.Tests.World;

public sealed class GeneratedCharacterDataTests
{
    [Fact]
    public void JobCatalog_UsesPinnedNumericIdsAndRejectsUnknownIds()
    {
        Assert.Equal("Novice", GeneratedJobRegistry.Get(0).Name);
        Assert.Equal("Swordman", GeneratedJobRegistry.Get(1).Name);
        Assert.Equal("Rune_Knight", GeneratedJobRegistry.Get(4054).Name);
        Assert.Equal("Dragon_Knight", GeneratedJobRegistry.Get(4252).Name);
        Assert.Throws<NotSupportedException>(() => GeneratedJobRegistry.Get(ushort.MaxValue));
    }

    [Fact]
    public void ProgressionRegistry_PreservesNoviceAndCoversLaterJobs()
    {
        var novice = GeneratedProgressionRegistry.Get(0);
        Assert.Equal((ushort)99, novice.MaxBaseLevel); Assert.Equal((ushort)10, novice.MaxJobLevel);
        Assert.Equal(548UL, novice.BaseExperienceToNext[1]); Assert.Equal(10UL, novice.JobExperienceToNext[1]);
        Assert.Equal(40U, novice.BaseHp[1]); Assert.Equal(11U, novice.BaseSp[1]);
        Assert.Equal(1U, novice.JobVitalityBonus[6]); Assert.Equal(1U, novice.JobIntelligenceBonus[9]);

        var swordman = GeneratedProgressionRegistry.Get(1);
        Assert.Equal((ushort)99, swordman.MaxBaseLevel); Assert.Equal((ushort)50, swordman.MaxJobLevel);
        Assert.Equal(novice.BaseExperienceToNext, swordman.BaseExperienceToNext);
        Assert.True(GeneratedProgressionRegistry.Get(4054).MaxBaseLevel >= 200);
        Assert.Throws<NotSupportedException>(() => GeneratedProgressionRegistry.Get(ushort.MaxValue));
    }

    [Fact]
    public void SkillCatalog_ResolvesBothDirectionsAndRejectsUnknownValues()
    {
        Assert.Equal((ushort)1, GeneratedSkillRegistry.GetByName("NV_BASIC").SkillId);
        Assert.Equal((ushort)5, GeneratedSkillRegistry.GetByName("SM_BASH").SkillId);
        Assert.Equal((ushort)19, GeneratedSkillRegistry.GetByName("MG_FIREBOLT").SkillId);
        Assert.Equal("LK_BERSERK", GeneratedSkillRegistry.GetById(359).Name);
        Assert.Throws<NotSupportedException>(() => GeneratedSkillRegistry.GetById(ushort.MaxValue));
        Assert.Throws<NotSupportedException>(() => GeneratedSkillRegistry.GetByName("NOT_A_SKILL"));
    }

    [Fact]
    public void SkillTrees_PreserveDirectInheritanceExcludeAndNumericPrerequisites()
    {
        var novice = GeneratedSkillTreeRegistry.Get(0);
        Assert.Equal((ushort)9, novice.DeclaredSkills.Single(entry => entry.SkillId == 1).MaxLevel);
        var trickDead = GeneratedSkillRegistry.GetByName("NV_TRICKDEAD").SkillId;
        Assert.True(novice.DeclaredSkills.Single(entry => entry.SkillId == trickDead).ExcludeFromInheritance);

        var swordman = GeneratedSkillTreeRegistry.Get(1);
        Assert.Contains(swordman.EffectiveSkills, entry => entry.SkillId == 1);
        Assert.DoesNotContain(swordman.EffectiveSkills, entry => entry.SkillId == trickDead);
        var twoHand = swordman.EffectiveSkills.Single(entry => entry.SkillId == GeneratedSkillRegistry.GetByName("SM_TWOHAND").SkillId);
        var sword = GeneratedSkillRegistry.GetByName("SM_SWORD").SkillId;
        Assert.Contains(twoHand.Prerequisites, requirement => requirement.SkillId == sword && requirement.Level == 1);

        var knight = GeneratedSkillTreeRegistry.Get(7);
        Assert.Contains(knight.EffectiveSkills, entry => entry.SkillId == 1);
        Assert.Contains(knight.EffectiveSkills, entry => entry.SkillId == GeneratedSkillRegistry.GetByName("SM_BASH").SkillId);
        var lordKnight = GeneratedSkillTreeRegistry.Get(4008);
        Assert.Equal((ushort)50, lordKnight.EffectiveSkills.Single(entry => entry.SkillId == 359).JobLevel);
        var summoner = GeneratedSkillTreeRegistry.Get(4218);
        Assert.Equal((ushort)100, summoner.EffectiveSkills.Single(entry => entry.SkillId == GeneratedSkillRegistry.GetByName("SU_POWEROFFLOCK").SkillId).BaseLevel);
    }

    [Fact]
    public void EveryGeneratedRelationshipResolvesAcrossRegistries()
    {
        var skillIds = GeneratedSkillRegistry.All.Select(skill => skill.SkillId).ToHashSet();
        foreach (var progression in GeneratedProgressionRegistry.All) Assert.Equal(progression.JobClass, GeneratedJobRegistry.Get(progression.JobClass).JobClass);
        foreach (var tree in GeneratedSkillTreeRegistry.All)
        {
            Assert.Equal(tree.JobClass, GeneratedJobRegistry.Get(tree.JobClass).JobClass);
            Assert.All(tree.InheritedFrom, parent => Assert.Equal(parent, GeneratedJobRegistry.Get(parent).JobClass));
            Assert.All(tree.DeclaredSkills.Concat(tree.EffectiveSkills), entry =>
            {
                Assert.Contains(entry.SkillId, skillIds);
                Assert.All(entry.Prerequisites, requirement => Assert.Contains(requirement.SkillId, skillIds));
            });
        }
    }
}
