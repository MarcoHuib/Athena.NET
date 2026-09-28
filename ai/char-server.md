# iRO CharServer development prompt

## Goal
Implement the CharServer required by the current unmodified stock iRO client, from authenticated world entry through character management and verified MapServer handoff.

Generic kRO/rAthena client parity is not a goal. `legacy/rathena/` remains a reference for character rules, database design, inter-server architecture, start data, deletion/rename concepts, and mature subsystem behavior.

## Current verified runtime state
- `0x0065` CharServer entry works.
- Raw account-ID response works.
- `0x082D` is sent with iRO slot values `9/9/0/9/9`.
- Legacy `0x006B` is skipped for iRO.
- `0x09A0 syncCount=12` works.
- Client issues 12 `0x09A1` sync requests.
- Character pages use `0x0B72` with 175-byte `CHARACTER_INFO` records.
- Empty pages serialize as exactly `72 0B 04 00`.
- Existing characters load from the database and serialize successfully.
- `CharNum` is verified at relative offset 138 and first-record `0x0B72` absolute offset 142; slots 0, 1, and 8 are tested.
- `0x0187` account keepalive/check is validated and echoed.
- PIN-disabled iRO sessions send no `0x08B9`.
- `0x0A39` create request parsing works.
- Occupied slot validation works and remains mandatory.
- Creating in a free adjacent slot has been verified at runtime, persisted to the DB, and returns `0x0B6F` length 177.
- `0x0066` character select works.
- `0x0071` map handoff works; runtime has successfully handed a created character to the advertised MapServer endpoint.

## Verified iRO wire contract
See `ai/iro-2026-wire.md`. Critical CharServer values:
- `0x0065` = 17 bytes.
- `0x082D` = 29 bytes; slots `9/9/0/9/9`.
- `0x09A0` = 6 bytes; `syncCount=12`.
- `0x09A1` = 2 bytes.
- `0x0B72` = 4-byte header + `N * 175`.
- `CHARACTER_INFO` = 175 bytes.
- `CharNum` = offset 138.
- `0x0187` = 6-byte echo after account validation.
- `0x0A39` = 36-byte create request.
- `0x0B6F` = 177-byte create success.
- `0x0066` = 3-byte select request.
- `0x0071` = 28-byte map handoff.

## Important behavior rules
- Do not weaken occupied-slot, ownership, account, or packet-length validation to match a client message.
- The stock client can display a misleading generic creation error for a server-side slot failure; internal typed failure reasons remain authoritative.
- Preserve database slot uniqueness per account.
- Keep iRO wire state explicit and stateful; do not resend full character data for every sync request unless verified.
- Do not add `0x006B`, `0x020D`, or PIN packets to the iRO init flow without new evidence.

## Useful legacy reference areas
Both repositories live under `legacy/` and should be treated as read-only reference material unless explicitly asked otherwise. For this server, use `legacy/rathena/` primarily for architecture/domain behavior and `legacy/openkore/` for packet naming or iRO/community protocol clues.

Use for behavior/data concepts, not regional client packet authority:
- `legacy/rathena/src/char/char.cpp`
- `legacy/rathena/src/char/char_clif.cpp`
- `legacy/rathena/src/char/char_mapif.cpp`
- `legacy/rathena/src/char/char_logif.cpp`
- `legacy/rathena/src/char/inter.cpp`
- `legacy/rathena/src/char/int_*`
- `legacy/rathena/conf/char_athena.conf`
- `legacy/rathena/conf/inter_athena.conf`
- `legacy/rathena/sql-files/main.sql`
- `legacy/rathena/sql-files/logs.sql`

## Immediate next milestone
The CharServer path is sufficiently proven to move focus to MapServer authentication. Only return to CharServer when MapServer evidence shows the handoff/auth node must carry additional iRO data.

## Later CharServer work
After successful MapServer entry:
- online-state synchronization and duplicate-login handling
- map transfer between MapServers
- rename/delete/slot-move flows as exercised by iRO
- party/guild/storage/mail and other inter-server systems required by actual iRO gameplay

## Inter-server service authentication (MapServer)

MapServer authenticates to CharServer with a non-secret `ServiceId` and a
shared MapServer `ServiceToken`, never a username/password pair. The legacy
`MapLogin`/`MapLoginAck` username/password handshake (backed by
`MapConfig.UserId`/`Password` and `CharConfig.UserId`/`Password`) has been
removed entirely and replaced by an HMAC-SHA256 challenge/response handshake,
structurally identical to - but completely independent of - the
CharServer -> LoginServer handshake documented in `ai/login-server.md`. The
two tokens (`ServiceAuthentication.CharServer.Token` for
CharServer -> LoginServer, `ServiceAuthentication.MapServer.Token` for
MapServer -> CharServer) are unrelated secrets and must never be equal in
production.

