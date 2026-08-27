using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Logging;
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
// It never sends a wire packet itself either - ProcessAsync's return value
// (MonsterEngagementTickResult) reports every world-observable change so MapTcpServer (the actual
// fan-out owner) can apply the same per-session visibility gating it already uses for
// MonsterRuntime.ProcessTick's own return value.
//
// "Session disappeared / wrong map / player dead" is not a special error path here - it is exactly
// what a null/dead/wrong-map PlayerCombatSnapshot already means to MonsterEngagementDomain.
// Evaluate (see that method's own doc comment), which resolves it to an ordinary Unlock decision -
// the same source-backed lifecycle a live but out-of-map-sync target would hit.
// `beforeFinalAttackRevalidation`: a genuine orchestration seam, not test-only plumbing - it fires
// immediately before TryApplyAttackAsync takes its second (execution-time) PlayerCombatSnapshot,
// i.e. exactly at the point pinned unit_attack_timer_sub itself re-validates range/target
// immediately before committing a hit. Production always passes the default no-op. Tests use it to
// drive an EXISTING real session mechanism (e.g. a genuine movement packet round-trip) at the exact
// instant a concurrent state change needs to land for a TOCTOU regression, rather than adding a
// test-only mutation method to MapClientSession itself.
internal sealed class MonsterEngagementTickProcessor(
    MonsterRegistry monsters, IMapCollisionProvider collisionProvider, IMovementPathProvider movementPathProvider, TimeProvider timeProvider,
    Func<Task>? beforeFinalAttackRevalidation = null)
{
    private readonly Func<Task> _beforeFinalAttackRevalidation = beforeFinalAttackRevalidation ?? (() => Task.CompletedTask);


    public async Task<MonsterEngagementTickResult> ProcessAsync(IReadOnlyCollection<MapClientSession> sessions, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        List<MonsterMovementChange>? movementChanges = null;
        List<MonsterAttackActionOutcome>? attackActions = null;

        foreach (var mob in monsters.AllInstances)
        {
            if (!mob.IsAlive) continue;
            var engagement = mob.Engagement;
            if (engagement.TargetAccountId is not { } targetAccountId) continue;

            // Requirement 5 (mid-walk combat retarget): consume any pending
            // CharacterMovementState retarget at the cell boundary it was actually reached,
            // BEFORE this tick's Evaluate decision - an already-random-walking mob that was just
            // attacked must finish its current in-flight cell, then have its NEXT cell boundary
            // resolve toward the attacker, never continue toward its stale idle-walk destination.
            // AdvanceMovementForCombat performs the whole consume-then-recompute-then-install
            // sequence atomically under MobInstance's own lock (see that method's own doc comment)
            // - this is the SAME lifecycle MapClientSession.ProcessDueMovementAsync already applies
            // for player movement, ported here via a plain delegate rather than giving MobInstance
            // an IMovementPathProvider dependency.
            var mobPositionBeforeAdvance = mob.GetPosition();
            var (crossed, retargetApplied) = mob.AdvanceMovementForCombat(
                now,
                (fromX, fromY, toX, toY) => collisionProvider.TryGetMap(mob.Map, out _)
                    ? movementPathProvider.ComputePath(mob.Map, fromX, fromY, toX, toY)
                    : [],
                mob.Spawn.Mob.WalkSpeed);
            if (retargetApplied)
            {
                // The replacement path's own first leg is exactly the "movement changed, tell
                // observers" event a fresh chase-start already produces - reuse WalkStarted's own
                // wire mapping (MapClientSession.NotifyMonsterMovedAsync sends the walk-entry
                // packet for it) rather than inventing a third packet shape for what is, from the
                // client's perspective, indistinguishable from any other newly (re)started walk.
                (movementChanges ??= []).Add(new MonsterMovementChange(mob, MonsterMovementChangeKind.WalkStarted));
                var reachedCell = mob.GetPosition();
                MapLogger.Info($"[iRO MAP DEBUG] Mob chase retarget applied mobActorId={mob.ActorId} previousCell=({mobPositionBeforeAdvance.X},{mobPositionBeforeAdvance.Y}) reachedCell=({reachedCell.X},{reachedCell.Y}) newDestination=({mob.MovementDestination.X},{mob.MovementDestination.Y}) wire=0x09FD");
            }
            else if (crossed.Count > 0)
            {
                var kind = mob.IsWalking ? MonsterMovementChangeKind.CellCrossed : MonsterMovementChangeKind.WalkFinished;
                (movementChanges ??= []).Add(new MonsterMovementChange(mob, kind));
            }

            var snapshot = await TryFindSnapshotAsync(sessions, targetAccountId, cancellationToken);
            var decision = MonsterEngagementDomain.Evaluate(mob, snapshot, now);
            LogDecision(mob, snapshot, decision);
            switch (decision)
            {
                case MonsterEngagementDecision.Unlock:
                    mob.TryUnlockTarget(now, RandomJitterMs);
                    break;

                case MonsterEngagementDecision.Chase chase:
                    if (ApplyChaseDecision(mob, chase, now)) (movementChanges ??= []).Add(new MonsterMovementChange(mob, MonsterMovementChangeKind.WalkStarted));
                    break;

                case MonsterEngagementDecision.Attack:
                    var interrupted = mob.IsWalking;
                    var mobPositionAtInterrupt = mob.GetPosition();
                    mob.StopChase();
                    mob.EnterAttackState();
                    // Requirement 6: a walking mob stopping to attack is a COMBAT interruption
                    // (pinned USW_FIXPOS), not an ordinary WalkFinished - only reported when the
                    // mob was actually still walking at this instant (an already-stationary mob
                    // that was already in range has nothing to fix-position).
                    if (interrupted)
                    {
                        (movementChanges ??= []).Add(new MonsterMovementChange(mob, MonsterMovementChangeKind.ChaseInterrupted));
                        MapLogger.Info($"[iRO MAP DEBUG] Mob chase interrupted mobActorId={mob.ActorId} mobPosition=({mobPositionAtInterrupt.X},{mobPositionAtInterrupt.Y}) wire=0x0088");
                    }

                    var action = await TryApplyAttackAsync(sessions, mob, targetAccountId, now, cancellationToken);
                    if (action is { } outcome)
                    {
                        (attackActions ??= []).Add(outcome);
                        MapLogger.Info($"[iRO MAP DEBUG] Mob attack accepted mobActorId={mob.ActorId} targetAccountId={targetAccountId} damage={outcome.Damage} isMiss={outcome.IsMiss} hpChanged={outcome.HpChanged} nextAttackAt={mob.NextAttackAt:O} wire=0x08C8");
                    }
                    else
                    {
                        MapLogger.Info($"[iRO MAP DEBUG] Mob attack rejected mobActorId={mob.ActorId} targetAccountId={targetAccountId} reason=revalidation-failed");
                    }
                    break;

                case MonsterEngagementDecision.Wait:
                    break;
            }
        }

        return movementChanges is null && attackActions is null
            ? MonsterEngagementTickResult.Empty
            : new MonsterEngagementTickResult(movementChanges ?? [], attackActions ?? []);
    }

    private static long RandomJitterMs() => System.Random.Shared.Next(0, 1000);

    // Section 16: logs the decision itself only for the transitions worth diagnosing live -
    // Unlock/Attack always (rare, state-changing events for the whole engagement), Chase only
    // (never Wait, which recurs every 100ms tick while an attack cooldown is pending and would
    // otherwise spam the log for an engaged-but-waiting mob doing nothing new).
    private static void LogDecision(MobInstance mob, PlayerCombatSnapshot? snapshot, MonsterEngagementDecision decision)
    {
        if (decision is MonsterEngagementDecision.Wait) return;
        var mobPosition = mob.GetPosition();
        var targetText = snapshot is { } s ? $"({s.X},{s.Y})" : "null";
        var distanceText = snapshot is { } s2 ? Math.Max(Math.Abs(s2.X - mobPosition.X), Math.Abs(s2.Y - mobPosition.Y)).ToString() : "n/a";
        var effectiveRange = mob.Spawn.Mob.AttackRange + (snapshot?.IsWalking == true ? 1 : 0);
        MapLogger.Info($"[iRO MAP DEBUG] Mob engagement decision mobActorId={mob.ActorId} decision={decision.GetType().Name} mobPosition=({mobPosition.X},{mobPosition.Y}) playerPosition={targetText} distance={distanceText} effectiveRange={effectiveRange}");
    }

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
    // (MonsterRuntime.ProcessIdleMovement's own HasActiveTarget guard). Returns true only when a
    // FRESH walk was started here (TryStartChase) - a mid-walk retarget instead goes through
    // AdvanceMovementForCombat above and reports its own WalkStarted-shaped outcome there, so this
    // method must not double-report it.
    private bool ApplyChaseDecision(MobInstance mob, MonsterEngagementDecision.Chase chase, DateTimeOffset now)
    {
        if (mob.TryRetargetChase(chase.DestinationX, chase.DestinationY))
        {
            mob.EnterChaseState();
            MapLogger.Info($"[iRO MAP DEBUG] Mob chase retarget requested mobActorId={mob.ActorId} currentCell=({mob.GetPosition().X},{mob.GetPosition().Y}) requestedDestination=({chase.DestinationX},{chase.DestinationY})");
            return false; // Deferred to the next cell boundary - AdvanceMovementForCombat reports it when applied.
        }

        if (!collisionProvider.TryGetMap(mob.Map, out _)) return false;
        var position = mob.GetPosition();
        var path = movementPathProvider.ComputePath(mob.Map, position.X, position.Y, chase.DestinationX, chase.DestinationY);
        if (path.Count < 2) return false; // No real path (target unreachable this tick) - matches unit_walktobl's own silent-failure contract.
        if (!mob.TryStartChase(path, mob.Spawn.Mob.WalkSpeed, now)) return false;
        mob.EnterChaseState();
        MapLogger.Info($"[iRO MAP DEBUG] Mob chase started mobActorId={mob.ActorId} from=({position.X},{position.Y}) destination=({chase.DestinationX},{chase.DestinationY}) wire=0x09FD");
        return true;
    }

    // Requirement 7 (TOCTOU closure): re-validates the FULL current-position attack decision
    // immediately before committing damage, using a brand-new snapshot taken at this exact
    // instant - never trusting the snapshot Evaluate used moments earlier in this same
    // ProcessAsync call, which a concurrent player move/teleport/death could have already made
    // stale. If the target is no longer attackable under a fresh MonsterEngagementDomain.Evaluate
    // (Unlock or Chase now, not Attack) - e.g. the player stepped out of range or disconnected in
    // between - no HP mutation and no attack outcome are produced; the mob is transitioned back to
    // Chase/Unlock according to that SAME re-evaluation, matching the source-backed unlock/chase
    // lifecycle rather than silently doing nothing.
    private async Task<MonsterAttackActionOutcome?> TryApplyAttackAsync(IReadOnlyCollection<MapClientSession> sessions, MobInstance mob, uint targetAccountId, DateTimeOffset now, CancellationToken cancellationToken)
    {
        MapClientSession? targetSession = null;
        foreach (var candidate in sessions)
        {
            if (candidate.AccountId == targetAccountId) { targetSession = candidate; break; }
        }
        if (targetSession is null) return null; // Disconnected between Evaluate and here - next tick's snapshot resolves to null and unlocks normally.

        await _beforeFinalAttackRevalidation();
        var freshSnapshot = await TrySnapshotAsync(targetSession, cancellationToken);
        var reEvaluated = MonsterEngagementDomain.Evaluate(mob, freshSnapshot, now);
        if (reEvaluated is not MonsterEngagementDecision.Attack)
        {
            // The target moved/died/disconnected between the tick's own Evaluate and this
            // execution instant - apply whatever the FRESH decision now calls for (never silently
            // drop it) so the mob's engagement state stays source-consistent tick to tick.
            switch (reEvaluated)
            {
                case MonsterEngagementDecision.Unlock: mob.TryUnlockTarget(now, RandomJitterMs); break;
                case MonsterEngagementDecision.Chase: mob.EnterChaseState(); break; // Path recompute deferred to next tick's own Chase handling.
            }
            return null;
        }

        var combatSnapshot = freshSnapshot!.Value; // Attack was re-confirmed, so freshSnapshot is necessarily non-null/alive/same-map.
        var result = MobBasicAttackCalculator.Calculate(mob.Spawn.Mob, combatSnapshot);
        mob.ScheduleNextAttack(now.AddMilliseconds(mob.Spawn.Mob.AttackDelay));

        (uint HpAfter, bool HpChanged)? applied;
        try
        {
            applied = await targetSession.ApplyIncomingMobBasicAttackAsync(result.Damage, cancellationToken);
        }
        catch (IOException)
        {
            applied = null; // Client disconnected mid-attack application; the orchestrator's own session cleanup removes it.
        }
        if (applied is not { } hpOutcome) return null; // MutateAsync rejected a stale row - do not emit a successful attack result (requirement 7).

        var position = mob.GetPosition();
        // srcSpeed/dstSpeed: pinned clif_damage's own attacker-amotion/target-dmotion pair
        // (clif.cpp:5271-5275; the battle_config.synchronize_damage transform is a no-op by
        // default - see MobDefinition.AttackMotion's own doc comment for the full trace). The
        // mob's own DamageMotion is NEVER used here - that field serves the OPPOSITE direction
        // (player attacks THIS mob), see that field's own doc comment.
        var srcSpeed = (uint)mob.Spawn.Mob.AttackMotion;
        var dstSpeed = (uint)PlayerDamageMotionCalculator.Calculate(combatSnapshot.Agility);
        return new MonsterAttackActionOutcome(mob.ActorId, mob.Map, position.X, position.Y, targetAccountId, result.Damage, result.IsMiss, srcSpeed, dstSpeed, hpOutcome.HpAfter, hpOutcome.HpChanged);
    }

    private static async Task<PlayerCombatSnapshot?> TrySnapshotAsync(MapClientSession session, CancellationToken cancellationToken)
    {
        try { return await session.TryGetCombatSnapshotAsync(cancellationToken); }
        catch (IOException) { return null; }
    }
}
