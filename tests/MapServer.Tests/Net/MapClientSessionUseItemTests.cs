using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using Athena.Net.MapServer.Config;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Wire-level integration test using MapClientSession's real socket path (RunAsync/
// HandlePacketAsync), matching the established pattern in MapClientSessionEquipmentMutationTests/
// MapClientSessionMonsterCombatTests - only CharServer persistence is a test double.
//
// Live-verified request framing (see ai/map-server.md "Item-use request"): 0x00A7, 9 bytes,
// opcode.W clientIndex.W accountId.L opaqueByte.B. Fixture uses the same First Aid Box (23484)
// container scenario the live capture exercised - clientIndex 4 -> SlotIndex 2 -> ItemId 23484.
public sealed class MapClientSessionUseItemTests
{
    private const uint AccountId = 2_000_000;
    private const uint CharId = 9;

    private sealed class RecordingInventoryListPersistence(CharacterInventorySnapshot initial) : ICharacterInventoryListPersistence
    {
        private CharacterInventorySnapshot _current = initial;
        public Task<CharacterInventoryReadResult> GetInventoryAsync(uint a, uint c, CancellationToken t) =>
            Task.FromResult(CharacterInventoryReadResult.Success(_current));
        public Task<bool> SetItemEquipAsync(uint a, uint c, uint slotIndex, uint equip, CancellationToken t) => Task.FromResult(false);
    }

    // Simulates CharServer's real InStableSlotOrder-derived behavior: consuming decrements (or
    // deletes, matching pinned pc_delitem) the row at the given slot; adding appends a new row
    // (or increments an existing stack) at the end of the current stable ordering - mirroring
    // MapServerSession.HandleInventoryConsumeAsync/HandleInventoryAddRequestAsync without a real
    // database.
    private sealed class RecordingInventoryPersistence(List<CharacterInventoryItem> rows) : ICharacterInventoryPersistence
    {
        public bool FailNextConsume { get; set; }
        public bool FailNextAdd { get; set; }
        public List<(uint SlotIndex, uint Amount)> ConsumeCalls { get; } = [];
        public List<(int ItemId, uint Amount)> AddCalls { get; } = [];

        public Task<InventoryAddPersistenceResult> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken)
        {
            AddCalls.Add((itemId, amount));
            if (FailNextAdd) { FailNextAdd = false; return Task.FromResult(InventoryAddPersistenceResult.Failed()); }

            var existingIndex = rows.FindIndex(r => r.ItemId == itemId);
            if (existingIndex >= 0)
            {
                var updated = rows[existingIndex].Amount + amount;
                rows[existingIndex] = rows[existingIndex] with { Amount = updated };
                return Task.FromResult(new InventoryAddPersistenceResult(true, updated, rows[existingIndex].SlotIndex, 0, true, 0, 0, 0));
            }

            var slotIndex = (uint)rows.Count;
            rows.Add(new CharacterInventoryItem(slotIndex, itemId, amount, 0, true, 0, 0, 0));
            return Task.FromResult(new InventoryAddPersistenceResult(true, amount, slotIndex, 0, true, 0, 0, 0));
        }

