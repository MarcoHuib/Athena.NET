using Athena.Net.CharServer.Db.Entities; using Athena.Net.CharServer.Net;
namespace Athena.Net.CharServer.Tests.Net;
public sealed class CharacterGameplayStatePersistenceTests
{
    [Fact]
    public void MultiFieldMutationProducesReloadableEntitySnapshot()
    {
        var character=Character(); var expected=CharacterGameplayStateDto.From(character);
        var updated=expected with{BaseLevel=2,JobLevel=4,BaseExperience=600,JobExperience=600,CurrentHp=45,MaxHp=45,StatPoints=51,SkillPoints=3};
        Assert.True(MapServerSession.TryApplyGameplayState(character,expected,updated));
        Assert.Equal(updated with{Version=1},CharacterGameplayStateDto.From(character));
    }
    [Fact]
    public void StaleVersionCannotOverwriteNewerPersistentState()
    {
        var character=Character(); character.GameplayStateVersion=6; var stale=CharacterGameplayStateDto.From(character) with{Version=5};
        Assert.False(MapServerSession.TryApplyGameplayState(character,stale,stale with{CurrentHp=1})); Assert.Equal(40U,character.Hp); Assert.Equal(6UL,character.GameplayStateVersion);
    }
    [Fact]
    public void GameplayStateAccessRequiresAuthenticatedSessionOwnership()
    {
        IReadOnlySet<(uint AccountId,uint CharId)> owned=new HashSet<(uint,uint)>{(7,9)};
        Assert.True(MapServerSession.IsGameplayStateRequestAuthorized(true,owned,7,9));
        Assert.False(MapServerSession.IsGameplayStateRequestAuthorized(false,owned,7,9));
        Assert.False(MapServerSession.IsGameplayStateRequestAuthorized(true,owned,7,10));
    }

    [Fact]
    public void InvalidGameplayStateCannotBePersisted()
    {
        var expected = CharacterGameplayStateDto.From(Character());

        Assert.False(MapServerSession.IsValidGameplayStateUpdate(expected, expected with { BaseLevel = 0 }));
        Assert.False(MapServerSession.IsValidGameplayStateUpdate(expected, expected with { CurrentHp = expected.MaxHp + 1 }));
        Assert.False(MapServerSession.IsValidGameplayStateUpdate(expected with { Version = ulong.MaxValue }, expected));
    }
    private static CharCharacter Character()=>new(){CharId=9,AccountId=7,GameplayStateVersion=0,BaseLevel=1,JobLevel=1,Hp=40,Sp=11,MaxHp=40,MaxSp=11,StatusPoint=48,Str=1,Agi=1,Vit=1,Int=1,Dex=1,Luk=1};
}
