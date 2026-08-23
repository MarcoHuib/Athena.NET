# Athena.NET Architecture Roadmap

## Status

This document records the agreed implementation order for Athena.NET.

The order is deliberate. Each phase must be stable and measurable before the next architectural layer is introduced. The goal is to avoid debugging the stock iRO protocol, transport changes, identity changes, and a distributed game-engine rewrite at the same time.

The supported target remains the **unmodified official International Ragnarok Online (iRO) client**.

This roadmap is the parent document for:

- `client-gateway-architecture.md` — the local Athena.Client, Athena.Gateway, QUIC, and modern Identity mapping.
- `orleans-game-engine-architecture.md` — the later Microsoft Orleans distributed backend/game-engine architecture.
- `../ai/iro-2026-wire.md` — the authority for verified stock-iRO wire behavior.

---

# Guiding rule

**Do not add the next architectural layer until the current layer is known-good.**

The intended evolution is:

```text
Phase 0
Stock iRO MVP over direct TCP
        |
        v
Phase 1
Athena.Client as a minimal TCP proxy
        |
        v
Phase 2
Athena.Gateway + QUIC/TLS 1.3
        |
        v
Phase 3
ASP.NET Core Identity + Athena game-session mapping
        |
        v
Phase 4
Microsoft Orleans distributed game engine
```

Security hardening beyond normal secure transport and server-side authentication is intentionally **not part of these phases yet**. Anti-tamper, device attestation-like signals, TPM/device keys, process verification, challenge/response schemes, bot-risk scoring, and similar measures belong to a later security-hardening project.

---

# Phase 0 — Complete the stock-iRO MVP

## Purpose

Finish Athena.NET as a stable stock-iRO server before inserting any proxy, gateway, QUIC transport, Identity migration, or Orleans runtime.

The baseline topology remains:

```text
Ragexe.exe
    |
    | direct legacy iRO TCP
    |
    +--> LoginServer
    +--> CharServer
    +--> MapServer
```

This direct path is the **golden diagnostic baseline**.

## MVP definition

The MVP is not complete merely because Ragexe reaches the first map.

The MVP is complete only when all of the following are working reliably with the stock client:

### Login and character flow

- Ragexe can log in repeatedly against Athena.NET.
- LoginServer returns the correct stock-iRO world response.
- CharServer authentication is stable.
- Existing characters can be loaded and selected.
- New characters can be created and persisted.
- A selected character can hand off to MapServer and enter the world.

### World and map flow

- MapServer entry is stable.
- Character spawn is stable.
- Movement is stable.
- Same-server and cross-server/map transitions are understood and implemented where required.
- Warps work from real world data rather than capture replay.
- Visible warp/NPC actors behave correctly.
- NPC interaction works.
- The required Ragnarok maps/world data are loadable and reachable.
- Map transitions do not require client modification.

### In-game chat

The stock client's in-game chat path must work end-to-end.

At minimum, the normal gameplay chat needed to make the world usable must be implemented and verified with Ragexe. More advanced social features can continue later, but basic chat cannot remain broken when the MVP is declared complete.

### Networking quality

The current lag/rubber-banding glitches must be resolved **before** the MVP is considered complete.

In particular:

- movement must not pause and then burst forward;
- packet processing must not accumulate unexplained bursts;
- server writes must not be delayed unexpectedly;
- MapServer loop/tick behavior must be measurable;
- disconnect/reconnect behavior must be predictable.

Instrument at least:

```text
socket receive
 -> packet framed
 -> packet parsed
 -> gameplay handler begins
 -> world state updated
 -> response serialized
 -> socket write
```

Use high-resolution timestamps so a delay can be assigned to the correct stage.

Where useful, also observe:

- handler duration;
- queue delay;
- thread-pool starvation;
- GC pauses;
- server tick duration;
- socket write delay;
- packet arrival bursts.

## Phase 0 exit gate

Do **not** start Phase 1 until:

