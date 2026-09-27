# iRO LoginServer development prompt

## Goal
Implement the LoginServer behavior required by the current unmodified stock iRO client and provide a stable authenticated handoff to Athena.NET's CharServer.

Generic kRO/rAthena client compatibility is not a goal. `legacy/rathena/` is reference material for architecture, account/auth concepts, configuration, persistence, bans, logging, and inter-server behavior.

## Current verified iRO state
- Stock client login request `0x0064` is parsed as 55 bytes.
- Observed version is `18`.
- Fixed strings terminate at the first NUL byte; trailing bytes in fixed fields are ignored.
- Successful stock-iRO login response is `0x0A4D`.
- `0x0A4D` uses a 64-byte header and 32-byte world/server entries.
- Development can advertise the official Chaos endpoint while external Windows network redirection sends traffic to Athena.NET.
- CharServer registration IP byte-order handling has been corrected so the registered endpoint is not octet-reversed.
- Authentication/database/config infrastructure remains based on the existing Athena.NET/rAthena-inspired implementation.

## iRO requirements
- Preserve exact packet IDs, lengths, byte order, token/header placement, and world-entry layout proven by captures.
- Refuse malformed/short login packets safely.
- Do not leak password bytes or fixed-field garbage to logs.
- Keep account/password verification, bans, auth-node lifecycle, server registration, and session ownership robust even when those internal concepts are borrowed from rAthena.
- Treat world-list contents/configuration as server policy, but serialize them in the verified iRO format.

## Identity architecture

Player authentication and inter-server (CharServer) authentication are two
separate domains that happen to both flow through LoginServer's TCP listener:

- **Player accounts** are ASP.NET Core Identity users. `AthenaIdentityUser :
  IdentityUser<Guid>` owns human authentication (`UserName` is the Ragnarok
  login name exactly as sent in the stock `0x0064` request; `Email` is a
  separate, independent identity reserved for a future website login and is
  never derived from `UserName`). Each `AthenaIdentityUser` has exactly one
  `AthenaGameAccount` (unique FK, 1:1, restrict-on-delete), which owns
  Ragnarok game-account state (`Sex`, `GroupId`, `State`, ban/expiration,
  VIP, pincode, character slots, web auth token, etc.) plus the legacy
  compatibility identifier the stock wire protocol still needs:
  `RagnarokAccountId uint32` (unique, separate from `AthenaGameAccount.Id`,
  which is the canonical `Guid`). `IPlayerAuthenticationService`
  (`IdentityPlayerAuthenticationService` in production) is the only thing
  `ClientSession` depends on for player login - it never references
  `UserManager`/`IdentityUser`/EF entities directly, so the wire-protocol
  code stays Identity-independent. See `docs/architecture-roadmap.md`
  ("Sequencing deviation" section) for why this was implemented ahead of
  Athena.Client/Gateway/QUIC, and for the important caveat that Identity
  protects server-side credential storage only - it does not encrypt the
  Ragexe TCP transport.
- **CharServer/service authentication** is a third, fully separate domain
  from both player Identity and the legacy `login` table (which no longer
  exists - see "Inter-server service authentication" below). CharServer is
  never an Identity user, never has a password, and never requires a
  database row. It proves possession of a shared `ServiceToken` via an
  HMAC-SHA256 challenge/response handled by `IServiceAuthenticationService`.
  Every inter-server ("Lc*") packet that manages player game-account state
  (ban, VIP, sex change, pincode, account info/data, email change, etc.) is
  rejected unless the socket has already authenticated as a service
  (`IServiceAuthenticationService.IsAuthenticated`); LoginServer used to
  process these packets without checking that first, which has been fixed
  and is covered by regression tests
  (`tests/LoginServer.Tests/Net/ClientSessionServiceAuthGate.test.cs`).
  Those handlers read/write `AthenaGameAccount` by `RagnarokAccountId`
  (the CharServer/MapServer-facing legacy id), not `AthenaGameAccount.Id`.
- The legacy `LoginId1`/`LoginId2`/`AuthNode` game-session handoff
  (`ILoginSessionService`/`LoginState`) is unchanged and independent of
  Identity - it is how CharServer and MapServer confirm a session, and it
  has nothing to do with human/Identity authentication.

## Inter-server service authentication

CharServer authenticates to LoginServer with a non-secret `ServiceId` and a
shared `ServiceToken`, never a username/password pair and never a database
row. The legacy `login` table (which used to hold a `sex='S'`, low-`account_id`
"service account" row for this purpose) has been dropped entirely - see
`docs/architecture-roadmap.md` for the migration that removes it.

The handshake, driven by `IServiceAuthenticationService`
(`ServiceAuthenticationService` in production) and mirrored byte-for-byte on
the CharServer side by `Athena.Net.CharServer.Net.ServiceAuthProofCalculator`,
is:

1. CharServer opens a TCP connection to LoginServer and sends `LcServiceHello`
   (`0x2750`) carrying its non-secret `ServiceId` and registration info
   (advertised IP/port, server name, maintenance/new-display flags).
2. LoginServer generates a cryptographically random 32-byte one-time nonce
   (`RandomNumberGenerator`) and replies with `LcServiceAuthChallenge`
   (`0x2751`) carrying it. The nonce is scoped to this connection only and can
   be consumed exactly once.
3. CharServer computes
   `HMAC-SHA256(ServiceToken, "Athena.NET/CharServer/Auth/v1" + 0x1F + ServiceId + 0x1F + nonce)`
   (see `ServiceAuthProofCalculator`) and sends only the 32-byte proof back as
   `LcServiceAuthProof` (`0x2752`). The `ServiceToken` itself never travels
   over the network - only this one-time derived proof does.
