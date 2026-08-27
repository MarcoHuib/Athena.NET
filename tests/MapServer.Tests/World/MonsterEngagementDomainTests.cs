namespace Athena.Net.MapServer.Tests.World;

using Athena.Net.MapServer.World;

// Pure decision-rule tests for MonsterEngagementDomain.Evaluate - see that type's own doc comment
// for the pinned mob_ai_sub_hard trace this is ported from. No packets, no sessions, no movement
// side effects: only the decision returned for a given MobInstance + PlayerCombatSnapshot pair.
public sealed class MonsterEngagementDomainTests
{
    private static MobDefinition MakeMob(int attackRange = 1) => new(
        Id: 2401, AegisName: "G_PORING", Name: "Poring", Level: 1, MaxHp: 55,
        Attack: 1, Attack2: 1, Defense: 2, MagicDefense: 5,
        Str: 6, Agi: 1, Vit: 1, Int: 0, Dex: 6, Luk: 5,
        AttackRange: attackRange, WalkSpeed: 400, AttackDelay: 1872,
        BaseExp: 0, JobExp: 0, Mode: MobMode.CanMove,
        Source: new("rAthena", "abc", "db/re/mob_db.yml", 1));

    private static MobInstance MakeEngagedInstance(ushort x, ushort y, uint targetAccountId = 500, int attackRange = 1)
    {
        var spawn = new MobSpawnDefinition(MakeMob(attackRange), "int_land01", 40, 5000, new("rAthena", "abc", "x.txt", 1));
        var instance = new MobInstance(1, spawn, x, y);
        instance.TryAcquireTarget(targetAccountId, allowChangeTargetWhileChasing: false);
        return instance;
    }

    private static PlayerCombatSnapshot MakeSnapshot(ushort x, ushort y, string map = "int_land01", bool alive = true, uint accountId = 500) =>
        new(accountId, map, x, y, alive, BaseLevel: 1, Vitality: 1);

    [Fact]
    public void Evaluate_TargetIsNull_Unlocks()
    {
        var mob = MakeEngagedInstance(10, 10);

        var decision = MonsterEngagementDomain.Evaluate(mob, target: null, now: 0);

        Assert.IsType<MonsterEngagementDecision.Unlock>(decision);
    }

    [Fact]
    public void Evaluate_TargetIsDead_Unlocks()
    {
        var mob = MakeEngagedInstance(10, 10);
        var snapshot = MakeSnapshot(10, 10, alive: false);

        var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now: 0);

        Assert.IsType<MonsterEngagementDecision.Unlock>(decision);
    }

    [Fact]
    public void Evaluate_TargetOnADifferentMap_Unlocks()
    {
        var mob = MakeEngagedInstance(10, 10);
        var snapshot = MakeSnapshot(10, 10, map: "iz_int03");

        var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now: 0);

        Assert.IsType<MonsterEngagementDecision.Unlock>(decision);
    }

    [Fact]
    public void Evaluate_TargetOutOfAttackRange_Chases()
    {
        var mob = MakeEngagedInstance(10, 10, attackRange: 1);
        var snapshot = MakeSnapshot(15, 10);

        var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now: 0);

        var chase = Assert.IsType<MonsterEngagementDecision.Chase>(decision);
        Assert.Equal((ushort)15, chase.DestinationX);
        Assert.Equal((ushort)10, chase.DestinationY);
    }

    [Fact]
    public void Evaluate_TargetWithinAttackRange_AndNoAttackScheduledYet_Attacks()
    {
        var mob = MakeEngagedInstance(10, 10, attackRange: 1);
        var snapshot = MakeSnapshot(11, 10); // Chebyshev distance 1.

        var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now: 0);

        Assert.IsType<MonsterEngagementDecision.Attack>(decision);
    }

    [Fact]
    public void Evaluate_TargetWithinAttackRange_ButOwnAttackDelayNotElapsed_Waits()
    {
        var mob = MakeEngagedInstance(10, 10, attackRange: 1);
        mob.ScheduleNextAttack(5000);
        var snapshot = MakeSnapshot(11, 10);

        var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now: 1000);

        Assert.IsType<MonsterEngagementDecision.Wait>(decision);
    }

    [Fact]
    public void Evaluate_TargetWithinAttackRange_AndAttackDelayElapsed_Attacks()
    {
        var mob = MakeEngagedInstance(10, 10, attackRange: 1);
        mob.ScheduleNextAttack(5000);
        var snapshot = MakeSnapshot(11, 10);

        var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now: 5000);

        Assert.IsType<MonsterEngagementDecision.Attack>(decision);
    }

    [Fact]
    public void Evaluate_ExactlyAtAttackRangeBoundary_Attacks()
    {
        var mob = MakeEngagedInstance(10, 10, attackRange: 3);
        var snapshot = MakeSnapshot(13, 10); // Chebyshev distance exactly 3.

        var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now: 0);

        Assert.IsType<MonsterEngagementDecision.Attack>(decision);
    }

    [Fact]
    public void Evaluate_OneCellBeyondAttackRange_Chases()
    {
        var mob = MakeEngagedInstance(10, 10, attackRange: 3);
        var snapshot = MakeSnapshot(14, 10); // Chebyshev distance 4.

        var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now: 0);

        Assert.IsType<MonsterEngagementDecision.Chase>(decision);
    }

    [Fact]
    public void Evaluate_DiagonalDistance_UsesChebyshevNotEuclidean()
    {
        var mob = MakeEngagedInstance(10, 10, attackRange: 2);
        var snapshot = MakeSnapshot(12, 12); // dx=2, dy=2 -> Chebyshev 2, within range 2.

        var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now: 0);

        Assert.IsType<MonsterEngagementDecision.Attack>(decision);
    }
}
