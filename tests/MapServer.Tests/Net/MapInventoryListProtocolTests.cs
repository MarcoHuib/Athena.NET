using System.Buffers.Binary;
using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

public sealed class MapInventoryListProtocolTests
{
    [Fact]
    public void BuildGetRequest_WritesOpcodeAccountAndCharId()
    {
        var packet = MapInventoryListProtocol.BuildGetRequest(7, 100);

        Assert.Equal(MapInventoryListProtocol.GetRequestLength, packet.Length);
        Assert.Equal(PacketConstants.MapInventoryListGetRequest, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(7U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(2)));
        Assert.Equal(100U, BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(6)));
    }

    [Fact]
    public void TryParseResponse_Success_WithItems_ReturnsSnapshot()
    {
        var inventory = new CharacterInventorySnapshot(
        [
            new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0),
            new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, 2301, 1, 0x000010, true, 0, 0, 0),
        ]);
        var packet = MapInventoryListProtocol.BuildResponse(0, 100, inventory);

        Assert.True(MapInventoryListProtocol.TryParseResponse(packet, out var result, out var charId, out var read));
        Assert.Equal((byte)0, result);
        Assert.Equal(100U, charId);
        Assert.True(read.Succeeded);
        Assert.Equal(2, read.Snapshot!.Items.Count);
        Assert.Equal(1201, read.Snapshot.Items[0].ItemId);
        Assert.Equal(0x000002u, read.Snapshot.Items[0].Equip);
        Assert.Equal(2301, read.Snapshot.Items[1].ItemId);
    }

    [Fact]
    public void TryParseResponse_FailureResult_ReturnsFailedRead()
    {
        var packet = MapInventoryListProtocol.BuildResponse(1, 100, null);

        Assert.True(MapInventoryListProtocol.TryParseResponse(packet, out var result, out _, out var read));
        Assert.Equal((byte)1, result);
        Assert.False(read.Succeeded);
        Assert.Null(read.Snapshot);
    }

    [Fact]
    public void TryParseResponse_EmptyInventory_ReturnsSuccessWithNoItems()
    {
        var packet = MapInventoryListProtocol.BuildResponse(0, 100, new CharacterInventorySnapshot([]));

        Assert.True(MapInventoryListProtocol.TryParseResponse(packet, out _, out _, out var read));
        Assert.True(read.Succeeded);
        Assert.Empty(read.Snapshot!.Items);
    }

    [Fact]
    public void TryParseResponse_DeclaredLengthMismatch_ReturnsFalse()
    {
        var packet = MapInventoryListProtocol.BuildResponse(0, 100, new CharacterInventorySnapshot([]));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)(packet.Length + 1));

        Assert.False(MapInventoryListProtocol.TryParseResponse(packet, out _, out _, out _));
    }

    [Fact]
    public void TryParseResponse_ItemCountMismatchWithPayload_ReturnsFalse()
    {
        var packet = MapInventoryListProtocol.BuildResponse(0, 100, new CharacterInventorySnapshot(
            [new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0)]));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9), 5); // claims 5 items but payload only has 1

        Assert.False(MapInventoryListProtocol.TryParseResponse(packet, out _, out _, out _));
    }

    [Fact]
    public void TryParseResponse_DuplicateDurableIds_ReturnsFalse()
    {
        var packet = MapInventoryListProtocol.BuildResponse(0, 100, new CharacterInventorySnapshot(
        [
            new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, true, 0, 0, 0),
            new CharacterInventoryItem(DurableId: 1, SlotIndex: 1, 6008, 1, 0, true, 0, 0, 0), // duplicate durableId 1
        ]));

        Assert.False(MapInventoryListProtocol.TryParseResponse(packet, out _, out _, out _));
    }

    [Fact]
    public void TryParseResponse_TooShort_ReturnsFalse()
    {
        Assert.False(MapInventoryListProtocol.TryParseResponse(new byte[MapInventoryListProtocol.ResponseHeaderLength - 1], out _, out _, out _));
    }
}
