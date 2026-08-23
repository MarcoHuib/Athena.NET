using System.Buffers.Binary;

namespace Athena.Net.CharServer.Net;

internal static class MapQuestStateProtocol
{
    internal const int RequestLength = 15;
    internal const int ResponseLength = 12;

    internal static bool TryParseRequest(ReadOnlySpan<byte> packet, out QuestStateRequest request)
    {
        request = default;
        if (packet.Length != RequestLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.MapQuestStateRequest)
        {
            return false;
        }

        request = new QuestStateRequest(
            BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]),
            BinaryPrimitives.ReadUInt32LittleEndian(packet[6..]),
            BinaryPrimitives.ReadUInt32LittleEndian(packet[10..]),
            packet[14]);
        return true;
    }

    internal static byte[] BuildResponse(uint charId, uint questId, byte state, bool success)
    {
        var packet = new byte[ResponseLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.MapQuestStateResponse);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), charId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), questId);
        packet[10] = state;
        packet[11] = success ? (byte)1 : (byte)0;
        return packet;
    }
}

internal readonly record struct QuestStateRequest(
    uint AccountId,
    uint CharId,
    uint QuestId,
    byte Operation);
