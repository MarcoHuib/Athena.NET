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

    // ZC_DELETE_ITEM_FROM_BODY (0x07FA), verified sailor-packet-export.txt frame 7291,
    // TCP-payload offset 0x0049, exact 8-byte match: `fa 07 00 00 04 00 02 00`. Matches pinned
    // clif_delitem (clif.cpp:2917): packetType.W deleteReason.W index.W amount.W. `clientIndex`
    // must already be the pinned client_index() wire value (server array position + 2,
    // clif.cpp:122-124) - callers pass the affected row's own SlotIndex + 2 at the moment of
    // consumption, the same transform BuildItemPickupAck's callers already use. `amount` is the
    // amount actually consumed FROM THAT ROW, not the total amount requested by the script
    // command (a multi-row delitem sends one of these packets per affected row). `deleteReason`
    // is `PacketConstants.ZcDeleteItemFromBodyReasonScriptDelitem` for the script `delitem`
    // command specifically - see that constant's own doc comment for the pinned pc_delitem trace.
    internal static byte[] BuildDeleteItemFromBody(ushort clientIndex, ushort amount, ushort deleteReason)
    {
        var packet = new byte[PacketConstants.ZcDeleteItemFromBodyLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcDeleteItemFromBody);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(2), deleteReason);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(4), clientIndex);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.AsSpan(6), amount);
        return packet;
    }
}

// Self-facing appearance projection derived from the authoritative equipment snapshot -
// distinct from IroMonsterCombatPackets, which is combat-result representation only.
internal static class IroCharacterAppearancePackets
{
    // PACKET_ZC_SPRITE_CHANGE (0x01D7), wide-field variant (PACKETVER_RE_NUM >= 20180704,
    // pinned build satisfies this): packetType.W AID.L type.B val.L val2.L = 15 bytes
    // (packets_struct.hpp:2591). Sent inside clif_parse_LoadEndAck (clif.cpp:10771) via
    // clif_changelook(sd, LOOK_WEAPON, ...), target=AREA (includes self). For a player,
    // clif_changelook always overwrites val with vd->look[LOOK_WEAPON] and sets
    // val2 = vd->look[LOOK_SHIELD] (clif.cpp:3979-3986, 4096-4099). weaponViewId is the
    // AliasName-resolved view_id (or the item's own nameid as fallback) per
    // map_session_data::update_look (pc.cpp:623-647) - verified against stock-iRO capture
    // (kill-poring-heal-jobup, frame 210): Knife 1201's LOOK_WEAPON val=1201, NOT its
    // weapon_type enum value. shieldViewId is 0 when no shield is equipped (this vertical
    // slice's CharacterEquipmentSnapshot does not yet model a shield slot).
    internal static byte[] BuildSpriteChangeWeapon(uint actorId, uint weaponViewId, uint shieldViewId = 0)
    {
        var packet = new byte[PacketConstants.ZcSpriteChangeLength];
        BinaryPrimitives.WriteInt16LittleEndian(packet, PacketConstants.ZcSpriteChange);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(2), actorId);
        packet[6] = PacketConstants.ZcSpriteChangeTypeWeapon;
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(7), weaponViewId);
        BinaryPrimitives.WriteUInt32LittleEndian(packet.AsSpan(11), shieldViewId);
        return packet;
    }
}
