using Athena.Net.MapServer.Generated.Skills;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterGameplayStateSessionTests
{
    // Fixture tree matching CharacterSkillServiceTests' own fixture shape - a single ungated skill,
    // isolated from any real generated job so these session/lock tests don't depend on job data.
    private static readonly GeneratedSkillTreeEntry LearnableEntry = new(SkillId: 1, MaxLevel: 9, BaseLevel: 0, JobLevel: 0, Prerequisites: [], ExcludeFromInheritance: false);
    private static readonly GeneratedSkillTreeDefinition LearnableTree = new(JobClass: 0, InheritedFrom: [], DeclaredSkills: [LearnableEntry], EffectiveSkills: [LearnableEntry]);

    [Fact]
    public async Task LearnSkillAsync_SuccessfulMutation_ReplacesBothStateAndSkillsTogether()
    {
        var store = new MemorySkillStore(State() with { SkillPoints = 1 });
        var session = new CharacterGameplayStateSession(7, store.State, store, CharacterSkillSnapshot.Empty, store);

        var result = await session.LearnSkillAsync(LearnableTree, requestedSkillId: 1, default);

        Assert.NotNull(result);
        Assert.Equal(0U, session.State.SkillPoints);
        Assert.Equal(1UL, session.State.Version);
        Assert.Equal((byte)1, session.Skills.CurrentLevel(1));
    }

    [Fact]
    public async Task LearnSkillAsync_ValidationRejection_NoIOAttempted_StateUnchanged()
    {
        var store = new MemorySkillStore(State() with { SkillPoints = 0 }); // no points -> ValidateUpgrade rejects before any persistence call
        var session = new CharacterGameplayStateSession(7, store.State, store, CharacterSkillSnapshot.Empty, store);

        var result = await session.LearnSkillAsync(LearnableTree, requestedSkillId: 1, default);

        Assert.Null(result);
        Assert.Equal(0U, session.State.SkillPoints);
        Assert.Equal(0UL, session.State.Version);
        Assert.Equal((byte)0, session.Skills.CurrentLevel(1));
        Assert.Equal(0, store.LearnCallCount);
    }

    [Fact]
    public async Task LearnSkillAsync_FailedPersistence_LeavesBothStateAndSkillsUnchanged()
    {
        var store = new MemorySkillStore(State() with { SkillPoints = 1 }) { FailLearns = true };
        var session = new CharacterGameplayStateSession(7, store.State, store, CharacterSkillSnapshot.Empty, store);

        var result = await session.LearnSkillAsync(LearnableTree, requestedSkillId: 1, default);

        Assert.Null(result);
        Assert.Equal(1U, session.State.SkillPoints);
        Assert.Equal(0UL, session.State.Version);
        Assert.Equal((byte)0, session.Skills.CurrentLevel(1));
    }

    [Fact]
    public async Task LearnSkillAsync_ReconnectReload_RestoresBothSkillPointsAndSkillLevel()
    {
        var store = new MemorySkillStore(State() with { SkillPoints = 1 });
        var first = new CharacterGameplayStateSession(7, store.State, store, CharacterSkillSnapshot.Empty, store);
        await first.LearnSkillAsync(LearnableTree, requestedSkillId: 1, default);

        var reloadedState = await store.GetAsync(7, 9, default);
        var reloadedSkills = await store.GetSkillsAsync(7, 9, default);
        var second = new CharacterGameplayStateSession(7, reloadedState!, store, reloadedSkills.Snapshot!, store);

        Assert.Equal(first.State, second.State);
        Assert.Equal(first.Skills.Learned, second.Skills.Learned);
        Assert.Equal((byte)1, second.Skills.CurrentLevel(1));
    }

    // Replay/concurrency scenario (task's own exact numbers): starting Version=10, SkillPoints=1,
    // SkillLevel=0; two concurrent LearnSkillAsync calls against the same skill both starting from
    // the same session - only one may succeed. The session's own SemaphoreSlim(1,1) (the SAME lock
    // MutateAsync uses) serializes the two calls: whichever runs second observes the first's
    // already-updated State/Skills (SkillPoints=0) and is correctly rejected by ValidateUpgrade
    // (NoSkillPoints) rather than racing CharServer or double-spending the point.
    [Fact]
    public async Task LearnSkillAsync_TwoConcurrentCallsAgainstSameSession_OnlyOneSucceeds()
    {
        var store = new MemorySkillStore(State() with { Version = 10, SkillPoints = 1 });
        var session = new CharacterGameplayStateSession(7, store.State, store, CharacterSkillSnapshot.Empty, store);

        var first = session.LearnSkillAsync(LearnableTree, requestedSkillId: 1, default);
        var second = session.LearnSkillAsync(LearnableTree, requestedSkillId: 1, default);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, r => r is not null);
        Assert.Single(results, r => r is null);
        Assert.Equal(11UL, session.State.Version);
        Assert.Equal(0U, session.State.SkillPoints);
        Assert.Equal((byte)1, session.Skills.CurrentLevel(1));
    }

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

    [Fact]
    public async Task IncreaseStatAsync_ValidMutation_PersistenceCalled_StatIncreases_StatusPointsDecrease_VersionIncreases()
    {
        var initial = State() with { StatPoints = 51 };
        var store = new MemoryStore(initial);
        var session = new CharacterGameplayStateSession(7, initial, store);

        var result = await session.IncreaseStatAsync(CharacterBaseStat.Strength, 1, default);

        Assert.NotNull(result);
        Assert.Equal((ushort)1, result.PreviousValue);
        Assert.Equal((ushort)2, result.NewValue);
        Assert.Equal(2U, result.StatusPointsSpent);
        Assert.Equal((ushort)2, session.State.Strength);
        Assert.Equal(49U, session.State.StatPoints);
        Assert.Equal(1UL, session.State.Version);
    }

    [Fact]
    public async Task IncreaseStatAsync_ValidationRejection_NoPersistenceAttempted_StateUnchanged()
    {
        var initial = State() with { StatPoints = 0 }; // no points -> ValidateIncrease rejects before any persistence call
        var store = new MemoryStore(initial);
        var session = new CharacterGameplayStateSession(7, initial, store);

        var result = await session.IncreaseStatAsync(CharacterBaseStat.Strength, 1, default);

        Assert.Null(result);
        Assert.Equal((ushort)1, session.State.Strength);
        Assert.Equal(0U, session.State.StatPoints);
        Assert.Equal(0UL, session.State.Version);
    }

    [Fact]
    public async Task IncreaseStatAsync_FailedPersistence_StateUnchanged()
    {
        var initial = State() with { StatPoints = 51 };
        var store = new MemoryStore(initial) { FailUpdates = true };
        var session = new CharacterGameplayStateSession(7, initial, store);

        var result = await session.IncreaseStatAsync(CharacterBaseStat.Strength, 1, default);

        Assert.Null(result);
        Assert.Equal((ushort)1, session.State.Strength);
        Assert.Equal(51U, session.State.StatPoints);
        Assert.Equal(0UL, session.State.Version);
    }

    // Two concurrent/replayed requests against the same session must serialize through the same
    // mutation lock IncreaseStatAsync shares with MutateAsync/LearnSkillAsync, so a character
    // can never overspend Status Points by racing two requests against the same starting state.
    // Starting StatPoints=2 covers exactly one increase (cost 2); the second concurrent call must
    // observe the first's already-updated State and be rejected as InsufficientStatusPoints.
    [Fact]
    public async Task IncreaseStatAsync_TwoConcurrentCallsAgainstSameSession_CannotOverspendStatusPoints()
    {
        var initial = State() with { Version = 10, StatPoints = 2 };
        var store = new MemoryStore(initial);
        var session = new CharacterGameplayStateSession(7, initial, store);

        var first = session.IncreaseStatAsync(CharacterBaseStat.Strength, 1, default);
        var second = session.IncreaseStatAsync(CharacterBaseStat.Strength, 1, default);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, r => r is not null);
        Assert.Single(results, r => r is null);
        Assert.Equal(11UL, session.State.Version);
        Assert.Equal(0U, session.State.StatPoints);
        Assert.Equal((ushort)2, session.State.Strength);
    }

    private static CharacterGameplayState State()=>new(9,0,0,1,1,0,0,40,11,40,11,48,0,1,1,1,1,1,1);
    private sealed class MemoryStore(CharacterGameplayState state):ICharacterGameplayStatePersistence
    {
        private CharacterGameplayState _state=state; public bool FailUpdates{get;set;}
        public Task<CharacterGameplayState?> GetAsync(uint accountId,uint characterId,CancellationToken ct)=>Task.FromResult<CharacterGameplayState?>(accountId==7&&characterId==_state.CharacterId?_state:null);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId,CharacterGameplayState expected,CharacterGameplayState updated,CancellationToken ct)
        { if(FailUpdates||accountId!=7||expected.Version!=_state.Version)return Task.FromResult<CharacterGameplayState?>(null); _state=updated with{Version=expected.Version+1}; return Task.FromResult<CharacterGameplayState?>(_state); }
    }

    // Combined ICharacterGameplayStatePersistence + ICharacterSkillPersistence fixture, mirroring
    // MemoryStore's exact version-check semantics but ALSO tracking a single learned skill's level
    // - the minimal in-memory stand-in for CharServer's own atomic composite mutation
    // (TryApplySkillLearn), used to exercise CharacterGameplayStateSession.LearnSkillAsync's own
    // orchestration/locking without a real DB round-trip.
    private sealed class MemorySkillStore(CharacterGameplayState state) : ICharacterGameplayStatePersistence, ICharacterSkillPersistence
    {
        private CharacterGameplayState _state = state;
        private byte _skillLevel;
        public bool FailLearns { get; set; }
        public int LearnCallCount { get; private set; }
        public CharacterGameplayState State => _state;

        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken ct)
            => Task.FromResult<CharacterGameplayState?>(accountId == 7 && characterId == _state.CharacterId ? _state : null);

        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken ct)
        {
            if (accountId != 7 || expected.Version != _state.Version) return Task.FromResult<CharacterGameplayState?>(null);
            _state = updated with { Version = expected.Version + 1 };
            return Task.FromResult<CharacterGameplayState?>(_state);
        }

        public Task<CharacterSkillReadResult> GetSkillsAsync(uint accountId, uint characterId, CancellationToken ct)
        {
            var rows = _skillLevel == 0 ? [] : new List<(ushort SkillId, byte Level, CharSkillFlag Flag)> { (1, _skillLevel, CharSkillFlag.Permanent) };
            return Task.FromResult(CharacterSkillReadResult.Success(CharacterSkillSnapshot.FromLogin(rows)));
        }

        public Task<CharacterSkillLearnResult?> LearnSkillAsync(uint accountId, CharacterGameplayState expectedGameplayState, ushort skillId, byte expectedCurrentLevel, CancellationToken ct)
        {
            LearnCallCount++;
            if (FailLearns || accountId != 7 || expectedGameplayState.Version != _state.Version || _skillLevel != expectedCurrentLevel || _state.SkillPoints == 0)
                return Task.FromResult<CharacterSkillLearnResult?>(null);
            _skillLevel = (byte)(expectedCurrentLevel + 1);
            _state = _state with { Version = _state.Version + 1, SkillPoints = _state.SkillPoints - 1 };
            return Task.FromResult<CharacterSkillLearnResult?>(new CharacterSkillLearnResult(_state, skillId, _skillLevel));
        }
    }
}
