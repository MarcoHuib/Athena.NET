using System.Buffers.Binary;

namespace Athena.Net.CharServer.Net;

// Response carries the persisted row's own authoritative Equip/Identified/Refine/
// Favorite/Bound fields (in addition to newAmount/slotIndex) so MapServer can
// construct the exact CharacterInventoryItem this add produced/updated without
// inventing or assuming any field value - CharServer is the only side that knows
// them (see MapServerSession.HandleInventoryAddRequestAsync, which reads them
// straight off the same row it just persisted).
internal static class MapInventoryAddProtocol
{
    internal const int RequestLength = 18;
    // opcode.W(2) charId.L(4) itemId.l(4) newAmount.L(4) slotIndex.L(4) equip.L(4)
    // identified.B(1) refine.B(1) favorite.B(1) bound.B(1) success.B(1) = 27.
    internal const int ResponseLength = 27;

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
        uint charId, int itemId, uint newAmount, uint slotIndex,
        uint equip, bool identified, byte refine, byte favorite, byte bound, bool success)
    {
        var packet = new byte[ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryAddResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), charId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6), itemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), newAmount);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(14), slotIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(18), equip);
        packet[22] = identified ? (byte)1 : (byte)0;
        packet[23] = refine;
        packet[24] = favorite;
        packet[25] = bound;
        packet[26] = success ? (byte)1 : (byte)0;
        return packet;
    }
}

internal readonly record struct InventoryAddRequest(uint AccountId, uint CharId, int ItemId, uint Amount);
