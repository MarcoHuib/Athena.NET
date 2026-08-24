using System.Buffers.Binary;
using System.Text;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

public static class IroWorldActorPackets
{
    private const int FixedLength = 84;

    public static byte[] BuildWorldActor(WorldActor actor)
    {
        var name = Encoding.ASCII.GetBytes(actor.Name);
        if (name.Length > PacketConstants.NameLength)
        {
            throw new ArgumentException("The actor name exceeds the 24-byte iRO field.", nameof(actor));
        }

        var packet = new byte[FixedLength + name.Length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNotifyStandEntry);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), (ushort)packet.Length);
        packet[4] = WorldActor.ObjectType;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(5), actor.ActorId);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(13), 300);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(19), actor.EffectState);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(23), actor.SpriteClass);
        WritePosition(packet.AsSpan(63, 3), actor.X, actor.Y, actor.Direction);
        packet[66] = actor.RadiusX;
        packet[67] = actor.RadiusY;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(73), uint.MaxValue);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(77), uint.MaxValue);
        name.CopyTo(packet.AsSpan(FixedLength));
        return packet;
    }

    public static byte[] BuildWarpActor(WarpActor actor) => BuildWorldActor(actor);

    public static byte[] BuildNpcName(uint actorId, string name)
    {
        var encoded = Encoding.ASCII.GetBytes(name);
        var packet = new byte[58];
        BinaryPrimitives.WriteUInt16LittleEndian(packet, 0x0adf);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId);
        encoded.AsSpan(0, Math.Min(encoded.Length, PacketConstants.NameLength - 1)).CopyTo(packet.AsSpan(10, PacketConstants.NameLength));
        return packet;
    }

    private static void WritePosition(Span<byte> buffer, ushort x, ushort y, byte direction)
    {
        buffer[0] = (byte)(x >> 2);
        buffer[1] = (byte)((x << 6) | ((y >> 4) & 0x3f));
        buffer[2] = (byte)((y << 4) | (direction & 0x0f));
    }
}
