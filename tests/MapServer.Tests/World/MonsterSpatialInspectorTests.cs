namespace Athena.Net.MapServer.Tests.World;

using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

// MonsterSpatialInspector exists specifically because RathenaCompatibleMobSpawnCellSelector's own
// spawn-time diagnostic cannot answer "what is at actorId N right now": WorldActorIdAllocator
// assigns the real actorId in MonsterRegistry's constructor AFTER TrySelectCell already returned a
// position, so the selector never sees the actorId at all. These tests prove the actorId-based
// correlation path a live stock-client hover/click (0x0368) actually needs.
//
// Step 6 cutover: MonsterSpatialInspector now reads the World-authoritative projection
// (MonsterFeedProjectionRegistry) instead of a local MonsterRegistry (see that inspector's own
// top-of-file doc comment). These tests still use a local MonsterRegistry/MobInstance to derive
// realistic position/identity/movement data (including exercising a real respawn cycle), then feed
// that data into a MonsterFeedProjectionRegistry via ApplySnapshot - exactly the same bridging
// pattern MonsterFeedProjection's own production reconciliation uses, just driven by a local
// MobInstance instead of a real World feed page here.
public sealed class MonsterSpatialInspectorTests
{
    private static MobDefinition MakeMob() => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872, AttackMotion: 672, DamageMotion: 480,
        BaseExp: 0, JobExp: 0, Mode: MobMode.CanMove,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static MapCollisionMap MakeAllWalkableMap(string name, int side) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    private sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
    {
        public bool TrySelectCell(MobSpawnDefinition spawn, int instanceIndex, out MobPosition position)
        {
            position = new MobPosition(x, y);
            return true;
        }
    }

    // Bridges one local MobInstance's CURRENT state into a freshly-seeded MonsterFeedProjectionRegistry,
    // mirroring the same MobInstance -> WorldMonsterInstance conversion WorldMonsterMapSimulation's
    // own ToWireInstance performs on the real World side.
    private static MonsterFeedProjectionRegistry SeedProjection(MobInstance instance)
    {
        var position = instance.GetPosition();
        var wireInstance = new WorldMonsterInstance(
            ActorId: instance.ActorId,
            IncarnationId: new WorldMonsterIncarnationId(instance.IncarnationId.Value),
            MapId: instance.Map,
            MobId: instance.Spawn.Mob.Id,
            X: position.X,
            Y: position.Y,
            Lifecycle: instance.IsAlive ? WorldMonsterLifecycleState.Alive : WorldMonsterLifecycleState.Dead,
            IsWalking: instance.IsWalking,
            DestinationX: instance.MovementDestination.X,
            DestinationY: instance.MovementDestination.Y,
            Engagement: WorldMonsterEngagementState.Unengaged,
            EngagedTarget: null);

        var projections = new MonsterFeedProjectionRegistry();
        var projection = projections.GetOrCreate(instance.Map);
        projection.ApplySnapshot([wireInstance], WorldSimulationEpoch.NewEpoch(), new MonsterCombatStateStore());
        return projections;
    }

    [Fact]
    public void TryDescribe_KnownActorId_ReturnsMatchingPositionAndCellFlags()
    {
        var map = MakeAllWalkableMap("int_land", 100);
        var provider = new MapCollisionProvider([map]);
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land", 1, 5000, 0, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator().Allocate, new FixedCellSelector(63, 69), TimeProvider.System);
        var instance = registry.AllInstances.Single();
        var inspector = new MonsterSpatialInspector(SeedProjection(instance), provider);

        var found = inspector.TryDescribe(instance.ActorId, "int_land", out var diagnostics);

        Assert.True(found);
        Assert.Equal(instance.ActorId, diagnostics.ActorId);
        Assert.Equal("G_PORING", diagnostics.MobAegisName);
        Assert.Equal("int_land", diagnostics.Map);
        Assert.Equal((ushort)63, diagnostics.X);
        Assert.Equal((ushort)69, diagnostics.Y);
        Assert.True(diagnostics.IsWalkable);
        Assert.True(diagnostics.IsTraversalCell);
        Assert.False(diagnostics.IsWater);
    }

    [Fact]
    public void TryDescribe_UnknownActorId_ReturnsFalse()
    {
        var map = MakeAllWalkableMap("int_land", 100);
        var provider = new MapCollisionProvider([map]);
        var projections = new MonsterFeedProjectionRegistry();
        projections.GetOrCreate("int_land"); // Tracked, but empty - no instances registered.
        var inspector = new MonsterSpatialInspector(projections, provider);

        Assert.False(inspector.TryDescribe(999, "int_land", out _));
    }

    [Fact]
    public void TryDescribe_WrongMapForActorId_ReturnsFalse()
    {
        // Mirrors MonsterFeedProjection's own same-map requirement - an actorId visible on one map
        // must not resolve when queried against a different map name.
        var map = MakeAllWalkableMap("int_land", 100);
        var provider = new MapCollisionProvider([map]);
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land", 1, 5000, 0, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator().Allocate, new FixedCellSelector(50, 50), TimeProvider.System);
        var instance = registry.AllInstances.Single();
        var inspector = new MonsterSpatialInspector(SeedProjection(instance), provider);

        Assert.False(inspector.TryDescribe(instance.ActorId, "int_land01", out _));
    }

    [Fact]
    public void TryDescribe_NoCollisionDataForMap_ReturnsFalse()
    {
        var provider = new MapCollisionProvider([]); // int_land deliberately uncovered.
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land", 1, 5000, 0, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator().Allocate, new FixedCellSelector(50, 50), TimeProvider.System);
        var instance = registry.AllInstances.Single();
        var inspector = new MonsterSpatialInspector(SeedProjection(instance), provider);

        Assert.False(inspector.TryDescribe(instance.ActorId, "int_land", out _));
    }

    [Fact]
    public void TryDescribe_ReflectsCurrentPosition_AfterRespawnMovesTheInstance()
    {
        // The actorId stays stable across a death/respawn cycle, but the position (and therefore
        // the cell diagnostics) must reflect the CURRENT resolved cell, not the original spawn
        // cell - this is exactly why correlation must go through a freshly re-seeded projection
        // (mirroring a real Respawned feed entry) rather than a value captured at spawn time.
        var map = MakeAllWalkableMap("int_land", 100);
        var provider = new MapCollisionProvider([map]);
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land", 1, 5000, 0, new("rAthena", "abc", "x.txt", 1));
        var clock = new FakeTimeProvider();
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator().Allocate, new FixedCellSelector(10, 10), clock);
        var instance = registry.AllInstances.Single();

        instance.ApplyDamage(instance.CurrentHp);
        registry.ScheduleRespawnIfNeeded(instance);
        clock.Advance(TimeSpan.FromMilliseconds(6000));
        registry.ProcessDueRespawns();

        var inspector = new MonsterSpatialInspector(SeedProjection(instance), provider);

        Assert.True(inspector.TryDescribe(instance.ActorId, "int_land", out var diagnostics));
        var position = instance.GetPosition();
        Assert.Equal(position.X, diagnostics.X);
        Assert.Equal(position.Y, diagnostics.Y);
    }
}