```text
Login
+ character create/select
+ MapServer entry
+ maps
+ movement
+ warps
+ NPCs
+ in-game chat
+ stable network timing
```

are reliable enough that direct TCP can be used as a trustworthy comparison baseline.

Record baseline latency/behavior before moving on.

---

# Phase 1 — Introduce Athena.Client over TCP

## Purpose

Add the local proxy as the smallest possible architectural change.

Topology:

```text
Ragexe.exe
    |
    | legacy TCP to localhost
    v
Athena.Client
    |
    | TCP
    v
Athena.NET Login / Char / Map servers
```

## Rules

`Athena.Client` is initially a **protocol-opaque transport component**.

It should:

- listen only on loopback;
- expose the local Login/Char/Map endpoints expected by Ragexe;
- create the required upstream TCP connections;
- copy bytes bidirectionally;
- preserve packet boundaries only through normal TCP stream semantics;
- add diagnostics for forwarding latency and byte counts.

It should **not**:

- parse `0x0064` or other Ragnarok packets;
- understand PACKETVER;
- authenticate users;
- hash passwords;
- allocate AccountIds;
- rewrite LoginId values;
- contain gameplay logic;
- perform anti-cheat/security hardening.

Client-facing Login/Char/Map handoff responses may advertise loopback endpoints so Ragexe reconnects to `Athena.Client`, while the real backend topology remains outside Ragexe.

## Required transport modes

Keep the direct baseline permanently available:

```text
TransportMode.Direct
TransportMode.ProxyTcp
```

Later:

```text
TransportMode.ProxyQuic
```

This is an architectural debugging feature, not a temporary convenience.

## Phase 1 exit gate

Proceed only when:

- the complete MVP works through `Athena.Client`;
- direct and proxied behavior are functionally equivalent;
- movement latency does not regress materially;
- no new buffering/rubber-banding is introduced;
- failures can be diagnosed by switching back to `Direct`.

---

# Phase 2 — Add Athena.Gateway and QUIC

## Purpose

Modernize the WAN transport without changing the stock iRO protocol or game-domain behavior.

Topology:

```text
Ragexe.exe
    |
    | legacy TCP, localhost only
    v
Athena.Client
    |
    | QUIC + TLS 1.3
    v
Athena.Gateway
    |
    | private server transport
    v
LoginServer / CharServer / MapServer
```

## QUIC responsibility

QUIC is the **client-to-server transport**.

It provides:

- TLS 1.3 encryption;
- one persistent modern connection;
- multiplexed streams;
- clean connection/session management;
- a single controlled public edge.

Suggested streams:

```text
QUIC connection
├── CONTROL
├── LOGIN
├── CHAR
└── MAP
```

`LOGIN`, `CHAR`, and `MAP` carry opaque stock-iRO byte streams.

The `CONTROL` stream is Athena-owned and may carry transport/session metadata required by the gateway. Do not turn it into a gameplay protocol.

## Gateway responsibility

`Athena.Gateway` should initially:

- terminate QUIC/TLS;
- associate streams with one client connection;
- route LOGIN/CHAR/MAP byte streams;
- keep the real Ragnarok services off the public internet;
- provide connection-level diagnostics and rate limits.

The gateway should not yet be a game engine and should not duplicate packet/business logic from LoginServer, CharServer, or MapServer.

## Phase 2 exit gate

Proceed only when:

- the complete Phase 0 MVP works through Gateway + QUIC;
- LOGIN, CHAR, and MAP handoffs remain correct;
- direct, TCP-proxy, and QUIC-proxy modes can still be compared;
- movement/chat/warp behavior is unchanged;
- p50/p95/p99 transport overhead is understood;
- the new transport has no unexplained stalls or bursts.

---

# Phase 3 — Add ASP.NET Core Identity and modern account mapping

## Purpose

Replace legacy password storage/authentication with ASP.NET Core Identity while preserving the exact stock-iRO client request/response behavior that already works.

