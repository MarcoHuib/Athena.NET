using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Wire-level integration test using MapClientSession's real socket path (RunAsync/
// HandlePacketAsync) and the real CharacterEquipmentMutationService - only CharServer
// persistence is a test double, matching the established pattern in
// MapClientSessionMonsterCombatTests.
public sealed class MapClientSessionEquipmentMutationTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    private sealed class RecordingInventoryListPersistence(CharacterInventorySnapshot initial) : ICharacterInventoryListPersistence
    {
        private CharacterInventorySnapshot _current = initial;
        public List<(uint SlotIndex, uint Equip)> Calls { get; } = [];
        public bool NextSetResult { get; set; } = true;

        public Task<CharacterInventoryReadResult> GetInventoryAsync(uint a, uint c, CancellationToken t) =>
            Task.FromResult(CharacterInventoryReadResult.Success(_current));

        public Task<bool> SetItemEquipAsync(uint accountId, uint characterId, uint slotIndex, uint equip, CancellationToken cancellationToken)
        {
            Calls.Add((slotIndex, equip));
            if (!NextSetResult) return Task.FromResult(false);
            var items = _current.Items.Select(i => i.SlotIndex == slotIndex ? i with { Equip = equip } : i).ToList();
            _current = new CharacterInventorySnapshot(items);
            return Task.FromResult(true);
        }
    }

    // Verified stock-iRO wire length (ai/iro-2026-wire.md, "Verified equip/unequip request
    // framing"): one opaque trailing byte beyond the pinned rAthena 8-byte shape.
    private static byte[] EquipRequestPacket(ushort clientIndex, uint position)
    {
        var packet = new byte[PacketConstants.IroCzReqWearEquipLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzReqWearEquip);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), position);
        return packet;
    }

    // Verified stock-iRO wire length: one opaque trailing byte beyond the pinned rAthena
    // 4-byte shape.
    private static byte[] UnequipRequestPacket(ushort clientIndex)
    {
        var packet = new byte[PacketConstants.IroCzReqTakeoffEquipLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzReqTakeoffEquip);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        return packet;
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, RecordingInventoryListPersistence Persistence)> SetupAsync(
        CharacterInventorySnapshot initialInventory)
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var client = new TcpClient();
        var connect = client.ConnectAsync((IPEndPoint)listener.LocalEndpoint);
        var serverClient = await listener.AcceptTcpClientAsync();
        await connect;
        listener.Stop();
        var stream = client.GetStream();

        var gameplayState = new CharacterGameplayState(CharId, 1, 0, 1, 1, 0, 0, 40, 11, 40, 11, 48, 0, 1, 1, 1, 1, 1, 1);
        var gameplayPersistence = new StubGameplayStatePersistence(gameplayState);
        var inventoryPersistence = new RecordingInventoryListPersistence(initialInventory);

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            gameplayStatePersistence: gameplayPersistence, inventoryListPersistence: inventoryPersistence,
            accountId: AccountId, charId: CharId);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        // Consume the fixed 4-packet iRO bootstrap (0x0B18/0x0283/0x0ADE/0x02EB).
        await ReadExact(stream, 4 + 6 + 6 + 13);

        return (client, stream, session, run, inventoryPersistence);
    }

    private sealed class StubGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == AccountId && c == CharId ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(null);
    }

    [Fact]
    public async Task UnequipEquippedKnife_PersistsAppearanceUpdatesAndAcks()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial);
        using var _ = client;

        await stream.WriteAsync(UnequipRequestPacket(2)); // client_index = slotIndex(0) + 2

        // Pinned ordering: appearance (0x01D7) sent BEFORE the unequip ack.
        var appearance = await ReadExact(stream, 15);
        Assert.Equal((short)0x01d7, BinaryPrimitives.ReadInt16LittleEndian(appearance));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(7))); // unarmed

        var ack = await ReadExact(stream, 9);
        Assert.Equal((short)0x099a, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(2)));
        Assert.Equal(0x000002u, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4)));
        Assert.Equal((byte)0, ack[8]); // success, inverted flag

        Assert.Single(persistence.Calls);
        Assert.Equal((0u, 0u), persistence.Calls[0]);
        Assert.Null(session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EquipUnequippedKnife_PersistsAppearanceUpdatesAndAcks()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial);
        using var _ = client;

        await stream.WriteAsync(EquipRequestPacket(2, 0x000002));

        // Pinned ordering: ack (0x0999) sent BEFORE the appearance update.
        var ack = await ReadExact(stream, 11);
        Assert.Equal((short)0x0999, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(2)));
        Assert.Equal(0x000002u, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4)));
        Assert.Equal((byte)0, ack[10]); // EquipAckResultOk, NOT inverted

        var appearance = await ReadExact(stream, 15);
        Assert.Equal((short)0x01d7, BinaryPrimitives.ReadInt16LittleEndian(appearance));
        Assert.Equal(1201u, BinaryPrimitives.ReadUInt32LittleEndian(appearance.AsSpan(7))); // Knife.ClientViewId

        Assert.Single(persistence.Calls);
        Assert.Equal((0u, 0x000002u), persistence.Calls[0]);
        Assert.Equal(1201, session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UnequipAlreadyUnequippedItem_SendsFailureAckWithoutMutatingState()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial);
        using var _ = client;

        await stream.WriteAsync(UnequipRequestPacket(2));

        var ack = await ReadExact(stream, 9);
        Assert.Equal((short)0x099a, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((byte)1, ack[8]); // failure, inverted flag

        Assert.Empty(persistence.Calls);
        Assert.Null(session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EquipPersistenceFailure_SendsFailureAckWithoutMutatingState()
    {
        var initial = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0, true, 0, 0, 0)]);
        var (client, stream, session, run, persistence) = await SetupAsync(initial);
        using var _ = client;
        persistence.NextSetResult = false;

        await stream.WriteAsync(EquipRequestPacket(2, 0x000002));

        var ack = await ReadExact(stream, 11);
        Assert.Equal((byte)2, ack[10]); // EquipAckResultFail

        Assert.Null(session.Equipment!.RightHandItemId);

        await client.GetStream().DisposeAsync();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
