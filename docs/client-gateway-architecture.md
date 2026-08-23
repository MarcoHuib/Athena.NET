# Athena.Client, Gateway, QUIC, and Identity Architecture

## Status

This document records the agreed future client/edge architecture for Athena.NET.

It deliberately starts **after the stock-iRO MVP is complete**. It must not be treated as a reason to insert additional network layers while core Ragnarok behavior is still being debugged.

Read together with:

- `architecture-roadmap.md` — implementation order and phase gates.
- `orleans-game-engine-architecture.md` — the later backend game-engine architecture.
- `../ai/iro-2026-wire.md` — verified stock-iRO wire facts.

The supported client remains the **unmodified official iRO Ragexe executable**.

---

# Scope

This document covers four steps:

1. a minimal local TCP `Athena.Client`;
2. `Athena.Gateway` and QUIC/TLS 1.3;
3. server-side mapping from the stock iRO login flow to ASP.NET Core Identity;
4. the stable boundary that Orleans can later sit behind.

It does **not** currently cover deep anti-cheat or client hardening.

---

# Phase prerequisite — the MVP must already be stable

Before this architecture is introduced, direct Ragexe -> Athena.NET must already provide a reliable game baseline.

Required:

- LoginServer login works;
- CharServer authentication works;
- character creation works;
- character selection works;
- MapServer entry works;
- movement is stable;
- lag/rubber-banding glitches are resolved;
- warps work;
- NPCs work;
- required maps/world data work;
- in-game chat works.

Direct TCP remains the permanent golden comparison path.

---

# Core design principles

## 1. Keep Ragexe stock

Ragexe continues speaking the protocol it already understands.

It is not taught:

- QUIC;
- ASP.NET Core Identity;
- Athena GameSession IDs;
- Orleans;
- a custom gameplay protocol.

Compatibility remains outside the executable.

## 2. Keep Athena.Client protocol-opaque

`Athena.Client` is a local transport sidecar.

It should not become another Ragnarok server implementation.

For Ragnarok traffic:

```text
Ragexe
 -> raw iRO TCP bytes
Athena.Client
 -> transport
Athena.Gateway / Athena.NET
 -> server-side iRO parsing
```

`Athena.Client` does not need to know that a byte stream contains `0x0064`, `0x0065`, `0x035F`, or `0x0C1F`.

That keeps PACKETVER and iRO protocol knowledge server-side.

## 3. Requests and responses are separate models

A client packet is a request.

A server packet is a response.

The application/domain model in between is neither.

For example:

```text
0x0064 client request
      |
      v
LoginRequest
      |
      v
Authentication
      |
      v
AuthenticationResult
      |
      v
AthenaGameSession
      |
      v
IroLoginResponse
      |
      v
0x0A4D server response
```

Do not use one DTO/model for both directions.

## 4. Password hashing is server-side only

The stock login request contains the user's supplied password.

Do not add an Athena.Client-side password hash.

The client side has no password-hash responsibility.

```text
Ragexe
 -> localhost
 -> Athena.Client
 -> QUIC/TLS
 -> server
 -> ASP.NET Core Identity
 -> password verification/hash logic
```

The plaintext password must not be logged or persisted.

## 5. Identity mapping is server-side

`Athena.Client` never decides who the player is.

The server maps:

```text
stock Ragnarok login name
 -> ASP.NET Core Identity user
 -> AthenaGameAccount
 -> numeric Ragnarok AccountId
 -> AthenaGameSession
```

The server then sends Ragexe the normal legacy session values it already understands.

---

# Phase 1 — Minimal TCP Athena.Client

## Target topology

```text
                     PLAYER PC

                  Ragexe.exe
                      |
            legacy TCP to loopback
                      |
                      v
                Athena.Client
                      |
                 upstream TCP
                      |
                      v
        LoginServer / CharServer / MapServer
```

## Local endpoints

Ragexe should connect only to loopback-facing Athena endpoints.

Conceptually:

```text
127.0.0.1:<login-port>
127.0.0.1:<char-port>
127.0.0.1:<map-port>
```

