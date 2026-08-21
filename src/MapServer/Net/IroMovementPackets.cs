using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

public readonly record struct IroMovementRequest(ushort TargetX, ushort TargetY, byte OpaqueExtra);

public static class IroMovementPackets
{
    public static bool TryParseRequest(ReadOnlySpan<byte> packet, out IroMovementRequest request)
    {
        request = default;
        if (packet.Length != PacketConstants.IroCzRequestMoveLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.IroCzRequestMove)
        {
            return false;
        }

        var targetX = (ushort)((packet[2] << 2) | (packet[3] >> 6));
        var targetY = (ushort)(((packet[3] & 0x3f) << 4) | (packet[4] >> 4));
        request = new IroMovementRequest(targetX, targetY, packet[5]);
        return true;
    }

    public static byte[] BuildResponse(
        uint tick,
        ushort fromX,
        ushort fromY,
        ushort targetX,
        ushort targetY)
    {
        var packet = new byte[12];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNotifyPlayerMove);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2, 4), tick);
        WriteMovement(packet.AsSpan(6, 6), fromX, fromY, targetX, targetY);
        return packet;
    }

    private static void WriteMovement(
        Span<byte> destination,
        ushort fromX,
        ushort fromY,
        ushort targetX,
        ushort targetY)
    {
        destination[0] = (byte)(fromX >> 2);
        destination[1] = (byte)((fromX << 6) | ((fromY >> 4) & 0x3f));
        destination[2] = (byte)((fromY << 4) | ((targetX >> 6) & 0x0f));
        destination[3] = (byte)((targetX << 2) | ((targetY >> 8) & 0x03));
        destination[4] = (byte)targetY;
        destination[5] = 0x88;
    }
}
