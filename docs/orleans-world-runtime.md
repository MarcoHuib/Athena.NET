# Orleans world runtime — phase 1

## Current architecture

```text
Ragexe
  ↓ Ragnarok TCP
MapServer (protocol and connection adapter)
  ↓ protocol-independent Orleans client calls
Athena.World (Orleans silo)
  ↓
IMapGrain("prontera")
```

Aspire models one local-development Orleans cluster, starts `Athena.World` as its silo, and supplies
the client configuration to MapServer. LoginServer and CharServer remain ordinary services. They are
not grains, and authentication, character persistence, SQL, and content loading remain outside Orleans.

The first migrated vertical slice is authenticated map presence. After the existing Ragnarok load-end
flow constructs authoritative server-side player state, MapServer registers a protocol-independent
`MapPlayerPresence` with the grain addressed by the normalized logical map name. Disconnect and map
transition cleanup unregister it. The existing process-local presence/AOI projection remains active so
client-visible spawn, movement, and disappearance packets retain their proven behavior during phase 1.

Presence registration is retry-safe. A `PresenceId` is created by `MapClientSession` once for one
logical world-presence lifecycle and is reused if registration is replayed. The first
`CharacterId + PresenceId` command returns `Registered`; replaying that same identity returns
`AlreadyRegistered` and remains success with one stored entry. A different `PresenceId` for a
character already owned by another presence returns `Conflict` and cannot overwrite the owner.

Unregistration includes both `CharacterId` and `PresenceId`. Replaying cleanup after removal returns
`AlreadyAbsent`, while delayed cleanup from an older lifecycle returns `PresenceMismatch` and cannot
remove a newer presence. Session takeover, map-transfer ownership, epochs, leases, and fencing tokens
remain explicitly deferred to later phases.

`IMapGrain` is intentionally coarse. Realtime movement, combat, monsters, NPC execution, collision,
pathfinding, and visibility are intended to execute locally inside the map authority as they migrate.
They must not become actor-per-monster, actor-per-cell, or per-operation distributed call graphs.

## World actor IDs

The current `MapServerWorld.Build` composition shares one `WorldActorIdAllocator` across NPC, warp, and
monster registries, preserving the global `110,000,000+` runtime actor namespace. This phase does not
instantiate those registries inside independent grain activations, so it does not create competing
allocators. Before map simulation itself moves into Orleans, phase 2 must assign non-overlapping actor-ID
ownership (or another simple globally unique scheme) while preserving the client-visible invariant.

## Future direction (not implemented here)

```text
Ragexe
  ↓ TCP localhost
Athena.Client
  ↓ QUIC
Athena.Edge
  ↓ Orleans client
Athena.World
```

QUIC, Athena.Client, Athena.Edge, production clustering, custom placement, Orleans persistence, and the
rest of the world migration are later phases. Physical silo IDs, hosts, and addresses must never enter
gameplay-facing contracts: gameplay always addresses logical map identities such as `prontera`.
