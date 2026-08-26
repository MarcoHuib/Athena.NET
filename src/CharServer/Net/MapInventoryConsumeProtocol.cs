using System.Buffers.Binary;

namespace Athena.Net.CharServer.Net;

// Consumes `amount` from the row at the given authoritative SlotIndex (pinned pc_delitem,
// pc.cpp:6103-6128) - CharServer resolves the row from SlotIndex via the SAME
// CharInventoryOrdering.InStableSlotOrder ordering the list/add/equip-update handlers already
// share, never a client-supplied item id.
internal static class MapInventoryConsumeProtocol
{
    internal const int RequestLength = 18;
    // opcode.W(2) success.B(1) charId.L(4) slotIndex.L(4) newAmount.L(4) rowDeleted.B(1) = 16.
    internal const int ResponseLength = 16;

    internal static bool TryParseRequest(ReadOnlySpan<byte> packet, out uint accountId, out uint charId, out uint slotIndex, out uint amount)
    {
        accountId = 0; charId = 0; slotIndex = 0; amount = 0;
        if (packet.Length != RequestLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapInventoryConsumeRequest)
        {
            return false;
        }
        accountId = BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]);
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]);
        slotIndex = BinaryPrimitives.ReadUInt32LittleEndian(packet[10..]);
        amount = BinaryPrimitives.ReadUInt32LittleEndian(packet[14..]);
        return true;
    }

    // rowDeleted mirrors pinned pc_delitem's own row-zeroing branch (pc.cpp:6114-6119:
    // `if (amount <= 0) { memset(...); }`) - true means the row's amount reached zero and no
    // longer exists as authoritative inventory data; MapServer must remove it from its own
    // runtime CharacterInventorySnapshot rather than leaving a zero-amount row behind.
    internal static byte[] BuildResponse(bool success, uint charId, uint slotIndex, uint newAmount, bool rowDeleted)
    {
        var packet = new byte[ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryConsumeResponse);
        packet[2] = success ? (byte)1 : (byte)0;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(3), charId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7), slotIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11), newAmount);
        packet[15] = rowDeleted ? (byte)1 : (byte)0;
        return packet;
    }
}
