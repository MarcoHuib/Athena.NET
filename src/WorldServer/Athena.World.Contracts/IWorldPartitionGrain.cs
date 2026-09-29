using Orleans;

namespace Athena.Net.World.Contracts;

/// <summary>Coarse authority for multiple map runtimes. A map is not an Orleans grain.</summary>
public interface IWorldPartitionGrain : IGrainWithStringKey
{
    Task<WorldPresenceRegistration> RegisterPresenceAsync(WorldPlayerPresence presence, WorldPlayerPublicState publicState);
    Task<WorldPresenceUnregistration> UnregisterPresenceAsync(string mapId, uint characterId, Guid presenceId);
    Task<WorldMovementResult> MovePlayerAsync(WorldMovementCommand command);
    Task<WorldMovementResult> TruncateMovementAsync(WorldMovementTruncation command);
    Task<WorldMovementAdvanceResult> AdvanceMovementAsync(WorldMovementAdvance command);
    Task<WorldMovementCancellationResult> CancelMovementAsync(WorldMovementCancellation command);
    Task<WorldTransferResult> TransferPlayerAsync(WorldTransferCommand command);
    Task<IncomingTransferResult> PrepareIncomingTransferAsync(IncomingWorldTransfer transfer);
    Task<IncomingTransferResult> CommitIncomingTransferAsync(Guid transferId);
    Task<OutgoingTransferResult> FinalizeOutgoingTransferAsync(Guid transferId);
    Task<WorldMapSnapshot> GetMapSnapshotAsync(string mapId);

    // Item 14: the World-owned player presence/event feed - same proven per-map epoch/sequence/
    // bounded-retention/snapshot-resync shape as PollMonsterFeedAsync below, applied to players so
    // two independent MapServer gateway replicas can each maintain their own cursor against the
    // same World-authoritative player presence state. See WorldPlayerFeedEntryKind's own doc
    // comment for exactly which existing presence mutations emit which entry kind.
    Task<WorldPlayerFeedPage> PollPlayerFeedAsync(WorldPlayerFeedCursor? cursor, string mapId);
    Task<WorldPlayerLookUpdateResult> UpdatePlayerLookAsync(uint characterId, Guid presenceId, byte direction, byte headDirection);
    Task<WorldPlayerPublicStateUpdateResult> UpdatePlayerPublicStateAsync(uint characterId, Guid presenceId, WorldPlayerPublicState publicState);
    // The ordering-critical seam for item 14 - see WorldMovementProjectionConfirmation's own doc
    // comment for why a MovementStarted feed entry may ONLY ever be produced from here, never from
    // MovePlayerAsync/TruncateMovementAsync themselves.
    Task<WorldMovementProjectionResult> ConfirmMovementProjectionAsync(WorldMovementProjectionConfirmation confirmation);
    // Cross-replica projection of an already-resolved monster -> player attack (HP mutation/cadence
    // itself stays MapServer-local - see WorldMonsterAttackActionCommand's own doc comment).
    Task<WorldMonsterAttackPublishResult> PublishMonsterAttackActionAsync(WorldMonsterAttackActionCommand command);

    // Final scope boundary (Step 7): World owns the monster's exact life identity (epoch/actor/
    // incarnation), position/movement, engagement, CurrentHp/MaxHp, the Alive->Dead transition,
    // respawn timing, AttackSequence idempotency, and the sequenced monster feed. MapServer owns
    // damage-formula calculation (weapon/ATK/DEF), local attack cadence/scheduling, packet
    // projection, EXP/progression, and quest/drop orchestration - it holds no authoritative
    // player->monster HP/lifecycle state of its own.
    //
    // ApplyMonsterDamageAsync is the SOLE seam MapServer-local combat crosses into World for
    // actually mutating HP/lifecycle - the live MapClientSession attack path is fully cut over to
    // it; the atomic clamped-subtract + Alive->Dead compare, and the exactly-once idempotency
    // ledger that guards it (AttackSequence), live here. NotifyMonsterAttackedAsync remains a
    // separate, narrower seam for engagement semantics only where still used (target acquisition/
    // refresh) - it is NOT the live player-hit HP-mutation path.
    // ValidateMonsterAttackWindowAsync is a read-only just-in-time recheck immediately before a
    // locally-cadenced attack actually executes (never an executable command, never a reservation/
    // claim), UpdatePresenceLifeStateAsync feeds World's own engagement rules, and
    // PollMonsterFeedAsync is the per-map sequenced feed of pure state transitions a MapServer
    // instance polls to project monster movement/lifecycle/engagement/HP to its connected sessions.
    Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch);
    Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId);
    Task<WorldMonsterDamageResult> ApplyMonsterDamageAsync(WorldMonsterDamageCommand command);
    Task<WorldMonsterAttackedResult> NotifyMonsterAttackedAsync(WorldMonsterAttackedCommand command);
    Task<WorldMonsterAttackWindowResult> ValidateMonsterAttackWindowAsync(WorldMonsterAttackWindowQuery query);
    Task<WorldPresenceLifeStateResult> UpdatePresenceLifeStateAsync(WorldPresenceLifeStateUpdate update);
}

[GenerateSerializer]
public sealed record WorldPlayerPresence(
    [property: Id(0)] Guid PresenceId,
    [property: Id(1)] uint ActorId,
    [property: Id(2)] uint CharacterId,
    [property: Id(3)] string MapId,
    [property: Id(4)] ushort X,
    [property: Id(5)] ushort Y,
    // Defaults to true (a session that has never explicitly reported otherwise is presumed
    // alive) so every existing positional-constructor call site remains valid. Updated only via
    // UpdatePresenceLifeStateAsync - see that method's own doc comment for the PresenceId-guarded
    // update contract this field requires, since a bare field with no update path would be
    // permanently stale after the first registration. World's own engagement rules
    // (WorldMonsterEngagementRules.Evaluate) read this to decide Unlock vs. Chase/InAttackRange -
    // MonsterEngagementDomain.Evaluate's existing local equivalent already gates identically on
    // PlayerCombatSnapshot.IsAlive.
    [property: Id(6)] bool IsAlive = true);

