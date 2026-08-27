using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Pinned rAthena CZ_REQ_WEAR_EQUIP_V5 shape (packetType.W index.W position.L) plus one
// trailing opaque byte verified present on the current stock-iRO wire (see
// PacketConstants.IroCzReqWearEquipLength doc comment - capture frames 388/449). `index` is
// the CLIENT inventory index (client_index() = server slot + 2, clif.cpp:122-124) - never an
// item id; resolved through the same server-slot mapping as the inventory-list packets.
// OpaqueTrailingByte is consumed (required for correct framing of the NEXT packet) but not
// interpreted - its semantics are unverified.
public readonly record struct IroEquipRequestPacket(ushort ClientIndex, uint Position, byte OpaqueTrailingByte)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out IroEquipRequestPacket value)
    {
        value = default;
        if (packet.Length != PacketConstants.IroCzReqWearEquipLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.IroCzReqWearEquip)
        {
            return false;
        }

        value = new(BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]), BinaryPrimitives.ReadUInt32LittleEndian(packet[4..]), packet[8]);
        return true;
    }
}

// Pinned rAthena CZ_REQ_TAKEOFF_EQUIP shape (packetType.W index.W, clif_packetdb.hpp:59) plus
// one trailing opaque byte verified present on the current stock-iRO wire (see
// PacketConstants.IroCzReqTakeoffEquipLength doc comment - capture frames 370/395). Same
// client-index semantics as the equip request. OpaqueTrailingByte is consumed but not
// interpreted.
public readonly record struct IroUnequipRequestPacket(ushort ClientIndex, byte OpaqueTrailingByte)
{
    public static bool TryParse(ReadOnlySpan<byte> packet, out IroUnequipRequestPacket value)
    {
        value = default;
        if (packet.Length != PacketConstants.IroCzReqTakeoffEquipLength ||
            BinaryPrimitives.ReadInt16LittleEndian(packet) != PacketConstants.IroCzReqTakeoffEquip)
        {
            return false;
        }

        value = new(BinaryPrimitives.ReadUInt16LittleEndian(packet[2..]), packet[4]);
        return true;
    }
}

internal static class IroEquipmentMutationPackets
{
    // ZC_ACK_WEAR_EQUIP_V5 (0x0999, packets_struct.hpp:1268-1274, PACKETVER_RE_NUM >= 20121107
    // gate, pinned build satisfies this): PacketType.W index.W wearLocation.L
    // wItemSpriteNumber.W result.B = 11 bytes. result is NOT inverted for this ack (only the
    // unequip ack is) - PacketConstants.EquipAckResultOk/FailLevel/Fail (0/1/2).
    // wItemSpriteNumber is left 0: pinned clif_equipitemack only sets it when the equipped
    // item's EquipLocation has EQP_VISIBLE (helm/garment/costume, pc.hpp:1143-1145), which the
    // Knife/armor generated so far never occupy.
    internal static byte[] BuildEquipAck(ushort clientIndex, uint wearLocation, byte result)
    {
        var packet = new byte[PacketConstants.IroZcReqWearEquipAckLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroZcReqWearEquipAck);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), wearLocation);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(8), 0); // wItemSpriteNumber
        packet[10] = result;
        return packet;
    }

    // ZC_ACK_TAKEOFF_EQUIP_V5 (0x099A, packets.hpp:1006-1013, PACKETVER >= 20130000 gate,
    // pinned build satisfies this): packetType.W index.W wearLocation.L flag.B = 9 bytes.
    // flag IS inverted for this ack (clif_unequipitemack, clif.cpp:4339-4341, `success =
    // !success` for PACKETVER >= 20110824): PacketConstants.UnequipAckFlagSuccess/Failure (0/1).
    internal static byte[] BuildUnequipAck(ushort clientIndex, uint wearLocation, bool success)
    {
        var packet = new byte[PacketConstants.IroZcReqTakeoffEquipAckLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.IroZcReqTakeoffEquipAck);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(4), wearLocation);
        packet[8] = success ? PacketConstants.UnequipAckFlagSuccess : PacketConstants.UnequipAckFlagFailure;
        return packet;
    }
}