4. LoginServer independently recomputes the expected proof and compares it to
   the submitted one with `CryptographicOperations.FixedTimeEquals` (a
   fixed-time comparison, so a wrong proof cannot be distinguished by timing).
   Only on an exact match does it set `IsAuthenticated = true` and register
   the connection as a live CharServer; any other outcome - wrong proof,
   expired/already-consumed challenge, or no `ServiceToken` configured at all
   (fail closed) - sends `LcServiceAuthResult` (`0x2753`) with a failure byte
   and closes the connection rather than allowing retries.

HMAC-SHA256 here authenticates *possession* of the shared `ServiceToken` and
prevents that secret from ever being sent over the wire - it does not encrypt
the connection. The player-facing stock-iRO TCP transport, and this internal
LoginServer<->CharServer transport, are both still plaintext for now; a future
TLS/mTLS layer may protect the internal connection later but is out of scope
here.

The `ServiceToken` is configured in `solutionfiles/secrets/secret.json` under
`ServiceAuthentication.CharServer.Token`, or via the
`ATHENA_NET_CHAR_SERVER_SERVICE_TOKEN` environment variable (checked first,
suited to deployment secret sources such as Kubernetes Secrets). It should be
a high-entropy random value (256 bits/32+ bytes is recommended) and both
LoginServer and CharServer must be configured with the identical value. It is
never stored in SQL Server, never hashed into any table, and never
represented as an ASP.NET Core Identity user.

Normal development player accounts are provisioned through ASP.NET Core
Identity, never by inserting rows directly:

```bash
./scripts/create-player-account.sh <username> [M|F] [email]
Password:
```

Run it as shown above and type the password at the hidden `Password:` prompt.
The password is deliberately never a positional argument to this script or to
LoginServer's own `--create-account-*` startup mode: a password passed as a
process argument is visible in shell history and, for as long as the process
runs, in every other process's view of its argument list (e.g. `ps aux`).

For non-interactive automation, the script also accepts the password on
stdin instead of the hidden prompt - source it from a secret manager or a
protected CI secret and pipe it in. Never place the plaintext password
directly in the command line (an `echo '<password>' | ...` example would
defeat the purpose, landing the password in shell history just the same).
The script reads the password itself (from stdin if piped, or a hidden
prompt if run from a terminal) and pipes it to the LoginServer child
process's stdin; `LoginServerApp.CreateAccountAsync`/`ReadPasswordFromStdin`
on the receiving end does the same for anyone invoking the one-shot mode
directly.

This shells out to LoginServer's own one-shot `--create-account-*` startup mode
(see `src/LoginServer/Startup/StartupOptions.cs` /
`src/LoginServer/Startup/LoginServerApp.cs`), which provisions the
`AthenaIdentityUser` + `AthenaGameAccount` pair transactionally through
`IPlayerAccountProvisioningService` using the same configuration/connection-string
resolution as the running server. The same console-driven flow is available while
the server is running via the `create:` command in `ConsoleCommandLoop` (typed
directly into LoginServer's own interactive console, not a process argument, so
the shell-history/process-list concern above does not apply to it). The
Ragexe login screen is authentication-only: no login packet can create an
account. The legacy rAthena `_M`/`_F` auto-register-on-login behavior and its
configuration (`new_account`, `allowed_regs`/`time_allowed`,
`start_limited_time`) have been removed entirely, not merely left unused - a
login for a name ending in `_M`/`_F` with no existing account is just an
unknown-username failure, the same as any other unknown username.

`PlayerAccountProvisioningService` is the single choke point every one of
these entry points goes through, so it rejects credentials the stock client
could never actually use before touching the database: username/password
length (`AccountNameMinLength`/`PasswordMinLength` from `login_athena.conf`
as the floor, `PacketConstants.NameLength - 1` = 23 characters as the ceiling
- the stock `0x0064` username/password fields are fixed-width, NUL-terminated
24-byte buffers), `sex` must be `M`/`F`, and `email` must be a syntactically
valid address.

## Useful legacy reference areas
Both repositories live under `legacy/` and should be treated as read-only reference material unless explicitly asked otherwise. For this server, use `legacy/rathena/` primarily for architecture/domain behavior and `legacy/openkore/` for packet naming or iRO/community protocol clues.

Use these for implementation ideas, not as iRO wire authority:
- `legacy/rathena/src/login/login.cpp`
- `legacy/rathena/src/login/loginclif.cpp`
- `legacy/rathena/src/login/loginchrif.cpp`
- `legacy/rathena/src/login/ipban.cpp`
- `legacy/rathena/src/login/loginlog.cpp`
- `legacy/rathena/conf/login_athena.conf`
- `legacy/rathena/conf/inter_athena.conf`
- `legacy/rathena/conf/subnet_athena.conf`
- `legacy/rathena/conf/msg_conf/login_msg.conf`
- `legacy/rathena/sql-files/main.sql`
- `legacy/rathena/sql-files/logs.sql`

## Next work
- Keep regression tests for `0x0064` parsing and `0x0A4D` serialization capture-derived and exact.
- Remove or isolate obsolete client-facing packet branches that exist only for generic kRO/rAthena compatibility when they are no longer needed by iRO.
- Continue hardening auth-node TTL, duplicate login, IP-ban, and malformed-packet behavior without changing the verified iRO wire flow.

## Definition of done
- A supported unmodified stock iRO client can authenticate repeatedly and receive a valid iRO world list.
- The selected world can authenticate at CharServer using the issued session/account data.
- Error paths are safe and do not expose secrets.
- Regression tests prevent reintroduction of `0x0AC4`/wrong-entry-layout behavior for the iRO path.
