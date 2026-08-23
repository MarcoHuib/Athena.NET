# Athena.NET Distributed Game Engine Architecture with Microsoft Orleans

## Status

This document records the intended **Phase 4 backend architecture** for Athena.NET.

It starts only after all earlier gates are complete:

```text
Phase 0  stock-iRO MVP is stable
Phase 1  Athena.Client TCP proxy is stable
Phase 2  Athena.Gateway + QUIC is stable
Phase 3  ASP.NET Core Identity mapping is stable
Phase 4  Orleans begins
```

Read together with:

- `architecture-roadmap.md`
- `client-gateway-architecture.md`
- `../ai/iro-2026-wire.md`

The supported game client remains the **unmodified official iRO Ragexe client**.

---

# Why Orleans comes last

Orleans changes the internal game-engine architecture.

It should not be introduced while Athena.NET is still proving:

- unknown stock-iRO packets;
- movement correctness;
- map transitions;
- chat;
- proxy buffering;
- QUIC behavior;
- account/session mapping.

By Phase 4, the client-facing edge should already be stable:

```text
Ragexe
 -> Athena.Client
 -> QUIC
 -> Athena.Gateway
 -> iRO protocol adapters
```

That stable boundary lets the backend evolve independently.

---

# Core idea

Ragexe continues to believe it is speaking to a traditional Ragnarok server architecture.

Athena.NET does not need to preserve that physical topology internally forever.

```text
CLIENT VIEW

Login
 -> Char
 -> Map


ATHENA INTERNAL VIEW

Athena.Gateway
 -> iRO protocol adapters
 -> semantic game commands/results
 -> Orleans distributed game engine
```

Orleans becomes the **backend game-engine runtime**.

It does not replace QUIC.

It does not run on the player's PC.

It is not exposed directly to Ragexe.

---

# Orleans is not one giant monolith

Moving game state into Orleans does **not** mean:

```text
LoginServer + CharServer + MapServer
             |
             v
       one huge server
```

The intended production model is:

```text
                  ORLEANS CLUSTER

       +-----------+-----------+-----------+
       |           |           |           |
       v           v           v           v
     Silo A      Silo B      Silo C      Silo D
       |           |           |           |
   activations  activations  activations  activations
```

One logical game world can run across many silo processes/containers/nodes.

For local development:

```text
one silo
```

may be sufficient.

For production:

```text
multiple silos
```

can host the same logical Athena game engine.

Application code addresses logical grain identities rather than hard-coded machine addresses.

---

# Boundary between iRO and the game engine

This is one of the most important long-term design rules.

The Orleans game engine should not contain packet IDs.

Example:

```text
Ragexe sends:
0x035F
    |
    v
iRO Map Adapter
    |
    v
MoveCharacterCommand
{
    CharacterId,
    Destination
}
    |
    v
Orleans game engine
    |
    v
MovementResult
    |
    v
iRO Map Adapter
    |
    v
serialize 0x0087
    |
    v
Ragexe
```

Therefore:

```text
PACKETVER / packet layouts
= iRO protocol boundary

movement / combat / world rules
= game engine
```

A future iRO wire adjustment should not require changing the Orleans domain model merely because a packet layout changes.

---

# Relationship to Identity and GameSession

Phase 3 establishes:

```text
IdentityUser
 -> AthenaGameAccount
 -> AthenaGameSession
```

Orleans should consume game identity, not passwords.

Conceptually:

```text
ASP.NET Core Identity
        |
        v
AthenaGameAccount
        |
        v
AthenaGameSession
        |
        v
Athena.Gateway / protocol edge
        |
        v
PlayerSessionGrain / game engine
```

Passwords remain an authentication-edge concern.

The Orleans game engine should never receive:

- the user's plaintext password;
- Identity password hashes;
- external IdP credentials.

---

# Initial grain strategy

Do not create a grain merely because an object exists in Ragnarok.

A distributed call is still a distributed call.

The first design should favor **coarse consistency boundaries for hot gameplay**.

---

## MapInstanceGrain — primary real-time boundary

A `MapInstanceGrain` is the proposed initial owner of active map simulation.

Example keys:

```text
prontera:channel-1
iz_int03:channel-1
instance:88421
```

Possible active state:

```text
MapInstanceGrain
├── PlayerPresence[]
├── positions / movement
├── MobState[]
├── NpcState[]
├── WarpState[]
├── visibility state
├── combat-local state
├── spawn state
├── map-local timers
└── transient script/runtime state
```

