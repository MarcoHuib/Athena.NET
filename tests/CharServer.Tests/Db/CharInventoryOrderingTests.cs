using Athena.Net.CharServer.Db;
using Athena.Net.CharServer.Db.Entities;

namespace Athena.Net.CharServer.Tests.Db;

// Proves CharInventoryOrdering.InStableOrder - the ONE stable, deterministic enumeration order
// CharServer uses for a full inventory-list read. This is NOT a runtime "slot" concept anymore:
// CharServer has no slot concept at all - MapServer alone assigns/owns runtime SlotIndex (see
// ai/map-server.md "Durable row identity vs runtime SlotIndex"). Each row's own real primary
// key (Id, exposed to MapServer as DurableId) is the row's actual stable identity.
public sealed class CharInventoryOrderingTests
{
    private static CharInventory Row(uint id, uint charId, uint nameId, uint equip = 0) =>
        new() { Id = id, CharId = charId, NameId = nameId, Amount = 1, Equip = equip };

    [Fact]
    public void InStableOrder_EquippedAndUnequippedRows_ShareOneOrdering()
    {
        // Knife (equipped), Cotton Shirt (equipped), First Aid Box (unequipped) - matching the
        // live-verified starter tutorial inventory shape.
        var rows = new[]
        {
            Row(1, charId: 100, nameId: 1201, equip: 0x000002),
            Row(2, charId: 100, nameId: 2301, equip: 0x000010),
            Row(3, charId: 100, nameId: 23484, equip: 0),
        }.AsQueryable();

        var ordered = rows.InStableOrder(100).ToList();

        Assert.Equal(3, ordered.Count);
        Assert.Equal(1201u, ordered[0].NameId);
        Assert.Equal(2301u, ordered[1].NameId);
        Assert.Equal(23484u, ordered[2].NameId);
    }

    [Fact]
    public void InStableOrder_OnlyIncludesRowsForTheRequestedCharacter()
    {
        var rows = new[]
        {
            Row(1, charId: 100, nameId: 1201),
            Row(2, charId: 200, nameId: 6008), // different character - must be excluded.
            Row(3, charId: 100, nameId: 2301),
        }.AsQueryable();

        var ordered = rows.InStableOrder(100).ToList();

        Assert.Equal(2, ordered.Count);
        Assert.DoesNotContain(ordered, r => r.CharId == 200);
    }

    [Fact]
    public void InStableOrder_OrdersByRowId_NotInsertionSequenceIntoTheArray()
    {
        var rows = new[]
        {
            Row(3, charId: 100, nameId: 23484),
            Row(1, charId: 100, nameId: 1201),
            Row(2, charId: 100, nameId: 2301),
        }.AsQueryable();

        var ordered = rows.InStableOrder(100).ToList();

        Assert.Equal(1u, ordered[0].Id);
        Assert.Equal(2u, ordered[1].Id);
        Assert.Equal(3u, ordered[2].Id);
    }
}