The exact ports are configuration.

Server responses which tell Ragexe where to connect next can advertise the local Athena.Client endpoints.

The real backend endpoint remains an Athena concern.

## Responsibilities

Phase 1 `Athena.Client` does only:

- loopback TCP listeners;
- upstream TCP connection management;
- bidirectional byte forwarding;
- connection lifecycle;
- forwarding diagnostics;
- latency/byte counters.

No Identity.

No packet parsing.

No security-hardening project.

No gameplay logic.

## Development modes

Keep:

```text
TransportMode.Direct
TransportMode.ProxyTcp
```

The same game tests should pass through both.

---

# Phase 2 — Athena.Gateway + QUIC/TLS 1.3

## Target topology

```text
                     PLAYER PC

                  Ragexe.exe
                      |
                 localhost TCP
                      |
                      v
                Athena.Client
                      |
                 QUIC/TLS 1.3
                      |
==================== INTERNET ====================
                      |
                      v
                Athena.Gateway
                      |
             private backend network
                      |
          +-----------+-----------+
          |           |           |
          v           v           v
      LoginServer  CharServer  MapServer
```

## Why QUIC

QUIC modernizes the WAN transport while leaving Ragexe unchanged.

Expected properties:

- encrypted transport through TLS 1.3;
- persistent client-to-gateway connection;
- multiple logical streams;
- stream isolation;
- modern connection management;
- one intentional public game edge.

QUIC should not be sold as a magical ping reducer. Its value is architecture, encryption, multiplexing, and connection behavior.

## Suggested stream model

```text
QUIC connection
├── CONTROL
├── LOGIN
├── CHAR
└── MAP
```

`LOGIN`, `CHAR`, and `MAP` carry opaque iRO stream bytes.

The gateway may use Athena-owned metadata to identify a stream as LOGIN/CHAR/MAP before forwarding it, but it should not require `Athena.Client` to parse Ragnarok packets.

## Gateway responsibility

Before Identity is introduced, Gateway responsibilities remain narrow:

- terminate QUIC/TLS;
- own the public connection;
- associate logical streams;
- route streams to the correct backend;
- enforce basic connection/rate limits;
- collect transport diagnostics;
- shield legacy backend ports from direct public exposure.

The gateway is not yet the game engine.

## Development modes

Keep:

```text
TransportMode.Direct
TransportMode.ProxyTcp
TransportMode.ProxyQuic
```

This allows immediate A/B diagnosis.

---

# Phase 3 — ASP.NET Core Identity integration

## Objective

Keep the exact client-visible iRO login/session flow that already works, but replace the legacy authentication implementation behind it with modern server-side Identity.

The client protocol is an adapter boundary.

---

# Stock iRO login request -> application request

The verified stock login request is:

```text
0x0064 request

packet id       2 bytes
version         4 bytes
username       24 bytes
password       24 bytes
client type     1 byte
-----------------------
total          55 bytes
```

`Athena.Client` does not parse it.

It arrives through the QUIC LOGIN stream at the server.

There the iRO Login Adapter maps it to an application request such as:

```text
LoginRequest
{
    Version,
    Username,
    Password,
    ClientType
}
```

This is a one-way mapping:

```text
wire request -> application request
```

It is not reused as the server response model.

---

# Server-side authentication

The application layer performs authentication:

```text
LoginRequest
     |
     v
AuthenticationService
     |
     v
ASP.NET Core Identity
```

ASP.NET Core Identity owns:

- password hashing/verification;
- account lockout policy;
- password storage;
- user lifecycle;
- related local account authentication concerns.

`Athena.Client` owns none of these.

## No client-side password hash

Do not implement:

```text
password
 -> hash in Athena.Client
 -> use hash as password
```

A client-generated reusable hash is still a credential.

Use the password received in the stock request only for the server-side authentication attempt, protected across the WAN by QUIC/TLS.

After authentication it should leave scope and must never be logged/persisted in plaintext.

---

# IdentityUser <-> AthenaGameAccount

