using Athena.Net.MapServer.Gameplay.Rules;

namespace Athena.Net.MapServer.World;

// Outcome of one basic-attack attempt against a live MobInstance.
//
// `EngagementAcquired`: true only for the actual acquisition transition (Idle -> Rush for a
// genuinely NEW target), never a re-hit of an already-targeted mob or a rejected steal attempt
// while chasing/attacking. This coordinator stays a pure state/rules layer and never logs itself
// (MapLogger has no place here) - the caller (an orchestration layer, e.g. MapClientSession or
// MonsterEngagementTickProcessor) surfaces this flag as its own operational diagnostic.
public readonly record struct MonsterAttackOutcome(
    bool Accepted,
    uint HpBefore,
    uint HpAfter,
    bool IsMiss,
    bool KilledByThisHit,
    bool EngagementAcquired,
    IReadOnlyList<QuestDropOutcome> QuestDrops);

// Coordinates one authoritative attack -> damage -> (exactly-once) death ->
// quest-drop -> respawn-scheduling transition against a target MobInstance.
//
// Damage is applied through MonsterCombatStateStore.ApplyDamage (Step 5) - NOT
// MobInstance.ApplyDamage, which is superseded on this migrated path (see that method's own doc
// comment). The store's own per-key critical section is what now enforces "two simultaneous
// lethal attacks -> one death, one quest-drop award", exactly like MobInstance.ApplyDamage's lock
// used to before HP ownership moved - see MonsterCombatStateStore.ApplyDamage's own doc comment
// for the exact sequencing. This coordinator stays entirely Orleans/World-contract-free: it takes
// `mapId` as a plain string (the same key component every other combat-state lookup already uses),
// never a grain reference or any World type.
//
// Depends only on IBasicAttackRules - this class never knows or asks which gameplay
// ruleset (Renewal/PreRenewal) is active. The concrete implementation is selected
// once at MapServer startup by GameplayRulesFactory and threaded in here.
public sealed class MonsterCombatCoordinator(MonsterRegistry monsters, QuestDropResolver questDrops, IBasicAttackRules basicAttackRules, MonsterCombatStateStore combatState)
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
        string mapId,
        uint attackerAccountId,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon,
        Func<uint, CharacterQuestStatus> attackerQuestStatus)
    {
        if (!target.IsAlive)
        {
            var idleHp = combatState.TryGet(mapId, target, out var idleEntry) ? idleEntry.CurrentHp : 0u;
            return new(false, idleHp, idleHp, false, false, false, []);
        }

        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.Spawn.Mob));
        var damageResult = combatState.ApplyDamage(mapId, target, target.IncarnationId, result.Damage);
        if (damageResult.Status != MonsterCombatDamageStatus.Applied)
            return new(false, damageResult.HpBefore, damageResult.HpAfter, false, false, false, []);
        var (hpBefore, hpAfter, killed) = (damageResult.HpBefore, damageResult.HpAfter, damageResult.KilledByThisHit);

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
        var engagementAcquired = !killed && mode.HasFlag(MobMode.CanAttack) && TryAcquireEngagement(target, attackerAccountId, mode);

        IReadOnlyList<QuestDropOutcome> drops = [];
        if (killed)
        {
            drops = questDrops.ResolveDrops(attackerQuestStatus, target.Spawn.Mob.Id);
            monsters.ScheduleRespawnIfNeeded(target);
        }

        return new(true, hpBefore, hpAfter, result.IsMiss, killed, engagementAcquired, drops);
    }

    // Section 16: reports ONLY the actual acquisition transition (Idle -> Rush for a genuinely NEW
    // target), never a re-hit of an already-targeted mob or a rejected steal attempt while
    // chasing/attacking - via MonsterAttackOutcome.EngagementAcquired, not a direct log call. This
    // coordinator is domain-adjacent state/rules, not operational diagnostics: the caller decides
    // whether/how to log the transition (MapLogger has no place in this class).
    private static bool TryAcquireEngagement(MobInstance target, uint attackerAccountId, MobMode mode)
    {
        var wasIdle = target.Engagement.State == MobCombatState.Idle;
        return target.TryAcquireTarget(attackerAccountId, mode) && wasIdle;
    }

    // Section 15's own optimization: a quest-state CharServer roundtrip is only ever NEEDED when
    // THIS hit actually kills the target (QuestDropResolver.ResolveDrops is only ever called in the
    // `killed` branch above) - resolving every distinct QuestId's state on EVERY ordinary
    // non-lethal hit (the live-log-observed "hit 1 -> roundtrip, hit 2 -> roundtrip, hit 3 -> kill"
    // pattern for a three-hit kill) is pure waste. This overload defers `resolveQuestStates` (an
    // async ICharacterQuestPersistence-backed resolver, e.g. one GetQuestStateAsync call per
    // distinct QuestId) until AFTER ApplyDamage has already determined `killed` atomically -
    // MonsterCombatStateStore.ApplyDamage's own per-key lock still enforces "two simultaneous
    // lethal hits -> one death, one quest-drop award" exactly as before (see that method's own doc
    // comment); this method only decides WHETHER to await the resolver at all, never races the
    // death determination itself.
    public async Task<MonsterAttackOutcome> AttackAsync(
        MobInstance target,
        string mapId,
        uint attackerAccountId,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon,
        Func<Task<Func<uint, CharacterQuestStatus>>> resolveQuestStates)
    {
        if (!target.IsAlive)
        {
            var idleHp = combatState.TryGet(mapId, target, out var idleEntry) ? idleEntry.CurrentHp : 0u;
            return new(false, idleHp, idleHp, false, false, false, []);
        }

        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.Spawn.Mob));
        var damageResult = combatState.ApplyDamage(mapId, target, target.IncarnationId, result.Damage);
        if (damageResult.Status != MonsterCombatDamageStatus.Applied)
            return new(false, damageResult.HpBefore, damageResult.HpAfter, false, false, false, []);
        var (hpBefore, hpAfter, killed) = (damageResult.HpBefore, damageResult.HpAfter, damageResult.KilledByThisHit);

        var mode = target.Spawn.Mob.Mode;
        var engagementAcquired = !killed && mode.HasFlag(MobMode.CanAttack) && TryAcquireEngagement(target, attackerAccountId, mode);

        IReadOnlyList<QuestDropOutcome> drops = [];
        if (killed)
        {
            var attackerQuestStatus = await resolveQuestStates();
            drops = questDrops.ResolveDrops(attackerQuestStatus, target.Spawn.Mob.Id);
            monsters.ScheduleRespawnIfNeeded(target);
        }

        return new(true, hpBefore, hpAfter, result.IsMiss, killed, engagementAcquired, drops);
    }
}
