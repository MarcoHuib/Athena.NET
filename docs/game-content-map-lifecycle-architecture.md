# Athena.NET Game Content and Map Lifecycle Architecture

## Status

This document records the agreed future direction for four related concerns:

- game-content hot updates;
- map telemetry;
- map lifecycle management (loaded/warm/lazy, eviction);
- future MapServer scaling (assigning complete maps to additional MapServers).

It is a child document of `architecture-roadmap.md`. Read that document first for the overall phase sequencing. This document does not introduce a new phase; it details how Phase 0 stays simple today while leaving a clean path toward later scaling and content-update work described here and, eventually, in `orleans-game-engine-architecture.md`.

Read together with:

- `architecture-roadmap.md` — implementation order and phase gates.
- `orleans-game-engine-architecture.md` — the later Orleans distributed backend, which must agree with the map-ownership rule in this document.
- `../ai/map-server.md` — current MapServer implementation state and packet-level evidence.
- `../ai/world-data.md` — current static/generated world-content pipeline.

The supported client remains the **unmodified official International Ragnarok Online (iRO) client**.

---

## Goals

- Keep today's single-MapServer implementation simple, correct, and observable.
- Establish one clear rule for map ownership that scales cleanly later without ever duplicating a normal map's world-space by default.
- Base every future lifecycle/scaling optimization on real telemetry, not assumptions inherited from rAthena or theoretical load.
- Separate static/reconstructible game content from mutable/durable runtime state, and describe a future path to updating content without restarting MapServer.
- Guarantee MVP/boss respawn timers cannot be exploited by map lifecycle changes (eviction, unload, restart, or future re-ownership).

## Non-goals (for this document and for Phase 0)

This document does **not** authorize implementing, in the current Phase 0 work:

- a Game Content Service or any content-distribution transport;
- lazy map loading or map eviction;
- multiple MapServer processes;
- normal-map channels/replicas as a scaling mechanism;
- cross-MapServer map handoff/transfer;
- Orleans, QUIC, ASP.NET Core Identity, or Kubernetes-based distribution;
- a message broker (RabbitMQ/Kafka/Dapr/etc.) selection;
- new projects, database migrations, or schema;
- PostgreSQL or any replacement for SQL Server.

Those remain later, evidence-gated work. This document exists so that when the evidence arrives, the direction is already agreed.

---

## 1. Initial MapServer topology

Athena.NET begins with exactly one MapServer process:

```text
LoginServer
     |
CharServer
     |
MapServer
   |
   +-- all normal world maps
```

There is currently no reason to run multiple MapServers. The first implementation optimizes for correctness, observability, and a stable stock-iRO baseline rather than premature distribution.

---

## 2. One logical map = one active runtime

**Rule:** within one logical world, a normal map has at most one active runtime and one authoritative owner.

```text
Chaos / prontera -> one runtime
Chaos / izlude   -> one runtime
Chaos / payon    -> one runtime
```

Normal-map replicas/channels are **not** the default scaling model. Do not create a situation such as:

```text
prontera:1
prontera:2
prontera:3
```

where players believe they share a map but cannot see or interact with one another. Normal Ragnarok world maps retain one shared world-space unless a future explicit gameplay feature deliberately introduces instancing/channels as a player-facing feature choice — not as an implicit infrastructure scaling trick.

Instanced dungeons are a separate, already-understood concept (a private, on-purpose copy of a map for a party/event) and are not prohibited by this rule.

---

## 3. Scale by assigning complete maps to MapServers

The intended future scaling strategy moves whole maps, never replicates them.

Start:

```text
MapServer 1
+-- prontera
+-- izlude
+-- payon
+-- geffen
+-- all other maps
```

Only once telemetry proves a real bottleneck, complete maps may be assigned to another MapServer:

```text
MapServer 1                    MapServer 1
+-- most world maps            +-- normal world
                        or
MapServer 2                    MapServer 2
+-- prontera                   +-- one or more proven expensive maps
```

**The rule: move the whole authoritative map; do not replicate the normal map.**

A future map assignment can conceptually be identified by:

```text
WorldId
MapName
MapServerOwner
```

This document does not prescribe a distributed-ownership implementation. With one MapServer, all maps simply resolve to the same process, so `MapServerOwner` is trivially constant today. Cross-MapServer transfer/handoff is only required once maps genuinely live on different server processes — it is not a prerequisite for today's runtime.

---

## 4. Telemetry first, lazy loading later

**This decision is important:** lazy map loading is *not* the initial implementation strategy.

Initially, all maps that Athena.NET supports are loaded normally. Real production/runtime statistics do not exist yet — it is not known which maps are cold, warm, or hot, and rAthena's or theoretical assumptions are not a substitute for measuring Athena.NET's own traffic.

### Telemetry capability

Document (do not yet implement) a map telemetry/observability capability. At minimum, useful per-map metrics include:

