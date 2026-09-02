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

## Deferred work

The configured non-overlapping `WorldPartitionActorRanges` are reserved for Phase 2B, when NPC and
monster runtime begins moving into partitions. Phase 2A does not allocate player IDs during transfer
and does not migrate monsters, NPC execution, combat, drops, status effects, or persistence into
Orleans.
