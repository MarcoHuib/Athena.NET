using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Tests.World;

internal sealed class FixedCellSelector(ushort x, ushort y) : IMobSpawnCellSelector
{
    public (ushort X, ushort Y) SelectCell(MobSpawnDefinition spawn, int instanceIndex) => (x, y);
}

public sealed class MonsterRegistryTests
{
    private static MobDefinition MakeMob() => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872,
        BaseExp: 0, JobExp: 0,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    [Fact]
    public void Construction_CreatesOneInstancePerSpawnCount()
    {
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land01", 3, 5000, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator(), new FixedCellSelector(50, 50), new FakeTimeProvider());

        Assert.Equal(3, registry.AllInstances.Count);
    }

    [Fact]
    public void Construction_AssignsUniqueActorIds()
    {
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land01", 5, 5000, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator(), new FixedCellSelector(50, 50), new FakeTimeProvider());

        var ids = registry.AllInstances.Select(i => i.ActorId).ToArray();
        Assert.Equal(ids.Length, ids.Distinct().Count());
    }

    [Fact]
    public void TwoInstancesOnSameMap_HaveIndependentHp()
    {
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land01", 2, 5000, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator(), new FixedCellSelector(50, 50), new FakeTimeProvider());
        var (first, second) = (registry.AllInstances[0], registry.AllInstances[1]);

        first.ApplyDamage(20);

        Assert.Equal(35u, first.CurrentHp);
        Assert.Equal(55u, second.CurrentHp); // Damaging one instance must not affect the other.
    }

    [Fact]
    public void KillingOneInstance_DoesNotKillOrRespawnAnother()
    {
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land01", 2, 5000, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator(), new FixedCellSelector(50, 50), new FakeTimeProvider());
        var (first, second) = (registry.AllInstances[0], registry.AllInstances[1]);

        first.ApplyDamage(55);

        Assert.False(first.IsAlive);
        Assert.True(second.IsAlive);
    }

    [Fact]
    public void InstancesOnDifferentMaps_DoNotShareRuntimeState()
    {
        var spawnA = new MobSpawnDefinition(MakeMob(), "int_land01", 1, 5000, new("rAthena", "abc", "x.txt", 1));
        var spawnB = new MobSpawnDefinition(MakeMob(), "int_land02", 1, 5000, new("rAthena", "abc", "x.txt", 2));
        var registry = new MonsterRegistry([spawnA, spawnB], new WorldActorIdAllocator(), new FixedCellSelector(50, 50), new FakeTimeProvider());

        var onMap1 = registry.AllInstances.Single(i => i.Map == "int_land01");
        var onMap2 = registry.AllInstances.Single(i => i.Map == "int_land02");

        onMap1.ApplyDamage(55);

        Assert.False(onMap1.IsAlive);
        Assert.True(onMap2.IsAlive);
        Assert.NotEqual(onMap1.ActorId, onMap2.ActorId);
    }

    [Fact]
    public void TryGetInstance_WrongMap_Fails()
    {
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land01", 1, 5000, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator(), new FixedCellSelector(50, 50), new FakeTimeProvider());
        var actorId = registry.AllInstances[0].ActorId;

        Assert.False(registry.TryGetInstance(actorId, "int_land02", out _));
        Assert.True(registry.TryGetInstance(actorId, "int_land01", out _));
    }

    [Fact]
    public void GetVisibleInstances_ExcludesDeadAndOutOfRange()
    {
        var spawn = new MobSpawnDefinition(MakeMob(), "int_land01", 1, 5000, new("rAthena", "abc", "x.txt", 1));
        var registry = new MonsterRegistry([spawn], new WorldActorIdAllocator(), new FixedCellSelector(50, 50), new FakeTimeProvider());

        Assert.Single(registry.GetVisibleInstances("int_land01", 51, 51, range: 14));
        Assert.Empty(registry.GetVisibleInstances("int_land01", 100, 100, range: 14));

        registry.AllInstances[0].ApplyDamage(55);
        Assert.Empty(registry.GetVisibleInstances("int_land01", 51, 51, range: 14));
    }

    [Fact]
    public void ProcessDueRespawns_RestoresOnlyMonstersWhoseDelayElapsed()
    {
        var clock = new FakeTimeProvider();
        var fastSpawn = new MobSpawnDefinition(MakeMob(), "int_land01", 1, 1000, new("rAthena", "abc", "x.txt", 1));
        var slowSpawn = new MobSpawnDefinition(MakeMob(), "int_land01", 1, 10000, new("rAthena", "abc", "x.txt", 2));
        var registry = new MonsterRegistry([fastSpawn, slowSpawn], new WorldActorIdAllocator(), new FixedCellSelector(50, 50), clock);
        var (fast, slow) = (registry.AllInstances[0], registry.AllInstances[1]);

        fast.ApplyDamage(55);
        slow.ApplyDamage(55);
        registry.ScheduleRespawnIfNeeded(fast);
        registry.ScheduleRespawnIfNeeded(slow);

        clock.Advance(TimeSpan.FromMilliseconds(1500));
        var respawned = registry.ProcessDueRespawns();

        Assert.Equal(1, respawned);
        Assert.True(fast.IsAlive);
        Assert.False(slow.IsAlive);
    }
}
