using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

public static class IroQuestPackets
{
    public const int AddQuestLength = 155;
    public static byte[] BuildAddActive(uint questId)
    {
        if (questId == 0) throw new ArgumentOutOfRangeException(nameof(questId));
        var packet = new byte[AddQuestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x0b0c);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), questId);
        packet[6] = 1;
        return packet;
    }
    public static byte[] BuildRemove(uint questId)
    {
        if (questId == 0) throw new ArgumentOutOfRangeException(nameof(questId));
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, 0x02b4);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), questId);
        return packet;
    }
}
