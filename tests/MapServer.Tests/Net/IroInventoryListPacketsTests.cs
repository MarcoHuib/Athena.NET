using System.Buffers.Binary;
using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.Net;

// Source-derived (not capture-derived): the reference capture only covers the combat burst, not
// the initial login/0x007D inventory-list packets. Layout traced from pinned
// clif_item_equip/clif_item_normal/clif_inventoryStart/clif_inventoryEnd (clif.cpp:2932-3058)
// and the corresponding structs (packets_struct.hpp:418-503, 1218-1243).
public sealed class IroInventoryListPacketsTests
{
    private static readonly WeaponItemDefinition Knife = new(
        1201, "Knife", "Knife", Stackable: false, ClientViewId: 1201, Attack: 17, WeaponLevel: 1, WeaponType.Dagger, EquipLocation: 0x000002,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    private static readonly EtcItemDefinition Wood = new(
        6008, "Wood", "Wood", Stackable: true, ClientViewId: 6008,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_etc.yml", 1));

    [Fact]
    public void BuildInventoryStart_MatchesTracedLayout()
    {
        var packet = IroInventoryListPackets.BuildInventoryStart();

        Assert.Equal(6, packet.Length);
        Assert.Equal((short)0x0b08, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((short)6, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(0, packet[4]); // INVTYPE_INVENTORY
        Assert.Equal(0, packet[5]); // empty name, null terminator only
    }

    [Fact]
    public void BuildInventoryEnd_MatchesTracedLayout()
    {
        var packet = IroInventoryListPackets.BuildInventoryEnd();

        Assert.Equal(4, packet.Length);
        Assert.Equal((short)0x0b0b, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal(0, packet[2]); // INVTYPE_INVENTORY
        Assert.Equal(0, packet[3]); // flag
    }

    [Fact]
    public void BuildItemListNormal_StackableItem_MatchesTracedLayout()
    {
        var item = new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 6008, 5, 0, true, 0, 0, 0);
        var packet = IroInventoryListPackets.BuildItemListNormal([(2, item, Wood)]);

        Assert.Equal(5 + 34, packet.Length);
        Assert.Equal((short)0x0b09, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((short)packet.Length, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(2)));
        Assert.Equal(0, packet[4]); // INVTYPE_INVENTORY

        var entry = packet.AsSpan(5);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(entry));
        Assert.Equal(6008u, BinaryPrimitives.ReadUInt32LittleEndian(entry[2..]));
        Assert.Equal(3, entry[6]); // IT_ETC
        Assert.Equal((short)5, BinaryPrimitives.ReadInt16LittleEndian(entry[7..]));
        Assert.Equal(0u, BinaryPrimitives.ReadUInt32LittleEndian(entry[9..])); // WearState = item_db equip (Etc = 0)
    }

    [Fact]
    public void BuildItemListEquip_EquippedWeapon_MatchesTracedLayout()
    {
        var item = new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 3, 0, 0);
        var packet = IroInventoryListPackets.BuildItemListEquip([(2, item, Knife)]);

        Assert.Equal(5 + 68, packet.Length);
        Assert.Equal((short)0x0b39, BinaryPrimitives.ReadInt16LittleEndian(packet));
        Assert.Equal((short)packet.Length, BinaryPrimitives.ReadInt16LittleEndian(packet.AsSpan(2)));

        var entry = packet.AsSpan(5);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(entry));
        Assert.Equal(1201u, BinaryPrimitives.ReadUInt32LittleEndian(entry[2..]));
        Assert.Equal(5, entry[6]); // IT_WEAPON
        Assert.Equal(0x000002u, BinaryPrimitives.ReadUInt32LittleEndian(entry[7..]));  // location (possible)
        Assert.Equal(0x000002u, BinaryPrimitives.ReadUInt32LittleEndian(entry[11..])); // wearState (actual)
        // option_count + option_data[5] (offsets 39-64) must stay zero - verifies RefiningLevel/
        // grade/Flag were NOT accidentally written into these offsets (the real bug this test
        // now guards against: they collided here, leaving 65-67 zero, which the client read as
        // Flag.IsIdentified=0 - unidentified - silently blocking all equip interaction).
        Assert.All(entry[39..65].ToArray(), b => Assert.Equal(0, b));
        Assert.Equal((byte)3, entry[65]); // RefiningLevel
        Assert.Equal((byte)0, entry[66]); // grade
        Assert.Equal((byte)1, entry[67]); // Flag.IsIdentified=1 (item.Identified=true)
    }

    [Fact]
    public void BuildItemListEquip_UnidentifiedItem_FlagIsIdentifiedIsZero()
    {
        var item = new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0, false, 0, 0, 0);
        var packet = IroInventoryListPackets.BuildItemListEquip([(2, item, Knife)]);

        var entry = packet.AsSpan(5);
        Assert.Equal((byte)0, entry[67] & 0b1); // Flag.IsIdentified=0
    }

    [Fact]
    public void BuildItemListEquip_MultipleItems_AssignsSequentialClientIndices()
    {
        var knifeItem = new CharacterInventoryItem(DurableId: 1, SlotIndex: 0, 1201, 1, 0x000002, true, 0, 0, 0);
        var armor = new ArmorItemDefinition(2301, "Cotton_Shirt", "Cotton Shirt", Stackable: false, ClientViewId: 2301, EquipLocation: 0x000010,
            new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));
        var armorItem = new CharacterInventoryItem(DurableId: 2, SlotIndex: 1, 2301, 1, 0x000010, true, 0, 0, 0);

        var packet = IroInventoryListPackets.BuildItemListEquip([(2, knifeItem, Knife), (3, armorItem, armor)]);

        Assert.Equal(5 + 2 * 68, packet.Length);
        var first = packet.AsSpan(5);
        var second = packet.AsSpan(5 + 68);
        Assert.Equal((ushort)2, BinaryPrimitives.ReadUInt16LittleEndian(first));
        Assert.Equal((ushort)3, BinaryPrimitives.ReadUInt16LittleEndian(second));
        Assert.Equal(2301u, BinaryPrimitives.ReadUInt32LittleEndian(second[2..]));
    }
}
