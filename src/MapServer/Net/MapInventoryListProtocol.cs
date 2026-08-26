using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Variable-length: opcode.W length.W result.B charId.L itemCount.W [item x itemCount].
// Framed via CharServerConnector.VariableLengthMinLength - `length` is the TOTAL packet
// length, matching pinned rAthena's own variable-length packet convention.
internal static class MapInventoryListProtocol
{
    internal const int GetRequestLength = 10;
    internal const int ResponseHeaderLength = 11;
    internal const int ItemLength = 20;

    // slotIndex.L itemId.L amount.L equip.L identified.B refine.B favorite.B bound.B
    private static void WriteItem(Span<byte> span, CharacterInventoryItem item)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span, item.SlotIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)item.ItemId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], item.Amount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], item.Equip);
        span[16] = item.Identified ? (byte)1 : (byte)0;
        span[17] = item.Refine;
        span[18] = item.Favorite;
        span[19] = item.Bound;
    }

    private static CharacterInventoryItem ReadItem(ReadOnlySpan<byte> span) => new(
        SlotIndex: BinaryPrimitives.ReadUInt32LittleEndian(span),
        ItemId: (int)BinaryPrimitives.ReadUInt32LittleEndian(span[4..]),
        Amount: BinaryPrimitives.ReadUInt32LittleEndian(span[8..]),
        Equip: BinaryPrimitives.ReadUInt32LittleEndian(span[12..]),
        Identified: span[16] != 0,
        Refine: span[17],
        Favorite: span[18],
        Bound: span[19]);

    internal static byte[] BuildGetRequest(uint accountId, uint characterId)
    {
        var packet = new byte[GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryListGetRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), characterId);
        return packet;
    }

    // Returns false (malformed/truncated packet) if: the declared length doesn't match the
    // actual packet size, itemCount doesn't agree with the payload length actually present, or
    // any two items declare the same SlotIndex (a real load pass never produces duplicates -
    // treated as a data/protocol invariant violation, not a case to silently resolve).
    internal static bool TryParseResponse(byte[] packet, out byte result, out uint charId, out CharacterInventoryReadResult inventory)
    {
        result = 1;
        charId = 0;
        inventory = CharacterInventoryReadResult.Failed();
        if (packet.Length < ResponseHeaderLength) return false;

        var declaredLength = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(2));
        if (declaredLength != packet.Length) return false;

        result = packet[4];
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(5));
        if (result != 0) return true;

        var itemCount = BinaryPrimitives.ReadUInt16LittleEndian(packet.AsSpan(9));
        var expectedLength = ResponseHeaderLength + itemCount * ItemLength;
        if (expectedLength != packet.Length) return false;

        var items = new CharacterInventoryItem[itemCount];
        var seenSlots = new HashSet<uint>();
        for (var i = 0; i < itemCount; i++)
        {
            var item = ReadItem(packet.AsSpan(ResponseHeaderLength + i * ItemLength, ItemLength));
            if (!seenSlots.Add(item.SlotIndex)) return false;
            items[i] = item;
        }

        inventory = CharacterInventoryReadResult.Success(new CharacterInventorySnapshot(items));
        return true;
    }

    // Test-only: MapServer never sends this response in production (CharServer does - see
    // Athena.Net.CharServer.Net.MapInventoryListProtocol.BuildResponse), but mirroring the
    // write logic here lets tests build fixtures without duplicating byte-packing across two
    // protocol files.
    internal static byte[] BuildResponse(byte result, uint charId, CharacterInventorySnapshot? inventory)
    {
        var itemCount = inventory?.Items.Count ?? 0;
        var length = ResponseHeaderLength + itemCount * ItemLength;
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryListGetResponse);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)length);
        packet[4] = result;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), charId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(9), (ushort)itemCount);
        if (inventory is not null)
        {
            for (var i = 0; i < inventory.Items.Count; i++)
                WriteItem(packet.AsSpan(ResponseHeaderLength + i * ItemLength, ItemLength), inventory.Items[i]);
        }
        return packet;
    }
}
