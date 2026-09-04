using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Net;

// Step 6 cutover: the MapServer-LOCAL half of monster<->player combat, projection-driven. World
// owns identity/position/movement/lifecycle/engagement/chase decisions entirely (per the approved
// Phase 2B authority boundary) - this type NEVER advances monster movement, retargets chase,
// decides Unlock/Chase, or mutates any engagement state; it only reads the shared
// MonsterFeedProjection (World-authoritative) plus its own local MonsterCombatStateStore
// (MapServer-authoritative cadence/HP), and decides purely WHEN a locally-cadenced attack fires.
//
// For every monster the projection reports as Alive + InAttackRange + with a current EngagedTarget:
//   read local NextAttackAt (MonsterCombatStateStore)
//   if due:
//     call World's ValidateMonsterAttackWindowAsync (a plain read-only recheck against CURRENT
//     World state, never a reservation/claim/exactly-once protocol - see that RPC's own doc
//     comment) with the EXACT (MapId, Epoch, ActorId, IncarnationId) life reference and the
//     target's (CharacterId, PresenceId)
//     only Valid -> the existing final session/gameplay revalidation, damage formula, player HP
//     mutation, and Ragexe combat packets proceed; ANY other result -> no HP mutation, no
//     successful-attack packet, and NextAttackAt is left untouched (the local cadence will simply
//     re-evaluate on its own next elapsed interval - see this type's own "do not turn this into a
//     reservation protocol" note below).
//
// Never called for a monster the projection does NOT currently report as InAttackRange with a
// target - this is a deliberate, mechanical enforcement of "World decides Unlock/Chase/InAttackRange,
// MapServer only decides Attack/Wait cadence on top of an already-InAttackRange projection".
internal sealed class MonsterAttackCadenceExecutor(
    MonsterFeedProjectionRegistry projections, MonsterCombatStateStore combatState, IWorldRuntime worldRuntime, TimeProvider timeProvider,
    Func<Task>? beforeFinalAttackRevalidation = null)
{
    private readonly Func<Task> _beforeFinalAttackRevalidation = beforeFinalAttackRevalidation ?? (() => Task.CompletedTask);

    public async Task<MonsterEngagementTickResult> ProcessAsync(IReadOnlyCollection<MapClientSession> sessions, CancellationToken cancellationToken)
    {
        var now = timeProvider.GetUtcNow();
        List<MonsterAttackActionOutcome>? attackActions = null;

        // Only maps with at least one active session are ever polled/projected at all (see
        // MonsterFeedProjectionRegistry's own doc comment) - grouping sessions by map here means
        // this loop only ever visits a projection that genuinely has observers, never a map this
        // MapServer process merely once touched.
        foreach (var mapGroup in sessions.GroupBy(session => session.CurrentMapName, StringComparer.OrdinalIgnoreCase))
        {
            if (!projections.TryGet(mapGroup.Key, out var projection) || projection.CurrentEpoch is not { } epoch) continue;
            var mapSessions = mapGroup.ToArray();

            foreach (var monster in projection.AllInstances)
            {
                if (monster.Lifecycle != WorldMonsterLifecycleState.Alive) continue;
                if (projection.EngagementOf(monster.ActorId) != WorldMonsterEngagementState.InAttackRange) continue;
                if (monster.EngagedTarget is not { } target) continue;

                var key = new MonsterCombatKey(mapGroup.Key, epoch, monster.ActorId, monster.IncarnationId);
                if (!combatState.TryGet(key, out var combat)) continue; // Not (yet) registered locally - the projection's own reconciliation will register it before this can matter.
                if (combat.NextAttackAt is { } nextAttackAt && now < nextAttackAt) continue; // Not due yet.

                var life = new WorldMonsterLifeReference(mapGroup.Key, epoch, monster.ActorId, monster.IncarnationId);
                var action = await TryApplyAttackAsync(mapSessions, monster, key, life, target, now, cancellationToken);
                if (action is { } outcome)
                {
                    (attackActions ??= []).Add(outcome);
                    MapLogger.Info($"[iRO MAP DEBUG] Mob attack accepted mobActorId={monster.ActorId} targetCharacterId={target.CharacterId} damage={outcome.Damage} isMiss={outcome.IsMiss} hpChanged={outcome.HpChanged} outcome=AttackAccepted");
                }
            }
        }

        return attackActions is null ? MonsterEngagementTickResult.Empty : new MonsterEngagementTickResult(attackActions);
    }

    // Calls World's ValidateMonsterAttackWindowAsync - a plain, read-only, idempotent query against
    // CURRENT authoritative state at the moment of the call (NEVER a reservation/claim - see that
    // RPC's own doc comment) - immediately before actually committing player HP damage. Only
    // WorldMonsterAttackWindowStatus.Valid proceeds to the existing final session/gameplay
    // revalidation + damage formula + HP mutation + Ragexe packets; any other result means no HP
    // mutation and no successful-attack packet at all - World's own next feed tick naturally
    // reports whatever chase/unlock transition follows (this executor never drives that itself).
    private async Task<MonsterAttackActionOutcome?> TryApplyAttackAsync(
        IReadOnlyCollection<MapClientSession> sessions, WorldMonsterInstance monster, MonsterCombatKey key, WorldMonsterLifeReference life,
        WorldPlayerTargetReference target, DateTimeOffset now, CancellationToken cancellationToken)
    {
        MapClientSession? targetSession = null;
        foreach (var candidate in sessions)
        {
            if (candidate.CharacterId == target.CharacterId) { targetSession = candidate; break; }
        }
        if (targetSession is null) return null; // Disconnected/moved off this map since the projection last observed it - World's own feed resolves the resulting Unlock.

        await _beforeFinalAttackRevalidation();

        var windowResult = await worldRuntime.ValidateMonsterAttackWindowAsync(
            new WorldMonsterAttackWindowQuery(life, target.CharacterId, target.PresenceId), cancellationToken);
        if (windowResult.Status != WorldMonsterAttackWindowStatus.Valid)
        {
            MapLogger.Info($"[iRO MAP DEBUG] Mob attack rejected mobActorId={monster.ActorId} targetCharacterId={target.CharacterId} reason={windowResult.Status}");
            return null;
        }

        // Final MapServer-local session/gameplay revalidation - the part of the old TOCTOU check
        // that is genuinely MapServer-local (the attacking player's own live session/gameplay
        // state), preserved unchanged around HP mutation.
        var freshSnapshot = await TrySnapshotAsync(targetSession, cancellationToken);
        if (freshSnapshot is not { IsAlive: true } combatSnapshot) return null;

        var staticMob = Athena.Net.MapServer.Generated.GameData.Mobs.GeneratedMobRegistry.Get(monster.MobId);
        var result = MobBasicAttackCalculator.Calculate(staticMob, combatSnapshot);
        combatState.ScheduleNextAttack(key, now.AddMilliseconds(staticMob.AttackDelay));

        (uint HpAfter, bool HpChanged)? applied;
        try
        {
            applied = await targetSession.ApplyIncomingMobBasicAttackAsync(result.Damage, cancellationToken);
        }
        catch (IOException)
        {
            applied = null; // Client disconnected mid-attack application; the orchestrator's own session cleanup removes it.
        }
        if (applied is not { } hpOutcome) return null; // MutateAsync rejected a stale row - do not emit a successful attack result.

        // srcSpeed/dstSpeed: pinned clif_damage's own attacker-amotion/target-dmotion pair - the
        // mob's own DamageMotion is NEVER used here (that field serves the opposite direction).
        var srcSpeed = (uint)staticMob.AttackMotion;
        var dstSpeed = (uint)PlayerDamageMotionCalculator.Calculate(combatSnapshot.Agility);
        return new MonsterAttackActionOutcome(monster.ActorId, key.MapId, monster.X, monster.Y, target.CharacterId, result.Damage, result.IsMiss, srcSpeed, dstSpeed, hpOutcome.HpAfter, hpOutcome.HpChanged);
    }

    private static async Task<PlayerCombatSnapshot?> TrySnapshotAsync(MapClientSession session, CancellationToken cancellationToken)
    {
        try { return await session.TryGetCombatSnapshotAsync(cancellationToken); }
        catch (IOException) { return null; }
    }
}
