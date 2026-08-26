using System.Buffers.Binary;

namespace Athena.Net.CharServer.Net;

// Response carries the persisted row's own authoritative DurableId (CharInventory.Id - the
// stable identity that never changes for this row's lifetime) plus its Equip/Identified/
// Refine/Favorite/Bound fields, so MapServer can construct the exact CharacterInventoryItem
// this add produced/updated without inventing or assuming any field value. IsNewRow tells
// MapServer whether this DurableId is brand-new (assign a fresh runtime slot - reusing a hole
// if one exists, or appending) or an existing stack (the row's CURRENT runtime slot, already
// tracked by MapServer via its own DurableId->SlotIndex mapping, must be reused unchanged).
// CharServer has no runtime-slot concept at all and never computes or returns one.
internal static class MapInventoryAddProtocol
{
    internal const int RequestLength = 18;
    // opcode.W(2) charId.L(4) itemId.l(4) newAmount.L(4) durableId.L(4) equip.L(4)
    // identified.B(1) refine.B(1) favorite.B(1) bound.B(1) isNewRow.B(1) success.B(1) = 28.
    internal const int ResponseLength = 28;

    internal static bool TryParseRequest(ReadOnlySpan<byte> packet, out InventoryAddRequest request)
    {
        request = default;
        if (packet.Length != RequestLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapInventoryAddRequest)
        {
            return false;
        }
        request = new InventoryAddRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]),
            BinaryPrimitives.ReadInt32LittleEndian(packet[10..]),
            BinaryPrimitives.ReadUInt32LittleEndian(packet[14..]));
        return true;
    }

    internal static byte[] BuildResponse(
        uint charId, int itemId, uint newAmount, uint durableId,
        uint equip, bool identified, byte refine, byte favorite, byte bound, bool isNewRow, bool success)
    {
        var packet = new byte[ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryAddResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), charId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6), itemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), newAmount);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(14), durableId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(18), equip);
        packet[22] = identified ? (byte)1 : (byte)0;
        packet[23] = refine;
        packet[24] = favorite;
        packet[25] = bound;
        packet[26] = isNewRow ? (byte)1 : (byte)0;
        packet[27] = success ? (byte)1 : (byte)0;
        return packet;
    }
}

internal readonly record struct InventoryAddRequest(uint AccountId, uint CharId, int ItemId, uint Amount);
