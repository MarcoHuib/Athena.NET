using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Db.Entities;

namespace Athena.Net.CharServer.Tests.Db;

// Proves the ONE authoritative stable server-side inventory SlotIndex ordering rule
// (CharInventoryOrdering.InStableSlotOrder) shared by MapServerSession's
// inventory-list/inventory-add/equip-update handlers - the actual root cause fixed
// in this task: inventory-add previously counted only Equip==0 rows, an
// incompatible namespace from the list/equip-update handlers' full-row ordering.
public sealed class CharInventoryOrderingTests
{
    private static CharInventory Row(uint id, uint charId, uint nameId, uint equip = 0) =>
        new() { Id = id, CharId = charId, NameId = nameId, Amount = 1, Equip = equip };

    [Fact]
    public void InStableSlotOrder_EquippedAndUnequippedRows_ShareOneOrdering()
    {
        // Knife (equipped), Cotton Shirt (equipped), First Aid Box (unequipped) - matching the
        // live-verified starter tutorial inventory shape described in this task.
        var rows = new[]
        {
            Row(1, charId: 100, nameId: 1201, equip: 0x000002),
            Row(2, charId: 100, nameId: 2301, equip: 0x000010),
            Row(3, charId: 100, nameId: 23484, equip: 0),
        }.AsQueryable();

        var ordered = rows.InStableSlotOrder(100).ToList();

        Assert.Equal(3, ordered.Count);
        Assert.Equal(1201u, ordered[0].NameId); // slot 0
        Assert.Equal(2301u, ordered[1].NameId); // slot 1
        Assert.Equal(23484u, ordered[2].NameId); // slot 2
    }

    [Fact]
    public void InStableSlotOrder_OnlyIncludesRowsForTheRequestedCharacter()
    {
        var rows = new[]
        {
            Row(1, charId: 100, nameId: 1201),
            Row(2, charId: 200, nameId: 6008), // different character - must be excluded.
            Row(3, charId: 100, nameId: 2301),
        }.AsQueryable();

        var ordered = rows.InStableSlotOrder(100).ToList();

        Assert.Equal(2, ordered.Count);
        Assert.DoesNotContain(ordered, r => r.CharId == 200);
    }

    [Fact]
    public void InStableSlotOrder_NewFourthRow_ReceivesSlotIndexThree()
    {
        var rows = new[]
        {
            Row(1, charId: 100, nameId: 1201, equip: 0x000002),
            Row(2, charId: 100, nameId: 2301, equip: 0x000010),
            Row(3, charId: 100, nameId: 23484, equip: 0),
            Row(4, charId: 100, nameId: 6008, equip: 0), // Wood, newly added.
        }.AsQueryable();

        var newRow = rows.First(r => r.NameId == 6008);
        // Same CountAsync(item.Id < row.Id) computation MapServerSession.HandleInventoryAddRequestAsync
        // uses, over the SAME InStableSlotOrder ordering.
        var slotIndex = rows.InStableSlotOrder(100).Count(r => r.Id < newRow.Id);

        Assert.Equal(3, slotIndex);
    }
}
