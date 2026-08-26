using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

internal sealed class FakeInventoryPersistence : ICharacterInventoryPersistence
{
    private readonly Dictionary<(uint CharId, int ItemId), uint> _stacks = new();
    private readonly Dictionary<uint, List<int>> _slotOrderByChar = new();
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

        var order = _slotOrderByChar.TryGetValue(charId, out var existing) ? existing : (_slotOrderByChar[charId] = []);
        var slotIndex = order.IndexOf(itemId);
        if (slotIndex < 0)
        {
            slotIndex = order.Count;
            order.Add(itemId);
        }

        return Task.FromResult(new InventoryAddPersistenceResult(true, updated, (uint)slotIndex, Equip: 0, Identified: true, Refine: 0, Favorite: 0, Bound: 0));
    }

    public uint Persisted(uint charId, int itemId) => _stacks.GetValueOrDefault((charId, itemId));
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

    // The authoritative CharacterInventoryItem this method returns is what callers (e.g.
    // MapClientSession's reward path) must use to update their own runtime
    // CharacterInventorySnapshot - it must carry the real persisted field values, not
    // invented/assumed ones.
    [Fact]
    public async Task AddItemAsync_Success_ReturnsAuthoritativeItemMatchingPersistedFields()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);

        var result = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.NotNull(result.Item);
        Assert.Equal(Wood.Id, result.Item!.ItemId);
        Assert.Equal(1u, result.Item.Amount);
        Assert.Equal(0u, result.Item.SlotIndex);
        Assert.Equal(0u, result.Item.Equip);
        Assert.True(result.Item.Identified);
    }

    [Fact]
    public async Task AddItemAsync_SecondAward_ReturnsSameSlotIndex_WithUpdatedAmount()
    {
        var persistence = new FakeInventoryPersistence();
        var session = new CharacterInventorySession(1, 100, persistence);

        var first = await session.AddItemAsync(Wood, 1, CancellationToken.None);
        var second = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.Equal(first.Item!.SlotIndex, second.Item!.SlotIndex);
        Assert.Equal(2u, second.Item.Amount);
    }

    [Fact]
    public async Task AddItemAsync_PersistenceFailure_ReturnsNullItem()
    {
        var persistence = new FakeInventoryPersistence { FailNextCall = true };
        var session = new CharacterInventorySession(1, 100, persistence);

        var result = await session.AddItemAsync(Wood, 1, CancellationToken.None);

        Assert.Null(result.Item);
    }
}