public enum WorldPresenceRegistrationStatus { Registered, AlreadyRegistered, Conflict }

[GenerateSerializer]
public sealed record WorldPresenceRegistration(
    [property: Id(0)] string PartitionId,
    [property: Id(1)] string MapId,
    [property: Id(2)] WorldPresenceRegistrationStatus Status,
    [property: Id(3)] int PresenceCount);

public enum WorldPresenceUnregistrationStatus { Removed, AlreadyAbsent, PresenceMismatch, MapMismatch }

[GenerateSerializer]
public sealed record WorldPresenceUnregistration(
    [property: Id(0)] string PartitionId,
    [property: Id(1)] string MapId,
    [property: Id(2)] WorldPresenceUnregistrationStatus Status,
    [property: Id(3)] int PresenceCount);

public enum WorldMovementStatus { Moved, NotFound, PresenceMismatch, SourceMismatch, Rejected }

[GenerateSerializer]
public sealed record WorldMovementCommand(
    [property: Id(0)] Guid PresenceId,
    [property: Id(1)] uint CharacterId,
    [property: Id(2)] string MapId,
    [property: Id(3)] ushort FromX,
    [property: Id(4)] ushort FromY,
    [property: Id(5)] ushort DestinationX,
    [property: Id(6)] ushort DestinationY);

[GenerateSerializer]
public readonly record struct WorldPosition([property: Id(0)] ushort X, [property: Id(1)] ushort Y);

[GenerateSerializer]
public sealed record WorldMovementResult(
    [property: Id(0)] WorldMovementStatus Status,
    [property: Id(1)] WorldPlayerPresence? Presence,
    [property: Id(2)] IReadOnlyList<WorldPosition>? Path = null,
    [property: Id(3)] Guid? MovementId = null);

[GenerateSerializer]
public sealed record WorldMovementTruncation(
    [property: Id(0)] Guid MovementId,
    [property: Id(1)] Guid PresenceId,
    [property: Id(2)] uint CharacterId,
    [property: Id(3)] string MapId,
    [property: Id(4)] int DestinationIndex);

public enum WorldMovementCancellationStatus { Cancelled, AlreadyAbsent, PresenceNotFound, PresenceMismatch, SourceMismatch }

[GenerateSerializer]
public sealed record WorldMovementCancellation(
    [property: Id(0)] Guid MovementId,
    [property: Id(1)] Guid PresenceId,
    [property: Id(2)] uint CharacterId,
    [property: Id(3)] string MapId);

[GenerateSerializer]
public sealed record WorldMovementCancellationResult(
    [property: Id(0)] WorldMovementCancellationStatus Status,
    [property: Id(1)] WorldPlayerPresence? Presence);

[GenerateSerializer]
public sealed record WorldMovementAdvance(
    [property: Id(0)] Guid MovementId,
    [property: Id(1)] Guid PresenceId,
    [property: Id(2)] uint CharacterId,
    [property: Id(3)] string MapId,
    [property: Id(4)] ushort ExpectedX,
    [property: Id(5)] ushort ExpectedY,
    [property: Id(6)] ushort NewX,
    [property: Id(7)] ushort NewY);

public enum WorldMovementAdvanceStatus { Advanced, AlreadyAdvanced, NotFound, PresenceMismatch, SourceMismatch, StaleRoute, Rejected }

[GenerateSerializer]
public sealed record WorldMovementAdvanceResult(
    [property: Id(0)] WorldMovementAdvanceStatus Status,
    [property: Id(1)] WorldPlayerPresence? Presence);

public enum WorldTransferType { SamePartition, CrossPartition }
public enum WorldTransferStatus { Completed, AlreadyCompleted, Conflict, SourceMismatch, NotFound }

[GenerateSerializer]
public sealed record WorldTransferCommand(
    [property: Id(0)] Guid TransferId,
    [property: Id(1)] Guid PresenceId,
    [property: Id(2)] uint CharacterId,
    [property: Id(3)] string SourceMapId,
    [property: Id(4)] string DestinationMapId,
    [property: Id(5)] ushort DestinationX,
    [property: Id(6)] ushort DestinationY);

[GenerateSerializer]
public sealed record IncomingWorldTransfer(
    [property: Id(0)] Guid TransferId,
    [property: Id(1)] WorldPlayerPresence Presence,
    [property: Id(2)] string SourcePartitionId,
    [property: Id(3)] string SourceMapId,
    [property: Id(4)] string DestinationMapId,
    [property: Id(5)] ushort DestinationX,
    [property: Id(6)] ushort DestinationY);

[GenerateSerializer]
public sealed record WorldTransferResult(
    [property: Id(0)] WorldTransferStatus Status,
    [property: Id(1)] WorldTransferType Type,
    [property: Id(2)] WorldPlayerPresence? Presence);

public enum IncomingTransferStatus { Prepared, AlreadyPrepared, Committed, AlreadyCommitted, Conflict, NotFound }

[GenerateSerializer]
public sealed record IncomingTransferResult(
    [property: Id(0)] IncomingTransferStatus Status,
    [property: Id(1)] WorldPlayerPresence? Presence);

public enum OutgoingTransferStatus { Finalized, AlreadyFinalized, NotFound, Stale }

[GenerateSerializer]
public sealed record OutgoingTransferResult([property: Id(0)] OutgoingTransferStatus Status);

[GenerateSerializer]
public sealed record WorldMapSnapshot(
    [property: Id(0)] string PartitionId,
    [property: Id(1)] string MapId,
    [property: Id(2)] IReadOnlyList<WorldPlayerPresence> Players);