```text
MapName
CurrentPlayers
PeakPlayers
TotalVisits
UniquePlayers
TotalPlayerMinutes
FirstVisitedAt
LastVisitedAt
NPC interactions
Mob kills
```

Where practical, also measure runtime cost:

```text
packet volume
map update/tick duration
CPU/update time attributable to a map
mob count
NPC count
```

The exact schema is not fixed by this document. The architectural goal is to be able to answer questions such as:

- Which maps were never visited in the last 7/30 days?
- Which maps have almost no player-minutes?
- Which maps are visited constantly?
- Which maps consume disproportionate runtime/CPU despite few players?
- Which maps are the best candidates for lazy loading?
- Which maps are candidates for their own MapServer?

Prefer durable aggregated statistics — for example daily summaries in SQL Server — so a MapServer restart does not erase historical usage information. Do **not** introduce PostgreSQL; SQL Server remains Athena.NET's lowest-risk persistence direction (see `orleans-game-engine-architecture.md`, "Start with SQL Server").

---

## 5. Future map lifecycle optimization

Once telemetry proves it useful, Athena.NET may introduce configurable map lifecycle policies:

```text
AlwaysLoaded
KeepWarm
Lazy
```

Example intent, not a rule:

```text
prontera -> AlwaysLoaded
izlude   -> AlwaysLoaded
```

while rarely visited interiors/fields may eventually become `Lazy`. This document does **not** state that all cities must always be loaded or that all fields must be lazy — actual measurements and gameplay function determine policy per map.

An intermediate, rAthena-inspired optimization may also be supported later:

```text
map becomes empty
      |
      | ~5 minutes (configurable)
      v
ordinary mobs may be evicted
      |
      | ~10 additional minutes (configurable)
      v
complete MapRuntime may be unloaded
```

The values above (5 minutes idle before mob eviction, 15 minutes total before runtime unload) are sensible starting defaults discussed for this architecture, not hard-coded architectural truths — they must be configurable.

The first release still keeps everything loaded and only measures usage.

---

## 6. Separate content definitions from runtime state

Document the desired future separation between:

- **static/reconstructible game content** — item definitions, monster definitions, spawn definitions, map definitions, NPC definitions/scripts, skill definitions, other imported reference/game data;
- **mutable runtime/world state** — the live, authoritative state that gameplay actually mutates.

Currently some content is compiled/generated into the server/runtime (see `../ai/world-data.md`), and changing it can require a MapServer restart.

The desired future architecture introduces a **Game Content Service / Game Content distribution boundary** so content can eventually be updated independently of the running MapServer:

```text
WorldDataImporter / content tooling
             |
             v
     Game Content Service
             |
      versioned snapshot
             |
             v
         MapServer
             |
      local in-memory snapshot
             |
          gameplay
```

**The Content Service must NOT become a synchronous dependency in the gameplay hot path.**

Bad:

```text
player attacks Poring
    ->
MapServer asks Content Service for Poring stats
    ->
calculate attack
```

Preferred:

```text
Content Service publishes validated version N
    ->
MapServer obtains version N
    ->
keeps immutable/local snapshot
    ->
gameplay performs local in-memory lookup
```

Server-to-server communication is therefore primarily a content/control-plane mechanism, not a per-action gameplay RPC mechanism — consistent with the existing Orleans guidance to keep external messaging out of the mandatory gameplay hot path (`orleans-game-engine-architecture.md`, "Queues, streams, RabbitMQ, Kafka, and Dapr").

---

## 7. Versioned content publishing

Desired characteristics of content publishing:

```text
content version
validate
publish
MapServer loads new snapshot
validate locally where necessary
atomic activation
rollback capability
```

Conceptually:

```text
Content v184 active

build v185
   |
validate
   |
publish
   |
MapServer loads complete v185
   |
atomic switch
   |
Content v185 active
```

Avoid partially upgraded state such as:

```text
items from v185
monsters from v184
skills from v183
```

The exact transport is intentionally **not** selected yet. Do not prematurely mandate gRPC, HTTP, RabbitMQ, Kafka, Orleans Streams, Dapr, or Kubernetes-specific mechanisms — those can be selected later based on actual requirements. The architectural contract (versioned, validated, atomically activated, rollback-capable snapshots) matters more than the transport today.

---

## 8. Content deployment != server deployment

Record this as an intended operational benefit:

```text
server code deployment
        !=
game content deployment
```

Ultimately, changing item/monster/NPC/static world content should not automatically require disconnecting all players by restarting MapServer. This is especially valuable for balance changes, data fixes, NPC/content changes, importer/regeneration updates, and rollback of bad content.

However, **not every possible content mutation is safely hot-swappable**. Different content categories may require different activation policies:

```text
textual/static metadata
    -> may activate immediately

monster definition
    -> existing monster vs next spawn needs an explicit policy

spawn definitions
    -> may apply on next spawn/map activation

NPC script
    -> needs a defined script-session/reload policy

collision/map geometry
    -> may require map-runtime reload
```

