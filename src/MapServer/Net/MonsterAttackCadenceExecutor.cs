using System.Linq;
using Athena.Net.MapServer.Gameplay.Rules.Renewal;
using Athena.Net.MapServer.Logging;
using Athena.Net.MapServer.World;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.Net;

// Step 6 cutover: the MapServer-LOCAL half of monster<->player combat, projection-driven. World
// owns identity/position/movement/lifecycle/engagement/chase decisions entirely (per the approved
// Phase 2B authority boundary) - this type NEVER advances monster movement, retargets chase,
// decides Unlock/Chase, or mutates any engagement state; it only reads the shared
// MonsterFeedProjection (World-authoritative) plus its own local MonsterAttackCadenceStore
// (MapServer-authoritative cadence/HP), and decides purely WHEN a locally-cadenced attack fires.
//
// For every monster the projection reports as Alive + InAttackRange + with a current EngagedTarget:
//   read local NextAttackAt (MonsterAttackCadenceStore)
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
    MonsterFeedProjectionRegistry projections, MonsterAttackCadenceStore combatState, IWorldRuntime worldRuntime, TimeProvider timeProvider,
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
        // MapServer process merely once touched. Filtered to IsWorldMapEligible sessions first,
        // same as MapTcpServer.ProcessOneMonsterTickAsync's own identical filter - a session whose
        // CurrentMapName is still empty (accepted but not yet authenticated/World-registered) must
        // never be grouped by map id here either (see IsWorldMapEligible's own doc comment).
        foreach (var mapGroup in sessions.Where(session => session.IsWorldMapEligible).GroupBy(session => session.CurrentMapName, StringComparer.OrdinalIgnoreCase))
        {
            if (!projections.TryGet(mapGroup.Key, out var projection) || !projection.SnapshotForCadence(out var epoch, out var instances)) continue;
            var mapSessions = mapGroup.ToArray();

            foreach (var (monster, engagement) in instances)
            {
                if (monster.Lifecycle != WorldMonsterLifecycleState.Alive) continue;
                if (engagement != WorldMonsterEngagementState.InAttackRange) continue;
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
        // Matches BOTH CharacterId AND PresenceId - never CharacterId alone (see
        // WorldPlayerTargetReference's own doc comment for why: a reconnect race can leave an OLD
        // session disconnecting while a NEW session with the SAME CharacterId but a genuinely
        // different PresenceId is already active on this map). Selecting purely by CharacterId here
        // could mutate player HP onto the wrong/replacement local session even though World's own
        // ValidateMonsterAttackWindowAsync recheck below is unchanged/mandatory - this is an
        // ADDITIONAL local guard, not a replacement for that World-side recheck.
        MapClientSession? targetSession = null;
        foreach (var candidate in sessions)
        {
            if (candidate.CharacterId == target.CharacterId && candidate.PresenceId == target.PresenceId) { targetSession = candidate; break; }
        }
        if (targetSession is null) return null; // Disconnected/moved off this map, or reconnected under a different PresenceId, since the projection last observed it - World's own feed resolves the resulting Unlock.

        await _beforeFinalAttackRevalidation();

        // DEBUG-LOG-ONLY correlation label so the generic gameplay-state mutation / CharServer round
        // trip lines say which monster attack they belong to (see CombatTiming.Context).
        CombatTiming.SetContext($"mob-attack:actorId={monster.ActorId}");

        // DEBUG-LOG-ONLY attack-path timing (never read by any decision): the three awaited steps that
        // run serially inside the shared monster tick for every attack that actually lands. Used to
        // attribute the extra tick time observed right after a monster attack.
        var validateStartedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        var windowResult = await worldRuntime.ValidateMonsterAttackWindowAsync(
            new WorldMonsterAttackWindowQuery(life, target.CharacterId, target.PresenceId), cancellationToken);
        var validateEndedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (windowResult.Status != WorldMonsterAttackWindowStatus.Valid)
        {
            MapLogger.Info($"[iRO MAP DEBUG] Mob attack rejected mobActorId={monster.ActorId} targetCharacterId={target.CharacterId} reason={windowResult.Status}");
            return null;
        }

        // Final MapServer-local session/gameplay revalidation - the part of the old TOCTOU check
        // that is genuinely MapServer-local (the attacking player's own live session/gameplay
        // state), preserved unchanged around HP mutation.
        var freshSnapshot = await TrySnapshotAsync(targetSession, cancellationToken);
        var snapshotEndedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (freshSnapshot is not { IsAlive: true } combatSnapshot) return null;

        var staticMob = Athena.Net.MapServer.Generated.GameData.Mobs.GeneratedMobRegistry.Get(monster.MobId);
        var result = MobBasicAttackCalculator.Calculate(staticMob, combatSnapshot);

        (uint HpAfter, bool HpChanged)? applied;
        try
        {
            applied = await targetSession.ApplyIncomingMobBasicAttackAsync(result.Damage, cancellationToken);
        }
        catch (IOException)
        {
            applied = null; // Client disconnected mid-attack application; the orchestrator's own session cleanup removes it.
        }
        var applyEndedAt = System.Diagnostics.Stopwatch.GetTimestamp();
        if (applied is not { } hpOutcome) return null; // MutateAsync rejected a stale row - do not emit a successful attack result, and do not consume a cadence slot for an attack that never actually landed.

        MapLogger.Info(
            $"[iRO MAP DEBUG] Mob attack timing mobActorId={monster.ActorId} " +
            $"validateWindowMs={System.Diagnostics.Stopwatch.GetElapsedTime(validateStartedAt, validateEndedAt).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"snapshotMs={System.Diagnostics.Stopwatch.GetElapsedTime(validateEndedAt, snapshotEndedAt).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"applyHpMutationMs={System.Diagnostics.Stopwatch.GetElapsedTime(snapshotEndedAt, applyEndedAt).TotalMilliseconds.ToString("F1", System.Globalization.CultureInfo.InvariantCulture)} " +
            $"damage={result.Damage} hpChanged={hpOutcome.HpChanged}");

        // Item 6 of the Step 6 correctness-hardening pass: NextAttackAt is advanced ONLY after the
        // local player HP mutation actually succeeded above - a persistence rejection/disconnect
        // (the `applied is not {}` branch above, already returned) must never consume a successful
        // attack cadence slot. Moving this call to AFTER the mutation (it used to run BEFORE,
        // unconditionally) closes that gap.
        combatState.ScheduleNextAttack(key, now.AddMilliseconds(staticMob.AttackDelay));

        // Item 14 §6 "Monster -> player": publish the already-resolved local attack for cross-replica
        // projection only - this is fire-and-forget from the CALLER's own responsiveness perspective
        // (the victim's own local gateway already has everything it needs to write its own 0x08C8/HP
        // sync right now, below, unaffected by this call's outcome). A fresh ActionId per call is
        // correct here (this is the FIRST publish attempt for this exact already-committed local
        // attack, never a retry) - World's own AlreadyPublished ledger exists for a caller-side
        // bounded retry of the SAME ActionId, which this executor does not currently perform (a
        // transient failure here simply means other replicas miss this one action's cross-replica
        // echo; the victim's own local wire projection below is entirely unaffected).
        try
        {
            await worldRuntime.PublishMonsterAttackActionAsync(
                new WorldMonsterAttackActionCommand(life, target.CharacterId, target.PresenceId, Guid.NewGuid(),
                    result.Damage, SourceAttackMotion: (uint)staticMob.AttackMotion, TargetDamageMotion: (uint)PlayerDamageMotionCalculator.Calculate(combatSnapshot.Agility),
                    Lethal: hpOutcome.HpAfter == 0),
                cancellationToken);
        }
        catch (IOException) { /* Transient World RPC failure - other replicas simply miss this one action's echo; never blocks the victim's own local projection below. */ }
        catch (OperationCanceledException) { /* Shutdown. */ }

        // srcSpeed/dstSpeed: pinned clif_damage's own attacker-amotion/target-dmotion pair - the
        // mob's own DamageMotion is NEVER used here (that field serves the opposite direction).
        var srcSpeed = (uint)staticMob.AttackMotion;
        var dstSpeed = (uint)PlayerDamageMotionCalculator.Calculate(combatSnapshot.Agility);
        // Live-reproduction fix: MonsterAttackActionOutcome.VictimAccountId is the wire-facing
        // ZC_NOTIFY_ACT3 dstId (see that record's own doc comment) - the client's runtime
        // actor/GID for the LOCAL PLAYER is its AccountId, never its CharacterId (MapClientSession.
        // ActorId => _accountId; ai/iro-2026-wire.md's own "Map handoff and identity" section;
        // pinned clif_damage's own targetID = dst.id, where a player bl.id is the account id).
        // `target.CharacterId` (WorldPlayerTargetReference) is the correct id for every World-side
        // call/match in this method (ValidateMonsterAttackWindowAsync, the targetSession lookup
        // above) but must NEVER be reused here - targetSession (already resolved above) carries the
        // real wire identity via its own AccountId property.
        return new MonsterAttackActionOutcome(monster.ActorId, key.MapId, monster.X, monster.Y, targetSession.AccountId, result.Damage, result.IsMiss, srcSpeed, dstSpeed, hpOutcome.HpAfter, hpOutcome.HpChanged);
    }

    private static async Task<PlayerCombatSnapshot?> TrySnapshotAsync(MapClientSession session, CancellationToken cancellationToken)
    {
        try { return await session.TryGetCombatSnapshotAsync(cancellationToken); }
        catch (IOException) { return null; }
    }
}
