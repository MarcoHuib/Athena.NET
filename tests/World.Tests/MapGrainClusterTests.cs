using Athena.Net.World.Contracts;
using Orleans.TestingHost;

namespace Athena.Net.World.Tests;

public sealed class MapGrainClusterTests : IAsyncLifetime
{
    private TestCluster _cluster = null!;

    public async Task InitializeAsync()
    {
        _cluster = new TestClusterBuilder().Build();
        await _cluster.DeployAsync();
    }

    public async Task DisposeAsync() => await _cluster.StopAllSilosAsync();

    [Fact]
    public async Task FirstRegistration_ReturnsRegisteredAndStoresOnePresence()
    {
        var map = Map("prontera");
        var presence = Presence(Guid.NewGuid());

        var result = await map.RegisterPresenceAsync(presence);

        Assert.Equal(MapPresenceRegistrationStatus.Registered, result.Status);
        Assert.Equal(1, result.PresenceCount);
        Assert.Equal([presence], (await map.GetPresenceAsync()).Players);
    }

    [Fact]
    public async Task LostResponseReplay_WithSamePresenceId_IsIdempotent()
    {
        var map = Map("prontera");
        var presence = Presence(Guid.NewGuid());

        var firstExecutionWhoseResponseCouldBeLost = await map.RegisterPresenceAsync(presence);
        var replay = await map.RegisterPresenceAsync(presence);

        Assert.Equal(MapPresenceRegistrationStatus.Registered, firstExecutionWhoseResponseCouldBeLost.Status);
        Assert.Equal(MapPresenceRegistrationStatus.AlreadyRegistered, replay.Status);
        Assert.Equal(1, replay.PresenceCount);
        Assert.Single((await map.GetPresenceAsync()).Players);
    }

    [Fact]
    public async Task DifferentPresenceId_ForSameCharacter_ReturnsConflictAndPreservesOwner()
    {
        var map = Map("prontera");
        var owner = Presence(Guid.NewGuid());
        var conflicting = owner with { PresenceId = Guid.NewGuid(), X = 200 };
        await map.RegisterPresenceAsync(owner);

        var result = await map.RegisterPresenceAsync(conflicting);

        Assert.Equal(MapPresenceRegistrationStatus.Conflict, result.Status);
        Assert.Equal([owner], (await map.GetPresenceAsync()).Players);
    }

    [Fact]
    public async Task UnregisterReplay_IsIdempotent()
    {
        var map = Map("prontera");
        var presence = Presence(Guid.NewGuid());
        await map.RegisterPresenceAsync(presence);

        var first = await map.UnregisterPresenceAsync(presence.CharacterId, presence.PresenceId);
        var replay = await map.UnregisterPresenceAsync(presence.CharacterId, presence.PresenceId);

        Assert.Equal(MapPresenceUnregistrationStatus.Removed, first.Status);
        Assert.Equal(MapPresenceUnregistrationStatus.AlreadyAbsent, replay.Status);
        Assert.Empty((await map.GetPresenceAsync()).Players);
    }

    [Fact]
    public async Task StaleUnregister_CannotRemoveNewerPresence()
    {
        var map = Map("prontera");
        var oldPresence = Presence(Guid.NewGuid());
        var newPresence = oldPresence with { PresenceId = Guid.NewGuid(), ActorId = 2002 };
        await map.RegisterPresenceAsync(oldPresence);
        await map.UnregisterPresenceAsync(oldPresence.CharacterId, oldPresence.PresenceId);
        await map.RegisterPresenceAsync(newPresence);

        var stale = await map.UnregisterPresenceAsync(oldPresence.CharacterId, oldPresence.PresenceId);

        Assert.Equal(MapPresenceUnregistrationStatus.PresenceMismatch, stale.Status);
        Assert.Equal([newPresence], (await map.GetPresenceAsync()).Players);
    }

    [Fact]
    public async Task LogicalMapIdentity_DoesNotDependOnPhysicalSiloIdentity()
    {
        var firstReference = Map("prontera");
        var secondReference = Map("prontera");
        var presence = Presence(Guid.NewGuid());

        await firstReference.RegisterPresenceAsync(presence);

        Assert.Equal([presence], (await secondReference.GetPresenceAsync()).Players);
    }

    private IMapGrain Map(string mapId) => _cluster.GrainFactory.GetGrain<IMapGrain>(mapId);

    private static MapPlayerPresence Presence(Guid presenceId) => new(presenceId, 2001, 1001, 150, 180);
}