        public Task<InventoryConsumePersistenceResult> ConsumeItemAsync(uint accountId, uint charId, uint slotIndex, uint amount, CancellationToken cancellationToken)
        {
            ConsumeCalls.Add((slotIndex, amount));
            if (FailNextConsume) { FailNextConsume = false; return Task.FromResult(InventoryConsumePersistenceResult.Failed()); }

            var index = rows.FindIndex(r => r.SlotIndex == slotIndex);
            if (index < 0 || rows[index].Amount < amount) return Task.FromResult(InventoryConsumePersistenceResult.Failed());

            var newAmount = rows[index].Amount - amount;
            if (newAmount == 0)
            {
                rows.RemoveAt(index);
                // Renumber later rows down by one, matching CharServer's real behavior.
                for (var i = 0; i < rows.Count; i++)
                    if (rows[i].SlotIndex > slotIndex)
                        rows[i] = rows[i] with { SlotIndex = rows[i].SlotIndex - 1 };
                return Task.FromResult(new InventoryConsumePersistenceResult(true, 0, RowDeleted: true));
            }

            rows[index] = rows[index] with { Amount = newAmount };
            return Task.FromResult(new InventoryConsumePersistenceResult(true, newAmount, RowDeleted: false));
        }
    }

    // Live-captured bytes: A7 00 04 00 80 84 1E 00 D2.
    private static byte[] UseItemRequestPacket(ushort clientIndex, uint accountId, byte opaqueByte = 0xd2)
    {
        var packet = new byte[PacketConstants.IroCzUseItemLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroCzUseItem);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), accountId);
        packet[8] = opaqueByte;
        return packet;
    }

    private static async Task<byte[]> ReadExact(Stream stream, int length)
    {
        var buffer = new byte[length];
        await stream.ReadExactlyAsync(buffer);
        return buffer;
    }

    private static CharacterInventorySnapshot ThreeStarterRowsPlusFirstAidBox() => new(
    [
        new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0), // Knife, equipped
        new CharacterInventoryItem(1, 2301, 1, 0x000010, true, 0, 0, 0), // Cotton Shirt, equipped
        new CharacterInventoryItem(2, 23484, 1, 0, true, 0, 0, 0), // First Aid Box, unequipped - clientIndex 4
    ]);

    private async Task<(TcpClient Client, NetworkStream Stream, MapClientSession Session, Task RunTask, RecordingInventoryPersistence Persistence)> SetupAsync(
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
        var inventoryListPersistence = new RecordingInventoryListPersistence(initialInventory);
        var inventoryPersistence = new RecordingInventoryPersistence([.. initialInventory.Items]);

        var session = new MapClientSession(
            1, serverClient, new CharServerConnector(new MapConfigStore(new MapConfig(), "unused.conf")), true,
            gameplayStatePersistence: gameplayPersistence, inventoryListPersistence: inventoryListPersistence,
            inventoryPersistence: inventoryPersistence, accountId: AccountId, charId: CharId);
        var run = session.RunAsync(CancellationToken.None);
        await session.CompleteIroAuthenticationAsync(new(AccountId, CharId, 1, 2, 0, 0, false, "iz_int01", 18, 26, 0, 0, 0));

        // Consume the fixed 4-packet iRO bootstrap (0x0B18/0x0283/0x0ADE/0x02EB).
        await ReadExact(stream, 4 + 6 + 6 + 13);

        return (client, stream, session, run, inventoryPersistence);
    }

    private sealed class StubGameplayStatePersistence(CharacterGameplayState state) : ICharacterGameplayStatePersistence
    {
        public Task<CharacterGameplayState?> GetAsync(uint a, uint c, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(a == AccountId && c == CharId ? state : null);
        public Task<CharacterGameplayState?> UpdateAsync(uint a, CharacterGameplayState e, CharacterGameplayState u, CancellationToken t) => Task.FromResult<CharacterGameplayState?>(u);
    }

    [Fact]
    public async Task UseFirstAidBox_ConsumesOneRow_GrantsFiveItems_SendsCorrectAck()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId)); // clientIndex 4 -> slotIndex 2 -> First Aid Box

        // ZC_USE_ITEM_ACK2 (0x01C8, 15 bytes): index.W itemId.L accountId.L amount.W result.B.
        var ack = await ReadExact(stream, 15);
        Assert.Equal((short)0x01c8, BinaryPrimitives.ReadInt16LittleEndian(ack));
        Assert.Equal((ushort)4, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(2))); // ack uses the SAME client index
        Assert.Equal(23484u, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(4))); // First Aid Box ClientViewId
        Assert.Equal(AccountId, BinaryPrimitives.ReadUInt32LittleEndian(ack.AsSpan(8)));
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(ack.AsSpan(12))); // amount after use: row deleted
        Assert.Equal((byte)1, ack[14]); // success

        // Five 0x0B41 grant packets follow, one per getitem statement in pinned order.
        var expectedGrants = new (int ItemId, ushort Count)[] { (11518, 10), (11614, 20), (12325, 15), (22542, 1), (23485, 1) };
        foreach (var (itemId, count) in expectedGrants)
        {
            var pickup = await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);
            Assert.Equal((short)PacketConstants.ZcItemPickupAck, BinaryPrimitives.ReadInt16LittleEndian(pickup));
            Assert.Equal(count, BinaryPrimitives.ReadUInt16LittleEndian(pickup.AsSpan(4)));
            Assert.Equal((uint)itemId, BinaryPrimitives.ReadUInt32LittleEndian(pickup.AsSpan(6)));
        }

        Assert.Single(persistence.ConsumeCalls);
        Assert.Equal((2u, 1u), persistence.ConsumeCalls[0]);
        Assert.Equal(5, persistence.AddCalls.Count);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseFirstAidBox_RowDeleted_RuntimeSnapshotImmediatelyReflectsRemovalAndGrants()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId));
        await ReadExact(stream, 15); // ack
        for (var i = 0; i < 5; i++) await ReadExact(stream, PacketConstants.ZcItemPickupAckLength);

        // Requirement 11: the First Aid Box slot is gone; no fifth stale row.
        Assert.DoesNotContain(session.Inventory!.Items, i => i.ItemId == 23484);
        // Requirement: the five granted items are immediately present in the SAME session.
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 11518 && i.Amount == 10);
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 11614 && i.Amount == 20);
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 12325 && i.Amount == 15);
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 22542 && i.Amount == 1);
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 23485 && i.Amount == 1);

        // Requirement: existing equipped Knife/Cotton Shirt indices remain stable (they were
        // BEFORE the deleted slot, so WithoutSlot's renumbering never touches them).
        Assert.Equal(0u, session.Inventory.Items.Single(i => i.ItemId == 1201).SlotIndex);
        Assert.Equal(1u, session.Inventory.Items.Single(i => i.ItemId == 2301).SlotIndex);
        Assert.Equal(1201, session.Equipment!.RightHandItemId);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_WrongAccountId_RejectsWithoutMutation()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(4, accountId: AccountId + 1)); // client-claimed account does not match session

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b }); // ping probe
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        Assert.Empty(persistence.ConsumeCalls);
        Assert.Equal(3, session.Inventory!.Items.Count);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_InvalidSlot_RejectsWithoutMutation()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(99, AccountId)); // slotIndex 97 - no such row

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        Assert.Empty(persistence.ConsumeCalls);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_NonUsableItem_RejectsWithoutMutation()
    {
        // clientIndex 2 -> slotIndex 0 -> Knife (a weapon, not usable).
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        Assert.Empty(persistence.ConsumeCalls);
        Assert.Equal(1201, session.Equipment!.RightHandItemId); // Knife untouched.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_ItemWithNoModeledUseEffect_RejectsWithoutMutation()
    {
        // A Usable item with no Grants (no source-backed effect implemented) must be rejected,
        // not silently treated as a no-op success.
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 22542, 1, 0, true, 0, 0, 0)]); // Concentration Potion
        var (client, stream, session, run, persistence) = await SetupAsync(inventory);
        using var _ = client;

        await stream.WriteAsync(UseItemRequestPacket(2, AccountId));

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        Assert.Empty(persistence.ConsumeCalls);
        Assert.Single(session.Inventory!.Items);

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_ConsumePersistenceFailure_DoesNotMutateRuntimeState_SendsNoAck()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;
        persistence.FailNextConsume = true;

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId));

        await stream.WriteAsync(new byte[] { 0x1c, 0x0b });
        var next = await ReadExact(stream, 2);
        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next)); // No ack/grant packets were sent.

        Assert.Equal(3, session.Inventory!.Items.Count); // Runtime snapshot untouched.
        Assert.Contains(session.Inventory.Items, i => i.ItemId == 23484);
        Assert.Empty(persistence.AddCalls); // Grants never executed after a failed consume.

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task UseItem_FramingLeavesNextPacketCorrectlyFramed()
    {
        var (client, stream, session, run, persistence) = await SetupAsync(ThreeStarterRowsPlusFirstAidBox());
        using var _ = client;
        persistence.FailNextConsume = true; // Take the short-circuit path to isolate framing.

        await stream.WriteAsync(UseItemRequestPacket(4, AccountId));
        await stream.WriteAsync(new byte[] { 0x1c, 0x0b }); // ping - must be read as an independent, correctly-framed packet.
        var next = await ReadExact(stream, 2);

        Assert.Equal((short)PacketConstants.ZcPingLive, BinaryPrimitives.ReadInt16LittleEndian(next));

        client.Close();
        await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
