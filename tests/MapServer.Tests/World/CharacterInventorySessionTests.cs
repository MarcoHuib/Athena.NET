using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// Models persisted rows by their stable DurableId directly - DurableId is never derived from,
// or resolved through, list/array position (that is exactly the invariant this migration
// introduced: durable identity != database/order position). A dictionary keyed by DurableId
// cannot go stale when an earlier row is deleted, unlike a row-index-keyed lookup would - a
// later row's DurableId always resolves to the SAME entry regardless of what else was removed.
internal sealed class FakeInventoryPersistence : ICharacterInventoryPersistence
{
    private sealed record PersistedRow(uint CharId, int ItemId, uint Amount);

    private readonly Dictionary<uint, PersistedRow> _rowsByDurableId = new();
    // Only used to find-or-create the stack for a given (charId, itemId) pair on add - never
    // used to resolve or invalidate a DurableId.
    private readonly Dictionary<(uint CharId, int ItemId), uint> _durableIdByStack = new();
    private uint _nextDurableId = 1;
    public bool FailNextCall { get; set; }
    public int CallCount { get; private set; }

    public Task<InventoryAddPersistenceResult> AddStackableItemAsync(uint accountId, uint charId, int itemId, uint amount, CancellationToken cancellationToken)
    {
        CallCount++;
        if (FailNextCall)
        {
            FailNextCall = false;
            return Task.FromResult(InventoryAddPersistenceResult.Failed());
        }

        var stackKey = (charId, itemId);
        var isNewRow = !_durableIdByStack.TryGetValue(stackKey, out var durableId);
        if (isNewRow)
        {
            durableId = _nextDurableId++;
            _durableIdByStack[stackKey] = durableId;
        }

        var current = _rowsByDurableId.TryGetValue(durableId, out var existingRow) ? existingRow.Amount : 0;
        var updated = current + amount;
        _rowsByDurableId[durableId] = new PersistedRow(charId, itemId, updated);

        return Task.FromResult(new InventoryAddPersistenceResult(true, updated, durableId, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0, isNewRow));
    }

    public uint Persisted(uint charId, int itemId) =>
        _durableIdByStack.TryGetValue((charId, itemId), out var durableId) && _rowsByDurableId.TryGetValue(durableId, out var row) ? row.Amount : 0;

    public Task<InventoryConsumePersistenceResult> ConsumeItemAsync(uint accountId, uint charId, uint durableId, uint amount, CancellationToken cancellationToken)
    {
        if (!_rowsByDurableId.TryGetValue(durableId, out var row) || row.CharId != charId || row.Amount < amount)
            return Task.FromResult(InventoryConsumePersistenceResult.Failed());

        var updated = row.Amount - amount;
        var rowDeleted = updated == 0;
        if (rowDeleted)
        {
            _rowsByDurableId.Remove(durableId);
            _durableIdByStack.Remove((charId, row.ItemId));
        }
        else
        {
            _rowsByDurableId[durableId] = row with { Amount = updated };
        }

        return Task.FromResult(new InventoryConsumePersistenceResult(true, updated, rowDeleted));
    }
}

public sealed class CharacterInventorySessionTests
{
    private static readonly ItemDefinition Wood = new EtcItemDefinition(6008, "Wood", "Wood", Stackable: true, ClientViewId: 6008, new("rAthena", "abc", "db/re/item_db_etc.yml", 1));

