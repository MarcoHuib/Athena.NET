using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionEquipmentTests
{
    [Fact]
    public async Task SuccessfulAuthenticationLoadsEquipmentSnapshot()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var gameplayState = new CharacterGameplayState(9, 3, 0, 2, 4, 123, 456, 30, 8, 45, 11, 51, 3, 2, 3, 4, 5, 6, 7);
        var equipment = new CharacterEquipmentSnapshot(RightHandItemId: 1201, RightHandRefine: 0);
        var gameplayPersistence = new StubGameplayStatePersistence(gameplayState);
        var equipmentPersistence = new StubEquipmentPersistence(equipment);
        await using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false,
            gameplayStatePersistence: gameplayPersistence, equipmentPersistence: equipmentPersistence);
        var auth = new MapAuthOkData(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0);

        await session.CompleteIroAuthenticationAsync(auth);

        Assert.Equal(equipment, session.Equipment);
    }

    [Fact]
    public async Task SuccessfulAuthentication_NoEquipmentRow_LeavesEquipmentNullWithoutFailingAuth()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var gameplayState = new CharacterGameplayState(9, 3, 0, 2, 4, 123, 456, 30, 8, 45, 11, 51, 3, 2, 3, 4, 5, 6, 7);
        var gameplayPersistence = new StubGameplayStatePersistence(gameplayState);
        var equipmentPersistence = new StubEquipmentPersistence(null);
        await using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false,
            gameplayStatePersistence: gameplayPersistence, equipmentPersistence: equipmentPersistence);
        var auth = new MapAuthOkData(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0);

        await session.CompleteIroAuthenticationAsync(auth);

        Assert.Null(session.Equipment);
        Assert.Equal(gameplayState, session.GameplayState!.State);
    }

    private sealed class StubGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == 7 && c == 9 ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(null);
    }

    private sealed class StubEquipmentPersistence(CharacterEquipmentSnapshot? equipment) : ICharacterEquipmentPersistence
    {
        public Task<CharacterEquipmentSnapshot?> GetEquipmentAsync(uint a, uint c, CancellationToken t) => Task.FromResult(a == 7 && c == 9 ? equipment : null);
    }
}
