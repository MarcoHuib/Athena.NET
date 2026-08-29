using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

public sealed class PlayerVisibilityCoordinatorTests
{
    [Fact]
    public async Task ReciprocalLogin_NewcomerGetsExistingAndExistingGetsSpawnExactlyOnce()
    {
        var registry = new PlayerPresenceRegistry();
        var coordinator = new PlayerVisibilityCoordinator(registry);
        var a = PlayerPresenceRegistryTests.Make(1, 101, "prontera", 100, 100, "A");
        var b = PlayerPresenceRegistryTests.Make(2, 102, "prontera", 105, 105, "B");
        var ao = new Observer(1);
        var bo = new Observer(2);

        await coordinator.RegisterAsync(a, ao, default);
        await coordinator.RegisterAsync(b, bo, default);

        Assert.Equal([(1u, PlayerEntryKind.ExistingStandingOrWalking)], bo.Entries);
        Assert.Equal([(2u, PlayerEntryKind.NewlySpawned)], ao.Entries);
        Assert.DoesNotContain(ao.Entries, entry => entry.ActorId == 1);
        Assert.DoesNotContain(bo.Entries, entry => entry.ActorId == 2);

        await coordinator.UpdateMovementAsync(b with { X = 106 }, false, default);
        Assert.Single(ao.Entries);
        Assert.Single(bo.Entries);
    }

    [Fact]
    public async Task MovementEdges_DiscoverAt14VanishAt15AndRediscoverOnce()
    {
        var registry = new PlayerPresenceRegistry();
        var coordinator = new PlayerVisibilityCoordinator(registry);
        var a = PlayerPresenceRegistryTests.Make(1, 101, "prontera", 100, 100, "A");
        var b = PlayerPresenceRegistryTests.Make(2, 102, "prontera", 115, 100, "B");
        var ao = new Observer(1);
        var bo = new Observer(2);
        await coordinator.RegisterAsync(a, ao, default);
        await coordinator.RegisterAsync(b, bo, default);
        Assert.Empty(ao.Entries);

        b = b with { X = 114, Movement = new(115, 100, 114, 100, 1) };
        await coordinator.UpdateMovementAsync(b, true, default);
        await coordinator.UpdateMovementAsync(b, false, default);
        Assert.Single(ao.Entries);
        Assert.Single(bo.Entries);

        b = b with { X = 115, Movement = null };
        await coordinator.UpdateMovementAsync(b, false, default);
        await coordinator.UpdateMovementAsync(b, false, default);
        Assert.Equal([2u], ao.Left);
        Assert.Equal([1u], bo.Left);

        b = b with { X = 114 };
        await coordinator.UpdateMovementAsync(b, false, default);
        Assert.Equal(2, ao.Entries.Count);
        Assert.Equal(2, bo.Entries.Count);
    }

    [Fact]
    public async Task CrowdedRegion_OneObserverDiscoversEachOfOneHundredActorsExactlyOnce()
    {
        var registry = new PlayerPresenceRegistry();
        var coordinator = new PlayerVisibilityCoordinator(registry);
        var observers = new List<Observer>();
        for (uint id = 1; id <= 100; id++)
        {
            var observer = new Observer(id);
            observers.Add(observer);
            await coordinator.RegisterAsync(PlayerPresenceRegistryTests.Make(id, id + 1000, "prontera", (ushort)(100 + id % 10), (ushort)(100 + id / 10)), observer, default);
        }
        var newcomer = new Observer(500);
        await coordinator.RegisterAsync(PlayerPresenceRegistryTests.Make(500, 1500, "prontera", 105, 105), newcomer, default);
        Assert.Equal(100, newcomer.Entries.Select(entry => entry.ActorId).Distinct().Count());
        Assert.Equal(100, newcomer.Entries.Count);
        Assert.All(observers, observer => Assert.Single(observer.Entries, entry => entry.ActorId == 500));
    }

    private sealed class Observer(uint actorId) : IPlayerPresenceObserver
    {
        private readonly HashSet<uint> _visible = [];
        public uint ActorId { get; } = actorId;
        public List<(uint ActorId, PlayerEntryKind Kind)> Entries { get; } = [];
        public List<uint> Left { get; } = [];
        public List<uint> Movement { get; } = [];
        public List<uint> Look { get; } = [];

        public Task PlayerEnteredViewAsync(PlayerPresence presence, PlayerEntryKind kind, CancellationToken cancellationToken)
        {
            if (_visible.Add(presence.ActorId)) Entries.Add((presence.ActorId, kind));
            return Task.CompletedTask;
        }
        public Task PlayerMovementChangedAsync(PlayerPresence presence, CancellationToken cancellationToken)
        {
            if (_visible.Contains(presence.ActorId)) Movement.Add(presence.ActorId);
            return Task.CompletedTask;
        }
        public Task PlayerLookChangedAsync(PlayerPresence presence, CancellationToken cancellationToken)
        {
            if (_visible.Contains(presence.ActorId)) Look.Add(presence.ActorId);
            return Task.CompletedTask;
        }
        public Task PlayerLeftViewAsync(uint id, CancellationToken cancellationToken)
        {
            if (_visible.Remove(id)) Left.Add(id);
            return Task.CompletedTask;
        }
        public void ForgetPlayer(uint id) => _visible.Remove(id);
    }
}