**Do not redesign Ragexe authentication. Adapt the working iRO flow to a modern server-side identity model.**

The key separation is:

```text
legacy iRO wire contract
        !=
modern server-side identity model
```

## Login request

Ragexe already sends the verified login request:

```text
0x0064 request

packet id      2 bytes
version        4 bytes
username      24 bytes
password      24 bytes
client type    1 byte

total         55 bytes
```

This is a **request**.

It is not an Identity model and it is not a response.

Flow:

```text
Ragexe
    |
    | 0x0064 request
    v
Athena.Client
    |
    | opaque bytes over QUIC
    v
Athena.Gateway / iRO Login Adapter
    |
    | parse
    v
LoginRequest
{
    Version,
    Username,
    Password,
    ClientType
}
    |
    v
Authentication service
    |
    v
ASP.NET Core Identity
```

## Password handling

There is **no client-side password hashing** in this architecture.

The stock client supplies the password in its legacy request to the local Athena.Client connection. Athena.Client transports those bytes through the encrypted QUIC/TLS channel.

Password verification/hashing happens only server-side through ASP.NET Core Identity.

```text
Ragexe
 -> localhost TCP
 -> Athena.Client
 -> QUIC/TLS
 -> server-side Login Adapter
 -> ASP.NET Core Identity password verification
```

Do not transform the password into a reusable client-side hash.

Do not persist or log the plaintext password.

## Identity-to-game-account mapping

ASP.NET Core Identity owns **human authentication**.

Ragnarok requires a numeric game account identity.

Keep those separate:

```text
AspNetUsers
--------------------------
Id = IdentityUserId
UserName
PasswordHash
...

          1 : 1

AthenaGameAccount
--------------------------
AccountId          uint32
IdentityUserId
RagnarokLoginName
Sex
CharacterSlots
game/account flags
...
```

`AccountId` remains the stable numeric identifier used by the existing Ragnarok game data and wire protocol.

`IdentityUserId` remains the Identity primary key.

`RagnarokLoginName` can decouple the stock client's 24-byte username field from the Identity username/email model where useful.

## Authentication result versus iRO response

After Identity succeeds, create an application result and a game session.

Example:

```text
AuthenticationResult
{
    IdentityUserId,
    AccountId
}
```

Then:

```text
AthenaGameSession
{
    GameSessionId,      // Athena-internal
    IdentityUserId,
    AccountId,
    LoginId1,
    LoginId2,
    SelectedCharId,
    State,
    ExpiresAt
}
```

Only after this server-side work does the iRO Login Adapter serialize the **response** expected by Ragexe.

```text
Identity success
    |
    v
AthenaGameAccount
    |
    v
AthenaGameSession
    |
    v
iRO Login Response model
    |
    v
0x0A4D response
    |
    v
Ragexe
```

This keeps request and response models distinct.

## Legacy session mapping

Ragexe does not need to understand `IdentityUserId` or `GameSessionId`.

It continues to use the legacy values it already understands.

### Login response

Athena issues/serializes the iRO login-session values such as:

```text
AccountId
LoginId1
LoginId2
Sex
world/server handoff data
```

in the proven stock-iRO response.

### Char request

Ragexe then opens the Char connection and sends the existing `0x0065` request containing the legacy session values.

The server maps:

```text
AccountId
+ LoginId1
+ LoginId2
```

back to the corresponding `AthenaGameSession`.

`Athena.Client` does not rewrite them.

### Character select

When the client selects a character, the server binds the selected `CharId` to the authoritative game session.

### Map request

On MapServer entry, the proven iRO map-auth header contains:

```text
AccountId
CharId
LoginId1
```

The server validates those against the existing game-session/map-handoff state.

Opaque official authentication material in the remainder of the stock packet must remain opaque unless future Athena requirements prove it is needed. Athena Identity must **not** be coupled to Epic/EOS token semantics merely because the stock executable happens to carry such data.

