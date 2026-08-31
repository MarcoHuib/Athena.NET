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
    public async Task LogicalMapIdentity_RegistersQueriesAndUnregistersRealPresence()
    {
        var map = _cluster.GrainFactory.GetGrain<IMapGrain>("prontera");
        var presence = new MapPlayerPresence(2001, 1001, 150, 180);

        var registration = await map.RegisterPresenceAsync(presence);
        var snapshot = await map.GetPresenceAsync();

        Assert.True(registration.Registered);
        Assert.Equal("prontera", registration.MapId);
        Assert.Equal(1, registration.PresenceCount);
        Assert.Equal([presence], snapshot.Players);
        Assert.True(await map.UnregisterPresenceAsync(presence.CharacterId));
        Assert.Empty((await map.GetPresenceAsync()).Players);
    }

    [Fact]
    public async Task LogicalMapIdentity_DoesNotDependOnPhysicalSiloIdentity()
    {
        var firstReference = _cluster.GrainFactory.GetGrain<IMapGrain>("prontera");
        var secondReference = _cluster.GrainFactory.GetGrain<IMapGrain>("prontera");

        await firstReference.RegisterPresenceAsync(new MapPlayerPresence(2002, 1002, 10, 20));

        Assert.Single((await secondReference.GetPresenceAsync()).Players);
    }
}
