using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionPlayerPresenceTests
{
    [Fact]
    public async Task TwoRealSessions_DiscoverMoveLookInfoAndDisconnectReciprocally()
    {
        var players = new PlayerPresenceRegistry();
        var coordinator = new PlayerVisibilityCoordinator(players);
        await using var a = await ConnectAsync(1, 101, "Alice", 100, 100, players, coordinator);
        await EnterWorldAsync(a);

        await using var b = await ConnectAsync(2, 102, "Bob", 105, 105, players, coordinator);
        await EnterWorldAsync(b);

        var aSeesB = await ReadVariablePacketAsync(a.Stream);
        Assert.Equal((short)0x09fe, BinaryPrimitives.ReadInt16LittleEndian(aSeesB));
        Assert.Equal(2u, BinaryPrimitives.ReadUInt32LittleEndian(aSeesB.AsSpan(5)));
        Assert.Equal("Bob", Encoding.ASCII.GetString(aSeesB.AsSpan(83)));

        var bSeesA = await ReadVariablePacketAsync(b.Stream);
        Assert.Equal((short)0x09ff, BinaryPrimitives.ReadInt16LittleEndian(bSeesA));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(bSeesA.AsSpan(5)));
        Assert.Equal("Alice", Encoding.ASCII.GetString(bSeesA.AsSpan(84)));

        await a.Stream.WriteAsync(BuildMovementRequest(101, 100));
        Assert.Equal((short)0x0087, BinaryPrimitives.ReadInt16LittleEndian(await ReadExactAsync(a.Stream, 12)));
        var movement = await ReadVariablePacketAsync(b.Stream);
        Assert.Equal((short)0x09fd, BinaryPrimitives.ReadInt16LittleEndian(movement));
        Assert.Equal(1u, BinaryPrimitives.ReadUInt32LittleEndian(movement.AsSpan(5)));

        await a.Stream.WriteAsync(new byte[] { 0x61, 0x03, 0x02, 0x00, 0x01, 0x7f });
        Assert.Equal(Convert.FromHexString("9C0001000000020001"), await ReadExactAsync(b.Stream, 9));

        var infoRequest = new byte[7];
        BinaryPrimitives.WriteInt16LittleEndian(infoRequest, 0x0368);
        BinaryPrimitives.WriteUInt32LittleEndian(infoRequest.AsSpan(2), 1);
        infoRequest[6] = 0xe3; // capture-proven opaque; must not affect lookup.
        await b.Stream.WriteAsync(infoRequest);
        var info = await ReadExactAsync(b.Stream, 106);
        Assert.Equal((short)0x0a30, BinaryPrimitives.ReadInt16LittleEndian(info));
        Assert.Equal("Alice", Encoding.ASCII.GetString(info.AsSpan(6, 24)).TrimEnd('\0'));

        await a.Session.StopAsync();
        var vanish = await ReadExactAsync(b.Stream, 7);
        Assert.Equal(Convert.FromHexString("80000100000000"), vanish);
        Assert.False(players.TryGetByActorId(1, out _));
    }

    private static async Task EnterWorldAsync(TestSession session)
    {
        await session.Stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        await ReadExactAsync(session.Stream, 15); // 0x01D7 self weapon
        await ReadExactAsync(session.Stream, 6);  // inventory start
        await ReadExactAsync(session.Stream, 4);  // inventory end
    }

    private static async Task<TestSession> ConnectAsync(uint accountId, uint charId, string name, ushort x, ushort y,
        PlayerPresenceRegistry players, PlayerVisibilityCoordinator coordinator)
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var endpoint = (IPEndPoint)listener.LocalEndpoint;
        var client = new TcpClient();
        var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port);
        var server = await listener.AcceptTcpClientAsync();
        await connecting;

        var state = new CharacterGameplayState(charId, 1, 0, 10, 5, 0, 0, 100, 20, 100, 20, 0, 0, 9, 9, 9, 9, 9, 9);
        var session = new MapClientSession((int)accountId, server,
            new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state),
            players: players, playerVisibility: coordinator,
            visibilityOptions: WorldVisibilityOptions.Default);
        var auth = new MapAuthOkData(accountId, charId, 1, 2, 0, 0, false, "prontera", x, y, 0, 0, 1, name,
            HairStyle: 4, HairColor: 2, ClothesColor: 1);
        await session.CompleteIroAuthenticationAsync(auth);
        var stream = client.GetStream();
        await ConsumeBootstrapAsync(stream);
        var runTask = session.RunAsync(CancellationToken.None);
        return new TestSession(client, server, stream, session, runTask);
    }

    private static async Task ConsumeBootstrapAsync(Stream stream)
    {
        await ReadExactAsync(stream, 29);
        var header = await ReadExactAsync(stream, 4);
        await ReadExactAsync(stream, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) - 4);
    }

    private static byte[] BuildMovementRequest(ushort x, ushort y)
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x035f);
        packet[2] = (byte)(x >> 2);
        packet[3] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        packet[4] = (byte)(y << 4);
        packet[5] = 0x44;
        return packet;
    }

    private static async Task<byte[]> ReadVariablePacketAsync(Stream stream)
    {
        var header = await ReadExactAsync(stream, 4);
        var packet = new byte[BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2))];
        header.CopyTo(packet, 0);
        (await ReadExactAsync(stream, packet.Length - 4)).CopyTo(packet, 4);
        return packet;
    }

    private static async Task<byte[]> ReadExactAsync(Stream stream, int length)
    {
        var buffer = new byte[length];
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await stream.ReadExactlyAsync(buffer, timeout.Token);
        return buffer;
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint accountId, uint characterId, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint accountId, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken cancellationToken) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private sealed class TestSession(TcpClient client, TcpClient server, NetworkStream stream, MapClientSession session, Task runTask) : IAsyncDisposable
    {
        public TcpClient Client { get; } = client;
        public TcpClient Server { get; } = server;
        public NetworkStream Stream { get; } = stream;
        public MapClientSession Session { get; } = session;
        public Task RunTask { get; } = runTask;
        public async ValueTask DisposeAsync()
        {
            await Session.StopAsync();
            Client.Dispose();
            Server.Dispose();
            try { await RunTask.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        }
    }
}
