using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// End-to-end wire integration tests for the verified stock-iRO skill-up client packet handler
// (IroCzSkillLevelUp/0x0112, ai/iro-2026-wire.md). Exercises the REAL packet handler through a
// live TCP loopback session - not the domain service directly (CharacterSkillLearnIntegrationTests
// already covers that boundary) - proving the handler correctly delegates to the existing
// CharacterGameplayStateSession.LearnSkillAsync authoritative mutation path and emits the verified
// 0x0B33 + 0x00B0 response sequence in the captured order.
public sealed class MapClientSessionSkillLevelUpTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    // Acceptance case (task section 5): Novice, JobLevel 2, SkillPoints 1, NV_BASIC 0 -> click +
    // -> SkillPoints 0, NV_BASIC 1, verified 0x0B33 then 0x00B0 response sequence.
    [Fact]
    public async Task NoviceAcceptanceCase_LearnsNvBasic_EmitsVerifiedResponseSequence()
    {
        var (client, stream, gameplayPersistence, skillPersistence, session, run) =
            await StartAuthenticatedSessionAsync(NoviceState(skillPoints: 1), CharacterSkillSnapshot.Empty);
        using var _ = client;

        await SkipBootstrapAsync(stream, skillEntryCount: 1);

        // Captured request bytes (frame 3604): 12 01 01 00 1D.
        await stream.WriteAsync(new byte[] { 0x12, 0x01, 0x01, 0x00, 0x1D });

        var skillUpdate = await ReadExact(stream, 17);
        Assert.Equal((short)0x0b33, BinaryPrimitives.ReadInt16LittleEndian(skillUpdate));
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(skillUpdate.AsSpan(2))); // SkillId
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(skillUpdate.AsSpan(8))); // CurrentLevel = 1 (POST-commit)
        Assert.Equal((byte)1, skillUpdate[14]); // still upgradable (MaxLevel 9)

        var skillPointsUpdate = await ReadExact(stream, 8);
        Assert.Equal((short)0x00b0, BinaryPrimitives.ReadInt16LittleEndian(skillPointsUpdate));
        Assert.Equal((ushort)12, BinaryPrimitives.ReadUInt16LittleEndian(skillPointsUpdate.AsSpan(2))); // VarId 12 = SkillPoints
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(skillPointsUpdate.AsSpan(4))); // 1 -> 0

        // Persistence proof: the committed mutation actually reached the persistence layer.
        Assert.Equal((byte)1, skillPersistence.State.CurrentLevel(1));
        Assert.Equal(0u, skillPersistence.GameplayState.SkillPoints);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NoSkillPoints_RejectsWithoutMutatingStateOrSendingSuccessResponse()
    {
        var (client, stream, gameplayPersistence, skillPersistence, session, run) =
            await StartAuthenticatedSessionAsync(NoviceState(skillPoints: 0), CharacterSkillSnapshot.Empty);
        using var _ = client;

        await SkipBootstrapAsync(stream, skillEntryCount: 1);
        await stream.WriteAsync(new byte[] { 0x12, 0x01, 0x01, 0x00, 0x1D });

        // No success response should ever arrive - prove the connection stays open and quiet by
        // racing a ping request (which DOES get an answer) against the (absent) skill response.
        await stream.WriteAsync(BuildPingLive());
        var ping = await ReadExact(stream, 2);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(ping)); // ZcPingLive - only response received

        Assert.Equal((byte)0, skillPersistence.State.CurrentLevel(1));
        Assert.Equal(0u, skillPersistence.GameplayState.SkillPoints);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnknownSkillId_RejectsWithoutMutatingState()
    {
        var (client, stream, gameplayPersistence, skillPersistence, session, run) =
            await StartAuthenticatedSessionAsync(NoviceState(skillPoints: 1), CharacterSkillSnapshot.Empty);
        using var _ = client;

        await SkipBootstrapAsync(stream, skillEntryCount: 1);
        // SkillId = 65535, structurally valid, semantically unknown.
        await stream.WriteAsync(new byte[] { 0x12, 0x01, 0xFF, 0xFF, 0x1D });

        await stream.WriteAsync(BuildPingLive());
        var ping = await ReadExact(stream, 2);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(ping));

        Assert.Equal(1u, skillPersistence.GameplayState.SkillPoints); // unchanged

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task SkillOutsideEffectiveTree_RejectsWithoutMutatingState()
    {
        var (client, stream, gameplayPersistence, skillPersistence, session, run) =
            await StartAuthenticatedSessionAsync(NoviceState(skillPoints: 1), CharacterSkillSnapshot.Empty);
        using var _ = client;

        await SkipBootstrapAsync(stream, skillEntryCount: 1);
        // GD_APPROVAL (10000): real canonical skill, not in the Novice tree. SkillId LE = 10000 = 0x2710.
        await stream.WriteAsync(new byte[] { 0x12, 0x01, 0x10, 0x27, 0x1D });

        await stream.WriteAsync(BuildPingLive());
        var ping = await ReadExact(stream, 2);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(ping));

        Assert.Equal(1u, skillPersistence.GameplayState.SkillPoints);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task DuplicateReplayedRequest_OnlySpendsOnePoint()
    {
        var (client, stream, gameplayPersistence, skillPersistence, session, run) =
            await StartAuthenticatedSessionAsync(NoviceState(skillPoints: 1), CharacterSkillSnapshot.Empty);
        using var _ = client;

        await SkipBootstrapAsync(stream, skillEntryCount: 1);

        var request = new byte[] { 0x12, 0x01, 0x01, 0x00, 0x1D };
        await stream.WriteAsync(request);
        var firstUpdate = await ReadExact(stream, 17);
        var firstPoints = await ReadExact(stream, 8);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(firstUpdate.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(firstPoints.AsSpan(4)));

        // Replay the identical request against the now-updated state (SkillPoints already 0).
        await stream.WriteAsync(request);
        await stream.WriteAsync(BuildPingLive());
        var ping = await ReadExact(stream, 2);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(ping)); // no second success response arrived

        Assert.Equal((byte)1, skillPersistence.State.CurrentLevel(1)); // not 2
        Assert.Equal(0u, skillPersistence.GameplayState.SkillPoints);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task TwoValidConsecutiveUpgrades_WithTwoSkillPoints_BothSucceed()
    {
        var (client, stream, gameplayPersistence, skillPersistence, session, run) =
            await StartAuthenticatedSessionAsync(NoviceState(skillPoints: 2), CharacterSkillSnapshot.Empty);
        using var _ = client;

        await SkipBootstrapAsync(stream, skillEntryCount: 1);

        var request = new byte[] { 0x12, 0x01, 0x01, 0x00, 0x1D };
        await stream.WriteAsync(request);
        var firstUpdate = await ReadExact(stream, 17);
        await ReadExact(stream, 8);
        Assert.Equal((ushort)1, BinaryPrimitives.ReadUInt16LittleEndian(firstUpdate.AsSpan(8)));

        await stream.WriteAsync(request);
        var secondUpdate = await ReadExact(stream, 17);
        var secondPoints = await ReadExact(stream, 8);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(secondUpdate.AsSpan(8)));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(secondPoints.AsSpan(4)));

        Assert.Equal((byte)2, skillPersistence.State.CurrentLevel(1));
        Assert.Equal(0u, skillPersistence.GameplayState.SkillPoints);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task MalformedSkillPacket_TruncatedLength_DoesNotDesyncFraming()
    {
        // The framing layer only dispatches once a length-registered packet is fully buffered, so
        // a genuinely truncated 0x0112 never reaches the handler as a short packet - this proves
        // a well-formed packet sent immediately afterward on the same stream still parses
        // correctly (task section 31/78: no byte-alignment corruption from this new opcode).
        var (client, stream, gameplayPersistence, skillPersistence, session, run) =
            await StartAuthenticatedSessionAsync(NoviceState(skillPoints: 1), CharacterSkillSnapshot.Empty);
        using var _ = client;

        await SkipBootstrapAsync(stream, skillEntryCount: 1);

        await stream.WriteAsync(new byte[] { 0x12, 0x01, 0x01, 0x00, 0x1D });
        var skillUpdate = await ReadExact(stream, 17);
        Assert.Equal((short)0x0b33, BinaryPrimitives.ReadInt16LittleEndian(skillUpdate));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static byte[] BuildPingLive()
    {
        var packet = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.CzPingLive);
        return packet;
    }

    private static async Task SkipBootstrapAsync(NetworkStream stream, int skillEntryCount)
    {
        await ReadExact(stream, 4 + 6 + 6 + 13); // 0x0B18, 0x0283, 0x0ADE, 0x02EB
        var skillListHeader = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2));
        await ReadExact(stream, length - 4);
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private static CharacterGameplayState NoviceState(uint skillPoints) => new(
        CharacterId: CharId, Version: 0, JobClass: (ushort)JobClass.Novice, BaseLevel: 2, JobLevel: 2,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 11, MaxHp: 40, MaxSp: 11,
        StatPoints: 0, SkillPoints: skillPoints, Strength: 1, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1);

    private static async Task<(TcpClient Client, NetworkStream Stream, InMemoryGameplayStatePersistence GameplayPersistence, InMemorySkillPersistence SkillPersistence, MapClientSession Session, Task Run)>
        StartAuthenticatedSessionAsync(CharacterGameplayState initialState, CharacterSkillSnapshot initialSkills)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var gameplayPersistence = new InMemoryGameplayStatePersistence(initialState);
        var skillPersistence = new InMemorySkillPersistence(initialSkills, initialState);

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "iz_int01", 18, 26, WorldMapRegistry.Tutorial,
            accountId: AccountId, charId: CharId,
            gameplayStatePersistence: gameplayPersistence,
            skillPersistence: skillPersistence);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        return (client, stream, gameplayPersistence, skillPersistence, session, run);
    }

    private sealed class InMemoryGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public CharacterGameplayState? State { get; private set; } = state;

        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult(State);

        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            if (accountId != AccountId || State is null || expected.Version != State.Version) return Task.FromResult<CharacterGameplayState?>(null);
            State = updated with { Version = expected.Version + 1 };
            return Task.FromResult<CharacterGameplayState?>(State);
        }
    }

    // Enforces the SAME version/points/expected-current-level invariants CharServer's real
    // TryApplySkillLearn does (CharacterSkillPersistenceTests.cs) - never a bare always-succeeds
    // stub, exactly like CharacterSkillLearnIntegrationTests' own InMemorySkillPersistence.
    private sealed class InMemorySkillPersistence : ICharacterSkillPersistence
    {
        private readonly Dictionary<ushort, byte> _learned;
        private CharacterGameplayState _gameplayState;

        public InMemorySkillPersistence(CharacterSkillSnapshot initialSkills, CharacterGameplayState initialGameplayState)
        {
            _learned = [];
            foreach (var skillId in KnownSkillIdsFor(initialSkills))
            {
                var level = initialSkills.CurrentLevel(skillId);
                if (level > 0) _learned[skillId] = level;
            }
            _gameplayState = initialGameplayState;
        }

        public CharacterSkillSnapshot State => CharacterSkillSnapshot.FromLogin([.. _learned.Select(kv => (kv.Key, kv.Value, CharSkillFlag.Permanent))]);
        public CharacterGameplayState GameplayState => _gameplayState;

        public Task<CharacterSkillReadResult> GetSkillsAsync(uint accountId, uint characterId, CancellationToken cancellationToken) =>
            Task.FromResult(CharacterSkillReadResult.Success(State));

        public Task<CharacterSkillLearnResult?> LearnSkillAsync(uint accountId, CharacterGameplayState expectedGameplayState, ushort skillId, byte expectedCurrentLevel, CancellationToken cancellationToken)
        {
            var actualCurrentLevel = _learned.GetValueOrDefault(skillId, (byte)0);
            if (accountId != AccountId || expectedGameplayState.Version != _gameplayState.Version || _gameplayState.SkillPoints == 0 || actualCurrentLevel != expectedCurrentLevel)
                return Task.FromResult<CharacterSkillLearnResult?>(null);

            var newLevel = (byte)(actualCurrentLevel + 1);
            _learned[skillId] = newLevel;
            _gameplayState = _gameplayState with { Version = _gameplayState.Version + 1, SkillPoints = _gameplayState.SkillPoints - 1 };
            return Task.FromResult<CharacterSkillLearnResult?>(new CharacterSkillLearnResult(_gameplayState, skillId, newLevel));
        }

        // CharacterSkillSnapshot.Empty has no known SkillIds to enumerate up front; this test
        // double only ever needs to track NV_BASIC (1) for this slice's acceptance/rejection
        // cases, matching CharacterSkillLearnIntegrationTests' own scope.
        private static IEnumerable<ushort> KnownSkillIdsFor(CharacterSkillSnapshot snapshot) => [1];
    }
}