    [Fact]
    public async Task AddItemAsync_FirstAward_CreatesNewStackWithAmount()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);

        var result = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(1u, result.NewAmount);
        Assert.Equal(1u, persistence.Persisted(100, 6008));
    }

    [Fact]
    public async Task AddItemAsync_SecondAward_IncreasesExistingStackToTwo()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);

        await session.AddItemAsync(Wood, 1, CancellationToken.None);
        var second = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.True(second.Success);
        Assert.Equal(2u, second.NewAmount);
    }

    [Fact]
    public async Task AddItemAsync_PersistenceFailure_ReportsFailure_NotFakeSuccess()
    {
        var persistence = new FakeInventoryPersistence { FailNextCall = true };
        var session = new CharacterInventorySession(1, 100, persistence);

        var result = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.False(result.Success);
        Assert.Equal(0u, persistence.Persisted(100, 6008));
    }

    [Fact]
    public async Task AddItemAsync_NonStackableItem_RejectsAmountGreaterThanOne()
    {
        var nonStackable = Wood with { Stackable = false };
        var session = new CharacterInventorySession(1, 100, new FakeInventoryPersistence());

        await Assert.ThrowsAsync<ArgumentException>(() => session.AddItemAsync(nonStackable, 2, CancellationToken.None));
    }

    [Fact]
    public async Task AddItemAsync_ReconnectReload_ReturnsPersistedAmount()
    {
        var persistence = new FakeInventoryPersistence();
        var firstSession = new CharacterInventorySession(1, 100, persistence);
        await firstSession.AddItemAsync(Wood, 1, CancellationToken.None);

        // Simulate reconnect: a brand-new session over the same persistence.
        var secondSession = new CharacterInventorySession(1, 100, persistence);
        var result = await secondSession.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.Equal(2u, result.NewAmount); // Picks up the persisted 1, not starting over from 0.
    }

    // The authoritative InventoryAddResultItem/DurableId this method returns is what callers
    // (e.g. MapClientSession's reward path) must use to update their own runtime
    // CharacterInventorySnapshot - it must carry the real persisted field values, not
    // invented/assumed ones. Runtime SlotIndex is NOT part of this result - only the caller,
    // which owns the live CharacterInventorySnapshot, can decide it (see InventoryAddResult's
    // own doc comment).
    [Fact]
    public async Task AddItemAsync_Success_ReturnsAuthoritativeItemMatchingPersistedFields()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);

        var result = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.NotNull(result.Item);
        Assert.True(result.IsNewRow);
        Assert.Equal(Wood.Id, result.Item!.Value.ItemId);
        Assert.Equal(1u, result.Item.Value.Amount);
        Assert.Equal(0u, result.Item.Value.Equip);
        Assert.True(result.Item.Value.Identified);
    }

    [Fact]
    public async Task AddItemAsync_SecondAward_ReturnsSameDurableId_IsNewRowFalse_WithUpdatedAmount()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);

        var first = await session.AddItemAsync(Wood, 1, CancellationToken.None);
        var second = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.Equal(first.DurableId, second.DurableId);
        Assert.False(second.IsNewRow);
        Assert.Equal(2u, second.Item!.Value.Amount);
    }

    [Fact]
    public async Task AddItemAsync_PersistenceFailure_ReturnsNullItem()
    {
        var persistence = new FakeInventoryPersistence { FailNextCall = true };
        var session = new CharacterInventorySession(1, 100, persistence);

        var result = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.Null(result.Item);
    }

    [Fact]
    public async Task ConsumeItemAsync_StackGreaterThanOne_DecrementsAmount_RowNotDeleted()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);
        var added = await session.AddItemAsync(Wood, 5, CancellationToken.None);

        var result = await session.ConsumeItemAsync(added.DurableId, 1, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(4u, result.NewAmount);
        Assert.False(result.RowDeleted);
        Assert.Equal(4u, persistence.Persisted(100, 6008));
    }

    [Fact]
    public async Task ConsumeItemAsync_LastUnit_DeletesRow()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);
        var added = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        var result = await session.ConsumeItemAsync(added.DurableId, 1, CancellationToken.None);

        Assert.True(result.Success);
        Assert.Equal(0u, result.NewAmount);
        Assert.True(result.RowDeleted);
        Assert.Equal(0u, persistence.Persisted(100, 6008));
    }

    [Fact]
    public async Task ConsumeItemAsync_InvalidDurableId_ReportsFailure_NotFakeSuccess()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);

        var result = await session.ConsumeItemAsync(999, 1, CancellationToken.None);

        Assert.False(result.Success);
    }

    // Proves FakeInventoryPersistence itself (and, by the same reasoning, the real
    // CharServer-backed implementation it stands in for) resolves DurableId as stable identity,
    // never list/array position: deleting an earlier durable row must never invalidate or shift
    // a later row's DurableId. Also proves the MapServer-side runtime-slot reuse that follows
    // (WithoutDurableId leaving a hole, WithNewItem reusing it) never touches the earlier
    // persisted row's own DurableId or its still-current runtime slot.
    [Fact]
    public async Task ConsumeItemAsync_DeletesEarlierRow_LaterRowDurableIdStillResolves_SlotReuseDoesNotAlterIt()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);
        var knife = new WeaponItemDefinition(1201, "Knife", "Knife", Stackable: false, ClientViewId: 1201, Attack: 17, WeaponLevel: 1, WeaponType.Dagger, Range: 1, EquipLocation: 0x000002, new("rAthena", "abc", "db/re/item_db_equip.yml", 1));
        var cottonShirt = new ArmorItemDefinition(2301, "Cotton_Shirt", "Cotton Shirt", Stackable: false, ClientViewId: 2301, EquipLocation: 0x000010, new("rAthena", "abc", "db/re/item_db_equip.yml", 1));
        var firstAidBox = new UsableItemDefinition(23484, "Firstaid_Box_5", "First aid Box (5)", Stackable: true, ClientViewId: 23484, new("rAthena", "abc", "db/re/item_db_usable.yml", 1));

        var knifeResult = await session.AddItemAsync(knife, 1, CancellationToken.None); // durableId 1
        var shirtResult = await session.AddItemAsync(cottonShirt, 1, CancellationToken.None); // durableId 2
        var firstAidResult = await session.AddItemAsync(firstAidBox, 1, CancellationToken.None); // durableId 3
        var woodResult = await session.AddItemAsync(Wood, 5, CancellationToken.None); // durableId 4
        Assert.Equal(3u, firstAidResult.DurableId);
        Assert.Equal(4u, woodResult.DurableId);

        var snapshot = new CharacterInventorySnapshot(
        [
            new(knifeResult.DurableId, SlotIndex: 0, knife.Id, 1, 0x000002, true, 0, 0, 0),
            new(shirtResult.DurableId, SlotIndex: 1, cottonShirt.Id, 1, 0x000010, true, 0, 0, 0),
            new(firstAidResult.DurableId, SlotIndex: 2, firstAidBox.Id, 1, 0, true, 0, 0, 0),
            new(woodResult.DurableId, SlotIndex: 3, Wood.Id, 5, 0, true, 0, 0, 0),
        ]);

        // Delete the First Aid Box (durableId 3) - an EARLIER row than Wood (durableId 4).
        var consumeResult = await session.ConsumeItemAsync(firstAidResult.DurableId, 1, CancellationToken.None);
        Assert.True(consumeResult.Success);
        Assert.True(consumeResult.RowDeleted);

        // Wood's durable row must still resolve by its OWN DurableId, completely unaffected by
        // the earlier row's deletion - proven by successfully consuming one unit from it here.
        var woodConsumeResult = await session.ConsumeItemAsync(woodResult.DurableId, 1, CancellationToken.None);
        Assert.True(woodConsumeResult.Success);
        Assert.Equal(4u, woodConsumeResult.NewAmount);
        Assert.False(woodConsumeResult.RowDeleted);

        // Runtime-slot side: the hole left at slot 2 may be reused by a brand-new grant, but this
        // must never alter Wood's own DurableId or its still-current runtime slot 3.
        var afterDelete = snapshot.WithoutDurableId(firstAidResult.DurableId);
        var grantResult = await session.AddItemAsync(new EtcItemDefinition(11518, "N_Blue_Potion", "Blue Potion", Stackable: true, ClientViewId: 11518, new("rAthena", "abc", "db/re/item_db_usable.yml", 1)), 10, CancellationToken.None);
        var afterReuse = afterDelete.WithNewItem(grantResult.DurableId, 11518, grantResult.NewAmount, 0, true, 0, 0, 0);

        Assert.Equal(2u, afterReuse.Items.Single(i => i.DurableId == grantResult.DurableId).SlotIndex); // reused the hole
        Assert.Equal(woodResult.DurableId, afterReuse.Items.Single(i => i.ItemId == Wood.Id).DurableId); // Wood's DurableId unchanged
        Assert.Equal(3u, afterReuse.Items.Single(i => i.DurableId == woodResult.DurableId).SlotIndex); // Wood's slot unchanged
    }
}