Why group these?

Because movement, nearby actors, mobs, NPCs, warps, and combat are highly chatty with one another.

Preferred:

```text
Map protocol adapter
 -> MapInstanceGrain.Move(...)
 -> result
```

Avoid:

```text
Map adapter
 -> PlayerGrain
 -> PositionGrain
 -> MapCellGrain
 -> CollisionGrain
 -> NpcGrain
 -> every nearby MobGrain
```

for every movement action.

Locality is more important than actor purity.

---

## CharacterGrain — durable character domain

A `CharacterGrain` can represent the character's durable identity/state.

Possible responsibilities:

- character identity;
- level/job/progression;
- persistent stats;
- inventory/equipment ownership;
- learned skills;
- currencies;
- persistent quests;
- save point;
- durable account ownership;
- offline state.

Do not require a `CharacterGrain` network call for every footstep.

When a character enters a map:

```text
CharacterGrain
 -> durable runtime snapshot
 -> MapInstanceGrain
```

During active gameplay, hot state can remain local to the map runtime and synchronize at defined authoritative boundaries.

---

## PlayerSessionGrain — live game session

After Phase 3, the live player already has an `AthenaGameSession`.

A future `PlayerSessionGrain` may coordinate game-session state such as:

- AccountId;
- selected CharId;
- current map/instance;
- online/offline state;
- reconnect state;
- active gateway/session association;
- map transfer coordination.

This is game-session state, not password/authentication state.

---

## PartyGrain

Party is a natural actor boundary:

- party identity;
- members;
- leader;
- invites;
- sharing/loot policy;
- cross-map party state.

---

## GuildGrain

Likewise:

```text
GuildGrain
├── members
├── ranks
├── permissions
├── guild EXP
├── notice
├── alliances
└── persistent guild state
```

---

## InstanceGrain

An instance/dungeon has a lifecycle independent from the physical node running it.

Possible state:

- instance identity;
- owning party/player;
- lifecycle;
- map list;
- objective/progression state;
- expiration;
- cleanup.

An `InstanceGrain` can coordinate one or more `MapInstanceGrain` instances.

---

## AccountGrain — optional

An `AccountGrain` may be useful for game-specific account state:

- account flags;
- game entitlements;
- character slots;
- game-wide settings.

Do not duplicate ASP.NET Core Identity credential responsibilities inside it.

---

# NPCs and mobs

Do not start with one grain per ordinary mob.

Normal mobs constantly interact with map-local state.

Initial design:

```text
MapInstanceGrain owns MobState[]
```

Similarly, normal NPC runtime state can initially remain map-local.

Create an independent grain only where the domain lifecycle genuinely crosses map/local boundaries, for example:

- global world boss coordination;
- persistent global events;
- globally unique persistent entity;
- player-owned persistent subsystem.

The fact that Orleans supports many virtual actors is not a reason to turn every small game object into a distributed boundary.

---

# Concurrency model

A grain activation provides a serialized execution boundary.

For one map this can simplify operations such as:

```text
move player
spawn mob
apply damage
trigger warp
despawn actor
NPC interaction
```

without arbitrary shared-memory locks around every collection.

Different maps/instances remain parallel:

```text
prontera       -> one activation
payon          -> another activation
instance 884   -> another activation
```

and can be hosted on different silos.

This is the intended scaling model.

---

# Hot maps

A single `MapInstanceGrain` is intentionally a single consistency boundary.

Do not pre-partition every map.

First measure realistic Ragnarok loads.

If one map becomes a hotspot, possible later strategies include:

### Channels

```text
prontera:1
prontera:2
prontera:3
```

### Dedicated placement

Allow selected hot map activations to run on suitable silo capacity.

### Region partitioning

Only consider spatial partitioning when profiling proves it necessary. Cross-region movement/visibility/combat makes this substantially more complicated.

---

# Timers and scheduling

Use the correct mechanism for the required lifetime.

## Activation-local timers

Good for:

- mob AI;
- spawn processing;
- short map effects;
- combat timers;
- short gameplay scheduling.

## Durable reminders

Good for lower-frequency work which must survive activation loss, such as:

- instance expiration;
- scheduled world tasks;
- daily/weekly reset coordination.

Do not use durable reminders as a high-frequency game tick.

---

# Persistence

