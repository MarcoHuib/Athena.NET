using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;
using Athena.Net.MapServer.World.GeneratedScripts;

namespace Athena.Net.MapServer.Tests.Net;

// Session-level canonicalization (CanonicalMapPolicy) at the MapServer boundaries: auth load, TeleportTo via
// a static warp, script warp (INpcScriptHost.WarpAsync) and the save-point request. A destination that names a
// legacy channel copy is folded onto its canonical map without editing the generated source that named it.
public sealed class MapClientSessionCanonicalMapTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    private sealed class RecordingPersistence : ICharacterPositionPersistence
    {
        public (string Map, ushort X, ushort Y)? Position { get; private set; }
        public (string Map, ushort X, ushort Y)? SavePoint { get; private set; }
        public Task<bool> SavePositionAsync(uint accountId, uint charId, string mapName, ushort x, ushort y, CancellationToken cancellationToken)
        {
            Position = (mapName, x, y);
            return Task.FromResult(true);
        }
        public Task<bool> SavePointAsync(uint accountId, uint charId, string mapName, ushort x, ushort y, CancellationToken cancellationToken)
        {
            SavePoint = (mapName, x, y);
            return Task.FromResult(true);
        }
    }

    private sealed class FixedGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(state);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState expected, CharacterGameplayState updated, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(updated);
    }

    private static async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task Run, RecordingPersistence Persistence)> SetupAsync(string authMap, WorldMapRegistry? registry = null)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var persistence = new RecordingPersistence();
        var state = new CharacterGameplayState(CharId, 1, 0, 10, 5, 0, 0, 100, 20, 100, 20, 0, 0, 9, 9, 9, 9, 9, 9);
        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            gameplayStatePersistence: new FixedGameplayStatePersistence(state), positionPersistence: persistence,
            worldMapRegistry: registry, accountId: AccountId, charId: CharId);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, authMap, 257, 204, 0, 0, 0));
        var stream = client.GetStream();
        await ReadExact(stream, 29);
        var header = await ReadExact(stream, 4);
        await ReadExact(stream, BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) - 4);
        return (client, stream, session, run, persistence);
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer).AsTask().WaitAsync(TimeSpan.FromSeconds(10));
        return buffer;
    }

    [Theory]
    [InlineData("prt_fild08c", "prt_fild08")]
    [InlineData("izlude_b", "izlude")]
    [InlineData("iz_int03", "iz_int")]
    [InlineData("int_land04", "int_land")]
    [InlineData("prt_fild08", "prt_fild08")]
    public async Task AuthLoad_OfAPersistedAlias_StartsTheSessionOnTheCanonicalMap_AtTheSameCell(string persisted, string expected)
    {
        var (client, _, session, run, _) = await SetupAsync(persisted);

        Assert.Equal(expected, session.CurrentMapName);
        Assert.Equal((ushort)257, session.CurrentX);
        Assert.Equal((ushort)204, session.CurrentY);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task ScriptWarpToALegacyCopy_LandsOnTheCanonicalMap_OnTheWireAndInPersistence()
    {
        var (client, stream, session, run, persistence) = await SetupAsync("iz_int");
        var host = (INpcScriptHost)session;

        await host.WarpAsync("izlude_d", 196, 209, CancellationToken.None);

        var mapChange = await ReadExact(stream, 22);
        Assert.Equal((short)0x0091, BinaryPrimitives.ReadInt16LittleEndian(mapChange));
        Assert.Equal("izlude.gat", Encoding.ASCII.GetString(mapChange.AsSpan(2, 16)).TrimEnd('\0'));
        Assert.Equal("izlude", session.CurrentMapName);
        Assert.Equal(("izlude", (ushort)196, (ushort)209), persistence.Position);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task SavePointOnALegacyCopy_IsPersistedAsTheCanonicalMap()
    {
        var (client, _, session, run, persistence) = await SetupAsync("iz_int");
        var host = (INpcScriptHost)session;

        await host.SetSavePointAsync("izlude_d", 128, 142, CancellationToken.None);

        Assert.Equal(("izlude", (ushort)128, (ushort)142), persistence.SavePoint);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
    }

    [Fact]
    public async Task StaticWarpWhoseSourceNamesALegacyCopy_StillFoldsOntoTheCanonicalMap()
    {
        // A pinned source row (not edited by Athena) whose destination is a channel copy: the generic
        // TeleportTo boundary resolves it, the source layer stays untouched.
        var warp = new WarpDefinition("iz001_d", "prt_fild08", 257, 204, 3, 3, "izlude_d", 25, 99, true, "test", 1);
        var (client, stream, session, run, _) = await SetupAsync("prt_fild08c", new WorldMapRegistry([warp]));

        await stream.WriteAsync(new byte[] { 0x7d, 0x00, 0xaa });
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (session.CurrentMapName != "izlude" && DateTime.UtcNow < deadline) await Task.Delay(10);

        Assert.Equal("izlude", session.CurrentMapName);
        Assert.Equal((ushort)25, session.CurrentX);
        Assert.Equal((ushort)99, session.CurrentY);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
    }
}
