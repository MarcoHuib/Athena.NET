using System.Buffers.Binary;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Self-facing initial inventory/equipment-list projection - the pinned PACKETVER 20220406
// equivalent of clif_inventorylist(sd) (clif.cpp:3062-3143). Pure representation of the
// authoritative CharacterInventorySnapshot (see ICharacterInventoryListPersistence) resolved
// through the generated item registry - no persistence or domain logic happens here.
//
// Pinned call order inside clif_parse_LoadEndAck (clif.cpp:10771-10783), all target=SELF:
//   clif_inventoryStart(INVTYPE_INVENTORY, "")
//   clif_item_equip(...) for every non-stackable row      -> 0x0B39, once per MAX_ITEMLIST batch
//   clif_item_normal(...) for every stackable row         -> 0x0B09, once per MAX_ITEMLIST batch
//   clif_inventoryEnd(INVTYPE_INVENTORY)
// This slice's starter inventory (Knife, armor, First Aid Box) never exceeds one batch, so
// batching beyond a single 0x0B39/0x0B09 pair per call is not yet implemented - extend only
// when a traced case needs more than MAX_ITEMLIST items in one list.
internal static class IroInventoryListPackets
{
    // ZC_INVENTORY_START (0x0B08, packets_struct.hpp:1218-1232, PACKETVER_RE_NUM >= 20180919
    // branch): packetType.W packetLength.W invType.B name[strLen]. clif_inventorylist always
    // calls this with name="" (clif.cpp:3067), giving strLen=1 (just the null terminator).
    private const short InventoryStartType = 0x0b08;
    private const byte InventoryTypeInventory = 0; // INVTYPE_INVENTORY (clif.cpp:99)

