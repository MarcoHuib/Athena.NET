using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Internal MapServer<->CharServer inventory-add protocol, mirroring
// MapQuestStateProtocol's shape (opcode, accountId, charId, payload,
// success flag). MapServer never touches CharInventory rows directly.
internal static class MapInventoryAddProtocol
{
    internal const int RequestLength = 18;
    internal const int ResponseLength = 15;

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

    internal static bool TryParseResponse(ReadOnlySpan<byte> packet, out uint charId, out int itemId, out uint newAmount, out bool success)
    {
        charId = 0; itemId = 0; newAmount = 0; success = false;
        if (packet.Length != ResponseLength || BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapInventoryAddResponse)
        {
            return false;
        }
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]);
        itemId = BinaryPrimitives.ReadInt32LittleEndian(packet[6..]);
        newAmount = BinaryPrimitives.ReadUInt32LittleEndian(packet[10..]);
        success = packet[14] == 1;
        return true;
    }
}
