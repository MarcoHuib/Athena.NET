using System.Buffers.Binary;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

internal static class MapQuestStateProtocol
{
    internal const int RequestLength = 15;
    internal const int ResponseLength = 12;

    internal static byte[] BuildRequest(
        uint accountId,
        uint charId,
        uint questId,
        CharacterQuestStatus operation)
    {
        var packet = new byte[RequestLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapQuestStateRequest);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), accountId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), charId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), questId);
        packet[14] = (byte)operation;
        return packet;
    }

    internal static bool TryParseResponse(
        ReadOnlySpan<byte> packet,
        out uint charId,
        out uint questId,
        out CharacterQuestStatus? state)
    {
        charId = 0;
        questId = 0;
        state = null;
        if (packet.Length != ResponseLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapQuestStateResponse)
        {
            return false;
        }

        charId = BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]);
        questId = BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]);
        if (packet[11] == 1 && packet[10] <= (byte)CharacterQuestStatus.Completed)
        {
            state = (CharacterQuestStatus)packet[10];
        }
        return true;
    }
}
