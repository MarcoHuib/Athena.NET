using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 6 hardening, item 4: a live World-projected monster with NO matching local combat-state
// entry is a reconciliation/invariant condition, never a legitimate zero-HP actor - MapClientSession
// must fail closed (never mark/send it as visible with a fabricated HP=0), not paper over the gap
// by inventing a sentinel value.
public sealed class MapClientSessionVisibilityFailClosedTests
{
    private const uint AccountId = 31;
    private const uint CharId = 33;
    private const string MapId = "int_land03";
    private const int PoringMobId = 1002;

    private static CharacterGameplayState FreshNovice() => new(
        CharacterId: CharId, Version: 1, JobClass: 0, BaseLevel: 1, JobLevel: 1,
        BaseExperience: 0, JobExperience: 0, CurrentHp: 40, CurrentSp: 10, MaxHp: 40, MaxSp: 10,
        StatPoints: 0, SkillPoints: 0, Strength: 9, Agility: 9, Vitality: 9, Intelligence: 9, Dexterity: 9, Luck: 9);

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint charId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(5));
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    [Fact]
    public async Task SendVisibleMonsterActorsAsync_AliveMonsterWithNoCombatStateEntry_NeverSentAsVisible()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();
        using var disposableClient = client;

        var combatState = new MonsterCombatStateStore(); // Deliberately empty - no Register call for the projected monster below.
        var projections = new MonsterFeedProjectionRegistry();
        var projection = projections.GetOrCreate(MapId);
        var epoch = WorldSimulationEpoch.NewEpoch();
        const uint actorId = 1;
        var instance = new WorldMonsterInstance(
            actorId, WorldMonsterIncarnationId.First, MapId, PoringMobId, X: 75, Y: 51,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 75, DestinationY: 51,
            WorldMonsterEngagementState.Unengaged, EngagedTarget: null);
        projection.ApplySnapshot([instance], epoch, combatState);
        // Directly undo the registration ApplySnapshot itself would normally perform, to construct
        // the exact reconciliation-invariant-violation state this test targets: a projection that
        // reports an Alive monster with genuinely NO corresponding combat-state entry (simulating a
        // bug/race elsewhere that this fail-closed guard exists specifically to catch, not a
        // scenario ApplySnapshot's own correct behavior would ever produce on its own).
        combatState.Remove(new MonsterCombatKey(MapId, epoch, actorId, WorldMonsterIncarnationId.First));

        var gameplayPersistence = new FixedGameplayStatePersistence(FreshNovice());
        var combat = new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules(), combatState);
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            MapId, 75, 51, WorldMapRegistry.Tutorial,
            gameplayStatePersistence: gameplayPersistence,
            accountId: AccountId, charId: CharId, monsterProjections: projections, combat: combat,
            combatState: combatState);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, MapId, 75, 51, 0, 0, 0));

        await ReadExact(stream, 4 + 6 + 6 + 13);
        await ReadDynamic(stream); // 0x0B32 skill list

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa }); // Client map-loaded packet - triggers SendVisibleMonsterActorsAsync via RefreshVisibleWorldActorsAsync.
        await ReadExact(stream, 15); // 0x01D7 self weapon look
        await ReadExact(stream, 6);  // 0x0B08 inventoryStart
        await ReadExact(stream, 4);  // 0x0B0B inventoryEnd

        // No monster spawn packet must arrive at all - confirmed by observing a harmless ping
        // response land next instead of a 0x09FF/0x09FD monster actor packet.
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
