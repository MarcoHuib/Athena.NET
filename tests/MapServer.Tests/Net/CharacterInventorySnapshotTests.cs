using Athena.Net.MapServer.Net;

namespace Athena.Net.MapServer.Tests.Net;

// Proves CharacterInventorySnapshot.WithItem - the ONE place a caller applies a single
// confirmed-persisted row mutation to its own runtime snapshot copy - enforces the
// authoritative slot-ordering invariant rather than silently guessing/repairing a violation:
// SlotIndex < count requires the same ItemId already at that slot (in-place update);
// SlotIndex == count appends a brand-new row; anything else is a hard failure.
public sealed class CharacterInventorySnapshotTests
{
    private static CharacterInventoryItem Item(uint slotIndex, int itemId, uint amount = 1, uint equip = 0) =>
        new(slotIndex, itemId, amount, equip, Identified: true, Refine: 0, Favorite: 0, Bound: 0);

    [Fact]
    public void WithItem_SlotIndexEqualsCount_AppendsNewRow()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1201), Item(1, 2301), Item(2, 23484)]);

        var updated = snapshot.WithItem(Item(3, 6008, amount: 1));

        Assert.Equal(4, updated.Items.Count);
        Assert.Equal(6008, updated.Items[3].ItemId);
        Assert.Equal(3u, updated.Items[3].SlotIndex);
    }

    [Fact]
    public void WithItem_SlotIndexLessThanCount_SameItemId_ReplacesInPlace_NoNewRow()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1201), Item(1, 6008, amount: 1)]);

        var updated = snapshot.WithItem(Item(1, 6008, amount: 2));

        Assert.Equal(2, updated.Items.Count); // No new row created.
        Assert.Equal(2u, updated.Items[1].Amount);
        Assert.Equal(1u, updated.Items[1].SlotIndex);
    }

    [Fact]
    public void WithItem_SlotIndexLessThanCount_DifferentItemId_ThrowsInvariantViolation()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1201), Item(1, 2301)]);

        Assert.Throws<InvalidOperationException>(() => snapshot.WithItem(Item(1, 6008)));
    }

    [Fact]
    public void WithItem_SlotIndexGreaterThanCount_ThrowsInvariantViolation_DoesNotGuessOrRepair()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1201)]);

        Assert.Throws<InvalidOperationException>(() => snapshot.WithItem(Item(5, 6008)));
    }

    [Fact]
    public void WithItem_EquippedRowsParticipateInSameOrdering_ReplacingUnequippedThirdRowDoesNotDisturbEquippedSlots()
    {
        var snapshot = new CharacterInventorySnapshot(
        [
            Item(0, 1201, equip: 0x000002), // Knife, equipped
            Item(1, 2301, equip: 0x000010), // Cotton Shirt, equipped
            Item(2, 23484), // First Aid Box, unequipped
        ]);

        var updated = snapshot.WithItem(Item(2, 23484, amount: 2));

        Assert.Equal(0x000002u, updated.Items[0].Equip);
        Assert.Equal(0x000010u, updated.Items[1].Equip);
        Assert.Equal(0u, updated.Items[0].SlotIndex);
        Assert.Equal(1u, updated.Items[1].SlotIndex);
    }

    [Fact]
    public void WithItem_NeverMutatesOriginalSnapshot()
    {
        var original = new CharacterInventorySnapshot([Item(0, 1201)]);

        var updated = original.WithItem(Item(1, 6008));

        Assert.Single(original.Items); // Original unchanged.
        Assert.Equal(2, updated.Items.Count);
    }

    // WithoutSlot applies a CharServer-confirmed row deletion (InventoryConsumePersistenceResult.
    // RowDeleted) - mirrors pinned pc_delitem's row-removal decision, and renumbers later rows
    // down by one to match what a fresh full inventory reload would produce.
    [Fact]
    public void WithoutSlot_RemovesLastRow_EarlierSlotsUnaffected()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1201, equip: 0x000002), Item(1, 2301, equip: 0x000010), Item(2, 23484)]);

        var updated = snapshot.WithoutSlot(2);

        Assert.Equal(2, updated.Items.Count);
        Assert.Equal(0u, updated.Items.Single(i => i.ItemId == 1201).SlotIndex);
        Assert.Equal(1u, updated.Items.Single(i => i.ItemId == 2301).SlotIndex);
        Assert.DoesNotContain(updated.Items, i => i.ItemId == 23484);
    }

    [Fact]
    public void WithoutSlot_RemovesMiddleRow_LaterRowsRenumberDownByOne()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1201), Item(1, 2301), Item(2, 23484), Item(3, 6008)]);

        var updated = snapshot.WithoutSlot(1);

        Assert.Equal(3, updated.Items.Count);
        Assert.Equal(0u, updated.Items.Single(i => i.ItemId == 1201).SlotIndex);
        Assert.Equal(1u, updated.Items.Single(i => i.ItemId == 23484).SlotIndex); // was 2, now 1
        Assert.Equal(2u, updated.Items.Single(i => i.ItemId == 6008).SlotIndex); // was 3, now 2
    }

    [Fact]
    public void WithoutSlot_OutOfRange_ThrowsInvariantViolation()
    {
        var snapshot = new CharacterInventorySnapshot([Item(0, 1201)]);

        Assert.Throws<InvalidOperationException>(() => snapshot.WithoutSlot(5));
    }

    [Fact]
    public void WithoutSlot_NeverMutatesOriginalSnapshot()
    {
        var original = new CharacterInventorySnapshot([Item(0, 1201), Item(1, 23484)]);

        var updated = original.WithoutSlot(1);

        Assert.Equal(2, original.Items.Count);
        Assert.Single(updated.Items);
    }
}