    internal static byte[] BuildInventoryStart()
    {
        var packet = new byte[6];
        BinaryPrimitives.WriteInt16LittleEndian(packet, InventoryStartType);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(2), (short)packet.Length);
        packet[4] = InventoryTypeInventory;
        packet[5] = 0; // name = "" (1-byte null terminator)
        return packet;
    }

    // ZC_INVENTORY_END (0x0B0B, packets_struct.hpp:1236-1243): packetType.W invType.B flag.B.
    // flag is always 0 at clif_inventoryEnd's only call site (clif.cpp:3057).
    private const short InventoryEndType = 0x0b0b;

    internal static byte[] BuildInventoryEnd()
    {
        var packet = new byte[4];
        BinaryPrimitives.WriteInt16LittleEndian(packet, InventoryEndType);
        packet[2] = InventoryTypeInventory;
        packet[3] = 0; // flag
        return packet;
    }

    // ZC_ITEM_LIST_NORMAL (0x0B09, PACKETVER_RE_NUM >= 20180912 branch): envelope
    // packetType.W packetLength.W invType.B, then NORMALITEM_INFO entries (34 bytes each,
    // packets_struct.hpp:418-445, clif_item_normal, clif.cpp:2987-3011):
    //   index.W ITID.L type.B count.W wearState.L slot.card[4].L(x4) hireExpireDate.L flag.B
    // WearState is the item_db's possible-equip-location (0 for a non-equippable stackable
    // item) - NOT the persisted Equip bitmask, matching clif_item_normal's own
    // `p->WearState = id->equip` (not `it->equip`).
    private const short ItemListNormalType = 0x0b09;
    private const int NormalItemLength = 34;

    internal static byte[] BuildItemListNormal(IReadOnlyList<(ushort ClientIndex, CharacterInventoryItem Item, ItemDefinition Definition)> items)
    {
        var length = 5 + items.Count * NormalItemLength;
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, ItemListNormalType);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(2), (short)length);
        packet[4] = InventoryTypeInventory;

        for (var i = 0; i < items.Count; i++)
        {
            var (clientIndex, item, definition) = items[i];
            var span = packet.AsSpan(5 + i * NormalItemLength, NormalItemLength);
            BinaryPrimitives.WriteUInt16LittleEndian(span, clientIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(span[2..], (uint)definition.ClientViewId);
            span[6] = ItemType(definition);
            BinaryPrimitives.WriteInt16LittleEndian(span[7..], (short)item.Amount);
            var wearState = definition is IEquippableItemDefinition equippable ? equippable.EquipLocation : 0;
            BinaryPrimitives.WriteUInt32LittleEndian(span[9..], wearState);
            // offsets 13..28: EQUIPSLOTINFO.card[4] - all zero, no cards on the tutorial rows
            // offset 29..32: HireExpireDate - zero, never a rental item here
            span[33] = (byte)((item.Identified ? 1 : 0) | (item.Favorite != 0 ? 2 : 0)); // Flag.IsIdentified/PlaceETCTab bitfield
        }
        return packet;
    }

    // ZC_EQUIPMENT_ITEMLIST (0x0B39, PACKETVER_RE_NUM >= 20200723 branch): envelope
    // packetType.W packetLength.W invType.B, then EQUIPITEM_INFO entries (68 bytes each,
    // packets_struct.hpp:457-503, clif_item_equip, clif.cpp:2932-2960):
    //   index.W ITID.L type.B location.L wearState.L slot.card[4].L(x4) hireExpireDate.L
    //   bindOnEquipType.W wItemSpriteNumber.W refiningLevel.B grade.B flag.B
    // location is the item_db's possible-equip-location (pc_equippoint) - here always the
    // same value as EquipLocation since this slice never models AliasName-shadowed equip
    // rules. wearState is the ROW's persisted Equip bitmask (it->equip) - the currently-worn
    // location, distinct from location. wItemSpriteNumber is left 0: EQP_VISIBLE covers only
    // helm/garment/costume slots (pc.hpp:1143-1145), which this slice's starter rows never
    // occupy.
    private const short ItemListEquipType = 0x0b39;
    private const int EquipItemLength = 68;

    internal static byte[] BuildItemListEquip(IReadOnlyList<(ushort ClientIndex, CharacterInventoryItem Item, IEquippableItemDefinition Definition)> items)
    {
        var length = 5 + items.Count * EquipItemLength;
        var packet = new byte[length];
        BinaryPrimitives.WriteInt16LittleEndian(packet, ItemListEquipType);
        BinaryPrimitives.WriteInt16LittleEndian(packet.AsSpan(2), (short)length);
        packet[4] = InventoryTypeInventory;

        for (var i = 0; i < items.Count; i++)
        {
            var (clientIndex, item, definition) = items[i];
            var itemDefinition = (ItemDefinition)definition;
            var span = packet.AsSpan(5 + i * EquipItemLength, EquipItemLength);
            BinaryPrimitives.WriteUInt16LittleEndian(span, clientIndex);
            BinaryPrimitives.WriteUInt32LittleEndian(span[2..], (uint)itemDefinition.ClientViewId);
            span[6] = ItemType(itemDefinition);
            BinaryPrimitives.WriteUInt32LittleEndian(span[7..], definition.EquipLocation);
            BinaryPrimitives.WriteUInt32LittleEndian(span[11..], item.Equip);
            // offsets 15..30: EQUIPSLOTINFO.card[4] - all zero
            // offsets 31..34: HireExpireDate - zero
            // offsets 35..36: bindOnEquipType - zero (not bound, not a bind-on-equip item)
            // offsets 37..38: wItemSpriteNumber - zero (see doc comment above)
            span[39] = item.Refine;
            span[40] = 0; // grade
            span[41] = (byte)((item.Identified ? 1 : 0) | (item.Favorite != 0 ? 4 : 0)); // Flag.IsIdentified/PlaceETCTab bitfield
        }
        return packet;
    }

    // itemtype() (clif.cpp:109-118): the item_db type enum, with two remapped special cases
    // this domain model does not yet represent (IT_SHADOWGEAR, IT_PETEGG) - neither applies to
    // any currently-generated item, so only the direct Weapon/Armor/Etc/Usable mapping is
    // implemented; extend when a traced item needs the remapped cases.
    private static byte ItemType(ItemDefinition definition) => definition switch
    {
        WeaponItemDefinition => 5, // IT_WEAPON (mmo.hpp:228)
        ArmorItemDefinition => 4,  // IT_ARMOR (mmo.hpp:227)
        UsableItemDefinition => 2, // IT_USABLE (mmo.hpp:225)
        EtcItemDefinition => 3,    // IT_ETC (mmo.hpp:226)
        _ => throw new NotSupportedException($"{definition.GetType().Name} has no modeled itemtype() mapping."),
    };
}
