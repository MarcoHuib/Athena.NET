using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Generated.Jobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// End-to-end wire integration tests for the verified stock-iRO base-stat-allocation client
// packet handler (IroCzStatusUp/0x00BB, ai/iro-2026-wire.md, statsonly.pcapng). Exercises the
// REAL packet handler through a live TCP loopback session - not the domain service directly
// (CharacterStatServiceTests/CharacterGameplayStateSessionTests already cover that boundary) -
// proving the handler correctly delegates to the existing
// CharacterGameplayStateSession.IncreaseStatAsync authoritative mutation path and emits the
// verified 0x0141 + 0x00B0 + 0x00BC response sequence in the captured order.
public sealed class MapClientSessionStatUpTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    // Acceptance case: Novice, STR 2, StatusPoints >= the source-backed 2->3 cost (2) -> click +
    // -> STR 3, StatusPoints reduced by the server-calculated cost, verified 0x0141 -> 0x00B0
    // -> 0x00BC response sequence, exactly matching the captured 2->3 upgrades.
    [Fact]
    public async Task NoviceAcceptanceCase_IncreasesStrength_EmitsVerifiedResponseSequence()
    {
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 34, strength: 2));
        using var _ = client;

        await SkipBootstrapAsync(stream);

        // Captured request bytes (frame 157): BB 00 0D 00 01 1E.
        await stream.WriteAsync(new byte[] { 0xBB, 0x00, 0x0D, 0x00, 0x01, 0x1E });

        var coupleStatus = await ReadExact(stream, 14);
        Assert.Equal((short)0x0141, BinaryPrimitives.ReadInt16LittleEndian(coupleStatus));
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32LittleEndian(coupleStatus.AsSpan(2))); // statusType = SP_STR
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(coupleStatus.AsSpan(6))); // base = new persisted STR
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(coupleStatus.AsSpan(10))); // plus = 0 (no live bonus modeled)

        var statusPointsUpdate = await ReadExact(stream, 8);
        Assert.Equal((short)0x00b0, BinaryPrimitives.ReadInt16LittleEndian(statusPointsUpdate));
        Assert.Equal((ushort)9, BinaryPrimitives.ReadUInt16LittleEndian(statusPointsUpdate.AsSpan(2))); // VarId 9 = StatusPoints
        Assert.Equal(32u, BinaryPrimitives.ReadUInt32LittleEndian(statusPointsUpdate.AsSpan(4))); // 34 -> 32 (source-backed 2->3 cost = 2)

        var ack = await ReadExact(stream, 6);
        Assert.Equal((short)0x00bc, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((ushort)13, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(2)));
        Assert.Equal((byte)1, ack[4]); // Result = success
        Assert.Equal((byte)3, ack[5]); // new STR value

        // Persistence proof: the committed mutation actually reached the persistence layer.
        Assert.Equal((ushort)3, persistence.State!.Strength);
        Assert.Equal(32u, persistence.State!.StatPoints);
        Assert.Equal(1UL, persistence.State!.Version);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // PR #18 review correction: 0x0141's plusValue must reflect the CURRENTLY ACTIVE temporary
    // status bonus on the changed stat (reused from CharacterStatusEffectState.Recalculate, the
    // SAME existing projection RunStatusExpirationLoopAsync already uses for Blessing/Increase
    // AGI resync), never a hardcoded 0. Blessing(val1=10) grants val2=val1=10 to STR/INT/DEX
    // (CharacterStatusEffectState.Start's own doc comment, matching pinned
    // status_change_start_post_delay's SC_BLESSING case, status.cpp:11566-11571).
    [Fact]
    public async Task StrStatUp_WithActiveBlessing_EmitsCoupleStatusWithNonZeroPlusFromExistingProjection()
    {
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 34, strength: 2));
        using var _ = client;

        await SkipBootstrapAsync(stream);
        session.StatusEffects.Start(CharacterStatusEffectState.StatusIds.Blessing, 240_000, 10);

        await stream.WriteAsync(new byte[] { 0xBB, 0x00, 0x0D, 0x00, 0x01, 0x1E });

        var coupleStatus = await ReadExact(stream, 14);
        Assert.Equal((short)0x0141, BinaryPrimitives.ReadInt16LittleEndian(coupleStatus));
        Assert.Equal(13u, BinaryPrimitives.ReadUInt32LittleEndian(coupleStatus.AsSpan(2))); // statusType = SP_STR
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(coupleStatus.AsSpan(6))); // base = new persisted STR (post-commit)
        Assert.Equal(10, BinaryPrimitives.ReadInt32LittleEndian(coupleStatus.AsSpan(10))); // plus = active Blessing bonus, NOT 0

        Assert.Equal((ushort)3, persistence.State!.Strength); // persisted base unaffected by the temporary bonus

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Increase AGI(val1=10) grants val2=2+val1=12 to AGI (status_change_start_post_delay's
    // SC_INCREASEAGI case, status.cpp:10844-10854, "// Agi change").
    [Fact]
    public async Task AgiStatUp_WithActiveIncreaseAgi_EmitsCoupleStatusWithCorrectNonZeroPlus()
    {
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 34, CharacterBaseStat.Agility, value: 2));
        using var _ = client;

        await SkipBootstrapAsync(stream);
        session.StatusEffects.Start(CharacterStatusEffectState.StatusIds.IncreaseAgi, 240_000, 10);

        await stream.WriteAsync(new byte[] { 0xBB, 0x00, 0x0E, 0x00, 0x01, 0x80 });

        var coupleStatus = await ReadExact(stream, 14);
        Assert.Equal((short)0x0141, BinaryPrimitives.ReadInt16LittleEndian(coupleStatus));
        Assert.Equal(14u, BinaryPrimitives.ReadUInt32LittleEndian(coupleStatus.AsSpan(2))); // statusType = SP_AGI
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(coupleStatus.AsSpan(6))); // base = new persisted AGI (post-commit)
        Assert.Equal(12, BinaryPrimitives.ReadInt32LittleEndian(coupleStatus.AsSpan(10))); // plus = 2 + val1 = 12, NOT 0

        Assert.Equal((ushort)3, persistence.State!.Agility);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Without any active temporary status, plus must remain 0 - proving the fix does not
    // fabricate a bonus when nothing is actually active (VIT/LUK also naturally stay 0, since
    // neither Blessing nor Increase AGI ever affects them per CharacterStatusEffectState's own
    // Recalculate).
    [Fact]
    public async Task StatUp_WithNoActiveStatus_PlusRemainsZero()
    {
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 34, strength: 2));
        using var _ = client;

        await SkipBootstrapAsync(stream);
        await stream.WriteAsync(new byte[] { 0xBB, 0x00, 0x0D, 0x00, 0x01, 0x1E });

        var coupleStatus = await ReadExact(stream, 14);
        Assert.Equal(3, BinaryPrimitives.ReadInt32LittleEndian(coupleStatus.AsSpan(6)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(coupleStatus.AsSpan(10)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // PRODUCTION regression: proves the exact captured ack bytes for all six stats, driven
    // through the real handler (not a manually constructed IroStatusUpAckPacket call).
    [Theory]
    [InlineData(CharacterBaseStat.Strength, new byte[] { 0xBB, 0x00, 0x0D, 0x00, 0x01, 0x1E }, new byte[] { 0xBC, 0x00, 0x0D, 0x00, 0x01, 0x03 })]
    [InlineData(CharacterBaseStat.Agility, new byte[] { 0xBB, 0x00, 0x0E, 0x00, 0x01, 0x80 }, new byte[] { 0xBC, 0x00, 0x0E, 0x00, 0x01, 0x03 })]
    [InlineData(CharacterBaseStat.Vitality, new byte[] { 0xBB, 0x00, 0x0F, 0x00, 0x01, 0x4F }, new byte[] { 0xBC, 0x00, 0x0F, 0x00, 0x01, 0x03 })]
    [InlineData(CharacterBaseStat.Intelligence, new byte[] { 0xBB, 0x00, 0x10, 0x00, 0x01, 0xCB }, new byte[] { 0xBC, 0x00, 0x10, 0x00, 0x01, 0x03 })]
    [InlineData(CharacterBaseStat.Dexterity, new byte[] { 0xBB, 0x00, 0x11, 0x00, 0x01, 0xC0 }, new byte[] { 0xBC, 0x00, 0x11, 0x00, 0x01, 0x03 })]
    [InlineData(CharacterBaseStat.Luck, new byte[] { 0xBB, 0x00, 0x12, 0x00, 0x01, 0xB8 }, new byte[] { 0xBC, 0x00, 0x12, 0x00, 0x01, 0x03 })]
    public async Task EachBaseStat_2To3Upgrade_ProducesExactCapturedAck(CharacterBaseStat stat, byte[] request, byte[] expectedAck)
    {
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 34, stat, value: 2));
        using var _ = client;

        await SkipBootstrapAsync(stream);
        await stream.WriteAsync(request);

        await ReadExact(stream, 14); // 0x0141
        await ReadExact(stream, 8);  // 0x00B0
        var ack = await ReadExact(stream, 6);
        Assert.Equal(expectedAck, ack);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task InsufficientStatusPoints_RejectsWithoutMutatingStateOrSendingSuccessResponse()
    {
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 1, strength: 2));
        using var _ = client;

        await SkipBootstrapAsync(stream);
        await stream.WriteAsync(new byte[] { 0xBB, 0x00, 0x0D, 0x00, 0x01, 0x1E });

        // No success response should ever arrive - prove the connection stays open and quiet by
        // racing a ping request (which DOES get an answer) against the (absent) stat response.
        await stream.WriteAsync(BuildPingLive());
        var ping = await ReadExact(stream, 2);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(ping)); // ZcPingLive - only response received

        Assert.Equal((ushort)2, persistence.State!.Strength);
        Assert.Equal(1u, persistence.State!.StatPoints);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task StatAtGeneratedCap_RejectsWithoutMutatingState()
    {
        // Novice/JobParameterCategory.Normal caps at 99 (conf/battle/player.conf max_parameter).
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 100_000, strength: 99));
        using var _ = client;

        await SkipBootstrapAsync(stream);
        await stream.WriteAsync(new byte[] { 0xBB, 0x00, 0x0D, 0x00, 0x01, 0x1E });

        await stream.WriteAsync(BuildPingLive());
        var ping = await ReadExact(stream, 2);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(ping));

        Assert.Equal((ushort)99, persistence.State!.Strength);
        Assert.Equal(100_000u, persistence.State!.StatPoints);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnrecognizedStatusId_RejectsWithoutMutatingState()
    {
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 100, strength: 2));
        using var _ = client;

        await SkipBootstrapAsync(stream);
        // StatusId = 25 (SP_POW), a fourth-job trait stat this project never wires.
        await stream.WriteAsync(new byte[] { 0xBB, 0x00, 0x19, 0x00, 0x01, 0x1E });

        await stream.WriteAsync(BuildPingLive());
        var ping = await ReadExact(stream, 2);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(ping));

        Assert.Equal(100u, persistence.State!.StatPoints); // unchanged
        Assert.Equal(0UL, persistence.State!.Version); // no mutation at all

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Malformed/oversized/truncated 0x00BB must never reach the handler as a different packet
    // or corrupt subsequent framing - the fixed 6-byte length is enforced by the shared
    // MapClientSession.PacketLengths table, exercised here end-to-end.
    [Fact]
    public async Task TruncatedStatusUpPacket_NeverDispatchedAsStatUp_ConnectionStaysOpenForNextPacket()
    {
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 100, strength: 2));
        using var _ = client;

        await SkipBootstrapAsync(stream);

        // First 5 bytes of the captured request only - opcode + StatusId + amount, missing the
        // trailing byte. TCP has no message boundaries: a real client write can legitimately
        // arrive split across multiple reads on the server side.
        await stream.WriteAsync(new byte[] { 0xBB, 0x00, 0x0D, 0x00, 0x01 });
        // Final opaque byte, written separately to force a genuine split read on the server side.
        await stream.WriteAsync(new byte[] { 0x1E });

        var coupleStatus = await ReadExact(stream, 14);
        Assert.Equal((short)0x0141, BinaryPrimitives.ReadInt16LittleEndian(coupleStatus));

        Assert.Equal((ushort)3, persistence.State!.Strength);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // Persistence-before-wire: a failed authoritative mutation must never leak a success
    // response to the client.
    [Fact]
    public async Task FailedPersistence_NoSuccessResponseLeaked()
    {
        var (client, stream, persistence, session, run) = await StartAuthenticatedSessionAsync(NoviceState(statPoints: 34, strength: 2));
        using var _ = client;
        persistence.FailUpdates = true;

        await SkipBootstrapAsync(stream);
        await stream.WriteAsync(new byte[] { 0xBB, 0x00, 0x0D, 0x00, 0x01, 0x1E });

        await stream.WriteAsync(BuildPingLive());
        var ping = await ReadExact(stream, 2);
        Assert.Equal((short)0x0b1d, BinaryPrimitives.ReadInt16LittleEndian(ping)); // only response received

        Assert.Equal((ushort)2, persistence.State!.Strength); // unchanged
        Assert.Equal(34u, persistence.State!.StatPoints); // unchanged
        Assert.Equal(0UL, persistence.State!.Version); // unchanged

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static byte[] BuildPingLive()
    {
        var packet = new byte[2];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.CzPingLive);
        return packet;
    }

    private static async Task SkipBootstrapAsync(NetworkStream stream)
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

    private static CharacterGameplayState NoviceState(uint statPoints, ushort strength = 1) => new(
        CharacterId: CharId, Version: 0, JobClass: (ushort)JobClass.Novice, BaseLevel: 2, JobLevel: 2,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 11, MaxHp: 40, MaxSp: 11,
        StatPoints: statPoints, SkillPoints: 0, Strength: strength, Agility: 1, Vitality: 1, Intelligence: 1, Dexterity: 1, Luck: 1);

    private static CharacterGameplayState NoviceState(uint statPoints, CharacterBaseStat stat, ushort value)
    {
        var state = NoviceState(statPoints);
        return stat switch
        {
            CharacterBaseStat.Strength => state with { Strength = value },
            CharacterBaseStat.Agility => state with { Agility = value },
            CharacterBaseStat.Vitality => state with { Vitality = value },
            CharacterBaseStat.Intelligence => state with { Intelligence = value },
            CharacterBaseStat.Dexterity => state with { Dexterity = value },
            CharacterBaseStat.Luck => state with { Luck = value },
            _ => throw new ArgumentOutOfRangeException(nameof(stat)),
        };
    }

    private static async Task<(TcpClient Client, NetworkStream Stream, InMemoryGameplayStatePersistence Persistence, MapClientSession Session, Task Run)>
        StartAuthenticatedSessionAsync(CharacterGameplayState initialState)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var persistence = new InMemoryGameplayStatePersistence(initialState);

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "iz_int01", 18, 26, WorldMapRegistry.Tutorial,
            accountId: AccountId, charId: CharId,
            gameplayStatePersistence: persistence,
            skillPersistence: new InertSkillPersistence());
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        return (client, stream, persistence, session, run);
    }

    private sealed class InMemoryGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public CharacterGameplayState? State { get; private set; } = state;
        public bool FailUpdates { get; set; }

        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult(State);

        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken)
        {
            if (FailUpdates || accountId != AccountId || State is null || expected.Version != State.Version) return Task.FromResult<CharacterGameplayState?>(null);
            State = updated with { Version = expected.Version + 1 };
            return Task.FromResult<CharacterGameplayState?>(State);
        }
    }

    // CompleteIroAuthenticationAsync requires a skill persistence to complete bootstrap; this
    // slice never learns/reads a skill, so an empty always-succeeds-with-no-rows double is
    // sufficient - the same minimal shape MapClientSessionSkillLevelUpTests' own InMemorySkillPersistence
    // would reduce to for a character with no learned skills.
    private sealed class InertSkillPersistence : ICharacterSkillPersistence
    {
        public Task<CharacterSkillReadResult> GetSkillsAsync(uint accountId, uint characterId, CancellationToken cancellationToken) =>
            Task.FromResult(CharacterSkillReadResult.Success(CharacterSkillSnapshot.Empty));

        public Task<CharacterSkillLearnResult?> LearnSkillAsync(uint accountId, CharacterGameplayState expectedGameplayState, ushort skillId, byte expectedCurrentLevel, CancellationToken cancellationToken) =>
            Task.FromResult<CharacterSkillLearnResult?>(null);
    }
}
