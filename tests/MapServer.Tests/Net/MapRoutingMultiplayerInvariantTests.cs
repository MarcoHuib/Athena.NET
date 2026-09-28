using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Tests.Net;

// Pins the multiplayer/World invariant behind the two-laptop live test:
//
//   Same canonical map id  -> one shared authoritative monster projection; every session on that map is
//                             introduced to the others and observes the SAME monster actor identities.
//   Different map ids      -> fully isolated player visibility and monster simulations.
//
// Live evidence (accountId 2000050 on prt_fild08, accountId 2000000 on prt_fild08c) was NOT a visibility
// bug: the two characters were on two PARALLEL channel copies of the same field (start_point used to pick
// one of iz_int/iz_int01..04 at random). Athena.NET has since removed the channels: the copies are
// canonicalized onto ONE shared map (CanonicalMapPolicy), which CanonicalMapMultiplayerIntegrationTests
// proves end to end against a real World grain. The isolation half of this contract still holds for
// genuinely DISTINCT maps, which is what the second test below keeps honest.
public sealed class MapRoutingMultiplayerInvariantTests
{
    private const uint AccountA = 2_000_050;
    private const uint CharA = 10_002;
    private const uint AccountB = 2_000_000;
    private const uint CharB = 1;

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    // Serves a distinct scripted monster snapshot per map id, exactly like World's per-map simulation.
    private sealed class PerMapWorldRuntime : IWorldRuntime
    {
        public Dictionary<string, IReadOnlyList<WorldMonsterInstance>> Snapshots { get; } = new(StringComparer.OrdinalIgnoreCase);
        private readonly Dictionary<string, WorldSimulationEpoch> _epochs = new(StringComparer.OrdinalIgnoreCase);

        public Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId, CancellationToken cancellationToken)
        {
            if (!_epochs.TryGetValue(mapId, out var epoch)) _epochs[mapId] = epoch = WorldSimulationEpoch.NewEpoch();
            if (cursor is null && Snapshots.TryGetValue(mapId, out var snapshot))
                return Task.FromResult(new WorldMonsterFeedPage(mapId, epoch, WorldMonsterFeedStatus.Ready, snapshot, Entries: null, AsOfSequence: 1));
            return Task.FromResult(new WorldMonsterFeedPage(mapId, epoch, WorldMonsterFeedStatus.Ready, Snapshot: null, Entries: [], AsOfSequence: (cursor?.Sequence ?? 0) + 1));
        }