ASP.NET Core Identity's user key should not replace the Ragnarok `uint32 AccountId`.

Use an explicit game-account bridge.

Conceptual model:

```text
AspNetUsers
{
    Id
    UserName
    PasswordHash
    ...
}

        1 : 1

AthenaGameAccount
{
    AccountId          uint32
    IdentityUserId
    RagnarokLoginName
    Sex
    CharacterSlots
    AccountFlags
    ...
}
```

Responsibilities:

```text
IdentityUserId
= modern human/account identity

AccountId
= stable Ragnarok/Athena game identity
```

Existing character/game data can continue using the numeric `AccountId`.

`RagnarokLoginName` allows the stock client's fixed login-name field to remain decoupled from future Identity username/email choices.

---

# Authentication result and GameSession

After Identity succeeds:

```text
AuthenticationResult
{
    IdentityUserId,
    AccountId
}
```

Then create a server-side session:

```text
AthenaGameSession
{
    GameSessionId,
    IdentityUserId,
    AccountId,
    LoginId1,
    LoginId2,
    SelectedCharId,
    State,
    ExpiresAt
}
```

`GameSessionId` is Athena-internal.

Ragexe does not need to know it.

`LoginId1`, `LoginId2`, and the numeric `AccountId` are the legacy compatibility identifiers Ragexe understands.

---

# Application result -> stock iRO response

The server creates a separate response model and serializes the proven successful-login response.

Conceptually:

```text
AuthenticationResult
       +
AthenaGameSession
       |
       v
IroLoginResponse
       |
       v
0x0A4D response
       |
       v
Ragexe
```

The exact stock-iRO serializer remains governed by verified wire evidence.

Do not make the response DTO identical to `LoginRequest`.

---

# Continuing the session through CharServer

After the successful login response, Ragexe already knows how to continue.

It opens its Char connection and sends the existing `0x0065` request containing the legacy account/session identifiers.

Server-side flow:

```text
Ragexe 0x0065
    |
    v
iRO Char Adapter
    |
    v
AccountId + LoginId1 + LoginId2 + Sex
    |
    v
lookup/validate AthenaGameSession
    |
    v
Char flow continues
```

The Identity password is no longer involved.

`Athena.Client` does not rewrite the identifiers.

---

# Character selection and MapServer mapping

When Ragexe selects a character:

```text
0x0066 request
 -> selected slot
 -> server resolves authoritative CharId
 -> AthenaGameSession.SelectedCharId = CharId
```

The server then sends the normal map handoff response.

For Ragexe, that response can advertise the local Athena.Client map endpoint.

The real backend target remains inside Athena routing.

On the following MapServer connection, the existing stock iRO map-auth request carries the proven legacy identifiers needed by Athena's current flow, including:

```text
AccountId
CharId
LoginId1
```

The server maps those to the already-established session/map handoff.

The large opaque remainder of the official stock MapServer-auth packet is **not part of the new ASP.NET Core Identity model**.

In particular:

- do not make Athena Identity depend on Epic/EOS tokens;
- do not introduce an EOS requirement merely because the stock executable carries official-service authentication material;
- preserve opaque bytes as required for compatibility;
- only give them semantics if future verified Athena behavior proves it necessary.

Athena.NET already has a working Login -> Char -> Map path. That working path is the reference for what must be preserved.

---

# Where the mapping lives

Correct:

```text
Athena.Gateway / server-side iRO adapters
    |
    +-- parse request
    +-- map to application request
    +-- authenticate through Identity
    +-- resolve AthenaGameAccount
    +-- create/resolve AthenaGameSession
    +-- build application result
    +-- serialize stock iRO response
```

Incorrect:

```text
Athena.Client
    +-- parse username/password
    +-- allocate AccountId
    +-- generate IdentityUserId
    +-- rewrite LoginId values
```

`Athena.Client` remains transport infrastructure.

---

# Browser/OIDC/external IdPs

Not required for this phase.

The first Identity implementation can use the username/password entered into the stock Ragexe login UI.

