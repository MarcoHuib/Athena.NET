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

## Definition of done
A supported stock iRO client can enter CharServer, view characters, create/delete/manage them as implemented, select a character, and transition to Athena.NET MapServer without client modification.
