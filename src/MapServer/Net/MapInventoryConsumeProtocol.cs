using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Internal MapServer<->CharServer inventory-consume protocol. MapServer never touches
// CharInventory rows directly - CharServer resolves the target row by its own real primary key
// (DurableId = CharInventory.Id), never a runtime/session-local slot.
internal static class MapInventoryConsumeProtocol
{
    internal const int RequestLength = 18;
    internal const int ResponseLength = 16;

    internal static byte[] BuildRequest(uint accountId, uint charId, uint durableId, uint amount)
    {
        var packet = new byte[RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryConsumeRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), charId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), durableId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(14), amount);
        return packet;
    }

    internal static bool TryParseResponse(
        ReadOnlySpan<byte> packet, out bool success, out uint charId, out uint durableId, out uint newAmount, out bool rowDeleted)
    {
        success = false; charId = 0; durableId = 0; newAmount = 0; rowDeleted = false;
        if (packet.Length != ResponseLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapInventoryConsumeResponse)
        {
            return false;
        }
        success = packet[2] == 1;
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[3..]);
        durableId = BinaryPrimitives.ReadUInt32LittleEndian(packet[7..]);
        newAmount = BinaryPrimitives.ReadUInt32LittleEndian(packet[11..]);
        rowDeleted = packet[15] == 1;
        return true;
    }
}
