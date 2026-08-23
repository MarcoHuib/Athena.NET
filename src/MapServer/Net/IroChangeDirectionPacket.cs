using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

public readonly record struct IroChangeDirectionPacket(byte HeadDirection, byte BodyDirection, byte OpaqueTrailingByte)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out IroChangeDirectionPacket value)
    {
        value = default;
        if (packet.Length != PacketConstants.IroCzChangeDirectionLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.IroCzChangeDirection)
        {
            return false;
        }

        // Pinned rAthena names offsets 2 and 4 head direction and body direction.
        // Offset 3 is padding in every captured sample; offset 5 is iRO-opaque.
        value = new(packet[2], packet[4], packet[5]);
        return true;
    }
}