// ---------------------------------------------------------------------------------------------
// Phase 2B: monster SIMULATION authority (identity, position, movement, target/engagement
// validity, death/respawn lifecycle) only. Deliberately excludes damage calculation, quest-drop
// orchestration, current HP, attack cadence, and final attack execution - all of that remains
// MapServer-local for this slice (see MonsterCombatCoordinator, unmoved). No IWorldMonsterGrain/
// MonsterGrain/MapGrain/CellGrain - every member above and below lives on this same coarse
// IWorldPartitionGrain, alongside players, per the approved architecture.
// ---------------------------------------------------------------------------------------------

// A monster's IncarnationId distinguishes its current life from a previous one that ended in
// death - ActorId alone is stable across an ordinary respawn (MonsterRegistry's own existing
// invariant), so ActorId cannot by itself prove a mutation targets the CURRENT life rather than a
// stale one racing a respawn. Starts at 1 on first spawn, incremented by exactly 1 on every
// respawn.
[GenerateSerializer]
public readonly record struct WorldMonsterIncarnationId([property: Id(0)] long Value)
{
    public static WorldMonsterIncarnationId First => new(1);
    public WorldMonsterIncarnationId Next() => new(Value + 1);
}

// A map's SimulationEpoch identifies exactly one (re)construction of that map's monster
// simulation state - deliberately an opaque Guid, never an activation-local monotonic counter,
// because a counter's own numbering could restart from the same small values after activation
// loss/process restart, reintroducing exactly the stale-mutation collision risk a fresh epoch
// exists to prevent (a restarted counter combined with a since-reissued ActorId and a
// since-restarted IncarnationId could otherwise coincidentally collide against an unrelated,
// newly-created monster). Every life-specific mutation and every feed cursor's identity includes
// this value - see WorldMonsterLifeReference and WorldMonsterFeedCursor.
[GenerateSerializer]
public readonly record struct WorldSimulationEpoch([property: Id(0)] Guid Value)
{
    public static WorldSimulationEpoch NewEpoch() => new(Guid.NewGuid());
}

// The full identity a life-specific mutation must present to be accepted - MapId+SimulationEpoch
// alone identifies WHICH map simulation instance is being addressed; ActorId+IncarnationId alone
// identifies WHICH monster life within it. All four together are required because none of the
// three narrower combinations is sufficient alone (see WorldSimulationEpoch's own doc comment for
// why epoch cannot be dropped even when ActorId+IncarnationId already narrow to one life).
[GenerateSerializer]
public sealed record WorldMonsterLifeReference(
    [property: Id(0)] string MapId,
    [property: Id(1)] WorldSimulationEpoch SimulationEpoch,
    [property: Id(2)] uint ActorId,
    [property: Id(3)] WorldMonsterIncarnationId IncarnationId);

public enum WorldMonsterLifecycleState { Alive, Dead }

// A monster's target identity is (CharacterId, PresenceId) TOGETHER, never CharacterId alone - a
// character can disconnect and reconnect with the same CharacterId but a genuinely different
// PresenceId, and a monster must not silently keep (or transfer) an existing engagement onto that
// replacement presence merely because the CharacterId number still matches. Every World-side
// engagement evaluation resolves this exact pair against the grain's CURRENT presence
// registration for CharacterId; if the currently-registered presence's PresenceId no longer
// matches PresenceId here, the authoritative result is Unlock (see WorldMonsterEngagementState's
// own doc comment) - never a silent reattribution to the new presence.
[GenerateSerializer]
public sealed record WorldPlayerTargetReference(
    [property: Id(0)] uint CharacterId,
    [property: Id(1)] Guid PresenceId);

// World's own copy of MonsterEngagementDomain's target-validity/range decision, narrowed to
// exclude attack cadence entirely (NextAttackAt/Attack/Wait stay MapServer-local - see
// WorldMonsterEngagementState's own doc comment). Unlock/Chase/InAttackRange mirror the pinned
// mob_ai_sub_hard branches MonsterEngagementDomain.Evaluate already traces; this enum is the
// state-holding counterpart to that decision, not a duplicate of its own logic.
public enum WorldMonsterEngagementState { Unengaged, Chasing, InAttackRange }

// A monster's full World-authoritative state. As of Step 7, this INCLUDES CurrentHp/MaxHp -
// World is the sole authority for both (see IWorldPartitionGrain's own Step 7 doc comment and
// ApplyMonsterDamageAsync) - a MapServer instance reads HP directly from this projection (via the
// feed) for any display/discovery purpose; it no longer stores authoritative HP itself. Damage
// calculation (weapon/ATK/DEF formula) and quest-drop orchestration remain MapServer-local; only
// the atomic mutation of these two fields lives in World.
//
// Deliberately has NO per-instance sequence field: the feed protocol already has
// WorldMonsterFeedEntry.Sequence (one incremental transition's own position) and
// WorldMonsterFeedPage.AsOfSequence (the atomic snapshot/cursor boundary for the WHOLE page) -
// either of those, not a third notion living inside each individual monster instance, is always
// the authoritative sequence position for any snapshot/entry this type appears in.
[GenerateSerializer]
public sealed record WorldMonsterInstance(
    [property: Id(0)] uint ActorId,
    [property: Id(1)] WorldMonsterIncarnationId IncarnationId,
    [property: Id(2)] string MapId,
    [property: Id(3)] int MobId,
    [property: Id(4)] ushort X,
    [property: Id(5)] ushort Y,
    [property: Id(6)] WorldMonsterLifecycleState Lifecycle,
    [property: Id(7)] bool IsWalking,
    [property: Id(8)] ushort DestinationX,
    [property: Id(9)] ushort DestinationY,
    [property: Id(10)] WorldMonsterEngagementState Engagement,
    [property: Id(11)] WorldPlayerTargetReference? EngagedTarget,
    [property: Id(12)] uint CurrentHp,
    [property: Id(13)] uint MaxHp);

