using Athena.Net.MapServer.Net;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class CharacterEquipmentMutationServiceTests
{
    private const uint AccountId = 7;
    private const uint CharId = 9;

    private static readonly WeaponItemDefinition Knife = new(
        1201, "Knife", "Knife", Stackable: false, ClientViewId: 1201, Attack: 17, WeaponLevel: 1, WeaponType.Dagger, EquipLocation: 0x000002,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    private static readonly ArmorItemDefinition CottonShirt = new(
        2301, "Cotton_Shirt", "Cotton Shirt", Stackable: false, ClientViewId: 2301, EquipLocation: 0x000010,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_equip.yml", 1));

    private static readonly EtcItemDefinition Wood = new(
        6008, "Wood", "Wood", Stackable: true, ClientViewId: 6008,
        new WorldSourceInfo("rAthena", "abc", "db/re/item_db_etc.yml", 1));

    private static readonly IReadOnlyDictionary<int, ItemDefinition> Items =
        new Dictionary<int, ItemDefinition> { [Knife.Id] = Knife, [CottonShirt.Id] = CottonShirt, [Wood.Id] = Wood };

    private sealed class RecordingPersistence : ICharacterInventoryListPersistence
    {
        public bool NextResult { get; set; } = true;
        public List<(uint SlotIndex, uint Equip)> Calls { get; } = [];

        public Task<CharacterInventoryReadResult> GetInventoryAsync(uint a, uint c, CancellationToken t) => throw new NotSupportedException();

        public Task<bool> SetItemEquipAsync(uint accountId, uint characterId, uint slotIndex, uint equip, CancellationToken cancellationToken)
        {
            Calls.Add((slotIndex, equip));
            return Task.FromResult(NextResult);
        }
    }

    [Fact]
    public async Task EquipAsync_UnequippedKnife_PersistsRightHandAndReturnsSuccess()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0, true, 0, 0, 0)]);
        var persistence = new RecordingPersistence();
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, updated) = await service.EquipAsync(inventory, 0, requestedPosition: 0x000002, Items, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Equal(0x000002u, outcome.Value.WearLocation);
        Assert.Equal(0x000002u, updated!.Items[0].Equip);
        Assert.Single(persistence.Calls);
        Assert.Equal((0u, 0x000002u), persistence.Calls[0]);
    }

    [Fact]
    public async Task EquipAsync_AlreadyEquippedItem_ReturnsFailWithoutPersisting()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var persistence = new RecordingPersistence();
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, updated) = await service.EquipAsync(inventory, 0, requestedPosition: 0x000002, Items, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Null(updated);
        Assert.Empty(persistence.Calls);
    }

    [Fact]
    public async Task EquipAsync_PositionNotInEquipLocation_ReturnsFail()
    {
        // Armor's EquipLocation is EQP_ARMOR (0x10); requesting EQP_HAND_R (0x2) should fail.
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 2301, 1, 0, true, 0, 0, 0)]);
        var persistence = new RecordingPersistence();
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, _) = await service.EquipAsync(inventory, 0, requestedPosition: 0x000002, Items, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Empty(persistence.Calls);
    }

    [Fact]
    public async Task EquipAsync_NonEquippableItem_ReturnsFail()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 6008, 1, 0, true, 0, 0, 0)]);
        var persistence = new RecordingPersistence();
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, _) = await service.EquipAsync(inventory, 0, requestedPosition: 0x000002, Items, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
    }

    [Fact]
    public async Task EquipAsync_UnknownSlotIndex_ReturnsNullOutcome()
    {
        var inventory = new CharacterInventorySnapshot([]);
        var persistence = new RecordingPersistence();
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, updated) = await service.EquipAsync(inventory, 5, requestedPosition: 0x000002, Items, CancellationToken.None);

        Assert.Null(outcome);
        Assert.Null(updated);
    }

    [Fact]
    public async Task EquipAsync_SlotConflict_UnequipsExistingItemFirst()
    {
        // Two knives; slot 0 already equipped, equip slot 1 into the same right hand.
        var inventory = new CharacterInventorySnapshot(
        [
            new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0),
            new CharacterInventoryItem(1, 1201, 1, 0, true, 0, 0, 0),
        ]);
        var persistence = new RecordingPersistence();
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, updated) = await service.EquipAsync(inventory, 1, requestedPosition: 0x000002, Items, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Success, outcome!.Value.Result);
        Assert.Equal(0u, updated!.Items[0].Equip); // old slot unequipped
        Assert.Equal(0x000002u, updated.Items[1].Equip); // new slot equipped
        Assert.Equal(2, persistence.Calls.Count);
        Assert.Equal((0u, 0u), persistence.Calls[0]); // unequip old first
        Assert.Equal((1u, 0x000002u), persistence.Calls[1]); // then equip new
    }

    [Fact]
    public async Task EquipAsync_PersistenceFailure_ReturnsFailWithoutMutatingSnapshot()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0, true, 0, 0, 0)]);
        var persistence = new RecordingPersistence { NextResult = false };
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, updated) = await service.EquipAsync(inventory, 0, requestedPosition: 0x000002, Items, CancellationToken.None);

        Assert.Equal(EquipMutationResult.Fail, outcome!.Value.Result);
        Assert.Null(updated);
    }

    [Fact]
    public async Task UnequipAsync_EquippedKnife_PersistsZeroAndReturnsSuccess()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var persistence = new RecordingPersistence();
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, updated) = await service.UnequipAsync(inventory, 0, CancellationToken.None);

        Assert.True(outcome!.Value.Success);
        Assert.Equal(0x000002u, outcome.Value.WearLocation); // reports what WAS equipped
        Assert.Equal(0u, updated!.Items[0].Equip);
        Assert.Single(persistence.Calls);
        Assert.Equal((0u, 0u), persistence.Calls[0]);
    }

    [Fact]
    public async Task UnequipAsync_AlreadyUnequippedItem_ReturnsFailureWithoutPersisting()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0, true, 0, 0, 0)]);
        var persistence = new RecordingPersistence();
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, updated) = await service.UnequipAsync(inventory, 0, CancellationToken.None);

        Assert.False(outcome!.Value.Success);
        Assert.Null(updated);
        Assert.Empty(persistence.Calls);
    }

    [Fact]
    public async Task UnequipAsync_UnknownSlotIndex_ReturnsFailureWithoutPersisting()
    {
        var inventory = new CharacterInventorySnapshot([]);
        var persistence = new RecordingPersistence();
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, updated) = await service.UnequipAsync(inventory, 5, CancellationToken.None);

        Assert.False(outcome!.Value.Success);
        Assert.Null(updated);
        Assert.Empty(persistence.Calls);
    }

    [Fact]
    public async Task UnequipAsync_PersistenceFailure_ReturnsFailureWithoutMutatingSnapshot()
    {
        var inventory = new CharacterInventorySnapshot([new CharacterInventoryItem(0, 1201, 1, 0x000002, true, 0, 0, 0)]);
        var persistence = new RecordingPersistence { NextResult = false };
        var service = new CharacterEquipmentMutationService(AccountId, CharId, persistence);

        var (outcome, updated) = await service.UnequipAsync(inventory, 0, CancellationToken.None);

        Assert.False(outcome!.Value.Success);
        Assert.Null(updated);
    }
}
