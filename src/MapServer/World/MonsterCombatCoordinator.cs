using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.MapServer.Logging;

namespace Athena.Net.MapServer.World;

// Outcome of one basic-attack attempt against a live MobInstance.
public readonly record struct MonsterAttackOutcome(
    bool Accepted,
    uint HpBefore,
    uint HpAfter,
    bool IsMiss,
    bool KilledByThisHit,
    IReadOnlyList<QuestDropOutcome> QuestDrops);

// Coordinates one authoritative attack -> damage -> (exactly-once) death ->
// quest-drop -> respawn-scheduling transition against a target MobInstance.
// This is the single place death is resolved: callers never mutate
// MobInstance HP directly, so "two simultaneous lethal attacks -> one death,
// one quest-drop award" is enforced by MobInstance.ApplyDamage's own lock,
// not by caller discipline.
//
// Depends only on IBasicAttackRules - this class never knows or asks which gameplay
// ruleset (Renewal/PreRenewal) is active. The concrete implementation is selected
// once at MapServer startup by GameplayRulesFactory and threaded in here.
public sealed class MonsterCombatCoordinator(MonsterRegistry monsters, QuestDropResolver questDrops, IBasicAttackRules basicAttackRules)
{
    // `attackerQuestStatus`: a synchronous, already-resolved per-quest-ID lookup (see
    // QuestDropResolver's doc comment) - the caller must obtain each relevant quest's status from
    // ICharacterQuestPersistence BEFORE calling Attack, e.g. by awaiting GetQuestStateAsync once per
    // distinct QuestId in the generated drop rules and closing over the results.
    //
    // `equippedWeapon`: the CURRENT authoritative right-hand weapon, already resolved by the caller
    // via EquippedWeaponResolver against the session's live CharacterEquipmentSnapshot - null means
    // genuinely unarmed (EquippedWeaponResolution.Unarmed), never "unknown"/"not looked up yet". This
    // coordinator never reads equipment state itself; it just forwards it into a BasicAttackContext for
    // IBasicAttackRules, so re-equipping/unequipping mid-session changes the very next attack's
    // calculation without any coordinator-side caching to invalidate.
    public MonsterAttackOutcome Attack(
        MobInstance target,
        uint attackerAccountId,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon,
        Func<uint, CharacterQuestStatus> attackerQuestStatus)
    {
        if (!target.IsAlive) return new(false, target.CurrentHp, target.CurrentHp, false, false, []);

        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.Spawn.Mob));
        var (hpBefore, hpAfter, killed) = target.ApplyDamage(result.Damage);

        // Pinned mob_ai_sub_hard's own target-acquisition gate ("if (md->attacked_id &&
        // mode&MD_CANATTACK)", mob.cpp:1937): a mob without MD_CANATTACK never promotes an
        // attacker into a combat target at all - checked here via the mob's own generated mode,
        // never a hardcoded mob-ID special case.
        //
        // Pinned mob_set_attacked_id, called from the walk-delay timer battle_damage schedules for
        // every hit that connects against a mob (battle.cpp:356-362) - see MobInstance.
        // TryAcquireTarget's own doc comment for why this project calls it immediately rather than
        // reproducing that intermediate timer hop. A killing hit must not re-acquire a target on an
        // instance that ApplyDamage just moved to Dead (TryAcquireTarget's own IsAlive guard already
        // makes this a no-op, but skipping the call entirely when killed also avoids the pointless
        // MSS_RUSH-on-a-dead-mob transition that TryAcquireTarget's own logic would otherwise not
        // reach anyway - either way, matches pinned mob_dead's own immediate unlock, mob.cpp:3863).
        var mode = target.Spawn.Mob.Mode;
        if (!killed && mode.HasFlag(MobMode.CanAttack)) LogIfEngagementAcquired(target, attackerAccountId, mode);

        IReadOnlyList<QuestDropOutcome> drops = [];
        if (killed)
        {
            drops = questDrops.ResolveDrops(attackerQuestStatus, target.Spawn.Mob.Id);
            monsters.ScheduleRespawnIfNeeded(target);
        }

        return new(true, hpBefore, hpAfter, result.IsMiss, killed, drops);
    }

    // Section 16: logs ONLY the actual acquisition transition (Idle -> Rush for a genuinely NEW
    // target), never a re-hit of an already-targeted mob or a rejected steal attempt while
    // chasing/attacking - avoids per-hit log spam for an ongoing engagement while still capturing
    // the moment this task's own live regression hinges on ("does the mob actually acquire the
    // attacker as a target").
    private static void LogIfEngagementAcquired(MobInstance target, uint attackerAccountId, MobMode mode)
    {
        var wasIdle = target.Engagement.State == MobCombatState.Idle;
        if (!target.TryAcquireTarget(attackerAccountId, mode) || !wasIdle) return;
        var position = target.GetPosition();
        MapLogger.Info($"[iRO MAP DEBUG] Mob engagement acquired mobActorId={target.ActorId} targetAccountId={attackerAccountId} mobPosition=({position.X},{position.Y}) combatState={target.Engagement.State}");
    }

    // Section 15's own optimization: a quest-state CharServer roundtrip is only ever NEEDED when
    // THIS hit actually kills the target (QuestDropResolver.ResolveDrops is only ever called in the
    // `killed` branch above) - resolving every distinct QuestId's state on EVERY ordinary
    // non-lethal hit (the live-log-observed "hit 1 -> roundtrip, hit 2 -> roundtrip, hit 3 -> kill"
    // pattern for a three-hit kill) is pure waste. This overload defers `resolveQuestStates` (an
    // async ICharacterQuestPersistence-backed resolver, e.g. one GetQuestStateAsync call per
    // distinct QuestId) until AFTER ApplyDamage has already determined `killed` atomically -
    // MobInstance.ApplyDamage's own lock still enforces "two simultaneous lethal hits -> one death,
    // one quest-drop award" exactly as before; this method only decides WHETHER to await the
    // resolver at all, never races the death determination itself.
    public async Task<MonsterAttackOutcome> AttackAsync(
        MobInstance target,
        uint attackerAccountId,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon,
        Func<Task<Func<uint, CharacterQuestStatus>>> resolveQuestStates)
    {
        if (!target.IsAlive) return new(false, target.CurrentHp, target.CurrentHp, false, false, []);

        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.Spawn.Mob));
        var (hpBefore, hpAfter, killed) = target.ApplyDamage(result.Damage);

        var mode = target.Spawn.Mob.Mode;
        if (!killed && mode.HasFlag(MobMode.CanAttack)) LogIfEngagementAcquired(target, attackerAccountId, mode);

        IReadOnlyList<QuestDropOutcome> drops = [];
        if (killed)
        {
            var attackerQuestStatus = await resolveQuestStates();
            drops = questDrops.ResolveDrops(attackerQuestStatus, target.Spawn.Mob.Id);
            monsters.ScheduleRespawnIfNeeded(target);
        }

        return new(true, hpBefore, hpAfter, result.IsMiss, killed, drops);
    }
}