// A serializable PROJECTION of a spawn declaration - not MobSpawnDefinition/MobDefinition
// themselves, which live in MapServer's/Athena.World.Monsters' file-linked source and reference
// types (e.g. WorldSourceInfo) with no reason to cross the Orleans wire. Carries exactly the
// per-mob stat fields World's own movement/engagement logic actually reads (confirmed by
// inspection of MonsterRuntime/MobInstance/MonsterEngagementDomain: WalkSpeed, AttackRange, Mode,
// MaxHp - nothing else from the much larger MobDefinition is ever consulted by simulation/
// engagement code, only by damage calculation, which stays MapServer-local) - World does not need,
// and does not have, the full generated mob-stat database (GeneratedMobs/GeneratedMobSpawnRegistry
// live under src/MapServer/Generated/, not file-linked into Athena.World.Monsters; see the plan's
// own spawn-initialization feasibility-check finding for why linking that generated tree wholesale
// is out of scope for this phase). The caller (MapServer, which DOES have that data) projects only
// these fields per spawn declaration.
[GenerateSerializer]
public sealed record WorldMonsterSpawnDefinition(
    [property: Id(0)] int MobId,
    [property: Id(1)] string MapId,
    [property: Id(2)] ushort X,
    [property: Id(3)] ushort Y,
    [property: Id(4)] ushort Xs,
    [property: Id(5)] ushort Ys,
    [property: Id(6)] int Count,
    [property: Id(7)] int RespawnDelayMs,
    [property: Id(8)] int RespawnRandomDelayMs,
    [property: Id(9)] string SpawnName,
    [property: Id(10)] int WalkSpeedMs,
    [property: Id(11)] int AttackRange,
    [property: Id(12)] uint MaxHp,
    [property: Id(13)] uint Mode);

// `Fingerprint` is a caller-supplied convenience value ONLY (logging/diagnostics, and a cheap
// pre-check) - it is NEVER trusted as proof two payloads are identical. The grain independently
// computes its OWN canonical fingerprint from the batch's actual spawn content (a deterministic,
// order-independent hash over every spawn's normalized fields) and compares that self-computed
// value against whatever it already has stored for the map; a caller-provided value that
// disagrees with what the grain itself computes is its own distinct rejection
// (WorldMonsterSpawnLoadStatus.CallerFingerprintMismatch), separate from an ordinary
// content-changed reload rejection - see LoadMonsterSpawnsAsync's own doc comment.
[GenerateSerializer]
public sealed record WorldMonsterSpawnBatch(
    [property: Id(0)] string MapId,
    [property: Id(1)] string Fingerprint,
    [property: Id(2)] IReadOnlyList<WorldMonsterSpawnDefinition> Spawns);

public enum WorldMonsterSpawnLoadStatus { Loaded, AlreadyLoaded, ContentMismatch, CallerFingerprintMismatch, SpawnMapMismatch }

[GenerateSerializer]
public sealed record WorldMonsterSpawnLoadResult(
    [property: Id(0)] WorldMonsterSpawnLoadStatus Status,
    [property: Id(1)] WorldSimulationEpoch SimulationEpoch);

// Cursor identity is (SimulationEpoch, Sequence) together, never Sequence alone - see
// WorldSimulationEpoch's own doc comment. A caller that has never polled a given map yet passes
// `null` to PollMonsterFeedAsync to receive an atomic bootstrap (WorldMonsterFeedPage with
// ResyncRequired=false, a full Snapshot, and a fresh cursor to resume from) rather than needing a
// separate bootstrap RPC - this is what makes bootstrap atomic from the caller's own perspective:
// there is no window where a caller could hold a cursor that does not correspond to the snapshot
// it was handed, because both are always returned together in one response.
[GenerateSerializer]
public readonly record struct WorldMonsterFeedCursor(
    [property: Id(0)] WorldSimulationEpoch SimulationEpoch,
    [property: Id(1)] long Sequence);

// `Moved` covers ordinary movement position updates - a walk starting, an intermediate cell being
// crossed, or a walk finishing. Every OTHER kind here already carries its own movement implication
// where relevant (e.g. ChaseStarted's own Instance snapshot reflects the mob now walking toward its
// target) - Moved exists specifically so a consumer projecting ordinary movement (idle wandering
// OR an already-engaged mob's ordinary chase cell-crossings) has a feed entry to react to.
//
// CORRECTED: `Moved` is NOT exclusive to unengaged mobs - an already-engaged mob's ordinary chase
// cell-crossings (no fresh retarget applied this tick) are ALSO reported via Moved, never
// suppressed, so a consumer's position mirror stays current even while a chase continues without
// producing any of the engagement-shaped kinds this tick (see WorldMonsterMapSimulation.Tick's own
// doc comment for the exact tick-ordering this guarantees). A prior revision of this doc comment
// claimed Moved was emitted only for a mob with no current target - that was inaccurate as of the
// tick restructuring that fixed the "engaged mob's feed goes stale mid-chase" bug and has been
// corrected here.
// HealthChanged: Step 7's atomic ApplyMonsterDamageAsync appends this whenever the clamped
// subtract actually reduces CurrentHp on a hit that does NOT kill the monster - a miss (Damage=0)
// never emits it, and a lethal hit emits Died only (Died's own instance snapshot already carries
// CurrentHp=0, so a redundant HealthChanged(0) immediately before it is never appended). This
// exists because the feed is cursor/entry based - merely widening WorldMonsterInstance with
// CurrentHp/MaxHp fields does not, by itself, cause an already-issued cursor to observe anything;
// a poller only learns of a change via a NEW entry (or a fresh snapshot). Without this entry, a
// non-lethal hit that changes no other tracked state would produce zero feed traffic, and a
// second MapServer process polling the same simulation would have nothing new to read - breaking
// cross-process HP convergence. FanOutEntryAsync deliberately does NOT give this kind a Died-style
// dedicated dispatch: it falls through to the same generic projection-update tail every other
// non-Died kind already uses, updating local projection state only - never forcing an unsolicited
// HP-info packet to bystander sessions.
// Item 14: PlayerAttackAction and MonsterAttackAction are combat-action entries, not state
// transitions - added to this SAME feed (rather than a separate one) so a player's killing hit is
// structurally guaranteed to be sequenced before the Died entry it produces (see
// WorldMonsterMapSimulation.ApplyDamage's own doc comment). PlayerAttackAction/MonsterAttackAction
// payloads ride on WorldMonsterFeedEntry.PlayerAttack/MonsterAttack respectively - both null for
// every other kind, exactly like the existing MovementKind optional field.
public enum WorldMonsterFeedEntryKind { Moved, EngagementAcquired, ChaseStarted, ChaseInterrupted, TargetUnlocked, InAttackRange, HealthChanged, Died, Respawned, PlayerAttackAction, MonsterAttackAction }

