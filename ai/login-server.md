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

## Development service-account provisioning

CharServer authenticates to LoginServer with `CharServer.UserId` and
`CharServer.Password` from `solutionfiles/secrets/secret.json`. LoginDb stores this
infrastructure identity in the normal `login` table with `sex='S'` and a reserved
`account_id` below 5. It is configuration-derived bootstrap data, not EF schema
seed data. After creating or resetting `LoginDb`, provision it idempotently:

```bash
./scripts/seed-login-server-account.sh
```

The script reads configured credentials without printing them, reserves account ID
1 by default, refreshes the configured password for an existing valid service row,
and refuses to convert a player or non-reserved row into a server identity. Normal
development player accounts remain explicit:

```bash
./scripts/create-player-account.sh <username> <password> M
```

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
