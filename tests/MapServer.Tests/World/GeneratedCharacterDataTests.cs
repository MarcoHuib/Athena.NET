using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Generated.Progression;
using Athena.Net.MapServer.Generated.Skills;

namespace Athena.Net.MapServer.Tests.World;

public sealed class GeneratedCharacterDataTests
{
    [Fact]
    public void JobCatalog_UsesPinnedNumericIdsAndRejectsUnknownIds()
    {
        Assert.Equal((ushort)0, (ushort)JobClass.Novice);
        Assert.Equal((ushort)1, (ushort)JobClass.Swordman);
        Assert.Equal((ushort)4054, (ushort)JobClass.RuneKnight);
        Assert.Equal((ushort)4252, (ushort)JobClass.DragonKnight);
        Assert.Equal("Novice", JobClassNames.GetRathenaName(JobClass.Novice));
        Assert.Equal("Swordman", JobClassNames.GetRathenaName(JobClass.Swordman));
        Assert.Equal("Rune_Knight", JobClassNames.GetRathenaName(JobClass.RuneKnight));
        Assert.Equal("Dragon_Knight", JobClassNames.GetRathenaName(JobClass.DragonKnight));
        Assert.Throws<NotSupportedException>(() => JobClassNames.GetRathenaName((JobClass)ushort.MaxValue));
    }

    [Fact]
    public void ProgressionRegistry_PreservesNoviceAndCoversLaterJobs()
    {
        var novice = GeneratedProgressionRegistry.Get(JobClass.Novice);
        Assert.Equal((ushort)99, novice.MaxBaseLevel); Assert.Equal((ushort)10, novice.MaxJobLevel);
        Assert.Equal(548UL, novice.BaseExperienceToNext[1]); Assert.Equal(10UL, novice.JobExperienceToNext[1]);
        Assert.Equal(40U, novice.BaseHp[1]); Assert.Equal(11U, novice.BaseSp[1]);
        Assert.Equal(1U, novice.JobVitalityBonus[6]); Assert.Equal(1U, novice.JobIntelligenceBonus[9]);

        var swordman = GeneratedProgressionRegistry.Get(JobClass.Swordman);
        Assert.Equal((ushort)99, swordman.MaxBaseLevel); Assert.Equal((ushort)50, swordman.MaxJobLevel);
        Assert.Equal(novice.BaseExperienceToNext, swordman.BaseExperienceToNext);
        Assert.True(GeneratedProgressionRegistry.Get(JobClass.RuneKnight).MaxBaseLevel >= 200);
        Assert.Throws<NotSupportedException>(() => GeneratedProgressionRegistry.Get((JobClass)ushort.MaxValue));
        Assert.Throws<NotSupportedException>(() => GeneratedProgressionRegistry.Get(ushort.MaxValue));

        // Regression coverage for the HP/SP formula fallback fix: every one of the 24
        // previously-sparse fourth-job classes (job_basepoints.yml Jobs-block starting at
        // line 19264, BaseAp-only, no BaseHp/BaseSp rows at all) must now resolve via
        // JobDatabase::calc_basehp/calc_basesp (src/map/pc.cpp), matching Dragon_Knight's
        // real pinned HpFactor/HpIncrease/SpFactor/SpIncrease (68/5828/7/14).
        var dragonKnight = GeneratedProgressionRegistry.Get(JobClass.DragonKnight);
        Assert.Equal(93U, dragonKnight.BaseHp[1]); Assert.Equal(152U, dragonKnight.BaseHp[2]); Assert.Equal(212U, dragonKnight.BaseHp[3]);
        Assert.Equal(10U, dragonKnight.BaseSp[1]);
        Assert.All(dragonKnight.BaseHp.Skip(1), value => Assert.True(value > 0));
        Assert.All(dragonKnight.BaseSp.Skip(1), value => Assert.True(value > 0));
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
        var novice = GeneratedSkillTreeRegistry.Get(JobClass.Novice);
        Assert.Equal((ushort)9, novice.DeclaredSkills.Single(entry => entry.SkillId == 1).MaxLevel);
        var trickDead = GeneratedSkillRegistry.GetByName("NV_TRICKDEAD").SkillId;
        Assert.True(novice.DeclaredSkills.Single(entry => entry.SkillId == trickDead).ExcludeFromInheritance);

        var swordman = GeneratedSkillTreeRegistry.Get(JobClass.Swordman);
        Assert.Contains(swordman.EffectiveSkills, entry => entry.SkillId == 1);
        Assert.DoesNotContain(swordman.EffectiveSkills, entry => entry.SkillId == trickDead);
        var twoHand = swordman.EffectiveSkills.Single(entry => entry.SkillId == GeneratedSkillRegistry.GetByName("SM_TWOHAND").SkillId);
        var sword = GeneratedSkillRegistry.GetByName("SM_SWORD").SkillId;
        Assert.Contains(twoHand.Prerequisites, requirement => requirement.SkillId == sword && requirement.Level == 1);

        var knight = GeneratedSkillTreeRegistry.Get(JobClass.Knight);
        Assert.Contains(knight.EffectiveSkills, entry => entry.SkillId == 1);
        Assert.Contains(knight.EffectiveSkills, entry => entry.SkillId == GeneratedSkillRegistry.GetByName("SM_BASH").SkillId);
        var lordKnight = GeneratedSkillTreeRegistry.Get(JobClass.LordKnight);
        Assert.Equal((ushort)50, lordKnight.EffectiveSkills.Single(entry => entry.SkillId == 359).JobLevel);
        var summoner = GeneratedSkillTreeRegistry.Get(JobClass.Summoner);
        Assert.Equal((ushort)100, summoner.EffectiveSkills.Single(entry => entry.SkillId == GeneratedSkillRegistry.GetByName("SU_POWEROFFLOCK").SkillId).BaseLevel);
    }

    [Fact]
    public void EveryGeneratedRelationshipResolvesAcrossRegistries()
    {
        var skillIds = GeneratedSkillRegistry.All.Select(skill => skill.SkillId).ToHashSet();
        foreach (var progression in GeneratedProgressionRegistry.All) Assert.True(JobClassNames.IsDefined((ushort)progression.JobClass));
        foreach (var tree in GeneratedSkillTreeRegistry.All)
        {
            Assert.True(JobClassNames.IsDefined(tree.JobClass));
            Assert.All(tree.InheritedFrom, parent => Assert.True(JobClassNames.IsDefined(parent)));
            Assert.All(tree.DeclaredSkills.Concat(tree.EffectiveSkills), entry =>
            {
                Assert.Contains(entry.SkillId, skillIds);
                Assert.All(entry.Prerequisites, requirement => Assert.Contains(requirement.SkillId, skillIds));
            });
        }
    }
}
