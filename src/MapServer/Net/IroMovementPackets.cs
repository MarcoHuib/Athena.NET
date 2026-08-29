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
        IroCoordinatePacking.WriteMovement(packet.AsSpan(6, 6), fromX, fromY, targetX, targetY);
        return packet;
    }
}
