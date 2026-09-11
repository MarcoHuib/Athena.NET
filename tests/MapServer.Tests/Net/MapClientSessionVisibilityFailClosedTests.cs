using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Step 6 hardening, item 4 established a fail-closed guard: a live World-projected monster with NO
// matching local combat-state entry was treated as a reconciliation/invariant violation and
// suppressed from discovery entirely, to avoid ever fabricating a sentinel HP value.
//
// Step 7 substep 5 SUPERSEDES that guard: CurrentHp/MaxHp packet projection now reads exclusively
// from the World-authoritative WorldMonsterInstance already carried by the projection snapshot/feed
// entry - it never needs (and never reads) the transitional local MonsterCombatStateStore for HP at
// all. There is therefore no fabricated-HP risk left to guard against, and gating discovery on a
// local store entry existing would incorrectly suppress a genuinely valid, Alive, World-projected
// monster merely because MapServer's OWN transitional bookkeeping (still required only for the
// still-live pre-substep9 player->monster attack path, see MonsterCombatCoordinator's own Step 7
// staging comment) happens to lack a corresponding entry. This test now proves the OPPOSITE of its
// original intent: packet visibility must no longer be gated on local combat-state existence.
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
    public async Task SendVisibleMonsterActorsAsync_AliveMonsterWithNoCombatStateEntry_StillDiscoveredUsingWorldInstanceHp()
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
        // Deliberately damaged (35/55) World-authoritative HP - distinct from what a legacy
        // combat-state entry would ever have reported (there is none here at all), so a passing
        // assertion on 35/55 below can only be explained by the packet reading WorldMonsterInstance,
        // never any local store.
        var instance = new WorldMonsterInstance(
            actorId, WorldMonsterIncarnationId.First, MapId, PoringMobId, X: 75, Y: 51,
            WorldMonsterLifecycleState.Alive, IsWalking: false, DestinationX: 75, DestinationY: 51,
            WorldMonsterEngagementState.Unengaged, EngagedTarget: null, CurrentHp: 35, MaxHp: 55);
        projection.ApplySnapshot([instance], epoch, combatState);
        // Directly undo the registration ApplySnapshot itself would normally perform - constructing
        // a projection that reports an Alive monster with genuinely NO corresponding transitional
        // combat-state entry. Pre-Step-7-substep-5 this was a reconciliation-invariant-violation
        // guard target (discovery was suppressed); post-substep-5 this must have NO effect on
        // discovery at all, since packet projection no longer consults this store for HP.
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

        // The monster spawn packet MUST still arrive - a missing local combat-state entry no longer
        // suppresses valid World-projected discovery - and its HP fields must be World's own 35/55,
        // never a fabricated/sentinel value and never anything read from the (deliberately empty)
        // local store.
        var standPacket = await ReadDynamic(stream);
        Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(standPacket));
        Assert.Equal(actorId, BinaryPrimitives.ReadUInt32LittleEndian(standPacket.AsSpan(5)));
        Assert.Equal(55, BinaryPrimitives.ReadInt32LittleEndian(standPacket.AsSpan(73))); // maxHp
        Assert.Equal(35, BinaryPrimitives.ReadInt32LittleEndian(standPacket.AsSpan(77))); // currentHp

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
