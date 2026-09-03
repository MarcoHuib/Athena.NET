using Orleans;

namespace Athena.Net.World.Contracts;

/// <summary>Coarse authority for multiple map runtimes. A map is not an Orleans grain.</summary>
public interface IWorldPartitionGrain : IGrainWithStringKey
{
    Task<WorldPresenceRegistration> RegisterPresenceAsync(WorldPlayerPresence presence);
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

    // Player -> monster combat is intentionally MapServer-local for Phase 2B (damage calculation,
    // quest-drop orchestration, and current HP all remain MapServer-owned - see
    // WorldMonsterInstance's own doc comment for why no CurrentHp field exists here). These five
    // members are the ONLY seam MapServer-local combat crosses into World: acquiring/refreshing
    // the authoritative target on a landed hit (NotifyMonsterAttackedAsync), reporting a death so
    // World's own respawn/lifecycle takes over (TryMarkMonsterDeadAsync), a read-only just-in-time
    // recheck immediately before a locally-cadenced attack actually executes
    // (ValidateMonsterAttackWindowAsync - never an executable command, never a reservation/claim),
    // life-state so World's own engagement rules can see it (UpdatePresenceLifeStateAsync), and the
    // per-map sequenced feed of pure state transitions a MapServer instance polls to project
    // monster movement/lifecycle/engagement to its connected sessions (PollMonsterFeedAsync).
    Task<WorldMonsterSpawnLoadResult> LoadMonsterSpawnsAsync(WorldMonsterSpawnBatch batch);
    Task<WorldMonsterFeedPage> PollMonsterFeedAsync(WorldMonsterFeedCursor? cursor, string mapId);
    Task<WorldMonsterDeathResult> TryMarkMonsterDeadAsync(WorldMonsterLifeReference reference);
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

// A monster's full World-authoritative state, EXCLUDING current HP (see this record's own field
// list - there is deliberately no CurrentHp here). Player -> monster damage stays MapServer-local
// for Phase 2B (see the interface's own doc comment); a MapServer instance keeps its own local
// combat-relevant state (CurrentHp, NextAttackAt, static combat inputs) keyed by the SAME
// (MapId, SimulationEpoch, ActorId, IncarnationId) tuple this record's own fields identify,
// distinct from this type so the authority boundary between "World-projected" and
// "MapServer-combat-local" state is mechanically obvious at every call site, never merely a
// convention a reviewer has to remember.
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
    [property: Id(11)] WorldPlayerTargetReference? EngagedTarget);

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

// `Moved` covers ordinary (non-engagement) idle-walk movement - a walk starting, an intermediate
// cell being crossed, or a walk finishing for a mob with no current target. Every OTHER kind here
// already carries its own movement implication where relevant (e.g. ChaseStarted's own Instance
// snapshot reflects the mob now walking toward its target) - Moved exists specifically so a
// consumer projecting ordinary wandering movement (a mob with no target at all) has a feed entry
// to react to; it is never emitted for a mob that currently has an engaged target (that mob's
// movement is always reported via the engagement-shaped kinds instead, never both).
public enum WorldMonsterFeedEntryKind { Moved, EngagementAcquired, ChaseStarted, ChaseInterrupted, TargetUnlocked, InAttackRange, Died, Respawned }

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
[GenerateSerializer]
public sealed record WorldMonsterFeedEntry(
    [property: Id(0)] long Sequence,
    [property: Id(1)] WorldMonsterFeedEntryKind Kind,
    [property: Id(2)] uint ActorId,
    [property: Id(3)] WorldMonsterIncarnationId IncarnationId,
    [property: Id(4)] WorldMonsterInstance Instance);

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

public enum WorldMonsterDeathStatus { MarkedDead, AlreadyDead, StaleLifeReference }

[GenerateSerializer]
public sealed record WorldMonsterDeathResult([property: Id(0)] WorldMonsterDeathStatus Status);

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
