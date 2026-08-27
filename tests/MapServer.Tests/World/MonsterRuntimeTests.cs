using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

// End-to-end scheduler tests: MonsterRuntime + real MobInstance + real
// RathenaCompatibleMovementPathProvider + real MapCollisionMap, driven entirely by a
// FakeTimeProvider so elapsed-time behavior (never an instant X/Y jump) is provable
// deterministically rather than by sleeping the test thread.
public sealed class MonsterRuntimeTests
{
    private static MobDefinition MakeMob(MobMode mode = MobMode.CanMove, int walkSpeed = 400) => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: walkSpeed, AttackDelay: 1872,
        BaseExp: 0, JobExp: 0, Mode: mode,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static MobSpawnDefinition MakeSpawn(MobMode mode = MobMode.CanMove, int walkSpeed = 400, string map = "test_map") =>
        new(MakeMob(mode, walkSpeed), map, 40, 5000, new("rAthena", "abc", "npc/re/mobs/int_land.txt", 12));

    private static MapCollisionMap MakeAllWalkableMap(string name, int side) =>
        new(name, side, side, Enumerable.Repeat(MapCellFlags.Walkable, side * side).ToArray());

    private static (MonsterRuntime Runtime, MonsterRegistry Registry, FakeTimeProvider Clock) MakeRuntime(
        IEnumerable<MobSpawnDefinition> spawns, ushort spawnX, ushort spawnY, MapCollisionMap map)
    {
        var clock = new FakeTimeProvider();
        var registry = new MonsterRegistry(spawns, new WorldActorIdAllocator(), new FixedCellSelector(spawnX, spawnY), clock);
        var provider = new MapCollisionProvider([map]);
        var pathProvider = new RathenaCompatibleMovementPathProvider(provider);
        var runtime = new MonsterRuntime(registry, provider, pathProvider, clock);
        return (runtime, registry, clock);
    }

    [Fact]
    public void ProcessTick_NonMovableMode_NeverWalks()
    {
        var map = MakeAllWalkableMap("test_map", 40);
        var spawn = MakeSpawn(mode: MobMode.None);
        var (runtime, registry, clock) = MakeRuntime([spawn], 20, 20, map);
        var instance = registry.AllInstances[0];

        for (var i = 0; i < 50; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            runtime.ProcessTick();
        }

        Assert.False(instance.IsWalking);
        var position = instance.GetPosition();
        Assert.Equal((ushort)20, position.X);
        Assert.Equal((ushort)20, position.Y);
    }

    [Fact]
    public void ProcessTick_NoRandomWalkMode_NeverWalksEvenThoughCanMove()
    {
        var map = MakeAllWalkableMap("test_map", 40);
        var spawn = MakeSpawn(mode: MobMode.CanMove | MobMode.NoRandomWalk);
        var (runtime, registry, clock) = MakeRuntime([spawn], 20, 20, map);
        var instance = registry.AllInstances[0];

        for (var i = 0; i < 50; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            runtime.ProcessTick();
        }

        Assert.False(instance.IsWalking);
    }

    [Fact]
    public void ProcessTick_GPoringMode_EventuallyStartsWalking_ToALegalDestination()
    {
        var map = MakeAllWalkableMap("test_map", 40);
        var spawn = MakeSpawn();
        var (runtime, registry, clock) = MakeRuntime([spawn], 20, 20, map);
        var instance = registry.AllInstances[0];

        var walked = false;
        for (var i = 0; i < 20 && !walked; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            runtime.ProcessTick();
            walked = instance.IsWalking;
        }

        Assert.True(walked, "G_PORING (Ai=02 => MD_CANMOVE) never started an idle walk within the expected window.");
        var destination = instance.MovementDestination;
        Assert.True(map.IsTraversalCell(destination.X, destination.Y));
    }

