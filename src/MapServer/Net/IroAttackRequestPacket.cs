using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Verified capture: kill-poring-heal-jobup.pcapng frame 614, 0x0437/8:
// 37 04 9D 1E 00 00 07 7F -> id.W targetActorId.L actionType.B opaqueByte.B.
// clif_parse_ActionRequest (clif.cpp:11818), pinned generic length 7
// (clif_packetdb.hpp:1149/1222); iRO adds one opaque trailing byte matching
// the established pattern (0x0360/0x0368/0x0361/0x0090 etc). actionType=7
// matches pinned e_damage_type::DMG_REPEAT (clif.hpp:699), "continuous attack".
public readonly record struct IroAttackRequestPacket(uint TargetActorId, byte ActionType, byte OpaqueTrailingByte)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out IroAttackRequestPacket value)
    {
        value = default;
        if (packet.Length != PacketConstants.IroCzAttackRequestLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.IroCzAttackRequest)
        {
            return false;
        }

        value = new(BinaryPrimitives.ReadUInt32LittleEndian(packet[2..]), packet[6], packet[7]);
        return true;
    }
}
