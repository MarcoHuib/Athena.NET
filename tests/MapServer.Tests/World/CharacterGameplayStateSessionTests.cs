using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterGameplayStateSessionTests
{
    [Fact]
    public async Task SuccessfulMutationCommitsReturnedAuthoritativeState()
    {
        var store=new MemoryStore(State()); var loaded=await store.GetAsync(7,9,default); Assert.NotNull(loaded); var session=new CharacterGameplayStateSession(7,loaded,store);
        var result=await session.MutateAsync(value=>value with{BaseExperience=600,BaseLevel=2,StatPoints=51,CurrentHp=45},default);
        Assert.NotNull(result); Assert.Equal(1UL,session.State.Version); Assert.Equal(600UL,session.State.BaseExperience); Assert.Equal(45U,session.State.CurrentHp);
    }

    [Fact]
    public async Task FailedPersistenceLeavesLocalAuthoritativeStateUnchanged()
    {
        var initial=State(); var store=new MemoryStore(initial){FailUpdates=true}; var session=new CharacterGameplayStateSession(7,initial,store);
        Assert.Null(await session.MutateAsync(value=>value with{CurrentHp=1},default)); Assert.Equal(initial,session.State);
    }

    [Fact]
    public async Task ReconnectLoadsPersistedMultiFieldMutation()
    {
        var store=new MemoryStore(State()); var first=new CharacterGameplayStateSession(7,State(),store);
        await first.MutateAsync(value=>value with{BaseExperience=600,JobExperience=600,BaseLevel=2,JobLevel=4,StatPoints=51,SkillPoints=3,CurrentHp=45},default);
        var reloaded=await store.GetAsync(7,9,default); var second=new CharacterGameplayStateSession(7,reloaded!,store);
        Assert.Equal(first.State,second.State);
    }

    private static CharacterGameplayState State()=>new(9,0,1,1,0,0,40,11,40,11,48,0,1,1,1,1,1,1);
    private sealed class MemoryStore(CharacterGameplayState state):ICharacterGameplayStatePersistence
    {
        private CharacterGameplayState _state=state; public bool FailUpdates{get;set;}
        public Task<CharacterGameplayState?> GetAsync(uint accountId,uint characterId,CancellationToken ct)=>Task.FromResult<CharacterGameplayState?>(accountId==7&&characterId==_state.CharacterId?_state:null);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId,CharacterGameplayState expected,CharacterGameplayState updated,CancellationToken ct)
        { if(FailUpdates||accountId!=7||expected.Version!=_state.Version)return Task.FromResult<CharacterGameplayState?>(null); _state=updated with{Version=expected.Version+1}; return Task.FromResult<CharacterGameplayState?>(_state); }
    }
}