Later, external OIDC providers, passkeys, browser-based flows, or a richer launcher can be added as an additional authentication path.

Those future flows should terminate in the same server-side concepts:

```text
IdentityUser
 -> AthenaGameAccount
 -> AthenaGameSession
```

They must not force the game-domain model to depend on one IdP.

---

# Security-hardening boundary

This document intentionally stops before deep client hardening.

Deferred to a later security design:

- launcher/Ragexe process binding;
- executable hashes;
- runtime inspection;
- Native AOT for anti-tamper;
- code-signing strategy beyond ordinary software distribution;
- device keys;
- TPM;
- signed custom challenge/response;
- advanced replay protocols;
- risk scoring;
- anti-OpenKore heuristics;
- server-side bot-behavior analysis.

QUIC/TLS and normal server-side authentication are still required, but they are transport/account security, not the later anti-cheat project.

---

# Performance requirements

The gameplay path must remain short:

```text
Ragexe
 -> localhost TCP
 -> Athena.Client
 -> QUIC
 -> Athena.Gateway
 -> MapServer
```

Do not insert:

- RabbitMQ;
- Kafka;
- Dapr;
- REST;
- databases;
- asynchronous job queues;

into the mandatory movement path.

Measure each phase against the Phase 0 direct baseline.

---

# Phase completion criteria

## TCP Client complete

```text
Direct MVP == ProxyTcp MVP
```

with no unexplained latency regression.

## QUIC Gateway complete

```text
ProxyTcp MVP == ProxyQuic MVP
```

functionally, with understood transport overhead.

## Identity complete

```text
stock Ragexe login UI
 -> 0x0064 request
 -> server-side LoginRequest
 -> ASP.NET Core Identity
 -> AthenaGameAccount
 -> AthenaGameSession
 -> independent 0x0A4D response
 -> CharServer
 -> MapServer
```

works repeatedly without changing Ragexe.

Only then is the edge stable enough to begin the Orleans backend project.

---

# Locked architectural decisions

The following decisions should not be casually reopened later.

### CG-001 — MVP before proxy

No Athena.Client until the direct stock-iRO MVP and lag issues are stable.

### CG-002 — Athena.Client is opaque

No PACKETVER/iRO parser in the local proxy.

### CG-003 — Direct mode stays

Direct TCP remains a permanent diagnostic baseline.

### CG-004 — TCP before QUIC

First prove a minimal local TCP proxy. Then introduce Gateway + QUIC.

### CG-005 — Identity after QUIC

Do not combine the initial network transport migration with the account-system migration.

### CG-006 — Request != response

`0x0064 -> LoginRequest` and `AuthenticationResult/GameSession -> 0x0A4D` are separate mappings.

### CG-007 — Password hashing is server-side

No Athena.Client password hash.

### CG-008 — Numeric AccountId remains

IdentityUserId is mapped to a stable Athena/Ragnarok `uint32 AccountId`.

### CG-009 — Athena.Client is not identity-authoritative

Account and session mapping live server-side.

### CG-010 — EOS is not the Athena Identity design

Opaque official EOS/Epic material is not required to define Athena's modern identity flow.

### CG-011 — Deep security hardening is later

The current project does not expand into TPM/device keys/process verification/risk scoring.

### CG-012 — Orleans comes last in this sequence

Orleans starts only after the Identity-backed edge flow is proven.

---

# Target architecture before Orleans

```text
                           PLAYER PC

                        Ragexe.exe
                            |
                    legacy TCP / loopback
                            |
                            v
                      Athena.Client
                            |
                       QUIC/TLS 1.3
                            |
========================= INTERNET =========================
                            |
                            v
                      Athena.Gateway
                            |
                  +---------+---------+
                  |                   |
                  v                   v
             iRO adapters       Connection routing
                  |
        +---------+----------+
        |                    |
        v                    v
ASP.NET Core Identity   AthenaGameSession
        |                    |
        +------> AthenaGameAccount
                             |
                             v
              Login / Char / Map backend
```

The next architectural project after this is `orleans-game-engine-architecture.md`.