## Start with SQL Server

Orleans does not require Athena.NET to migrate to PostgreSQL, MongoDB, Cassandra, or another database.

The lowest-risk first design is to keep the current SQL Server-based persistence approach and use compatible Orleans infrastructure/persistence where appropriate.

Conceptually:

```text
Orleans cluster
    |
    +-- cluster membership
    +-- selected grain persistence
    +-- reminders
    |
SQL Server
```

Keep Orleans infrastructure state logically separate from the game-domain schema where useful.

## Do not persist every game tick

The database is not the movement loop.

Categorize state:

### Durable

- character progression;
- inventory ownership;
- currencies;
- quest milestones;
- guild/party persistent state;
- instance milestones which must survive failure.

### Reconstructible

- static map definitions;
- static NPC definitions;
- spawn definitions.

### Ephemeral

- current interpolation;
- temporary AI target;
- short-lived combat decisions;
- transient visibility calculations.

Persist at meaningful consistency boundaries, not after every footstep.

## NoSQL remains optional

Introduce another store only when a concrete access pattern justifies it.

Examples might later include:

- high-volume analytics;
- time-series telemetry;
- cache acceleration;
- specialized graph queries.

Distributed application architecture does not automatically imply NoSQL.

---

# Failure model

If a silo fails, Orleans can activate a logical grain elsewhere, but only persisted/reconstructible state can be recovered.

Do not assume:

```text
Orleans == zero state loss automatically
```

The game engine must define which state is durable and when it is committed.

For example:

```text
MapInstanceGrain activation disappears
    |
    +-- static map data reloads
    +-- durable character snapshot reloads
    +-- ephemeral AI/interpolation may be reconstructed/discarded
```

Failure behavior is part of game design.

---

# Idempotency and important mutations

Distributed calls can fail or be retried.

High-value operations should have explicit duplicate/idempotency rules where required.

Examples:

```text
GiveItem
SpendCurrency
TradeCommit
ClaimReward
CreateGuild
```

may require operation IDs or domain-specific commit rules.

Do not put distributed transactions around every movement/combat call.

Keep hot gameplay local whenever possible.

---

# Queues, streams, RabbitMQ, Kafka, and Dapr

Do not introduce external messaging into the mandatory gameplay hot path.

Bad:

```text
movement
 -> RabbitMQ
 -> consumer
 -> map grain
```

Preferred:

```text
movement
 -> MapInstanceGrain
```

Asynchronous systems can still be useful after authoritative gameplay mutations for:

- telemetry;
- audit;
- analytics;
- notifications;
- moderation;
- achievements;
- external integrations.

Orleans Streams can also be evaluated where pub/sub semantics genuinely fit, but not every C# call needs to become an event.

---

# On-premises deployment

The architecture must remain fully deployable on-premises.

Possible production topology:

```text
                     ON-PREM KUBERNETES

+--------------------------------------------------+
| Athena.Gateway pods                             |
|                                                  |
| Athena Orleans silo pods                        |
|   Silo A   Silo B   Silo C   ...                |
|                                                  |
| SQL Server                                      |
| Observability stack                             |
+--------------------------------------------------+
```

Azure is not required merely to use Orleans.

Local development should remain simpler, for example through .NET Aspire with one silo and the existing supporting services.

---

# Observability

Distributed game-engine migration must be measurable.

Track:

- silo count/health;
- grain activations;
- calls by grain type;
- call p50/p95/p99;
- timeouts/failures;
- scheduler/queue pressure;
- CPU/memory;
- hot map identities;
- persistence latency;
- Gateway -> game-engine latency.

For a gameplay operation:

```text
iRO packet received
 -> parsed
 -> semantic command created
 -> Orleans call
 -> grain starts
 -> grain completes
 -> result mapped
 -> iRO response serialized
 -> response sent
```

should be traceable without logging credentials or sensitive packet bodies.

---

# Orleans migration plan

Phase 4 itself should still be incremental.

## Phase 4A — Add Orleans beside the existing backend

Introduce:

```text
Athena.Game.Abstractions
Athena.Game.Orleans
Athena.Silo
```

Run one silo locally.

Do not immediately rewrite MapServer.

Use a bounded subsystem to prove:

- grain contracts;
- serialization;
- persistence;
- tests;
- observability;
- restart/failure behavior.

## Phase 4B — Move low-risk coordination first

Good candidates may include:

- PlayerSession coordination;
- Party;
- Guild;
- Instance lifecycle.

Choose based on the state of Athena.NET at that time.

## Phase 4C — Prototype MapInstanceGrain

Build an Orleans-backed map runtime behind an interface.

Compare it against the existing stable MapServer implementation for:

- movement latency;
- NPC interactions;
- warps;
- chat fan-out;
- mob load;
- allocations/GC;
- failure behavior.

Do not accept an architectural rewrite that reintroduces the lag/rubber-banding solved in Phase 0.

## Phase 4D — Move durable game-domain actors

Introduce `CharacterGrain` and other domain actors where ownership is clear.

Define one authoritative owner for mutable state.

Avoid duplicated state without explicit synchronization rules.

## Phase 4E — Make Orleans the game-domain authority

Eventually the iRO Login/Char/Map layers can become thinner compatibility/protocol adapters.

The game rules live behind semantic interfaces in Orleans.

Ragexe still sees the same stock protocol.

## Phase 4F — Multi-silo production

Move from one development silo to multiple on-prem production silos.

Prove:

- clustering;
- node failure;
- grain reactivation;
- persistence;
- rolling deployments;
- load behavior.

Only then add more sophisticated placement or specialized storage.

---

# Proposed long-term solution boundaries

Names are illustrative and should not force an early repository restructure.

```text
src/
├── Athena.Gateway/
├── Athena.Protocol.Iro/
│   ├── Login/
│   ├── Char/
│   └── Map/
│
├── Athena.Identity/
│   ├── Identity/
│   ├── GameAccounts/
│   └── GameSessions/
│
├── Athena.Game.Abstractions/
│   ├── Commands/
│   ├── Results/
│   ├── Models/
│   └── GrainInterfaces/
│
├── Athena.Game.Orleans/
│   ├── PlayerSessionGrain/
│   ├── CharacterGrain/
│   ├── MapInstanceGrain/
│   ├── PartyGrain/
│   ├── GuildGrain/
│   └── InstanceGrain/
│
└── Athena.Silo/
```

Existing `LoginServer`, `CharServer`, and `MapServer` remain during migration and should only be thinned or reorganized when the replacement path is proven.

---

# Locked architectural decisions

### ORL-001 — Orleans begins after Identity

Do not mix the Identity migration and Orleans game-engine migration.

### ORL-002 — QUIC remains the edge transport

Orleans does not replace Client <-> Gateway QUIC.

### ORL-003 — Orleans is never exposed to Ragexe

Only trusted server-side components access the cluster.

### ORL-004 — Packet IDs stay out of the game engine

iRO adapters translate wire requests/responses to semantic commands/results.

### ORL-005 — MapInstanceGrain is the initial hot boundary

Start with map-local simulation grouped together rather than one grain per tiny entity.

### ORL-006 — No grain per ordinary mob by default

Keep high-frequency map-local entities local until measurement proves otherwise.

### ORL-007 — SQL Server first

Do not change distributed runtime and primary database technology at the same time without a concrete requirement.

### ORL-008 — No queue in movement

RabbitMQ/Kafka/Dapr/streams are not mandatory hops for synchronous gameplay.

### ORL-009 — One silo in development is fine

Multiple silos are a production/scaling concern, not a requirement for every developer run.

### ORL-010 — The Phase 0 performance baseline survives

Every Orleans hot-path migration is compared against the already-stable non-Orleans implementation.

---

# Final target architecture

```text
                            PLAYER

                         Ragexe.exe
                             |
                      localhost iRO TCP
                             |
                             v
                       Athena.Client
                             |
                        QUIC/TLS 1.3
                             |
========================== INTERNET ==========================
                             |
                             v
                       Athena.Gateway
                             |
              +--------------+--------------+
              |                             |
              v                             v
       ASP.NET Core Identity          iRO protocol edge
              |                             |
              v                             |
       AthenaGameAccount                    |
              |                             |
              +------> AthenaGameSession <--+
                                            |
                                  semantic commands/results
                                            |
                                            v
                              Microsoft Orleans Cluster
                          +---------+---------+---------+
                          |         |         |         |
                          v         v         v         v
                        Silo A    Silo B    Silo C    Silo D
                          |
                          v
                        SQL Server
```

The strategic objective is simple:

**keep the stock iRO protocol at the edge, while gaining complete freedom to build a modern distributed .NET game engine behind it.**