// Semantic player -> monster combat action, no packet IDs (item 14 §7). AttackerActorId is always
// resolved by World from its own current registered presence for AttackerCharacterId/
// AttackerPresenceId - never trusted from a caller-supplied value (item 14 §7's explicit
// requirement) - see WorldPartitionGrain.ApplyMonsterDamageAsync's own attacker-presence lookup.
[GenerateSerializer]
public sealed record WorldPlayerAttackAction(
    [property: Id(0)] uint AttackerActorId,
    [property: Id(1)] uint AttackerCharacterId,
    [property: Id(2)] Guid AttackerPresenceId,
    [property: Id(3)] uint Damage,
    [property: Id(4)] uint SourceAttackMotion,
    [property: Id(5)] uint TargetDamageMotion,
    [property: Id(6)] bool Hit,
    [property: Id(7)] bool Lethal);

// Semantic monster -> player combat action (item 14 §6's "Monster -> player" secondary path). HP
// mutation/cadence stay MapServer-local; this is purely the cross-replica projection payload for an
// already-resolved local attack - see PublishMonsterAttackActionAsync's own doc comment.
[GenerateSerializer]
public sealed record WorldMonsterAttackAction(
    [property: Id(0)] uint TargetActorId,
    [property: Id(1)] uint TargetCharacterId,
    [property: Id(2)] Guid TargetPresenceId,
    [property: Id(3)] uint Damage,
    [property: Id(4)] uint SourceAttackMotion,
    [property: Id(5)] uint TargetDamageMotion,
    [property: Id(6)] bool Lethal);

// The Ragexe wire-projection-relevant distinction WorldMonsterFeedEntryKind alone cannot express:
// whether a movement transition is a FRESH walk beginning (a real 0x09FD walk-entry packet is
// warranted), an ORDINARY mid-walk cell crossing (projection-only, pinned unit_walktoxy_nextcell's
// own sendMove=false continuation - no repeated walk-entry packet), a walk reaching its natural end
// (projection-only, no fabricated stop/fixpos packet), or a COMBAT interruption of an in-flight walk
// (pinned USW_FIXPOS - the one case that warrants the 0x0088 ZC_STOPMOVE packet). This mirrors
// MonsterMovementChangeKind's own doc comment on the MapServer side exactly - same four cases, same
// wire-projection consequences - so a future MapServer feed consumer can reuse the identical
// decision table its own local MonsterRuntime/MonsterEngagementTickProcessor already use, rather
// than trying to re-derive "is this a fresh walk or an ordinary continuation" from
// WorldMonsterInstance.IsWalking alone (which cannot distinguish those two cases: both leave
// IsWalking=true).
public enum WorldMonsterMovementKind { WalkStarted, CellCrossed, WalkFinished, ChaseInterrupted }

// A PURE STATE TRANSITION - never an executable command. In particular, InAttackRange means
// "the authoritative monster is now engaged and in range," nothing more; it never means "attack
// now" and must never be treated as one by a consumer (see PollMonsterFeedAsync's own doc comment
// for why: the feed is deliberately replayable/resyncable, and a feed entry that directly meant
// "apply player HP damage" would need delivery/idempotency guarantees - exactly-once, or an
// ack/claim protocol - this phase does not build; a crash-and-retry replaying this entry must be
// harmless). A consumer maintains its OWN local mirror of engagement state, updated as these
// entries arrive, and its own separately-scheduled local attack cadence (NextAttackAt) decides
// WHEN to actually attack while that mirror says InAttackRange - see
// ValidateMonsterAttackWindowAsync for the read-only recheck a consumer performs at that moment,
// immediately before mutating player HP. Target identity, when relevant to Kind, is read from
// Instance.EngagedTarget - deliberately no separate TargetCharacterId field here, to avoid two
// competing notions of "who is the target" between this entry and the Instance it already embeds.
// `MovementKind` is null when this entry carries no movement transition at all (e.g. Died,
// Respawned, TargetUnlocked with no accompanying position change, EngagementAcquired for a mob
// already in range at the moment of acquisition) - a consumer must only apply MovementKind's own
// wire-projection rule (see WorldMonsterMovementKind's own doc comment) when it is present.
[GenerateSerializer]
public sealed record WorldMonsterFeedEntry(
    [property: Id(0)] long Sequence,
    [property: Id(1)] WorldMonsterFeedEntryKind Kind,
    [property: Id(2)] uint ActorId,
    [property: Id(3)] WorldMonsterIncarnationId IncarnationId,
    [property: Id(4)] WorldMonsterInstance Instance,
    [property: Id(5)] WorldMonsterMovementKind? MovementKind = null,
    [property: Id(6)] WorldPlayerAttackAction? PlayerAttack = null,
    [property: Id(7)] WorldMonsterAttackAction? MonsterAttack = null);

