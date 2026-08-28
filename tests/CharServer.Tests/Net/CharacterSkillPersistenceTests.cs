using Athena.Net.CharServer.Db.Entities; using Athena.Net.CharServer.Net;
namespace Athena.Net.CharServer.Tests.Net;
public sealed class CharacterSkillPersistenceTests
{
    [Fact]
    public void NewSkillInsertDecrementsPointsAndBumpsVersion()
    {
        var character = Character();
        var expected = CharacterGameplayStateDto.From(character);
        Assert.True(MapServerSession.TryApplySkillLearn(character, null, expected, expectedCurrentLevel: 0, out var newLevel, out var isNewRow));
        Assert.Equal((byte)1, newLevel);
        Assert.True(isNewRow);
        Assert.Equal(0U, character.SkillPoint);
        Assert.Equal(1UL, character.GameplayStateVersion);
    }

    [Fact]
    public void ExistingSkillIncrementDecrementsPointsAndBumpsVersion()
    {
        var character = Character();
        var skillRow = new CharSkill { CharId = character.CharId, SkillId = 1, SkillLevel = 3, Flag = 0 };
        var expected = CharacterGameplayStateDto.From(character);
        Assert.True(MapServerSession.TryApplySkillLearn(character, skillRow, expected, expectedCurrentLevel: 3, out var newLevel, out var isNewRow));
        Assert.Equal((byte)4, newLevel);
        Assert.False(isNewRow);
        Assert.Equal((byte)4, skillRow.SkillLevel);
        Assert.Equal(0U, character.SkillPoint);
        Assert.Equal(1UL, character.GameplayStateVersion);
    }

    [Fact]
    public void StaleVersionRejectsMutationWithNoPartialWrites()
    {
        var character = Character(); character.GameplayStateVersion = 6;
        var stale = CharacterGameplayStateDto.From(character) with { Version = 5 };
        Assert.False(MapServerSession.TryApplySkillLearn(character, null, stale, expectedCurrentLevel: 0, out _, out _));
        Assert.Equal(1U, character.SkillPoint);
        Assert.Equal(6UL, character.GameplayStateVersion);
    }

    [Fact]
    public void NoSkillPointsRejectsMutation()
    {
        var character = Character(); character.SkillPoint = 0;
        var expected = CharacterGameplayStateDto.From(character);
        Assert.False(MapServerSession.TryApplySkillLearn(character, null, expected, expectedCurrentLevel: 0, out _, out _));
        Assert.Equal(0U, character.SkillPoint);
        Assert.Equal(0UL, character.GameplayStateVersion);
    }

    [Fact]
    public void ExpectedCurrentLevelMismatchRejectsMutation_NoPartialWrites()
    {
        var character = Character();
        var skillRow = new CharSkill { CharId = character.CharId, SkillId = 1, SkillLevel = 3, Flag = 0 };
        var expected = CharacterGameplayStateDto.From(character);
        // Caller believes the current level is 2, but the actual persisted row is 3 - a stale/
        // replayed request must be rejected without mutating either the row or the character.
        Assert.False(MapServerSession.TryApplySkillLearn(character, skillRow, expected, expectedCurrentLevel: 2, out _, out _));
        Assert.Equal((byte)3, skillRow.SkillLevel);
        Assert.Equal(1U, character.SkillPoint);
        Assert.Equal(0UL, character.GameplayStateVersion);
    }

    [Fact]
    public void CharacterIdMismatchRejectsMutation()
    {
        var character = Character();
        var expected = CharacterGameplayStateDto.From(character) with { CharacterId = character.CharId + 1 };
        Assert.False(MapServerSession.TryApplySkillLearn(character, null, expected, expectedCurrentLevel: 0, out _, out _));
        Assert.Equal(1U, character.SkillPoint);
        Assert.Equal(0UL, character.GameplayStateVersion);
    }

    // Replay/concurrency scenario (task's own exact numbers): starting Version=10, SkillPoints=1,
    // SkillLevel=0; two mutations both expect Version=10 - only one may succeed, ending at
    // Version=11, SkillPoints=0, SkillLevel=1. The second must fail with zero partial writes.
    [Fact]
    public void DuplicateReplayedMutationOnlySucceedsOnce()
    {
        var character = Character(); character.GameplayStateVersion = 10; character.SkillPoint = 1;
        var expected = CharacterGameplayStateDto.From(character);

        var firstSucceeded = MapServerSession.TryApplySkillLearn(character, null, expected, expectedCurrentLevel: 0, out var firstLevel, out var firstIsNew);
        Assert.True(firstSucceeded);
        Assert.Equal((byte)1, firstLevel);
        Assert.True(firstIsNew);
        Assert.Equal(0U, character.SkillPoint);
        Assert.Equal(11UL, character.GameplayStateVersion);

        // Second attempt replays the SAME originally-captured `expected` (still Version=10) -
        // the character has already moved to Version=11, so this must be rejected.
        var secondSucceeded = MapServerSession.TryApplySkillLearn(character, null, expected, expectedCurrentLevel: 0, out _, out _);
        Assert.False(secondSucceeded);
        Assert.Equal(0U, character.SkillPoint);
        Assert.Equal(11UL, character.GameplayStateVersion);
    }

    private static CharCharacter Character() => new() { CharId = 9, AccountId = 7, GameplayStateVersion = 0, BaseLevel = 1, JobLevel = 1, Hp = 40, Sp = 11, MaxHp = 40, MaxSp = 11, StatusPoint = 48, SkillPoint = 1, Str = 1, Agi = 1, Vit = 1, Int = 1, Dex = 1, Luk = 1 };
}
