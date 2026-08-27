using System.Buffers.Binary;
using Athena.Net.CharServer.Net;

namespace Athena.Net.CharServer.Tests.Net;

public sealed class MapInventoryListProtocolTests
{
    [Fact]
    public void TryParseGet_ValidPacket_ReadsAccountAndCharId()
    {
        var packet = new byte[MapInventoryListProtocol.GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryListGetRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), 100);

        Assert.True(MapInventoryListProtocol.TryParseGet(packet, out var accountId, out var charId));
        Assert.Equal(7U, accountId);
        Assert.Equal(100U, charId);
    }

    [Fact]
    public void TryParseGet_WrongOpcodeOrLength_ReturnsFalse()
    {
        var wrongOpcode = new byte[MapInventoryListProtocol.GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(wrongOpcode, PacketConstants.MapGameplayStateGetRequest);
        Assert.False(MapInventoryListProtocol.TryParseGet(wrongOpcode, out _, out _));

        var wrongLength = new byte[MapInventoryListProtocol.GetRequestLength - 1];
        Assert.False(MapInventoryListProtocol.TryParseGet(wrongLength, out _, out _));
    }

    [Fact]
    public void BuildResponse_MultipleRows_WritesLengthPrefixAndAllItems()
    {
        var rows = new List<CharacterInventoryRowDto>
        {
            new(DurableId: 1, ItemId: 1201, Amount: 1, Equip: 0x000002, Identified: true, Refine: 0, Favorite: 0, Bound: 0), // equipped Knife
            new(DurableId: 2, ItemId: 6008, Amount: 5, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0),        // stackable Wood
        };
        var response = MapInventoryListProtocol.BuildResponse(0, 100, rows);

        Assert.Equal(MapInventoryListProtocol.ResponseHeaderLength + 2 * MapInventoryListProtocol.ItemLength, response.Length);
        Assert.Equal(PacketConstants.MapInventoryListGetResponse, BinaryPrimitives.ReadInt16LittleEndian(response));
        Assert.Equal((ushort)response.Length, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(2)));
        Assert.Equal((byte)0, response[4]);
        Assert.Equal(100U, BinaryPrimitives.ReadUInt32LittleEndian(response.AsSpan(5)));
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(9)));

        var firstItem = response.AsSpan(MapInventoryListProtocol.ResponseHeaderLength, MapInventoryListProtocol.ItemLength);
        Assert.Equal(1U, BinaryPrimitives.ReadUInt32LittleEndian(firstItem)); // durableId 1
        Assert.Equal(1201U, BinaryPrimitives.ReadUInt32LittleEndian(firstItem[4..]));
        Assert.Equal(0x000002U, BinaryPrimitives.ReadUInt32LittleEndian(firstItem[12..]));

        var secondItem = response.AsSpan(MapInventoryListProtocol.ResponseHeaderLength + MapInventoryListProtocol.ItemLength, MapInventoryListProtocol.ItemLength);
        Assert.Equal(2U, BinaryPrimitives.ReadUInt32LittleEndian(secondItem)); // durableId 2
        Assert.Equal(6008U, BinaryPrimitives.ReadUInt32LittleEndian(secondItem[4..]));
    }

    [Fact]
    public void BuildResponse_FailureResult_EmitsHeaderOnlyNoItems()
    {
        var response = MapInventoryListProtocol.BuildResponse(1, 100, null);

        Assert.Equal(MapInventoryListProtocol.ResponseHeaderLength, response.Length);
        Assert.Equal((byte)1, response[4]);
        Assert.Equal((ushort)0, BinaryPrimitives.ReadUInt16LittleEndian(response.AsSpan(9)));
    }

    [Fact]
    public void BuildResponse_EmptyInventory_EmitsHeaderOnlyNoItems()
    {
        var response = MapInventoryListProtocol.BuildResponse(0, 100, new List<CharacterInventoryRowDto>());

        Assert.Equal(MapInventoryListProtocol.ResponseHeaderLength, response.Length);
        Assert.Equal((byte)0, response[4]);
    }
}
