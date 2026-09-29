# Orleans world runtime — Phase 2A

## Authority model

```text
Ragexe
  ↓ Ragnarok TCP
MapServer (protocol adapter and client projection)
  ↓ protocol-independent commands containing logical MapId values
IWorldRuntime / WorldPartitionResolver
  ↓ Orleans
WorldPartitionGrain
  └── MapRuntime (map-local simulation unit inside the partition)
```

`WorldPartitionGrain` is the distributed authority. `MapRuntime` is a local simulation unit inside
that authority; a map is not an Orleans grain. Partition IDs are routing details resolved below the
gameplay boundary. The logical topology is independent of physical silo placement, and both initial
partitions may run in one silo.

The initial topology is loaded from the shared `conf/world_partitions.json` representation by both
MapServer and Athena.World:

```text
prontera-region
├── prontera
└── prt_fild*

world-rest
└── every other served map
```

Each grain resolves every map it is asked to own and rejects a map whose configured owner differs
from its own grain key. MapServer cannot bypass this authority check by addressing the wrong grain.

## Presence and transfer invariants

A connected world session creates one stable `PresenceId`. Both `PresenceId` and the player
`ActorId` remain unchanged across same-partition and cross-partition map transfers. Incoming
transfers carry the immutable authoritative source `WorldPlayerPresence`; the destination never
reconstructs an actor ID from a character ID.

Unregister requires `CharacterId`, `PresenceId`, and the expected `MapId`. A cleanup for an old map
returns `MapMismatch` and cannot remove the same presence after it has moved to a newer map.

Same-partition transfers update the two local map runtimes atomically within one grain turn. A
cross-partition transfer uses this bounded, explicit state machine:

```text
source Active → outgoing TransferringOut
target PrepareIncoming → PendingIncoming (CharacterId reserved)
target CommitIncoming → Active
source FinalizeOutgoing → old Active removed, transfer completed
```

Prepare stores the source snapshot and reserves its `CharacterId`; ordinary registration and a
different transfer cannot claim or overwrite that pending owner. Replaying the same prepare returns
`AlreadyPrepared`, replaying commit returns `AlreadyCommitted`, replaying finalization returns
`AlreadyFinalized`, and replaying the full transfer returns `AlreadyCompleted`. Source authority is
not released before target commit succeeds. Finalization validates the recorded source map and
presence identity, so a delayed operation cannot remove a newer owner created by a later transfer.

## Movement

The public movement command is protocol-independent intent: presence identity, character identity,
logical map, expected source position, and requested destination. It contains neither Ragnarok
packet data nor a MapServer-asserted `CollisionValidatedPath`. World resolves the authoritative route
and position; MapServer uses the returned route only as a timed per-cell client projection. A rejected
expected source position or route must not start local walking.

Timed traversal, retargeting, arrival, OnTouch, NPC touch, and warp packet ordering remain MapServer
projection responsibilities during this migration slice. Accepting an intent does not turn walking
into an instant client-visible teleport: cells are still emitted according to the existing movement
clock, and movement response ordering remains before a resulting map-change packet.

## Cross-replica player visibility and combat (item 14)

`WorldPartitionGrain` owns a second per-map feed, `WorldPlayerMapSimulation`, mirroring the monster
feed's own shape exactly: a per-map epoch, monotonic sequence, bounded retained entries (4096),
atomic snapshot bootstrap, and resync on stale cursor/epoch. `PollPlayerFeedAsync` is the sole
distribution path for player presence - registration, unregistration, same/cross-partition transfer,
movement, and look/public-state changes each append a `WorldPlayerFeedEntry`. Two independent
MapServer gateway processes each maintain their own cursor against the same map's feed.

`PlayerPresenceRegistry`/`PlayerVisibilityCoordinator` on the MapServer side are now purely
feed-derived projections, populated by `PlayerFeedProjection` (mirroring `MonsterFeedProjection`)
from the per-map tick loop's own player-feed poll - `MapClientSession` no longer mutates them
directly for registration, movement, look-change, or public-state refresh. A remote-replica player
and a local session are treated identically: both are discovered, moved, and vanished exclusively
through this feed.

Movement-start ordering is structural: `ConfirmMovementProjectionAsync` is the *only* RPC that ever
appends a `MovementStarted` entry, and MapServer calls it only after examining World's authoritative
path for a warp/script trigger and truncating it if needed (`MapClientSession.ResolveWorldMovementTargetAsync`)
- so a pre-truncation destination can never leak into the feed. World derives the confirmed
destination from its own active-movement state, never from a MapServer-supplied value.

Player -> monster combat actions ride the *existing* monster feed as a new `PlayerAttackAction` entry
kind, appended by `WorldMonsterMapSimulation.ApplyDamage` immediately before `HealthChanged`/`Died` in
the same atomic mutation - this makes "the attacker's action is sequenced before the resulting
death" a structural feed-ordering guarantee across replicas, not a same-process timing trick. The
existing `AttackSequence` idempotency ledger already prevents a replay from appending a duplicate
action. A same-process fast path still delivers the attacker's own action immediately for
responsiveness, deduplicated against the later feed delivery via a per-session marker set
synchronously before any RPC yields control. Monster -> player combat keeps its existing MapServer-local
HP mutation/cadence; `PublishMonsterAttackActionAsync` is a narrow, idempotent (ActionId-keyed)
publish-only RPC that lets other replicas project an already-resolved local attack.

## Deferred work

Global actor-ID allocation for Phase 2B's monster/NPC runtime is a lease-based block allocator
(`IActorIdBlockAuthorityGrain`, `Athena.Net.World.Contracts.LeasedBlockActorIdAllocator`) - a single
well-known Orleans grain leases non-overlapping `[start, end)` blocks to any requesting authority
(a partition, MapServer's own NPC/warp allocator, or a future authority), with each requester
allocating locally from its leased block. This deliberately replaced an earlier, since-abandoned
design that would have hardcoded a fixed numeric range per partition directly in
`conf/world_partitions.json` - that approach didn't scale as the number of partitions/maps/authorities
grows, so `conf/world_partitions.json` remains pure map-ownership topology with no actor-ID concept
at all. Phase 2A does not allocate player IDs during transfer and does not migrate monsters, NPC
execution, combat, drops, status effects, or persistence into Orleans.
