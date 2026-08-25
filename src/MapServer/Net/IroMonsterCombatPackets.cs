using System.Buffers.Binary;

namespace Athena.Net.MapServer.Net;

// Client-facing combat-result serializers for the Poring live-wire slice.
// Pure representation of already-authoritative MonsterCombatCoordinator
// results - no damage calculation happens here. Verified against
// kill-poring-heal-jobup.pcapng (see ai/iro-2026-wire.md).
internal static class IroMonsterCombatPackets
{
    // ZC_NOTIFY_ACT3 (0x08C8), verified capture frames 620/659, exact 34-byte match:
    // srcId.L dstId.L tick.L srcSpeed.L dstSpeed.L damage.L isSpDamage.B div.W type.B damage2.L
    // (clif.cpp:5220).
    internal static byte[] BuildNotifyAct3(uint srcActorId, uint dstActorId, uint tick, uint srcSpeed, uint dstSpeed, uint damage, byte div, byte actionType)
    {
        var packet = new byte[PacketConstants.ZcNotifyAct3Length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNotifyAct3);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), srcActorId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), dstActorId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(10), tick);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(14), srcSpeed);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(18), dstSpeed);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(22), damage);
        packet[26] = 0; // isSpDamage
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(27), div);
        packet[29] = actionType;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(30), 0); // damage2
        return packet;
    }

    // ZC_NOTIFY_VANISH (0x0080), verified capture frame 694, exact 7-byte match:
    // id.L type.B (clif.cpp:945). type=1 is explicitly "died" per pinned source comment.
    internal static byte[] BuildNotifyVanish(uint actorId, byte reason)
    {
        var packet = new byte[PacketConstants.ZcNotifyVanishLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcNotifyVanish);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId);
        packet[6] = reason;
        return packet;
    }

    // ZC_ITEM_PICKUP_ACK (0x0B41), verified capture frame 699, exact 70-byte match to
    // pinned PACKET_ZC_ITEM_PICKUP_ACK (packets_struct.hpp:540) under the pinned RE
    // PACKETVER branch. All fields beyond index/count/nameid/identified/type/result are
    // zero for a plain stackable Etc item like Wood, matching the captured bytes exactly.
    // `clientIndex` must already be the pinned client_index() wire value (server array
    // position + 2, clif.cpp:122-124) - callers pass InventoryAddResult.SlotIndex + 2,
    // never the raw server-side position.
    internal static byte[] BuildItemPickupAck(ushort clientIndex, ushort count, int itemId, byte itemType)
    {
        var packet = new byte[PacketConstants.ZcItemPickupAckLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcItemPickupAck);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), clientIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), count);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(6), (uint)itemId);
        packet[10] = 1; // IsIdentified
        packet[11] = 0; // IsDamaged
        // offsets 12..31: EQUIPSLOTINFO.card[4] (all zero, not equipment)
        // offset 32: location (u32, zero - not equipped)
        packet[32] = itemType;
        packet[33] = PacketConstants.ZcItemPickupResultSuccess;
        // offsets 34..69: HireExpireDate/bindOnEquipType/option_data/favorite/look/refine/grade - all zero
        return packet;
    }
}
