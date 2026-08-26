using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

internal sealed class FakeInventoryPersistence : ICharacterInventoryPersistence
{
    private readonly Dictionary<(uint CharId, int ItemId), uint> _stacks = new();
    private readonly Dictionary<uint, List<int>> _rowOrderByChar = new();
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
        var key = (charId, itemId);
        _stacks.TryGetValue(key, out var current);
        var updated = current + amount;
        _stacks[key] = updated;

        var order = _rowOrderByChar.TryGetValue(charId, out var existing) ? existing : (_rowOrderByChar[charId] = []);
        var rowIndex = order.IndexOf(itemId);
        var isNewRow = rowIndex < 0;
        if (isNewRow)
        {
            rowIndex = order.Count;
            order.Add(itemId);
        }
        var durableId = _durableIdByRow.TryGetValue((charId, rowIndex), out var existingDurableId) ? existingDurableId : _durableIdByRow[(charId, rowIndex)] = _nextDurableId++;

        return Task.FromResult(new InventoryAddPersistenceResult(true, updated, durableId, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0, isNewRow));
    }

    private readonly Dictionary<(uint CharId, int RowIndex), uint> _durableIdByRow = new();

    public uint Persisted(uint charId, int itemId) => _stacks.GetValueOrDefault((charId, itemId));

    public Task<InventoryConsumePersistenceResult> ConsumeItemAsync(uint accountId, uint charId, uint durableId, uint amount, CancellationToken cancellationToken)
    {
        if (!_rowOrderByChar.TryGetValue(charId, out var order)) return Task.FromResult(InventoryConsumePersistenceResult.Failed());
        var rowIndex = _durableIdByRow.Where(kv => kv.Key.CharId == charId && kv.Value == durableId).Select(kv => (int?)kv.Key.RowIndex).FirstOrDefault();
        if (rowIndex is null || rowIndex.Value >= order.Count) return Task.FromResult(InventoryConsumePersistenceResult.Failed());

        var itemId = order[rowIndex.Value];
        var key = (charId, itemId);
        var current = _stacks.GetValueOrDefault(key);
        if (current < amount) return Task.FromResult(InventoryConsumePersistenceResult.Failed());

        var updated = current - amount;
        _stacks[key] = updated;
        var rowDeleted = updated == 0;
        if (rowDeleted) order.RemoveAt(rowIndex.Value);
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
}
