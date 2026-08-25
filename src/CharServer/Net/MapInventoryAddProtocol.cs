using System.Buffers.Binary;

namespace Athena.Net.CharServer.Net;

internal static class MapInventoryAddProtocol
{
    internal const int RequestLength = 18;
    internal const int ResponseLength = 19;

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

    internal static byte[] BuildResponse(uint charId, int itemId, uint newAmount, uint slotIndex, bool success)
    {
        var packet = new byte[ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryAddResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), charId);
        BinaryPrimitives.WriteInt32LittleEndian(packet.AsSpan(6), itemId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), newAmount);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(14), slotIndex);
        packet[18] = success ? (byte)1 : (byte)0;
        return packet;
    }
}

internal readonly record struct InventoryAddRequest(uint AccountId, uint CharId, int ItemId, uint Amount);
