using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Generated.GameData.Mobs;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// End-to-end proof of MapClientSession.NotifyMonsterMovedAsync (called by MapTcpServer's shared
// monster tick loop in production - see that method's own doc comment): an already-visible
// instance gets the capture-verified 0x09FD walk packet (IroMonsterActorPacketsTests), an instance
// that just walked into a stationary session's own 14-cell range gets a fresh 0x09FF discovery
// packet exactly once, and an instance still out of range or on a different map gets nothing.
public sealed class MapClientSessionMonsterMovementTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    private sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
    {
        public bool TrySelectCell(MobSpawnDefinition spawn, int index, out MobPosition position)
        {
            position = new MobPosition(x, y);
            return true;
        }
    }

    // A disconnected test session's default gameplayStatePersistence (charConnector) always fails
    // GetAsync, which makes CompleteIroAuthenticationAsync call HandleAuthFail() and never send
    // the bootstrap burst this test's setup depends on - matching the exact trap
    // MapClientSession's own test-facing-constructor doc comment warns about. Every test in this
    // file supplies this fixture explicitly instead.
    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private static CharacterGameplayState FreshNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, MobInstance Target)> SetupAsync(MobInstance? sharedTarget = null, MonsterRegistry? sharedRegistry = null, MonsterRuntime? monsterRuntime = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var allocator = new WorldActorIdAllocator();
        var registry = sharedRegistry ?? new MonsterRegistry(
            [new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0))],
            allocator, new FixedCellSelector(75, 51), TimeProvider.System);
        var target = sharedTarget ?? registry.AllInstances[0];

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            "int_land03", 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: new FixedGameplayStatePersistence(FreshNovice()),
            accountId: AccountId, charId: CharId, monsters: registry, monsterRuntime: monsterRuntime);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "int_land03", 75, 51, 0, 0, 0));

        // Consume the fixed iRO bootstrap burst (0x0B18/0x0283/0x0ADE/0x02EB) - no inventory
        // packets follow here since no inventory list persistence override was supplied for this
        // narrowly-scoped movement-notification test.
        await ReadExact(stream, 4 + 6 + 6 + 13);

        return (client, stream, session, run, target);
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_MonsterAlreadyVisible_SendsWalkEntryOnTheWire()
    {
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;

        // Trigger visibility (0x007D map-loaded) so the actor is added to _visibleActorIds.
        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        await ReadExact(stream, 4);  // 0x0B0B inventoryEnd
        var spawn = await ReadDynamic(stream);
        var actorId = BinaryPrimitives.ReadUInt32LittleEndian(spawn.AsSpan(5));
        Assert.Equal(target.ActorId, actorId);

        // NotifyMonsterMovedAsync only sends 0x09FD for an instance that is ACTUALLY mid-walk
        // (see that method's own doc comment) - matching how the real MonsterRuntime.ProcessTick
        // only ever reports instances whose position just changed.
        Assert.True(target.TryStartIdleWalk([(75, 51), (76, 51)], cellDurationMs: 400, now: 1, nowOffset: DateTimeOffset.UnixEpoch));

        await session.NotifyMonsterMovedAsync(target, CancellationToken.None);

        var walkPacket = await ReadDynamic(stream);
        Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(walkPacket));
        Assert.Equal((byte)5, walkPacket[4]); // NPC_MOB_TYPE
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(walkPacket.AsSpan(5)));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_MonsterWalksIntoRangeOfAStationaryPlayer_SendsAFreshStandEntry()
    {
        // Session never sends 0x007D in this test - proving discovery does not depend on the
        // player's own map-load/movement re-scan (item 7's "monster moving INTO visibility" case:
        // nothing else re-checks GetVisibleInstances for a player who never moves).
        var (client, stream, session, run, target) = await SetupAsync();
        using var _ = client;

        await session.NotifyMonsterMovedAsync(target, CancellationToken.None);

        var standPacket = await ReadDynamic(stream);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(standPacket));
        Assert.Equal((byte)5, standPacket[4]); // NPC_MOB_TYPE
        Assert.Equal(target.ActorId, BinaryPrimitives.ReadUInt32LittleEndian(standPacket.AsSpan(5)));

        // A second notification for the SAME still-visible instance must not resend a duplicate
        // discovery packet - only the first crossing into visibility does.
        await session.NotifyMonsterMovedAsync(target, CancellationToken.None);
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_MonsterOutOfRangeAndNotYetVisible_SendsNothing()
    {
        var allocator = new WorldActorIdAllocator();
        // 200 cells away - far outside the 14-cell visibility range used by both
        // MonsterRegistry.GetVisibleInstances and NotifyMonsterMovedAsync's own discovery check.
        var farSpawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land03", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([farSpawn], allocator, new FixedCellSelector(275, 275), TimeProvider.System);
        var farTarget = registry.AllInstances[0];

        var (client, stream, session, run, _) = await SetupAsync(sharedTarget: farTarget, sharedRegistry: registry);
        using var _2 = client;

        await session.NotifyMonsterMovedAsync(farTarget, CancellationToken.None);

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task NotifyMonsterMovedAsync_MonsterOnADifferentMap_SendsNothing()
    {
        var allocator = new WorldActorIdAllocator();
        var otherMapSpawn = new MobSpawnDefinition(GeneratedMobs.GPoring, "int_land04", 1, 5000, new WorldSourceInfo("rAthena", "e985006171d2eb320ee512a653f4c83aea3d81b6", "test", 0));
        var registry = new MonsterRegistry([otherMapSpawn], allocator, new FixedCellSelector(75, 51), TimeProvider.System);
        var otherMapTarget = registry.AllInstances[0];

        var (client, stream, session, run, _) = await SetupAsync(sharedTarget: otherMapTarget, sharedRegistry: registry);
        using var _2 = client;

        await session.NotifyMonsterMovedAsync(otherMapTarget, CancellationToken.None);

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