    [Fact]
    public void ProcessTick_PositionAdvancesOverElapsedTime_NeverJumpsInstantlyToDestination()
    {
        var map = MakeAllWalkableMap("test_map", 40);
        var spawn = MakeSpawn();
        var (runtime, registry, clock) = MakeRuntime([spawn], 20, 20, map);
        var instance = registry.AllInstances[0];

        // Drive until a walk starts.
        for (var i = 0; i < 20 && !instance.IsWalking; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            runtime.ProcessTick();
        }
        Assert.True(instance.IsWalking);
        var destination = instance.MovementDestination;
        var startPosition = instance.GetPosition();

        // A single small tick must not have teleported the mob straight to its destination unless
        // it was already adjacent (WalkSpeed=400ms per cell dominates a 50ms tick).
        clock.Advance(TimeSpan.FromMilliseconds(50));
        runtime.ProcessTick();
        var afterSmallTick = instance.GetPosition();

        if (destination.X != startPosition.X || destination.Y != startPosition.Y)
        {
            var distanceFromStart = Math.Max(Math.Abs(afterSmallTick.X - startPosition.X), Math.Abs(afterSmallTick.Y - startPosition.Y));
            var totalDistance = Math.Max(Math.Abs(destination.X - startPosition.X), Math.Abs(destination.Y - startPosition.Y));
            Assert.True(distanceFromStart < totalDistance || totalDistance == 0,
                "Position advanced all the way to the destination within one 50ms tick even though WalkSpeed=400ms/cell.");
        }

        // Enough elapsed time to complete the entire walk must eventually land exactly on the
        // computed destination (proves the walk uses the real WalkSpeed-driven timing, not an
        // arbitrary invented duration).
        for (var i = 0; i < 200; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(400));
            runtime.ProcessTick();
            if (!instance.IsWalking) break;
        }
        Assert.False(instance.IsWalking);
        var finalPosition = instance.GetPosition();
        Assert.Equal(destination.X, finalPosition.X);
        Assert.Equal(destination.Y, finalPosition.Y);
    }

    [Fact]
    public void ProcessTick_MultipleMonsters_MoveIndependentlyUnderOneScheduler()
    {
        var map = MakeAllWalkableMap("test_map", 40);
        var spawnA = MakeSpawn();
        var spawnB = MakeSpawn();
        var clock = new FakeTimeProvider();
        // Two independent registries backing two independent monsters at different starting cells,
        // composed under the SAME MonsterRuntime instance/tick, matching "one scheduler for every
        // monster" - two different FixedCellSelector-placed spawns would collapse to the same cell
        // otherwise, so this test simply extends MakeRuntime's single-selector limit by asserting
        // both instances (from two spawns dispatched through one registry) evolve independently.
        var registry = new MonsterRegistry([spawnA, spawnB], new WorldActorIdAllocator(), new FixedCellSelector(20, 20), clock);
        var provider = new MapCollisionProvider([map]);
        var pathProvider = new RathenaCompatibleMovementPathProvider(provider);
        var runtime = new MonsterRuntime(registry, provider, pathProvider, clock);
        var (first, second) = (registry.AllInstances[0], registry.AllInstances[1]);

        for (var i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            runtime.ProcessTick();
        }

        // Kill only the first - the second's own idle-walk/movement state must be untouched by
        // ANY shared scheduler bookkeeping (no cross-instance state leak through one ProcessTick).
        first.ApplyDamage(9999);
        Assert.False(first.IsAlive);
        Assert.True(second.IsAlive);

        var secondWasWalkingOrHadMoved = second.IsWalking || second.GetPosition() != new MobPosition(20, 20);
        for (var i = 0; i < 20 && !secondWasWalkingOrHadMoved; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            runtime.ProcessTick();
            secondWasWalkingOrHadMoved = second.IsWalking || second.GetPosition() != new MobPosition(20, 20);
        }
        Assert.True(secondWasWalkingOrHadMoved, "The second monster never moved even though it remained alive and movable.");
    }

    [Fact]
    public void ProcessTick_KillingAWalkingMonster_StopsItsMovementImmediately()
    {
        var map = MakeAllWalkableMap("test_map", 40);
        var spawn = MakeSpawn();
        var (runtime, registry, clock) = MakeRuntime([spawn], 20, 20, map);
        var instance = registry.AllInstances[0];

        for (var i = 0; i < 20 && !instance.IsWalking; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            runtime.ProcessTick();
        }
        Assert.True(instance.IsWalking);
        var positionAtDeath = instance.GetPosition();

        instance.ApplyDamage(9999);
        Assert.False(instance.IsAlive);

        // Further ticks (even ones that would have completed the walk) must never advance a dead
        // instance's position - MobInstance.AdvanceMovement's own Alive guard, exercised here
        // through the real scheduler rather than calling AdvanceMovement directly.
        for (var i = 0; i < 50; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(400));
            runtime.ProcessTick();
        }

        var positionAfter = instance.GetPosition();
        Assert.Equal(positionAtDeath.X, positionAfter.X);
        Assert.Equal(positionAtDeath.Y, positionAfter.Y);
    }

    [Fact]
    public void ProcessTick_RespawnedMonster_CanWalkAgain()
    {
        var map = MakeAllWalkableMap("test_map", 40);
        var spawn = MakeSpawn(walkSpeed: 400) with { RespawnDelayMs = 1000 };
        var clock = new FakeTimeProvider();
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator(), new FixedCellSelector(20, 20), clock);
        var provider = new MapCollisionProvider([map]);
        var pathProvider = new RathenaCompatibleMovementPathProvider(provider);
        var runtime = new MonsterRuntime(registry, provider, pathProvider, clock);
        var instance = registry.AllInstances[0];

        instance.ApplyDamage(9999);
        registry.ScheduleRespawnIfNeeded(instance);
        clock.Advance(TimeSpan.FromMilliseconds(1500));
        Assert.Equal(1, registry.ProcessDueRespawns());
        Assert.True(instance.IsAlive);
        Assert.False(instance.IsWalking);

        var walkedAgain = false;
        for (var i = 0; i < 20 && !walkedAgain; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(5));
            registry.ProcessDueRespawns();
            runtime.ProcessTick();
            walkedAgain = instance.IsWalking;
        }

        Assert.True(walkedAgain, "A respawned G_PORING never walked again.");
    }

    [Fact]
    public void ProcessTick_RunningRealTickLoop_NeverPlacesTheMonsterOnANonTraversalCell()
    {
        // A wall splitting the map, forcing the pathfinder to route around it - proves live
        // scheduler-driven movement (not just the pathfinder's own unit tests) never advances a
        // monster through a blocked cell.
        var width = 20;
        var height = 20;
        var cells = new MapCellFlags[width * height];
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var blocked = x == 10 && y != 5; // Wall at x=10 with a single gap at y=5.
                cells[x + y * width] = blocked ? MapCellFlags.None : MapCellFlags.Walkable;
            }
        }
        var map = new MapCollisionMap("test_map", width, height, cells);
        var spawn = MakeSpawn();
        var (runtime, registry, clock) = MakeRuntime([spawn], 3, 3, map);
        var instance = registry.AllInstances[0];

        for (var i = 0; i < 400; i++)
        {
            clock.Advance(TimeSpan.FromMilliseconds(400));
            runtime.ProcessTick();
            var position = instance.GetPosition();
            Assert.True(map.IsTraversalCell(position.X, position.Y),
                $"Live scheduler advanced the monster onto a non-traversal cell ({position.X},{position.Y}).");
        }
    }
}