The handshake, driven by `IMapServiceAuthenticationService`
(`MapServiceAuthenticationService` in production, in `MapServerSession`) and
mirrored byte-for-byte on the MapServer side by
`Athena.Net.MapServer.Net.ServiceAuthProofCalculator`, is:

1. MapServer opens a TCP connection to CharServer and sends `MapServiceHello`
   (`0x2b40`) carrying its non-secret `ServiceId` and its advertised map
   IP/port.
2. CharServer generates a cryptographically random 32-byte one-time nonce and
   replies with `MapServiceAuthChallenge` (`0x2b41`) carrying it. The nonce is
   scoped to this connection only and can be consumed exactly once.
3. MapServer computes an HMAC-SHA256 proof over the complete hello payload
   (ServiceId, advertised IP, advertised port) plus the nonce, and sends only
   the 32-byte proof back as `MapServiceAuthProof` (`0x2b42`). The MapServer
   `ServiceToken` itself never travels over the network - only this one-time
   derived proof does.
4. CharServer independently recomputes the expected proof and compares it to
   the submitted one with `CryptographicOperations.FixedTimeEquals` (a
   fixed-time comparison). Only on an exact match does it finalize
   authentication (`MarkAuthenticated`), register the MapServer into
   `MapServerRegistry`, and begin accepting map-list/auth/gameplay-persistence
   packets from that session. Any other outcome - wrong proof,
   expired/already-consumed challenge, malformed hello/proof, packet
   out-of-order, or no `ServiceToken` configured at all - sends
   `MapServiceAuthResult` (`0x2b43`) with a failure byte and closes the
   connection (fail closed) rather than allowing retries on the same socket.

Wire format details (message layout, domain-separation context string,
field-length prefixing) are documented directly on
`MapServiceAuthProofCalculator`'s doc comment on both sides
(`src/CharServer/Net/MapServiceAuthProofCalculator.cs` and
`src/MapServer/Net/ServiceAuthProofCalculator.cs`); the two implementations
must stay byte-for-byte identical.

### Handshake connection state machine

Each TCP connection's `IMapServiceAuthenticationService` (a fresh instance
per connection) tracks an explicit `MapServiceAuthConnectionState`
(`Unauthenticated -> ChallengeIssued -> ProofVerified -> Authenticated`, with
any invalid transition moving to the terminal `Failed` state), structurally
identical to LoginServer's `ServiceAuthConnectionState` for the
CharServer -> LoginServer handshake. In particular:

- A second `MapServiceHello` while a challenge is outstanding, or on an
  already-authenticated connection, is rejected and the connection closed -
  never silently reset.
- `MarkAuthenticated` only succeeds immediately after this same instance's
  own successful `VerifyProof` call (`ProofVerified` state) - it is
  impossible to reach `Authenticated` any other way.
- A challenge is consumed exactly once regardless of whether the proof that
  consumes it is correct - a replayed or repeated proof against the same
  challenge always fails.
- A challenge/proof issued on one TCP connection can never authenticate a
  different connection - each connection has its own
  `IMapServiceAuthenticationService` instance and its own one-outstanding-
  challenge state.
- No map registration, character auth request, or gameplay/persistence
  packet (`MapSendMaps`, `MapAuthRequest`, `MapSavePosition`, quest/skill/
  inventory persistence, etc.) is accepted from a `MapServerSession` before
  `_authenticated` is set to `true` by a successful handshake.

### Config resolution

`MapServerServiceTokenProvider` (CharServer side, in
`src/CharServer/Net/MapServerServiceTokenProvider.cs`) resolves the token
from the `ATHENA_NET_MAP_SERVER_SERVICE_TOKEN` environment variable first,
falling back to `ServiceAuthentication.MapServer.Token` in
`solutionfiles/secrets/secret.json`. It must be Base64-encoded and decode to
at least 32 bytes (256 bits); a missing value, invalid Base64, or a too-short
decoded value are all treated identically as "not configured" and fail
closed - never a fallback to an empty/default credential. The MapServer-side
resolution (`Athena.Net.MapServer.Net.MapServerServiceTokenProvider`) is
symmetric, reading the same secret path/environment-variable name.

## Character gameplay-state contract

An authenticated MapServer can read and transactionally replace the persistent
gameplay-state subset of a character owned by that connection. CharServer validates
the consumed MapAuth ownership tuple and an optimistic `gameplay_state_version`,
then saves a multi-field mutation atomically. EXP, healing, status, and combat rules
are intentionally not part of this persistence slice.

## Definition of done
A supported stock iRO client can enter CharServer, view characters, create/delete/manage them as implemented, select a character, and transition to Athena.NET MapServer without client modification.
