# Char server migration prompt

Goal
- Build the C# char server to match legacy behavior and integrate with login + map servers.

Current state (C#)
- Char server project scaffolded with config loader/logging and login server connection loop.
- Login server registration uses 0x2710 and handles auth/account data responses (0x2713/0x2717).
- Char server listens for client connections and supports auth handshake plus DB-backed char list/create/delete.
- Char DB context maps the legacy `char`, `inventory`, `skill`, and `hotkey` tables and can auto-migrate via Aspire.
- Character creation uses start map/zeny/items (including pre-renewal via `ATHENA_NET_CHAR_PRE_RENEWAL`) from `char_athena.conf` and `start_status_points` from `inter_athena.conf`; delete rules honor level/party/guild/birthdate checks.
- Map server integration MVP is in place: map server login (0x2af8), map list ingest, auth node handoff, and HC_NOTIFY_ZONESVR on char select.
- Missing: accessible map list responses, accreg/online sync flows, and map/char inter-server extras (keepalive, map transfer, online list).

Legacy references
- legacy/src/char/char.cpp
- legacy/src/char/char_clif.cpp
- legacy/src/char/char_mapif.cpp
- legacy/src/char/char_logif.cpp
- legacy/src/char/inter.cpp
- legacy/src/char/int_* (guild, party, storage, mail, homun, mercenary, etc.)
- legacy/conf/char_athena.conf
- legacy/conf/inter_athena.conf
- legacy/sql-files/main.sql
- legacy/sql-files/logs.sql

Phased build plan
1) MVP handshake
- Connect to login server, validate accounts, char list, char create/delete.
- Persist to DB with correct schema and defaults.

2) Map server integration
- Map server registration, session handoff, auth nodes, and server list.
- Online character tracking and conflict resolution.

3) Subsystems parity
- Guild/party/mail/storage/auction/homun/mercenary data flow.
- Fame lists, rankings, and timers.

Next tasks
- Add accessible map list responses and wire map server list.
- Extend inter-server packet protocol (login <-> char <-> map) beyond auth (online list, keepalive, map transfer).

Definition of done
- Same client flow as legacy: login -> char select -> map transfer.
- Core DB tables and in-memory tracking match legacy behavior.

Cleanup notes
- Remove sections when each phase is complete.
