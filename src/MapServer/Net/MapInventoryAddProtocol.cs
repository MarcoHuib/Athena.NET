using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Internal MapServer<->CharServer inventory-add protocol, mirroring
// MapQuestStateProtocol's shape (opcode, accountId, charId, payload,
// success flag). MapServer never touches CharInventory rows directly.
//
// The response carries the persisted row's own authoritative Equip/Identified/
// Refine/Favorite/Bound fields alongside newAmount/slotIndex - CharServer is the
// only side that knows these values (set at insert time, e.g. Identify=1, or
// already persisted on an existing stack row), so MapServer must never invent or
// assume them when reconstructing the authoritative CharacterInventoryItem this
// add produced/updated (see MapClientSession's reward-path runtime-snapshot
// update, which consumes exactly these fields).
internal static class MapInventoryAddProtocol
{
    internal const int RequestLength = 18;
    // opcode.W(2) charId.L(4) itemId.l(4) newAmount.L(4) slotIndex.L(4) equip.L(4)
    // identified.B(1) refine.B(1) favorite.B(1) bound.B(1) success.B(1) = 27.
    internal const int ResponseLength = 27;

    internal static byte[] BuildRequest(uint accountId, uint charId, int itemId, uint amount)
    {
        var packet = new byte[RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryAddRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), charId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(10), itemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(14), amount);
        return packet;
    }

    internal static bool TryParseResponse(
        ReadOnlySpan<byte> packet,
        out uint charId, out int itemId, out uint newAmount, out uint slotIndex,
        out uint equip, out bool identified, out byte refine, out byte favorite, out byte bound,
        out bool success)
    {
        charId = 0; itemId = 0; newAmount = 0; slotIndex = 0;
        equip = 0; identified = false; refine = 0; favorite = 0; bound = 0;
        success = false;
        if (packet.Length != ResponseLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapInventoryAddResponse)
        {
            return false;
        }
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]);
        itemId = BinaryPrimitives.ReadInt32LittleEndian(packet[6..]);
        newAmount = BinaryPrimitives.ReadUInt32LittleEndian(packet[10..]);
        slotIndex = BinaryPrimitives.ReadUInt32LittleEndian(packet[14..]);
        equip = BinaryPrimitives.ReadUInt32LittleEndian(packet[18..]);
        identified = packet[22] != 0;
        refine = packet[23];
        favorite = packet[24];
        bound = packet[25];
        success = packet[26] == 1;
        return true;
    }
}
