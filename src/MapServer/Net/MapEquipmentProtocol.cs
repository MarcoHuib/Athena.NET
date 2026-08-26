using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

internal static class MapEquipmentProtocol
{
    internal const int GetRequestLength = 10;
    internal const int ResponseLength = 13;

    internal static byte[] BuildGetRequest(uint accountId, uint characterId)
    {
        var packet = new byte[GetRequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapEquipmentGetRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), characterId);
        return packet;
    }

    internal static bool TryParseResponse(byte[] packet, out byte result, out uint charId, out CharacterEquipmentSnapshot? equipment)
    {
        result = 1;
        charId = 0;
        equipment = null;
        if (packet.Length != ResponseLength) return false;

        result = packet[2];
        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(3));
        if (result == 0)
        {
            var hasRightHand = packet[7] != 0;
            var rightHandItemId = BinaryPrimitives.ReadUInt32LittleEndian(packet.AsSpan(8));
            var rightHandRefine = packet[12];
            equipment = new CharacterEquipmentSnapshot(hasRightHand ? (int)rightHandItemId : null, rightHandRefine);
        }
        return true;
    }
}