        public Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldMonsterSpawnLoadResult(WorldMonsterSpawnLoadStatus.Loaded, WorldSimulationEpoch.NewEpoch()));
        public Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldMonsterAttackWindowResult(WorldMonsterAttackWindowStatus.StaleLifeReference));
        public Task<WorldMonsterDamageResult> ApplyMonsterDamageAsync(WorldMonsterDamageCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(string mapId, WorldPresenceLifeStateUpdate update, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldPresenceRegistration> RegisterPresenceAsync(string mapId, WorldPlayerPresence presence, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceRegistration("test-partition", mapId, WorldPresenceRegistrationStatus.Registered, 1));
        public Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId, CancellationToken cancellationToken) =>
            Task.FromResult(new WorldPresenceUnregistration("test-partition", mapId, WorldPresenceUnregistrationStatus.Removed, 0));
        public Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed record Player(MapClientSession Session, TcpClient Client, NetworkStream Stream);

    private static MapConfigStore ConfigStore() => new(new MapConfig(), "unused.conf");

    private static MapServerWorld MakeWorld() => new(
        WorldMapRegistry.Tutorial, [], new MonsterCombatCoordinator(new QuestDropResolver([]), new RenewalBasicAttackRules()),
        EmptyMapCollisionProvider.Instance, new UnverifiedGridLineMovementPathProvider(), new MonsterFeedProjectionRegistry(), new MonsterAttackCadenceStore());

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    private static async Task<byte[]> ReadDynamic(Stream stream)
    {
        var header = await ReadExact(stream, 4);
        var length = BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2));
        return [.. header, .. await ReadExact(stream, length - 4)];
    }

    // Boots one authenticated session on `mapId` against the SHARED world (players/visibility/monsters).
    private static async Task<Player> ConnectAsync(MapServerWorld world, IWorldRuntime runtime, uint accountId, uint charId, string name, string mapId, ushort x, ushort y)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connectTask = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connectTask;
        listener.Stop();

        var state = new CharacterGameplayState(charId, 1, 0, 10, 5, 0, 0, 100, 20, 100, 20, 0, 0, 9, 9, 9, 9, 9, 9);
        var session = new MapClientSession(
            (int)accountId, serverClient, new CharServerConnector(ConfigStore()), iroAuthenticated: true,
            mapName: mapId, x: x, y: y,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            accountId: accountId, charId: charId,
            monsterProjections: world.MonsterProjections, combat: world.Combat, combatState: world.CombatState,
            movementPathProvider: world.MovementPathProvider, collisionProvider: world.Collision,
            players: world.Players, playerVisibility: world.PlayerVisibility, visibilityOptions: world.Visibility,
            distributedWorld: runtime);
        _ = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new MapAuthOkData(accountId, charId, 1, 2, 0, 0, false, mapId, x, y, 0, 0, 1, name));

        var stream = client.GetStream();
        await ReadExact(stream, 4 + 6 + 6 + 13);
        var skillListHeader = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(skillListHeader.AsSpan(2)) - 4);
        return new Player(session, client, stream);
    }

    // Map-loaded (0x007D) -> the session becomes World-visible; waits for its presence registration so
    // the ORDER of two players entering is deterministic (existing player-presence tests do the same).
    private static async Task EnterWorldAsync(MapServerWorld world, Player player, uint accountId)
    {
        await player.Stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExact(player.Stream, 15); // 0x01D7 self weapon
        await ReadExact(player.Stream, 6);  // inventory start
        await ReadExact(player.Stream, 4);  // inventory end
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!(world.Players.TryGetByActorId(accountId, out _) && player.Session.IsWorldMapEligible))
        {
            timeout.Token.ThrowIfCancellationRequested();
            await Task.Delay(5, timeout.Token);
        }
    }

    private static WorldMonsterInstance Monster(uint actorId, string mapId, ushort x, ushort y) => new(
        actorId, WorldMonsterIncarnationId.First, mapId, MobId: 1002, x, y, WorldMonsterLifecycleState.Alive,
        IsWalking: false, DestinationX: x, DestinationY: y, WorldMonsterEngagementState.Unengaged, EngagedTarget: null, CurrentHp: 55, MaxHp: 55);

    private static async Task<HashSet<uint>> ReadMonsterDiscoveriesAsync(Stream stream, int expectedCount)
    {
        var ids = new HashSet<uint>();
        for (var i = 0; i < expectedCount; i++)
        {
            var packet = await ReadDynamic(stream);
            Assert.Equal((short)PacketConstants.ZcNotifyStandEntry, BinaryPrimitives.ReadInt16LittleEndian(packet)); // 0x09FF monster stand entry
            Assert.Equal((byte)5, packet[4]);                                                                         // objecttype NPC_MOB_TYPE
            ids.Add(BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5)));
        }
        return ids;
    }

    private static async Task AssertNothingMoreSentAsync(Stream stream)
    {
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var reply = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(reply));
    }

    [Fact]
    public async Task SameCanonicalMap_TwoAuthenticatedSessions_SeeEachOther_AndObserveTheSameWorldMonsterActors()
    {
        const string mapId = "prt_fild08";
        var world = MakeWorld();
        var runtime = new PerMapWorldRuntime();
        var monsters = new[] { Monster(111_000_001, mapId, 258, 200), Monster(111_000_002, mapId, 260, 202) };
        runtime.Snapshots[mapId] = monsters;

        var a = await ConnectAsync(world, runtime, AccountA, CharA, "Alice", mapId, 259, 197);
        await EnterWorldAsync(world, a, AccountA);
        var b = await ConnectAsync(world, runtime, AccountB, CharB, "Bob", mapId, 257, 204);
        await EnterWorldAsync(world, b, AccountB);
        using var _a = a.Client;
        using var _b = b.Client;

        // Both sessions are active and World-visible on the SAME canonical map.
        Assert.True(a.Session.IsWorldMapEligible);
        Assert.True(b.Session.IsWorldMapEligible);
        Assert.Equal(mapId, a.Session.CurrentMapName, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(mapId, b.Session.CurrentMapName, StringComparer.OrdinalIgnoreCase);

        // PlayerVisibilityCoordinator introduces each player to the other (existing player -> 0x09FF,
        // newly entering player -> 0x09FE at the first player, reciprocal).
        var aSeesB = await ReadDynamic(a.Stream);
        Assert.Equal((short)0x09fe, BinaryPrimitives.ReadInt16LittleEndian(aSeesB));
        Assert.Equal(AccountB, BinaryPrimitives.ReadUInt32LittleEndian(aSeesB.AsSpan(5)));
        var bSeesA = await ReadDynamic(b.Stream);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(bSeesA));
        Assert.Equal(AccountA, BinaryPrimitives.ReadUInt32LittleEndian(bSeesA.AsSpan(5)));

        // One monster tick over BOTH sessions: one map, two sessions, ONE projection.
        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, runtime);
        await server.ProcessOneMonsterTickAsync([a.Session, b.Session], CancellationToken.None);

        var expected = monsters.Select(monster => monster.ActorId).ToHashSet();
        Assert.Equal(expected, await ReadMonsterDiscoveriesAsync(a.Stream, monsters.Length));
        Assert.Equal(expected, await ReadMonsterDiscoveriesAsync(b.Stream, monsters.Length)); // The SAME actor ids, not a second copy.

        Assert.True(world.MonsterProjections.TryGet(mapId, out var projection));
        Assert.Equal(monsters.Length, projection.AllInstances.Length);
        Assert.False(world.MonsterProjections.TryGet("prt_fild08c", out _)); // No sibling-copy simulation was ever created.
        await a.Session.DisposeAsync();
        await b.Session.DisposeAsync();
    }

    [Fact]
    public async Task DistinctMaps_SamePhysicalCoordinates_AreIsolated_NoCrossVisibility_NoSharedMonsters()
    {
        // Two genuinely different canonical maps (not channel copies of one map) at the same coordinates:
        // different map ids MUST stay isolated.
        const string baseMap = "prt_fild08";
        const string variantMap = "prt_fild07";
        var world = MakeWorld();
        var runtime = new PerMapWorldRuntime();
        var baseMonsters = new[] { Monster(111_000_001, baseMap, 258, 200), Monster(111_000_002, baseMap, 260, 202) };
        var variantMonsters = new[] { Monster(111_000_101, variantMap, 258, 200), Monster(111_000_102, variantMap, 260, 202) };
        runtime.Snapshots[baseMap] = baseMonsters;
        runtime.Snapshots[variantMap] = variantMonsters;

        var a = await ConnectAsync(world, runtime, AccountA, CharA, "Alice", baseMap, 259, 197);
        await EnterWorldAsync(world, a, AccountA);
        var b = await ConnectAsync(world, runtime, AccountB, CharB, "Bob", variantMap, 257, 204);
        await EnterWorldAsync(world, b, AccountB);
        using var _a = a.Client;
        using var _b = b.Client;

        // Both connected and registered in the SHARED player registry, yet neither is introduced to the other.
        Assert.True(world.Players.TryGetByActorId(AccountA, out _));
        Assert.True(world.Players.TryGetByActorId(AccountB, out _));
        await AssertNothingMoreSentAsync(a.Stream);
        await AssertNothingMoreSentAsync(b.Stream);

        // One tick, TWO maps: each session receives only its own map's monsters and the two projections
        // are separate instances.
        var server = new MapTcpServer(ConfigStore(), new CharServerConnector(ConfigStore()), world, runtime);
        await server.ProcessOneMonsterTickAsync([a.Session, b.Session], CancellationToken.None);

        var baseSeen = await ReadMonsterDiscoveriesAsync(a.Stream, baseMonsters.Length);
        var variantSeen = await ReadMonsterDiscoveriesAsync(b.Stream, variantMonsters.Length);
        Assert.Equal(baseMonsters.Select(m => m.ActorId).ToHashSet(), baseSeen);
        Assert.Equal(variantMonsters.Select(m => m.ActorId).ToHashSet(), variantSeen);
        Assert.Empty(baseSeen.Intersect(variantSeen));
        await AssertNothingMoreSentAsync(a.Stream);
        await AssertNothingMoreSentAsync(b.Stream);

        Assert.True(world.MonsterProjections.TryGet(baseMap, out var baseProjection));
        Assert.True(world.MonsterProjections.TryGet(variantMap, out var variantProjection));
        Assert.NotSame(baseProjection, variantProjection);
        await a.Session.DisposeAsync();
        await b.Session.DisposeAsync();
    }
}
