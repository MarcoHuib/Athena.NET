using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

public sealed record IroMapAuthPacket(uint AccountId, uint CharId, uint LoginId1)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out IroMapAuthPacket parsed)
    {
        parsed = default!;
        if (packet.Length != PacketConstants.IroCzMapAuthLength)
        {
            return false;
        }

        if (BinaryPrimitives.ReadInt16LittleEndian(packet[..2]) != PacketConstants.IroCzMapAuth)
        {
            return false;
        }

        parsed = new IroMapAuthPacket(
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(2, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(6, 4)),
            BinaryPrimitives.ReadUInt32LittleEndian(packet.Slice(10, 4)));
        return true;
    }
}
