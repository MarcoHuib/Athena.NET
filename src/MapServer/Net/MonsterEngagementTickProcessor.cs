using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Orchestrates ONE tick of monster<->player combat for every currently-engaged MobInstance
// (MobEngagement.TargetAccountId is not null) - resolves TargetAccountId -> live MapClientSession,
// obtains a synchronized PlayerCombatSnapshot, invokes MonsterEngagementDomain's source-backed
// rules, and applies the resulting chase/attack/unlock outcome. Extracted out of MapTcpServer
// (which remains the top-level owner of _sessions and the central monster tick loop, and is this
// processor's only production caller) so this exact orchestration algorithm is directly callable
// and testable WITHOUT starting a real listening TcpListener or reflecting into MapTcpServer's
// private session dictionary - a test constructs this same type, hands it the real sessions it
// already opened over loopback TCP, and calls ProcessAsync exactly like MapTcpServer.
// RunMonsterTickLoopAsync does.
//
// Responsibility boundary (do not blur these): this type contains ORCHESTRATION only - session
// lookup, calling the domain, calling MobInstance's own mutators, calling MapClientSession's own
// narrow snapshot/apply-attack methods. It contains NO combat/AI rules of its own (those live in
// MonsterEngagementDomain), no damage formula (MobBasicAttackCalculator), and no player-state
// ownership (MapClientSession). MonsterRuntime/MobInstance and MonsterEngagementDomain never see a
// MapClientSession or a session lookup - this processor is the ONLY place those two worlds meet.
//
// "Session disappeared / wrong map / player dead" is not a special error path here - it is exactly
// what a null/dead/wrong-map PlayerCombatSnapshot already means to MonsterEngagementDomain.
// Evaluate (see that method's own doc comment), which resolves it to an ordinary Unlock decision -
// the same source-backed lifecycle a live but out-of-map-sync target would hit.
internal sealed class MonsterEngagementTickProcessor(MonsterRegistry monsters, IMapCollisionProvider collisionProvider, IMovementPathProvider movementPathProvider, TimeProvider timeProvider)
{
    public async Task ProcessAsync(IReadOnlyCollection<MapClientSession> sessions, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        foreach (var mob in monsters.AllInstances)
        {
            if (!mob.IsAlive) continue;
            var engagement = mob.Engagement;
            if (engagement.TargetAccountId is not { } targetAccountId) continue;

            var snapshot = await TryFindSnapshotAsync(sessions, targetAccountId, cancellationToken);
            var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now.UtcTicks);
            switch (decision)
            {
                case MonsterEngagementDecision.Unlock:
                    mob.TryUnlockTarget(now.UtcTicks, RandomJitterMs);
                    break;

                case MonsterEngagementDecision.Chase chase:
                    ApplyChaseDecision(mob, chase, now);
                    break;

                case MonsterEngagementDecision.Attack:
                    await ApplyAttackDecisionAsync(sessions, mob, targetAccountId, now, cancellationToken);
                    break;

                case MonsterEngagementDecision.Wait:
                    break;
            }
        }
    }

    private static long RandomJitterMs() => System.Random.Shared.Next(0, 1000);

    private static async Task<PlayerCombatSnapshot?> TryFindSnapshotAsync(IReadOnlyCollection<MapClientSession> sessions, uint targetAccountId, CancellationToken cancellationToken)
    {
        foreach (var candidate in sessions)
        {
            if (candidate.AccountId != targetAccountId) continue;
            return await TrySnapshotAsync(candidate, cancellationToken);
        }
        return null;
    }

    // Pinned mob_ai_sub_hard's own out-of-range branch (mob.cpp:2213's unit_walktobl) - reuses the
    // SAME collision-backed path provider MonsterRuntime's idle-walk AI already uses (one
    // pathfinding foundation, per MapServerWorld.Build's own doc comment), but this is NOT idle
    // movement: it is a server-owned combat chase, so it goes through MobInstance's own
    // TryRetargetChase/TryStartChase (pinned unit_walktobl's mid-walk-retarget-vs-fresh-walk split,
    // unit.cpp:950-998 - see MobInstance.TryRetargetChase's own doc comment) rather than
    // MonsterRuntime's idle-walk scheduler, which this engaged mob is no longer eligible for
    // (MonsterRuntime.ProcessIdleMovement's own HasActiveTarget guard).
    private void ApplyChaseDecision(MobInstance mob, MonsterEngagementDecision.Chase chase, DateTimeOffset now)
    {
        if (mob.TryRetargetChase(chase.DestinationX, chase.DestinationY))
        {
            mob.EnterChaseState();
            return;
        }

        if (!collisionProvider.TryGetMap(mob.Map, out _)) return;
        var position = mob.GetPosition();
        var path = movementPathProvider.ComputePath(mob.Map, position.X, position.Y, chase.DestinationX, chase.DestinationY);
        if (path.Count < 2) return; // No real path (target unreachable this tick) - matches unit_walktobl's own silent-failure contract.
        if (mob.TryStartChase(path, mob.Spawn.Mob.WalkSpeed, now)) mob.EnterChaseState();
    }

    // Pinned mob_ai_sub_hard's own in-range branch (mob.cpp:2141-2166's unit_attack) - stops any
    // remaining chase movement, performs one authoritative hit via MobBasicAttackCalculator (the
    // mob-attacker mirror of the player-attacker WeaponAttackCalculator this project already uses
    // for player->mob), applies it through the target session's own narrow
    // ApplyIncomingMobBasicAttackAsync gate, and re-arms this mob's own attack-delay timer
    // (unit_attack_timer_sub's own ud->attacktimer re-arm, unit.cpp:3337) using the mob_db
    // AttackDelay field - mirroring AttackDelayCalculator's role on the player-attack side.
    private static async Task ApplyAttackDecisionAsync(IReadOnlyCollection<MapClientSession> sessions, MobInstance mob, uint targetAccountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        mob.StopChase();
        mob.EnterAttackState();

        MapClientSession? targetSession = null;
        foreach (var candidate in sessions)
        {
            if (candidate.AccountId == targetAccountId) { targetSession = candidate; break; }
        }
        if (targetSession is null) return; // Disconnected between Evaluate and here - next tick's snapshot resolves to null and unlocks normally.

        var snapshot = await TrySnapshotAsync(targetSession, cancellationToken);
        if (snapshot is not { IsAlive: true } combatSnapshot || !string.Equals(combatSnapshot.Map, mob.Map, StringComparison.OrdinalIgnoreCase)) return;

        var result = MobBasicAttackCalculator.Calculate(mob.Spawn.Mob, combatSnapshot);
        mob.ScheduleNextAttack(now.UtcTicks + mob.Spawn.Mob.AttackDelay);

        try
        {
            await targetSession.ApplyIncomingMobBasicAttackAsync(mob.ActorId, mob.Spawn.Mob.AegisName, result.Damage, result.IsMiss, cancellationToken);
        }
        catch (IOException)
        {
            // Client disconnected mid-attack application; the orchestrator's own session cleanup removes it.
        }
    }

    private static async Task<PlayerCombatSnapshot?> TrySnapshotAsync(MapClientSession session, CancellationToken cancellationToken)
    {
        try { return await session.TryGetCombatSnapshotAsync(cancellationToken); }
        catch (IOException) { return null; }
    }
}
