using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Proves 0x0B32 lands in the correct official capture-proven position within the concatenated
// map-entry bootstrap byte stream (0x0B18, 0x0283, 0x0ADE, 0x02EB, 0x0B32) - not merely that it
// is sent somewhere. See ai/map-server.md for the capture evidence this ordering is anchored to.
public sealed class MapClientSessionSkillBootstrapTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    [Fact]
    public async Task Bootstrap_SendsFourFixedPacketsThenSkillListInOfficialCaptureOrder()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();
        using var _ = client;

        var gameplayPersistence = new FixedGameplayStatePersistence(new(CharId, 0, 0, 2, 2, 0, 0, 40, 11, 40, 11, 48, 1, 1, 1, 1, 1, 1, 1));
        var skillPersistence = new FixedSkillPersistence(CharacterSkillSnapshot.Empty);

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "iz_int01", 18, 26, WorldMapRegistry.Tutorial,
            accountId: AccountId, charId: CharId,
            gameplayStatePersistence: gameplayPersistence,
            skillPersistence: skillPersistence);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        var inventoryExpansion = await ReadExact(stream, 4);
        Assert.Equal((short)0x0b18, BinaryPrimitives.ReadInt16LittleEndian(inventoryExpansion));

        var accountIdPacket = await ReadExact(stream, 6);
        Assert.Equal((short)0x0283, BinaryPrimitives.ReadInt16LittleEndian(accountIdPacket));

        var overweight = await ReadExact(stream, 6);
        Assert.Equal((short)0x0ade, BinaryPrimitives.ReadInt16LittleEndian(overweight));

        var acceptEnter = await ReadExact(stream, 13);
        Assert.Equal((short)0x02eb, BinaryPrimitives.ReadInt16LittleEndian(acceptEnter));

        // 0x0B32 must come immediately after 0x02EB, matching the capture-proven order exactly -
        // not merely appear somewhere later in the stream.
        var skillListHeader = await ReadExact(stream, 4);
        Assert.Equal((short)0x0b32, BinaryPrimitives.ReadInt16LittleEndian(skillListHeader));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    // A generic job (Novice, JobClass=0) with SkillPoints=0 and no persisted skills still shows
    // its normally-learnable, requirement-satisfied skills (e.g. NV_BASIC, which has no
    // BaseLevel/JobLevel/prerequisite gate) - learned skills and merely-visible-but-unlearned
    // skills must not be hidden just because no skill point is available (task section 45).
    [Fact]
    public async Task SkillList_WithZeroSkillPoints_StillShowsClientVisibleUnlearnedSkills()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();
        using var _ = client;

        var gameplayPersistence = new FixedGameplayStatePersistence(new(CharId, 0, 0, 2, 2, 0, 0, 40, 11, 40, 11, 48, 0, 0, 1, 1, 1, 1, 1)); // SkillPoints=0
        var skillPersistence = new FixedSkillPersistence(CharacterSkillSnapshot.Empty);

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "iz_int01", 18, 26, WorldMapRegistry.Tutorial,
            accountId: AccountId, charId: CharId,
            gameplayStatePersistence: gameplayPersistence,
            skillPersistence: skillPersistence);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        var skillListHeader = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2));
        var body = await ReadExact(stream, length - 4);

        Assert.True(body.Length >= 15); // at least NV_BASIC's entry is present
        var firstSkillId = BinaryPrimitives.ReadUInt16LittleEndian(body);
        Assert.Equal((ushort)1, firstSkillId); // NV_BASIC
        var upgradable = body[12];
        Assert.Equal((byte)1, upgradable); // level 0 < MaxLevel, per pinned upFlag semantics - independent of SkillPoints

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, cts.Token);
        return buffer;
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private sealed class FixedSkillPersistence(CharacterSkillSnapshot snapshot) : ICharacterSkillPersistence
    {
        public Task<CharacterSkillReadResult> GetSkillsAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult(CharacterSkillReadResult.Success(snapshot));
        public Task<CharacterSkillLearnResult?> LearnSkillAsync(uint accountId, CharacterGameplayState expectedGameplayState, ushort skillId, byte expectedCurrentLevel, CancellationToken cancellationToken) => Task.FromResult<CharacterSkillLearnResult?>(null);
    }
}
