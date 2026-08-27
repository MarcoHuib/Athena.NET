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

    private static (bool Success, MobPosition Position) Fixed(ushort x, ushort y) => (true, new MobPosition(x, y));

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

        Assert.False(instance.TryRespawn(500, () => Fixed(0, 0)));
        Assert.False(instance.IsAlive);
    }

    [Fact]
    public void TryRespawn_AtOrAfterDueTime_RestoresAliveAndFullHp()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        instance.ApplyDamage(55);
        instance.TryScheduleRespawn(1000);

        Assert.True(instance.TryRespawn(1000, () => Fixed(0, 0)));
        Assert.True(instance.IsAlive);
        Assert.Equal(55u, instance.CurrentHp);
    }

    [Fact]
    public void TryRespawn_FiresOnlyOnce()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        instance.ApplyDamage(55);
        instance.TryScheduleRespawn(1000);

        Assert.True(instance.TryRespawn(1000, () => Fixed(0, 0)));
        Assert.False(instance.TryRespawn(1000, () => Fixed(0, 0))); // Already alive; nothing left to respawn.
    }

    [Fact]
    public void TryRespawn_WhileAlive_Fails()
    {
        var instance = new MobInstance(1, MakeSpawn(), 0, 0);
        Assert.False(instance.TryRespawn(999999, () => Fixed(0, 0)));
    }

    [Fact]
    public void TryRespawn_AppliesTheSelectedPosition()
    {
        var instance = new MobInstance(1, MakeSpawn(), 10, 10);
        instance.ApplyDamage(55);
        instance.TryScheduleRespawn(1000);

        Assert.True(instance.TryRespawn(1000, () => Fixed(77, 88)));

        var position = instance.GetPosition();
        Assert.Equal((ushort)77, position.X);
        Assert.Equal((ushort)88, position.Y);
    }

    [Fact]
    public void TryRespawn_BeforeDueTime_DoesNotInvokeSelector()
    {
        var instance = new MobInstance(1, MakeSpawn(), 10, 10);
        instance.ApplyDamage(55);
        instance.TryScheduleRespawn(1000);

        var invoked = false;
        instance.TryRespawn(500, () => { invoked = true; return Fixed(0, 0); });

        Assert.False(invoked);
        var position = instance.GetPosition();
        Assert.Equal((ushort)10, position.X);
        Assert.Equal((ushort)10, position.Y);
    }

    [Fact]
    public void TryRespawn_SelectorReportsTemporaryFailure_LeavesInstanceDeadAndRespawnScheduled()
    {
        // Matches pinned mob_spawn's own "search failed, reschedule via mob_delayspawn, try again
        // later" behavior (mob.cpp:1152-1159) - a false selector result must not force an
        // arbitrary/placeholder position, and must not clear the scheduled respawn so a later
        // ProcessDueRespawns sweep can simply try again.
        var instance = new MobInstance(1, MakeSpawn(), 10, 10);
        instance.ApplyDamage(55);
        instance.TryScheduleRespawn(1000);

        var result = instance.TryRespawn(1000, () => (false, default));

        Assert.False(result);
        Assert.False(instance.IsAlive);
        var position = instance.GetPosition();
        Assert.Equal((ushort)10, position.X);
        Assert.Equal((ushort)10, position.Y);

        // The next sweep can still succeed once the selector starts returning a real cell.
        Assert.True(instance.TryRespawn(1000, () => Fixed(20, 30)));
        Assert.True(instance.IsAlive);
    }

    [Fact]
    public void GetPosition_ReadsXAndYTogetherAsOnePair()
    {
        var instance = new MobInstance(1, MakeSpawn(), 12, 34);
        var position = instance.GetPosition();

        Assert.Equal((ushort)12, position.X);
        Assert.Equal((ushort)34, position.Y);
    }

    [Fact]
    public void CreatePending_IsNotAlive_AndHasZeroHp()
    {
        var instance = MobInstance.CreatePending(1, MakeSpawn(), dueTimestamp: 1000);

        Assert.False(instance.IsAlive);
        Assert.Equal(0u, instance.CurrentHp);
    }

    [Fact]
    public void CreatePending_RespawnsThroughTheNormalRetryPath()
    {
        var instance = MobInstance.CreatePending(1, MakeSpawn(), dueTimestamp: 1000);

        Assert.True(instance.TryRespawn(1000, () => Fixed(15, 25)));

        Assert.True(instance.IsAlive);
        Assert.Equal(55u, instance.CurrentHp);
        var position = instance.GetPosition();
        Assert.Equal((ushort)15, position.X);
        Assert.Equal((ushort)25, position.Y);
    }

    [Fact]
    public void CreatePending_BeforeDueTime_DoesNotRespawn()
    {
        var instance = MobInstance.CreatePending(1, MakeSpawn(), dueTimestamp: 1000);

        Assert.False(instance.TryRespawn(500, () => Fixed(15, 25)));
        Assert.False(instance.IsAlive);
    }
}
