using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Variable-length: opcode.W length.W result.B charId.L itemCount.W [item x itemCount].
// Framed via CharServerConnector.VariableLengthMinLength - `length` is the TOTAL packet
// length, matching pinned rAthena's own variable-length packet convention.
internal static class MapInventoryListProtocol
{
    internal const int GetRequestLength = 10;
    internal const int ResponseHeaderLength = 11;
    // durableId.L(4) itemId.L(4) amount.L(4) equip.L(4) identified.B(1) refine.B(1)
    // favorite.B(1) bound.B(1) = 20. CharServer's rows carry NO runtime SlotIndex at all -
    // MapServer assigns the initial dense slot mapping from list ORDER via
    // CharacterInventorySnapshot.FromLogin, never from a wire field.
    internal const int ItemLength = 20;

    private static void WriteRow(Span<byte> span, uint durableId, int itemId, uint amount, uint equip, bool identified, byte refine, byte favorite, byte bound)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(span, durableId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[4..], (uint)itemId);
        BinaryPrimitives.WriteUInt32LittleEndian(span[8..], amount);
        BinaryPrimitives.WriteUInt32LittleEndian(span[12..], equip);
        span[16] = identified ? (byte)1 : (byte)0;
        span[17] = refine;
        span[18] = favorite;
        span[19] = bound;
    }

    private static (uint DurableId, int ItemId, uint Amount, uint Equip, bool Identified, byte Refine, byte Favorite, byte Bound) ReadRow(ReadOnlySpan<byte> span) => (
        DurableId: BinaryPrimitives.ReadUInt32LittleEndian(span),
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

    // Returns false (malformed/truncated packet) if the declared length doesn't match the
    // actual packet size, or itemCount doesn't agree with the payload length actually present.
    // Rows may not declare a duplicate DurableId - a real load pass never produces duplicates
    // (each row's DurableId is a unique primary key) - treated as a data/protocol invariant
    // violation, not a case to silently resolve.
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

        var rows = new (uint DurableId, int ItemId, uint Amount, uint Equip, bool Identified, byte Refine, byte Favorite, byte Bound)[itemCount];
        var seenIds = new HashSet<uint>();
        for (var i = 0; i < itemCount; i++)
        {
            var row = ReadRow(packet.AsSpan(ResponseHeaderLength + i * ItemLength, ItemLength));
            if (!seenIds.Add(row.DurableId)) return false;
            rows[i] = row;
        }

        inventory = CharacterInventoryReadResult.Success(CharacterInventorySnapshot.FromLogin(rows));
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
            {
                var item = inventory.Items[i];
                WriteRow(packet.AsSpan(ResponseHeaderLength + i * ItemLength, ItemLength), item.DurableId, item.ItemId, item.Amount, item.Equip, item.Identified, item.Refine, item.Favorite, item.Bound);
            }
        }
        return packet;
    }
}
