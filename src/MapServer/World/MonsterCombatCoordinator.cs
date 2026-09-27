using Athena.Net.MapServer.Gameplay.Rules;
using Athena.Net.World.Contracts;

namespace Athena.Net.MapServer.World;

// Outcome of one player -> monster basic-attack attempt, projected from World's own authoritative
// WorldMonsterDamageResult (see BuildOutcome below). `EngagementAcquired` reports whether this hit
// resulted in this player becoming (or remaining) the monster's locked target, straight from
// World's own Engagement field - this coordinator itself never calls into World (stays
// Orleans-free, see this type's own doc comment below).
public readonly record struct MonsterAttackOutcome(
    bool Accepted,
    uint HpBefore,
    uint HpAfter,
    bool IsMiss,
    bool KilledByThisHit,
    bool EngagementAcquired,
    IReadOnlyList<QuestDropOutcome> QuestDrops);

// A calculated candidate hit - the damage formula's own result, computed against the target's
// static definition only (no local HP/lethality guess any more - World alone decides both, via
// ApplyMonsterDamageAsync). `WouldAcquireEngagement` is a pure LOCAL signal ("this mob is
// CanAttack-capable") the caller uses to decide whether to request engagement acquisition on the
// World RPC - it carries no engagement-state mutation of its own; World independently
// re-validates/no-ops it.
public readonly record struct MonsterAttackCandidate(uint Damage, bool IsMiss, bool WouldAcquireEngagement);

// Substep 9 cutover: this coordinator is now PURE damage calculation + outcome projection. It owns
// no HP/lethality state of its own at all - World's ApplyMonsterDamageAsync is the sole authority
// for CurrentHp/MaxHp/the Alive->Dead transition. This type never calls into World and never
// mutates any local store; MapClientSession's own PendingMonsterDamageAttempt/DispatchPending
// DamageAttemptAsync own the actual RPC round trip and idempotency bookkeeping (AttackSequence).
//
// This coordinator remains entirely Orleans/World-contract-free (only value types cross this
// boundary, never a grain reference or IWorldRuntime).
//
// Depends only on IBasicAttackRules - this class never knows or asks which gameplay ruleset
// (Renewal/PreRenewal) is active.
public sealed class MonsterCombatCoordinator(QuestDropResolver questDrops, IBasicAttackRules basicAttackRules)
{
    // Computes the damage formula's result against the target's static definition - no HP lookup,
    // no lethality guess, no store access of any kind. The caller (MapClientSession) sends this
    // candidate's Damage/IsMiss verbatim to World's ApplyMonsterDamageAsync, which alone decides
    // whether it is lethal.
    public MonsterAttackCandidate CalculateAttack(
        WorldMonsterActorView target,
        EffectiveCharacterStats attacker,
        ushort attackerBaseLevel,
        WeaponItemDefinition? equippedWeapon)
    {
        var result = basicAttackRules.Calculate(new BasicAttackContext(attacker, attackerBaseLevel, equippedWeapon, target.StaticMob));
        var wouldAcquireEngagement = target.StaticMob.Mode.HasFlag(MobMode.CanAttack);
        return new MonsterAttackCandidate(result.Damage, result.IsMiss, wouldAcquireEngagement);
    }

    // Projects World's own authoritative WorldMonsterDamageResult into this coordinator's existing
    // MonsterAttackOutcome shape, so the existing damage/HP-info/EXP/quest-drop packet-building
    // code in MapClientSession is reused with minimal edits. Quest drops are resolved only when
    // this hit was lethal AND a quest-status resolver was supplied - the resolver passed in on the
    // live path is always backed by an ALREADY-RESOLVED snapshot captured before the World RPC was
    // ever sent (see PendingMonsterDamageAttempt.QuestStatusSnapshot in MapClientSession.cs) -
    // BuildOutcome itself performs no persistence I/O and never re-resolves anything.
    public MonsterAttackOutcome BuildOutcome(WorldMonsterDamageResult result, WorldMonsterActorView target, bool isMiss, Func<uint, CharacterQuestStatus>? attackerQuestStatus)
    {
        var accepted = result.Status is WorldMonsterDamageStatus.Applied or WorldMonsterDamageStatus.ReplayedSequence;
        var engagementAcquired = result.Engagement is WorldMonsterAttackedStatus.Acquired or WorldMonsterAttackedStatus.AlreadyCurrentTarget;
        IReadOnlyList<QuestDropOutcome> drops = result.KilledByThisHit && attackerQuestStatus is not null
            ? questDrops.ResolveDrops(attackerQuestStatus, target.MobId)
            : [];
        return new MonsterAttackOutcome(accepted, result.HpBefore, result.HpAfter, isMiss, result.KilledByThisHit, engagementAcquired, drops);
    }
}
