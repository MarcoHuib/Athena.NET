using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

public static class IroWorldActorPackets
{
    private const int FixedLength = 84;

    public static byte[] BuildWarpActor(WarpActor actor)
    {
        var name = Encoding.ASCII.GetBytes(actor.Name);
        if (name.Length > PacketConstants.NameLength)
        {
            throw new ArgumentException("The actor name exceeds the 24-byte iRO field.", nameof(actor));
        }

        var packet = new byte[FixedLength + name.Length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNotifyStandEntry);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[4] = WarpActor.ObjectType;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), actor.ActorId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(13), 300);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(23), actor.SpriteClass);
        WritePosition(packet.AsSpan(63, 3), actor.X, actor.Y, 0);
        packet[66] = actor.RadiusX;
        packet[67] = actor.RadiusY;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(73), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(77), uint.MaxValue);
        name.CopyTo(packet.AsSpan(FixedLength));
        return packet;
    }

    private static void WritePosition(Span<byte> buffer, ushort x, ushort y, byte direction)
    {
        buffer[0] = (byte)(x >> 2);
        buffer[1] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        buffer[2] = (byte)((y << 4) | (direction & 0x0f));
    }
}
