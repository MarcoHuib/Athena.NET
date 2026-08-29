using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// 0x00BC ZC_STATUS_CHANGE_ACK - verified stock-iRO base-stat-allocation success response
// (statsonly.pcapng, ai/iro-2026-wire.md): opcode.W(2) statusId.W(2) result.B(1) newValue.B(1)
// = 6 bytes. Observed success Result=1 across all six captured upgrades, e.g. BC 00 0D 00 01 03
// (STR ack, new value 3).
//
// Pure serializer over already-resolved values, per the same split IroSkillLevelUpdatePackets
// uses - never queries CharacterStatService, GeneratedProgressionRegistry, the database, or any
// session/service. The caller is responsible for supplying the POST-COMMIT stat value (never a
// client-supplied or pre-mutation value) BEFORE calling this.
//
// Failure-response behavior (a non-1 Result, or any rejection-path packet at all) is explicitly
// NOT captured - see ai/iro-2026-wire.md's open item. This type therefore only builds the
// verified success shape; there is deliberately no "failure" overload here to guess at.
internal static class IroStatusUpAckPacket
{
    private const byte ResultSuccess = 1;

    internal static byte[] BuildSuccess(ushort statusId, byte newValue)
    {
        var packet = new byte[PacketConstants.ZcStatusUpAckLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcStatusUpAck);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), statusId);
        packet[4] = ResultSuccess;
        packet[5] = newValue;
        return packet;
    }
}
