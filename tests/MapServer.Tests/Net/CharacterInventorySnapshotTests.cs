using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Proves the durable-row-identity/runtime-SlotIndex split: FromLogin (initial dense assignment
// from CharServer's stable row order), WithUpdatedItem (in-place update, slot preserved),
// WithNewItem (first-free-slot reuse - pinned pc_additem's own "search for first empty slot"
// behavior), and WithoutDurableId (row removal that leaves a HOLE, never renumbers later rows -
// pinned pc_delitem, pc.cpp:6103-6128, zeroes the array slot in place).
public sealed class CharacterInventorySnapshotTests
{
    private static CharacterInventoryItem Item(uint slotIndex, uint durableId, int itemId, uint amount = 1, uint equip = 0) =>
        new(durableId, slotIndex, itemId, amount, equip, Identified: true, Refine: 0, Favorite: 0, Bound: 0);

    [Fact]
    public void FromLogin_AssignsDenseSlotsInCharServerRowOrder()
    {
        var rows = new List<(uint DurableId, int ItemId, uint Amount, uint Equip, bool Identified, byte Refine, byte Favorite, byte Bound)>
        {
            (1, 1201, 1, 0x000002, true, 0, 0, 0),
            (2, 2301, 1, 0x000010, true, 0, 0, 0),
            (3, 23484, 1, 0, true, 0, 0, 0),
        };

        var snapshot = CharacterInventorySnapshot.FromLogin(rows);

        Assert.Equal(3, snapshot.Items.Count);
        Assert.Equal(0u, snapshot.Items[0].SlotIndex);
        Assert.Equal(1u, snapshot.Items[0].DurableId);
        Assert.Equal(1u, snapshot.Items[1].SlotIndex);
        Assert.Equal(2u, snapshot.Items[1].DurableId);
        Assert.Equal(2u, snapshot.Items[2].SlotIndex);
        Assert.Equal(3u, snapshot.Items[2].DurableId);
    }

    [Fact]
    public void WithUpdatedItem_PreservesCurrentSlot_UpdatesFields()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1, 1201), Item(1, 2, 6008, amount: 1)]);

        var updated = snapshot.WithUpdatedItem(durableId: 2, itemId: 6008, amount: 2, equip: 0, identified: true, refine: 0, favorite: 0, bound: 0);

        Assert.Equal(2, updated.Items.Count);
        var row = updated.Items.Single(i => i.DurableId == 2);
        Assert.Equal(2u, row.Amount);
        Assert.Equal(1u, row.SlotIndex); // slot unchanged
    }

    [Fact]
    public void WithUpdatedItem_UnknownDurableId_ThrowsInvariantViolation()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1, 1201)]);

        Assert.Throws<InvalidOperationException>(() =>
            snapshot.WithUpdatedItem(durableId: 99, itemId: 6008, amount: 1, equip: 0, identified: true, refine: 0, favorite: 0, bound: 0));
    }

    [Fact]
    public void WithNewItem_NoHoles_AppendsAtNextSlot()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1, 1201), Item(1, 2, 2301)]);

        var updated = snapshot.WithNewItem(durableId: 3, itemId: 6008, amount: 1, equip: 0, identified: true, refine: 0, favorite: 0, bound: 0);

        Assert.Equal(3, updated.Items.Count);
        Assert.Equal(2u, updated.Items.Single(i => i.DurableId == 3).SlotIndex);
    }

    [Fact]
    public void WithNewItem_DuplicateDurableId_ThrowsInvariantViolation()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1, 1201)]);

        Assert.Throws<InvalidOperationException>(() =>
            snapshot.WithNewItem(durableId: 1, itemId: 1201, amount: 1, equip: 0, identified: true, refine: 0, favorite: 0, bound: 0));
    }

    [Fact]
    public void WithoutDurableId_RemovesRow_LeavesHole_LaterSlotsUnchanged()
    {
        var snapshot = new CharacterInventorySnapshot(
        [
            Item(0, 1, 1201, equip: 0x000002),
            Item(1, 2, 2301, equip: 0x000010),
            Item(2, 3, 23484), // First Aid Box
            Item(3, 4, 6008),  // Wood
        ]);

        var updated = snapshot.WithoutDurableId(3);

        Assert.Equal(3, updated.Items.Count);
        Assert.DoesNotContain(updated.Items, i => i.DurableId == 3);
        var wood = updated.Items.Single(i => i.DurableId == 4);
        Assert.Equal(3u, wood.SlotIndex); // unaffected by the deletion - no renumbering.
    }

    [Fact]
    public void WithoutDurableId_UnknownDurableId_ThrowsInvariantViolation()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1, 1201)]);

        Assert.Throws<InvalidOperationException>(() => snapshot.WithoutDurableId(99));
    }

    // The user's exact required scenario: slot 0 Knife, slot 1 Cotton Shirt, slot 2 FirstAidBox,
    // slot 3 Wood. Consuming FirstAidBox (slot 2) must leave a hole; Wood must remain slot 3.
    // Granting a new item must reuse the hole at slot 2, and Wood must still be slot 3.
    [Fact]
    public void HoleThenReuse_MatchesLiveFirstAidBoxSequence()
    {
        var snapshot = new CharacterInventorySnapshot(
        [
            Item(0, 1, 1201),  // Knife
            Item(1, 2, 2301),  // Cotton Shirt
            Item(2, 3, 23484), // First Aid Box
            Item(3, 4, 6008),  // Wood
        ]);

        var afterConsume = snapshot.WithoutDurableId(3);
        Assert.Equal(3, afterConsume.Items.Count);
        Assert.Equal(3u, afterConsume.Items.Single(i => i.DurableId == 4).SlotIndex); // Wood still slot 3

        var afterGrant = afterConsume.WithNewItem(durableId: 5, itemId: 11518, amount: 1, equip: 0, identified: true, refine: 0, favorite: 0, bound: 0);

        Assert.Equal(2u, afterGrant.Items.Single(i => i.DurableId == 5).SlotIndex); // reuses the hole
        Assert.Equal(3u, afterGrant.Items.Single(i => i.DurableId == 4).SlotIndex); // Wood unaffected
    }

    [Fact]
    public void WithUpdatedItem_NeverMutatesOriginalSnapshot()
    {
        var original = new CharacterInventorySnapshot([Item(0, 1, 1201, amount: 1)]);

        var updated = original.WithUpdatedItem(durableId: 1, itemId: 1201, amount: 5, equip: 0, identified: true, refine: 0, favorite: 0, bound: 0);

        Assert.Equal(1u, original.Items.Single().Amount);
        Assert.Equal(5u, updated.Items.Single().Amount);
    }

    [Fact]
    public void WithoutDurableId_NeverMutatesOriginalSnapshot()
    {
        var original = new CharacterInventorySnapshot([Item(0, 1, 1201), Item(1, 2, 23484)]);

        var updated = original.WithoutDurableId(2);

        Assert.Equal(2, original.Items.Count);
        Assert.Single(updated.Items);
    }
}