The current working Athena.NET Login -> Char -> Map behavior is the compatibility reference.

## Out of scope for Phase 3

Do not expand this phase into a full anti-cheat/security-hardening project.

Explicitly deferred:

- launcher/process relationship enforcement;
- Ragexe executable hash enforcement;
- runtime module inspection;
- Native AOT as anti-tamper;
- device keys;
- TPM-backed identity;
- signed custom handshakes;
- custom server challenge schemes;
- additional replay-protection protocols beyond what the chosen transport/session design actually requires;
- risk scoring;
- server-side bot-behavior analytics.

These may be designed later after the basic client/gateway/identity architecture is stable.

Browser/OIDC/external IdP login is also **optional future functionality**, not required for this phase. ASP.NET Core Identity can first authenticate the username/password received through the existing stock Ragexe login UI.

## Phase 3 exit gate

Proceed to Orleans only when:

- ASP.NET Core Identity has replaced legacy password authentication/storage;
- `0x0064` maps cleanly to a server-side `LoginRequest`;
- password verification is server-side only;
- `IdentityUserId <-> AthenaGameAccount.AccountId` mapping is stable;
- successful authentication creates an Athena game session;
- the iRO response is generated separately from the request model;
- CharServer and MapServer can resolve the correct game session through their existing legacy identifiers;
- Login -> Char -> Map still works through Athena.Client + QUIC + Gateway;
- no client modification is required.

---

# Phase 4 — Introduce Microsoft Orleans

## Purpose

Only after the stock client, transport, gateway, and identity mapping are stable should Athena.NET begin changing the backend game-engine model.

At that point the edge is:

```text
Ragexe
 -> Athena.Client
 -> QUIC
 -> Athena.Gateway
 -> iRO protocol adapters
```

Behind that stable compatibility boundary, the game backend can evolve toward Microsoft Orleans:

```text
iRO protocol adapters
 -> strongly typed game commands/results
 -> Orleans distributed game engine
```

See `orleans-game-engine-architecture.md`.

## Phase 4 principle

Do not rewrite Login, Char, Map, transport, Identity, and the game engine simultaneously.

Introduce Orleans incrementally behind interfaces and compare every new implementation with the known-good backend.

---

# Later security-hardening project

The earlier security ideas are deliberately preserved as **later work**, but are not prerequisites for Phase 1–4.

Potential later topics include:

```text
Athena.Launcher lifecycle ownership
Ragexe process/socket ownership checks
executable/version validation
code signing / Native AOT
device-bound keys
TPM with software fallback
signed connection challenges
advanced replay controls
client-risk signals
server-side bot/behavior detection
anti-tamper
production edge hardening
```

These should receive their own threat model and architecture document when Athena.NET reaches that stage.

---

# Final target sequence

```text
TODAY / MVP

Ragexe
 -> direct LoginServer
 -> CharServer
 -> MapServer
 -> complete/stable stock-iRO world baseline


PHASE 1

Ragexe
 -> localhost TCP
 -> Athena.Client
 -> TCP
 -> Athena.NET


PHASE 2

Ragexe
 -> localhost TCP
 -> Athena.Client
 -> QUIC/TLS 1.3
 -> Athena.Gateway
 -> Athena.NET


PHASE 3

Ragexe 0x0064 request
 -> Athena.Client
 -> QUIC
 -> Gateway / iRO adapter
 -> LoginRequest
 -> ASP.NET Core Identity
 -> AthenaGameAccount
 -> AthenaGameSession
 -> iRO 0x0A4D response
 -> Ragexe
 -> normal Char/Map session continuation


PHASE 4

Ragexe
 -> Athena.Client
 -> QUIC
 -> Athena.Gateway
 -> iRO protocol boundary
 -> Microsoft Orleans distributed game engine
```

This order is part of the architecture. Changing it should require an explicit reason because the phase gates exist to keep Athena.NET debuggable.
