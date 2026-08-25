using Athena.Net.MapServer.World;

public sealed class MobInstanceTests
{
    private static MobDefinition MakeMob(uint maxHp = 55) => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: maxHp,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: 1, WalkSpeed: 400, AttackDelay: 1872,
        BaseExp: 0, JobExp: 0,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static MobSpawnDefinition MakeSpawn(uint maxHp = 55, int respawnMs = 5000) =>
        new(MakeMob(maxHp), "int_land01", 40, respawnMs, new("rAthena", "abc", "npc/re/mobs/int_land.txt", 12));

    [Fact]
    public void Spawn_CreatesAliveInstance_WithFullHp()
    {
        var instance = new MobInstance(110000001, MakeSpawn(), 50, 50);
        Assert.True(instance.IsAlive);
        Assert.Equal(55u, instance.CurrentHp);
    }

    [Fact]
    public void ApplyDamage_NonLethal_DecreasesHpAndStaysAlive()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        var (before, after, killed) = instance.ApplyDamage(10);

        Assert.Equal(55u, before);
        Assert.Equal(45u, after);
        Assert.False(killed);
        Assert.True(instance.IsAlive);
    }

    [Fact]
    public void ApplyDamage_Lethal_TransitionsToDeadExactlyOnce()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        var (_, after, killed) = instance.ApplyDamage(55);

        Assert.Equal(0u, after);
        Assert.True(killed);
        Assert.False(instance.IsAlive);
    }

    [Fact]
    public void ApplyDamage_OverkillDoesNotUnderflow()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        var (_, after, killed) = instance.ApplyDamage(9999);

        Assert.Equal(0u, after);
        Assert.True(killed);
    }

    [Fact]
    public void ApplyDamage_AfterDeath_DoesNotKillAgainOrChangeHp()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        instance.ApplyDamage(55);
        var (before, after, killedAgain) = instance.ApplyDamage(10);

        Assert.Equal(0u, before);
        Assert.Equal(0u, after);
        Assert.False(killedAgain);
    }

    [Fact]
    public void TryScheduleRespawn_OnlySucceedsOnceForOneDeath()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        instance.ApplyDamage(55);

        Assert.True(instance.TryScheduleRespawn(1000));
        Assert.False(instance.TryScheduleRespawn(2000)); // Second schedule attempt for the same death is rejected.
    }

    [Fact]
    public void TryScheduleRespawn_WhileAlive_Fails()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        Assert.False(instance.TryScheduleRespawn(1000));
    }

    [Fact]
    public void TryRespawn_BeforeDueTime_DoesNothing()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        instance.ApplyDamage(55);
        instance.TryScheduleRespawn(1000);

        Assert.False(instance.TryRespawn(500));
        Assert.False(instance.IsAlive);
    }

    [Fact]
    public void TryRespawn_AtOrAfterDueTime_RestoresAliveAndFullHp()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        instance.ApplyDamage(55);
        instance.TryScheduleRespawn(1000);

        Assert.True(instance.TryRespawn(1000));
        Assert.True(instance.IsAlive);
        Assert.Equal(55u, instance.CurrentHp);
    }

    [Fact]
    public void TryRespawn_FiresOnlyOnce()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        instance.ApplyDamage(55);
        instance.TryScheduleRespawn(1000);

        Assert.True(instance.TryRespawn(1000));
        Assert.False(instance.TryRespawn(1000)); // Already alive; nothing left to respawn.
    }

    [Fact]
    public void TryRespawn_WhileAlive_Fails()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        Assert.False(instance.TryRespawn(999999));
    }
}