// Explicit initialization/continuity status - a bare bool (ResyncRequired) cannot express "this
// map has never been loaded, or was unloaded, and a consumer must call LoadMonsterSpawnsAsync
// before treating anything returned here as authoritative" without being indistinguishable from
// "this map IS genuinely loaded, with a real (possibly empty) spawn set, and Snapshot=[] simply
// means zero monsters were declared" - those are two different situations a consumer must be able
// to tell apart (see PollMonsterFeedAsync's own doc comment for the exact consumer contract this
// status exists to satisfy).
//   Ready: the map is loaded; Snapshot/Entries are authoritative (a bootstrap or incremental page
//     respectively) exactly as ResyncRequired=false always meant before this status existed.
//   ResyncRequired: the caller's cursor is stale (wrong epoch or out-of-retention-window) against
//     a map that IS loaded - the returned Snapshot is a fresh, authoritative bootstrap to resync
//     from, exactly as ResyncRequired=true always meant before this status existed.
//   SpawnInitializationRequired: this map's simulation has never been loaded, OR was unloaded by
//     the touched-window expiry policy and has not been touched since - the caller MUST call
//     LoadMonsterSpawnsAsync before this map's monster state means anything; Snapshot is an EMPTY
//     placeholder here, never a real (even if legitimately zero-monster) authoritative snapshot -
//     never conflate this with a genuinely loaded, zero-monster map.
public enum WorldMonsterFeedStatus { Ready, ResyncRequired, SpawnInitializationRequired }

[GenerateSerializer]
public sealed record WorldMonsterFeedPage(
    [property: Id(0)] string MapId,
    [property: Id(1)] WorldSimulationEpoch SimulationEpoch,
    [property: Id(2)] WorldMonsterFeedStatus Status,
    [property: Id(3)] IReadOnlyList<WorldMonsterInstance>? Snapshot,
    [property: Id(4)] IReadOnlyList<WorldMonsterFeedEntry>? Entries,
    [property: Id(5)] long AsOfSequence)
{
    // Preserved for callers that only care about the binary "must I fully reconcile client-visible
    // projection before advancing my cursor" question - both ResyncRequired and
    // SpawnInitializationRequired demand exactly that (a SpawnInitializationRequired map has
    // nothing loaded yet, which is itself a "start from scratch" resync case), only Ready does not.
    public bool ResyncRequired => Status != WorldMonsterFeedStatus.Ready;
}

// Step 7: the sole atomic HP-mutation command. AttackSequence is a per-attacker monotonic long
// (NOT a Guid/TTL cache - see AttackSequenceState's own doc comment for why a time-evicted dedup
// key is unsound for a non-lethal command), minted client-side by the same MapClientSession that
// owns this attacker/life pair and never recomputed on retry - a retry of an ambiguous prior
// attempt resends this exact command verbatim, including the original Damage roll, never a fresh
// one (WeaponAttackCalculator.Calculate rolls Random.Shared.Next fresh every call, so a recomputed
// retry would legitimately collide with Conflict below). Damage=0 is a legal, meaningful value - a
// miss still needs to reach World so it can still refresh engagement via AcquireEngagement.
[GenerateSerializer]
public sealed record WorldMonsterDamageCommand(
    [property: Id(0)] WorldMonsterLifeReference Life,
    [property: Id(1)] uint AttackerCharacterId,
    [property: Id(2)] Guid AttackerPresenceId,
    [property: Id(3)] long AttackSequence,
    [property: Id(4)] uint Damage,
    [property: Id(5)] bool AcquireEngagement);

// HpBefore/HpAfter/KilledByThisHit fold directly into MapServer's existing MonsterAttackOutcome
// shape. MaxHp rides along so the HP-info packet reads MaxHp from the same authority as HpAfter -
// otherwise a future MaxHp change (boss mode, buffs) could produce a torn HP bar between two
// sources of truth. Engagement reuses WorldMonsterAttackedStatus verbatim (no parallel enum) -
// null means no engagement attempt was made on this call (AcquireEngagement was false).
[GenerateSerializer]
public sealed record WorldMonsterDamageResult(
    [property: Id(0)] WorldMonsterDamageStatus Status,
    [property: Id(1)] uint HpBefore,
    [property: Id(2)] uint HpAfter,
    [property: Id(3)] uint MaxHp,
    [property: Id(4)] bool KilledByThisHit,
    [property: Id(5)] WorldMonsterAttackedStatus? Engagement,
    // Diagnostic/test-assertion convenience only (item 14): the feed Sequence of the
    // PlayerAttackAction entry this command's own commit appended (or, on ReplayedSequence, the one
    // the ORIGINAL commit appended) - correctness of the attack-action-before-Died guarantee never
    // depends on this field, only on actual feed order (see WorldMonsterMapSimulation.ApplyDamage).
    [property: Id(6)] long? AttackActionSequence = null);

// Deliberately absent: MonsterNotAttackable (a passive/no-CanAttack mob is still fully damageable -
// only engagement acquisition cares about CanAttack) and NotFound (folded into StaleLifeReference,
// matching how a stale-life lookup collapses "not found" into the same status elsewhere).
public enum WorldMonsterDamageStatus
{
    Applied,
    ReplayedSequence,
    StaleSequence,
    Conflict,
    StaleLifeReference,
    StaleAttackerPresence,
    AttackerNotEngageable,
    AlreadyDead
}

[GenerateSerializer]
public sealed record WorldMonsterAttackedCommand(
    [property: Id(0)] WorldMonsterLifeReference Life,
    [property: Id(1)] uint AttackerCharacterId,
    [property: Id(2)] Guid AttackerPresenceId);

public enum WorldMonsterAttackedStatus { Acquired, AlreadyCurrentTarget, StaleLifeReference, StaleAttackerPresence, MonsterNotAttackable, AttackerNotEngageable }

[GenerateSerializer]
public sealed record WorldMonsterAttackedResult([property: Id(0)] WorldMonsterAttackedStatus Status);

