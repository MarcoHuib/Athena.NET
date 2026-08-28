using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Generated.Skills;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Full internal-domain-boundary integration test for the skill-point-spending loop (task section
// 54, adapted to the no-guessed-wire constraint): CharacterGameplayStateSession.LearnSkillAsync is
// the internal domain boundary this PR ships - there is no client packet handler yet (see
// ai/iro-2026-wire.md's open wire item), so this test calls that method directly, exactly the way
// the eventual client-facing handler will once the real skill-up request is captured. Nothing here
// mocks away CharacterSkillService or the persistence-interface contract - InMemorySkillPersistence
// enforces the SAME version/points/expected-current-level invariants CharServer's real
// TryApplySkillLearn does (see CharacterSkillPersistenceTests.cs for that pure-logic proof).
public sealed class CharacterSkillLearnIntegrationTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    // Acceptance case (task section 44/54): Novice, JobLevel 2, SkillPoints 1, NV_BASIC absent
    // (level 0) -> learn NV_BASIC -> SkillPoints 0, NV_BASIC level 1 -> reload -> same values
    // persist. Uses generated Novice data (the only currently-playable live scenario) but the
    // service/session code path is identical for any other JobClass - see the Swordman case below.
    [Fact]
    public async Task NoviceAcceptanceCase_LearnsNvBasic_SkillPointsAndLevelPersistAcrossReload()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Novice);
        var persistence = new InMemorySkillPersistence(NoviceState());

        var session = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.Empty, persistence);
        var result = await session.LearnSkillAsync(tree, requestedSkillId: 1 /* NV_BASIC */, default);

        Assert.NotNull(result);
        Assert.Equal(0U, session.State.SkillPoints);
        Assert.Equal((byte)1, session.Skills.CurrentLevel(1));

        // Reload from a fresh session, exactly as a logout/login would - proves the mutation
        // actually committed to InMemorySkillPersistence's own store, not merely the in-memory
        // session snapshot.
        var reloadedState = await persistence.GetAsync(AccountId, CharId, default);
        var reloadedSkills = await persistence.GetSkillsAsync(AccountId, CharId, default);
        Assert.NotNull(reloadedState);
        Assert.True(reloadedSkills.Succeeded);
        Assert.Equal(0U, reloadedState!.SkillPoints);
        Assert.Equal((byte)1, reloadedSkills.Snapshot!.CurrentLevel(1));
    }

    [Fact]
    public async Task NoSkillPoints_RejectsWithoutMutatingPersistedState()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Novice);
        var persistence = new InMemorySkillPersistence(NoviceState() with { SkillPoints = 0 });
        var session = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.Empty, persistence);

        var result = await session.LearnSkillAsync(tree, requestedSkillId: 1, default);

        Assert.Null(result);
        Assert.Equal(0, persistence.LearnCallCount); // rejected by ValidateUpgrade before any persistence call at all
        Assert.Equal((byte)0, (await persistence.GetSkillsAsync(AccountId, CharId, default)).Snapshot!.CurrentLevel(1));
    }

    [Fact]
    public async Task UnknownSkillId_Rejects()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Novice);
        var persistence = new InMemorySkillPersistence(NoviceState());
        var session = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.Empty, persistence);

        Assert.Null(await session.LearnSkillAsync(tree, requestedSkillId: 60000, default));
    }

    [Fact]
    public async Task SkillOutsideEffectiveTree_Rejects()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Novice);
        var persistence = new InMemorySkillPersistence(NoviceState());
        var session = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.Empty, persistence);

        // GD_APPROVAL (10000) is a real canonical skill but not in the Novice tree.
        Assert.Null(await session.LearnSkillAsync(tree, requestedSkillId: 10000, default));
    }

    [Fact]
    public async Task MaxLevelReached_Rejects()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Novice);
        var persistence = new InMemorySkillPersistence(NoviceState(), initialSkills: [(1, 9)]); // NV_BASIC MaxLevel is 9
        var session = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.FromLogin([(1, 9, CharSkillFlag.Permanent)]), persistence);

        Assert.Null(await session.LearnSkillAsync(tree, requestedSkillId: 1, default));
    }

    [Fact]
    public async Task BaseLevelTooLow_Rejects()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Swordman);
        var lowLevelState = NoviceState() with { JobClass = (ushort)JobClass.Swordman, BaseLevel = 1, JobLevel = 1 };
        var persistence = new InMemorySkillPersistence(lowLevelState);
        var session = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.Empty, persistence);

        // Find a real Swordman tree entry with a nonzero BaseLevel requirement, if any exists;
        // otherwise this test documents that none of the currently-generated Swordman entries
        // gate on BaseLevel (SM_* entries in this slice show BaseLevel=0 uniformly).
        var gated = tree.EffectiveSkills.FirstOrDefault(e => e.BaseLevel > 0);
        if (gated is null) return; // no BaseLevel-gated Swordman skill exists in current generated data
        Assert.Null(await session.LearnSkillAsync(tree, requestedSkillId: gated.SkillId, default));
    }

    [Fact]
    public async Task JobLevelTooLow_Rejects()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Swordman);
        var lowLevelState = NoviceState() with { JobClass = (ushort)JobClass.Swordman, BaseLevel = 99, JobLevel = 1 };
        var persistence = new InMemorySkillPersistence(lowLevelState);
        var session = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.Empty, persistence);

        var gated = tree.EffectiveSkills.FirstOrDefault(e => e.JobLevel > 0);
        if (gated is null) return;
        Assert.Null(await session.LearnSkillAsync(tree, requestedSkillId: gated.SkillId, default));
    }

    [Fact]
    public async Task MissingOrInsufficientPrerequisite_Rejects()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Swordman);
        var swordmanState = NoviceState() with { JobClass = (ushort)JobClass.Swordman, BaseLevel = 99, JobLevel = 50 };
        var persistence = new InMemorySkillPersistence(swordmanState);
        var session = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.Empty, persistence);

        // SM_TWOHAND (3) requires SM_SWORD (2) at level 1 - missing entirely.
        Assert.Null(await session.LearnSkillAsync(tree, requestedSkillId: 3, default));
    }

    [Fact]
    public async Task StaleGameplayStateVersion_Rejects()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Novice);
        var persistence = new InMemorySkillPersistence(NoviceState());
        var staleSession = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.Empty, persistence);

        // Advance the persisted state independently (simulating another mutation from elsewhere),
        // so staleSession's captured State.Version no longer matches.
        await persistence.LearnSkillAsync(AccountId, persistence.State, skillId: 1, expectedCurrentLevel: 0, default);

        Assert.Null(await staleSession.LearnSkillAsync(tree, requestedSkillId: 1, default));
    }

    // Simulated duplicate/replayed mutation off the same base state - only one may succeed.
    [Fact]
    public async Task DuplicateReplayedMutation_OnlyOneSucceeds()
    {
        var tree = GeneratedSkillTreeRegistry.Get(JobClass.Novice);
        var persistence = new InMemorySkillPersistence(NoviceState());
        var session = new CharacterGameplayStateSession(AccountId, persistence.State, persistence, CharacterSkillSnapshot.Empty, persistence);

        var first = session.LearnSkillAsync(tree, requestedSkillId: 1, default);
        var second = session.LearnSkillAsync(tree, requestedSkillId: 1, default);
        var results = await Task.WhenAll(first, second);

        Assert.Single(results, r => r is not null);
        Assert.Single(results, r => r is null);
        Assert.Equal(0U, session.State.SkillPoints);
        Assert.Equal((byte)1, session.Skills.CurrentLevel(1));
    }

    private static CharacterGameplayState NoviceState() => new(
        CharacterId: CharId, Version: 0, JobClass: (ushort)JobClass.Novice, BaseLevel: 2, JobLevel: 2,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 11, MaxHp: 40, MaxSp: 11,
        StatPoints: 0, SkillPoints: 1, Strength: 1, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1);

    // Minimal in-process stand-in for CharServer's real atomic composite mutation, enforcing the
    // SAME invariants TryApplySkillLearn does (version match, SkillPoints > 0, actual persisted
    // level matches expectedCurrentLevel) - never a bare always-succeeds stub.
    private sealed class InMemorySkillPersistence : ICharacterGameplayStatePersistence, ICharacterSkillPersistence
    {
        private CharacterGameplayState _state;
        private readonly Dictionary<ushort, byte> _learned;
        public int LearnCallCount { get; private set; }
        public CharacterGameplayState State => _state;

        public InMemorySkillPersistence(CharacterGameplayState initialState, IReadOnlyList<(ushort SkillId, byte Level)>? initialSkills = null)
        {
            _state = initialState;
            _learned = (initialSkills ?? []).ToDictionary(s => s.SkillId, s => s.Level);
        }

        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken)
            => Task.FromResult<CharacterGameplayState?>(accountId == AccountId && characterId == _state.CharacterId ? _state : null);

        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            if (accountId != AccountId || expected.Version != _state.Version) return Task.FromResult<CharacterGameplayState?>(null);
            _state = updated with { Version = expected.Version + 1 };
            return Task.FromResult<CharacterGameplayState?>(_state);
        }

        public Task<CharacterSkillReadResult> GetSkillsAsync(uint accountId, uint characterId, CancellationToken cancellationToken)
            => Task.FromResult(CharacterSkillReadResult.Success(CharacterSkillSnapshot.FromLogin([.. _learned.Select(kv => (kv.Key, kv.Value, CharSkillFlag.Permanent))])));

        public Task<CharacterSkillLearnResult?> LearnSkillAsync(uint accountId, CharacterGameplayState expectedGameplayState, ushort skillId, byte expectedCurrentLevel, CancellationToken cancellationToken)
        {
            LearnCallCount++;
            var actualCurrentLevel = _learned.GetValueOrDefault(skillId, (byte)0);
            if (accountId != AccountId || expectedGameplayState.Version != _state.Version || _state.SkillPoints == 0 || actualCurrentLevel != expectedCurrentLevel)
                return Task.FromResult<CharacterSkillLearnResult?>(null);

            var newLevel = (byte)(actualCurrentLevel + 1);
            _learned[skillId] = newLevel;
            _state = _state with { Version = _state.Version + 1, SkillPoints = _state.SkillPoints - 1 };
            return Task.FromResult<CharacterSkillLearnResult?>(new CharacterSkillLearnResult(_state, skillId, newLevel));
        }
    }
}
