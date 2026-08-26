using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapClientSessionEquipmentTests
{
    [Fact]
    public async Task SuccessfulAuthenticationLoadsInventoryAndDerivesEquipment()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var gameplayState = new CharacterGameplayState(9, 3, 0, 2, 4, 123, 456, 30, 8, 45, 11, 51, 3, 2, 3, 4, 5, 6, 7);
        var inventory = new CharacterInventorySnapshot(
        [
            new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0), // equipped Knife
            new CharacterInventoryItem(1, 2301, 1, 0x000010, true, 0, 0, 0), // equipped armor
        ]);
        var gameplayPersistence = new StubGameplayStatePersistence(gameplayState);
        var inventoryPersistence = new StubInventoryListPersistence(CharacterInventoryReadResult.Success(inventory));
        await using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false,
            gameplayStatePersistence: gameplayPersistence, inventoryListPersistence: inventoryPersistence);
        var auth = new MapAuthOkData(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0);

        await session.CompleteIroAuthenticationAsync(auth);

        Assert.Equal(inventory, session.Inventory);
        Assert.Equal(1201, session.Equipment!.RightHandItemId);
    }

    [Fact]
    public async Task SuccessfulAuthentication_NoRightHandItemEquipped_ConfirmedUnarmedWithoutFailingAuth()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var gameplayState = new CharacterGameplayState(9, 3, 0, 2, 4, 123, 456, 30, 8, 45, 11, 51, 3, 2, 3, 4, 5, 6, 7);
        var gameplayPersistence = new StubGameplayStatePersistence(gameplayState);
        var inventoryPersistence = new StubInventoryListPersistence(CharacterInventoryReadResult.Success(new CharacterInventorySnapshot([])));
        await using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), false,
            gameplayStatePersistence: gameplayPersistence, inventoryListPersistence: inventoryPersistence);
        var auth = new MapAuthOkData(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0);

        await session.CompleteIroAuthenticationAsync(auth);

        Assert.NotNull(session.Equipment);
        Assert.Null(session.Equipment!.RightHandItemId);
        Assert.Equal(gameplayState, session.GameplayState!.State);
    }

    [Fact]
    public async Task FailedInventoryRead_FailsAuthentication_NeverConfusedWithEmptyInventory()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start(); var endpoint = (IPEndPoint)listener.LocalEndpoint;
        using var client = new TcpClient(); var connecting = client.ConnectAsync(endpoint.Address, endpoint.Port); using var server = await listener.AcceptTcpClientAsync(); await connecting;
        var gameplayState = new CharacterGameplayState(9, 3, 0, 2, 4, 123, 456, 30, 8, 45, 11, 51, 3, 2, 3, 4, 5, 6, 7);
        var gameplayPersistence = new StubGameplayStatePersistence(gameplayState);
        var inventoryPersistence = new StubInventoryListPersistence(CharacterInventoryReadResult.Failed());
        await using var session = new MapClientSession(1, server, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused")), true,
            gameplayStatePersistence: gameplayPersistence, inventoryListPersistence: inventoryPersistence);
        var auth = new MapAuthOkData(7, 9, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0);

        await session.CompleteIroAuthenticationAsync(auth);

        Assert.Null(session.Inventory);
        Assert.Null(session.Equipment);
        var refusal = new byte[3];
        await client.GetStream().ReadExactlyAsync(refusal);
        Assert.Equal(PacketConstants.ZcRefuseEnter, System.Buffers.Binary.BinaryPrimitives.ReadInt16LittleEndian(refusal));
    }

    private sealed class StubGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == 7 && c == 9 ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(null);
    }

    private sealed class StubInventoryListPersistence(CharacterInventoryReadResult result) : ICharacterInventoryListPersistence
    {
        public Task<CharacterInventoryReadResult> GetInventoryAsync(uint a, uint c, CancellationToken t) => Task.FromResult(result);
        public Task<bool> SetItemEquipAsync(uint a, uint c, uint slotIndex, uint equip, CancellationToken t) => Task.FromResult(false);
    }
}