[GenerateSerializer]
public sealed record WorldMonsterAttackWindowQuery(
    [property: Id(0)] WorldMonsterLifeReference Life,
    [property: Id(1)] uint TargetCharacterId,
    [property: Id(2)] Guid TargetPresenceId);

// Deliberately a multi-case result, never a bare boolean, so a caller can log/diagnose exactly
// which invariant failed rather than only "no". This is a plain read-only query against current
// grain state at the moment of the call - never a reservation, claim, or the start of any
// exactly-once protocol (see WorldMonsterFeedEntry's own doc comment for why no such protocol
// exists in this phase). A caller invokes this ONLY when its own local attack cadence has already
// decided an attack is due - never on every tick for every engaged mob - and must not mutate
// player HP or emit a success packet on any result other than Valid.
public enum WorldMonsterAttackWindowStatus { Valid, StaleLifeReference, TargetNotFound, StaleTargetPresence, TargetDead, NotCurrentTarget, OutOfRange }

[GenerateSerializer]
public sealed record WorldMonsterAttackWindowResult([property: Id(0)] WorldMonsterAttackWindowStatus Status);

// PresenceId-guarded exactly like every other per-presence mutation on this grain - a stale
// PresenceId (one that no longer matches the grain's current registration for characterId) must
// never mutate the current presence's IsAlive value. Called by MapServer at the existing
// authoritative player death/revive transitions; deliberately a small, dedicated update rather
// than resending the full WorldPlayerPresence, which would conflate "player moved" with "player's
// life state changed" for no reason - see WorldPlayerPresence.IsAlive's own doc comment.
[GenerateSerializer]
public sealed record WorldPresenceLifeStateUpdate(
    [property: Id(0)] uint CharacterId,
    [property: Id(1)] Guid PresenceId,
    [property: Id(2)] bool IsAlive);

public enum WorldPresenceLifeStateStatus { Updated, StalePresence, NotFound }

[GenerateSerializer]
public sealed record WorldPresenceLifeStateResult([property: Id(0)] WorldPresenceLifeStateStatus Status);

// Item 14 §6 "Monster -> player": ActionId is a client(MapServer)-minted Guid, stable across a
// bounded local retry, so a transient RPC failure followed by a retry of the SAME already-resolved
// local attack is recognized as AlreadyPublished rather than appended a second time - the "short
// bounded in-process pending publish/retry mechanism" item 14 explicitly allows, reused here for
// dedup identity rather than an actual retry loop. Damage/motion/Lethal are the ALREADY-RESOLVED
// outcome of MonsterAttackCadenceExecutor's existing local HP mutation - this RPC never mutates HP
// itself, purely publishes for cross-replica projection (item 14's explicit "do not move the whole
// monster attack cadence/player HP model into World" boundary).
[GenerateSerializer]
public sealed record WorldMonsterAttackActionCommand(
    [property: Id(0)] WorldMonsterLifeReference Life,
    [property: Id(1)] uint TargetCharacterId,
    [property: Id(2)] Guid TargetPresenceId,
    [property: Id(3)] Guid ActionId,
    [property: Id(4)] uint Damage,
    [property: Id(5)] uint SourceAttackMotion,
    [property: Id(6)] uint TargetDamageMotion,
    [property: Id(7)] bool Lethal);

public enum WorldMonsterAttackPublishStatus { Published, AlreadyPublished, StaleLifeReference, StaleTargetPresence }

[GenerateSerializer]
public sealed record WorldMonsterAttackPublishResult([property: Id(0)] WorldMonsterAttackPublishStatus Status);

// ---------------------------------------------------------------------------------------------
// Item 14: the World-owned player presence/event feed. Mirrors the monster feed's proven shape
// (WorldMonsterFeedCursor/WorldMonsterFeedPage/WorldMonsterFeedEntryKind above) exactly - per-map
// epoch/generation, monotonic sequence, bounded retained entries, atomic snapshot bootstrap, resync
// on stale cursor/epoch. See WorldPlayerMapSimulation (Athena.World project) for the implementation
// this contract serializes across the wire.
// ---------------------------------------------------------------------------------------------

// Protocol-neutral public/cosmetic player state (item 14 §1) - deliberately NOT a duplicate
// gameplay-authoritative store: WorldPlayerPresence (position/identity/life-state) remains the sole
// gameplay authority; this is purely the display-facing state another MapServer replica needs to
// construct the existing 0x09FE/0x09FF/0x09FD/0x0A30 packets for a remote player, with no gameplay
// effect of its own. No packet IDs or byte layouts here - see IroPlayerActorPackets (MapServer
// project) for where this becomes wire bytes.
[GenerateSerializer]
public sealed record WorldPlayerPublicState(
    [property: Id(0)] string CharacterName,
    [property: Id(1)] byte Direction,
    [property: Id(2)] byte HeadDirection,
    [property: Id(3)] ushort JobClass,
    [property: Id(4)] byte Sex,
    [property: Id(5)] ushort BaseLevel,
    [property: Id(6)] ushort WalkSpeed,
    [property: Id(7)] ushort HairStyle,
    [property: Id(8)] ushort HairColor,
    [property: Id(9)] ushort ClothesColor,
    [property: Id(10)] ushort BodyStyle,
    [property: Id(11)] uint WeaponAppearance,
    [property: Id(12)] uint ShieldAppearance,
    [property: Id(13)] ushort HeadBottomAppearance,
    [property: Id(14)] ushort HeadTopAppearance,
    [property: Id(15)] ushort HeadMidAppearance,
    [property: Id(16)] ushort RobeAppearance,
    [property: Id(17)] short Manner,
    [property: Id(18)] byte Karma,
    [property: Id(19)] uint Option,
    [property: Id(20)] ushort Font);

