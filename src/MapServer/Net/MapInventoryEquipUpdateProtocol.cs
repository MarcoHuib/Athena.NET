using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

internal static class MapInventoryEquipUpdateProtocol
{
    internal const int RequestLength = 18;
    internal const int ResponseLength = 11;

    internal static byte[] BuildRequest(uint accountId, uint characterId, uint slotIndex, uint equip)
    {
        var packet = new byte[RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapInventoryEquipUpdateRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), characterId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), slotIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(14), equip);
        return packet;
    }

    internal static bool TryParseResponse(byte[] packet, out bool success, out uint charId, out uint slotIndex)
    {
        success = false;
        charId = 0;
        slotIndex = 0;
        if (packet.Length != ResponseLength) return false;
        success = packet[2] == 0;
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(3));
        slotIndex = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(7));
        return true;
    }
}