Document the need for explicit per-category activation policies instead of presuming all content changes have identical semantics.

---

## 9. Durable state must remain separate from content

The Game Content Service is **not** the owner of mutable live world state. Use this distinction:

```text
Content:
    "Baphomet has this monster definition and this respawn rule"

Runtime/durable world state:
    "The Baphomet for this world/spawn was killed and may respawn at
     2026-... UTC"
```

Examples of durable state: characters, inventory ownership, progression, quests, important world events, MVP/boss respawn deadlines.

Ordinary mob instances can remain ephemeral/reconstructible (see below).

---

## 10. MVP/boss lifecycle

This rule directly affects future lazy maps and multi-MapServer operation.

**An MVP must NEVER respawn early simply because:**

```text
a map became empty
mobs were evicted
a map runtime was unloaded
MapServer restarted
map ownership moved to another MapServer
```

Otherwise players could exploit map lifecycle/restarts to farm MVP drops/cards.

The durable identity of an MVP spawn should conceptually be tied to something like:

```text
WorldId + SpawnId
```

**NOT** to a `MapServer` process ID. For example, `Chaos / prt_maze03 / baphomet-spawn` has one durable lifecycle regardless of which future MapServer executes that map.

Persist the concrete next eligible spawn timestamp. For a randomized respawn window, choose the concrete next timestamp once when the boss dies and persist it. Do **not** reroll the random respawn time every time the map loads.

Conceptually:

```text
KilledAtUtc
NextSpawnAtUtc
```

If a map is unloaded when the deadline passes, no background map runtime is required merely to keep the timer. When the map is later activated:

```text
if Now < NextSpawnAtUtc:
    MVP remains absent

if Now >= NextSpawnAtUtc:
    MVP may materialize
```

The MapRuntime is not the durable clock for an MVP — the persisted timestamp is. The exact database schema is not part of this document; SQL Server is the current persistence assumption.

---

## 11. Normal mobs

Normal mobs are different from MVPs. Their runtime state is generally ephemeral. A future lifecycle optimization may safely discard normal mob instances when a map has been empty for a configured period and reconstruct them from spawn definitions when players return.

Do not overcomplicate normal-mob persistence merely to support lazy loading. Special bosses/global entities can have stronger durable policies where needed, per the MVP rule above.

---

## 12. Map runtime loading

Conceptual distinction:

```text
MapDefinition
    !=
MapRuntime
```

A map can exist in content while no active runtime is materialized. Future lazy loading can work approximately as:

```text
player attempts transition
    ->
EnsureMapLoaded(destination)
    ->
construct runtime from local content snapshot
    ->
initialize required runtime state
    ->
admit/send player to destination
```

The stock iRO client should not need to know whether the server internally lazy-loaded a map. Destination readiness must be established before telling Ragexe to enter it.

This is a future optimization, not something implemented as part of this documentation task or as a Phase 0 requirement.

---

## 13. Relation to Orleans

`orleans-game-engine-architecture.md` describes the eventual Phase 4 distributed backend. Its model must agree with the rule in section 2 above:

```text
one logical normal map
    ->
one active authoritative MapInstanceGrain/runtime
```

Different maps can execute on different Orleans silos — that is the desired horizontal scaling unit, for example:

```text
prontera -> Silo A
payon    -> Silo B
geffen   -> Silo C
```

**Not**, by default:

```text
prontera:1
prontera:2
prontera:3
```

If one individual map eventually exceeds the capacity of a single runtime, that is a separate future architecture problem. Possible spatial partitioning/channels should only be considered after profiling proves that one-map-one-runtime cannot meet real load — this document does not solve that hypothetical problem today.

The existing Orleans guidance that normal mobs should remain map-local rather than becoming one grain per mob (`orleans-game-engine-architecture.md`, "NPCs and mobs") is unchanged and consistent with this document.

---

## 14. Roadmap sequencing

```text
NOW
----
Complete stable stock-iRO gameplay baseline.

At first:
- one MapServer
- supported maps loaded normally
- add map telemetry/observability
- gather real usage/runtime data

THEN
----
Use evidence to decide:
- which maps should remain AlwaysLoaded
- whether KeepWarm/Lazy is worthwhile
- whether ordinary-mob eviction is useful

CONTENT EVOLUTION
-----------------
Introduce the versioned Game Content boundary when the stable game-domain
baseline is ready for independent content updates.

SCALING
-------
Only after metrics prove a need:
- assign complete hot maps to dedicated MapServers
- maintain exactly one owner/runtime per normal map
- implement the required cross-server map handoff

LATER
-----
Proxy / QUIC / Identity / Orleans continue according to the existing
architecture roadmap and its gates.
```

Multi-MapServer deployment is not a Phase 0 requirement. Lazy loading is not a Phase 0 MVP exit requirement. Telemetry is the first step; optimization follows evidence.