// Movement-projection state needed for a resync/bootstrap snapshot taken while a player is already
// walking (item 14 §3's snapshot requirement), separate from WorldPlayerFeedEntryKind.MovementStarted
// itself - this record is what a consumer reconstructs a client-facing walk entry from regardless of
// whether it arrived via a live MovementStarted entry or a fresh snapshot. StartedAtLocalTickStamp is
// an OPAQUE long a MapServer gateway stamped from its OWN Environment.TickCount at the moment it
// called ConfirmMovementProjectionAsync - World never reads or interprets it, only carries it through
// (item 14's explicit "do not leak Environment.TickCount into World" constraint: World's own logic
// never branches on this value, it is pure pass-through payload for the projecting replica's own
// packet builder to reuse, exactly as PlayerMovementPresence.StartTick already does locally today).
[GenerateSerializer]
public sealed record WorldPlayerMovementProjection(
    [property: Id(0)] ushort StartX,
    [property: Id(1)] ushort StartY,
    [property: Id(2)] ushort DestinationX,
    [property: Id(3)] ushort DestinationY,
    [property: Id(4)] long StartedAtLocalTickStamp);

// One player's full feed-projectable state: authoritative presence + public/cosmetic state + an
// optional in-flight movement projection (null when stationary). This is what both a bootstrap/
// resync Snapshot entry AND every incremental WorldPlayerFeedEntry.Entry carry - always the FULL
// current state, never a delta, matching WorldMonsterFeedEntry.Instance's own "always full state"
// convention (a consumer never needs to merge partial updates).
[GenerateSerializer]
public sealed record WorldPlayerPresenceEntry(
    [property: Id(0)] WorldPlayerPresence Presence,
    [property: Id(1)] WorldPlayerPublicState PublicState,
    [property: Id(2)] WorldPlayerMovementProjection? Movement = null);

// Registered/Unregistered/TransferredIn/TransferredOut/MovementStarted/MovementFinished/LookChanged
// are lifecycle/state transitions; Moved is the same high-frequency per-cell position update the
// monster feed's own Moved kind represents (see WorldPartitionGrain.AdvanceMovementAsync's own feed
// append). MovementStarted is ONLY ever appended from ConfirmMovementProjectionAsync - see that
// method's own doc comment for why MovePlayerAsync/TruncateMovementAsync themselves must never
// append it directly (item 14 §3's critical pre-truncation-leak prevention requirement).
public enum WorldPlayerFeedEntryKind { Registered, Unregistered, Moved, MovementStarted, MovementFinished, LookChanged, TransferredIn, TransferredOut }

[GenerateSerializer]
public sealed record WorldPlayerFeedEntry(
    [property: Id(0)] long Sequence,
    [property: Id(1)] WorldPlayerFeedEntryKind Kind,
    [property: Id(2)] uint ActorId,
    [property: Id(3)] uint CharacterId,
    [property: Id(4)] WorldPlayerPresenceEntry Entry);

// Cursor identity is (Epoch, Sequence) together - same rationale as WorldMonsterFeedCursor (see its
// own doc comment): a bare Sequence alone cannot distinguish "still within this map's current
// generation" from "this sequence number happened to repeat after an unrelated epoch rotation".
[GenerateSerializer]
public readonly record struct WorldPlayerFeedCursor(
    [property: Id(0)] WorldSimulationEpoch Epoch,
    [property: Id(1)] long Sequence);

public enum WorldPlayerFeedStatus { Ready, ResyncRequired }

[GenerateSerializer]
public sealed record WorldPlayerFeedPage(
    [property: Id(0)] string MapId,
    [property: Id(1)] WorldSimulationEpoch Epoch,
    [property: Id(2)] WorldPlayerFeedStatus Status,
    [property: Id(3)] IReadOnlyList<WorldPlayerPresenceEntry>? Snapshot,
    [property: Id(4)] IReadOnlyList<WorldPlayerFeedEntry>? Entries,
    [property: Id(5)] long AsOfSequence)
{
    public bool ResyncRequired => Status != WorldPlayerFeedStatus.Ready;
}

public enum WorldPlayerLookUpdateStatus { Updated, StalePresence, NotFound }

[GenerateSerializer]
public sealed record WorldPlayerLookUpdateResult([property: Id(0)] WorldPlayerLookUpdateStatus Status);

public enum WorldPlayerPublicStateUpdateStatus { Updated, StalePresence, NotFound }

[GenerateSerializer]
public sealed record WorldPlayerPublicStateUpdateResult([property: Id(0)] WorldPlayerPublicStateUpdateStatus Status);

// Item 14 §3: the ordering-critical movement-projection confirmation. MapClientSession calls this
// EXACTLY ONCE per accepted movement, AFTER it has already examined World's authoritative path
// (MovePlayerAsync's result) for a warp/script trigger and, if one was found, already called
// TruncateMovementAsync to shorten World's own route to match - never before. World itself derives
// the confirmed destination from its OWN current _movements[characterId] entry's (already-possibly-
// truncated) Path - never from a MapServer-supplied destination (item 14's explicit "do not trust a
// MapServer-supplied arbitrary destination" requirement) - so even a buggy/malicious caller cannot
// cause a pre-truncation destination to leak into the feed: there is no field on this record for one.
[GenerateSerializer]
public sealed record WorldMovementProjectionConfirmation(
    [property: Id(0)] Guid MovementId,
    [property: Id(1)] Guid PresenceId,
    [property: Id(2)] uint CharacterId,
    [property: Id(3)] string MapId,
    [property: Id(4)] long LocalTickStamp);

public enum WorldMovementProjectionStatus { Confirmed, AlreadyConfirmed, NotFound, PresenceMismatch, SourceMismatch }

[GenerateSerializer]
public sealed record WorldMovementProjectionResult(
    [property: Id(0)] WorldMovementProjectionStatus Status,
    [property: Id(1)] WorldPlayerPresenceEntry? Entry);
