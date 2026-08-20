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
- iRO Renewal compatibility follows the stock 2026 capture described below. It remains isolated behind `IroRenewalCompatibility`; generic kRO/rAthena behavior is retained outside that path.
- PIN configuration survives secret merging, startup logs the absolute config path/effective PIN flags, and iRO sends no 0x08b9 when PIN is disabled.
- Missing: accessible map list responses, accreg/online sync flows, and map/char inter-server extras (keepalive, map transfer, online list).

Stock iRO 2026 protocol evidence

BEWEZEN DOOR CAPTURE
- Client char enter is 0x0065 (17 bytes), followed by the server's raw 4-byte account ID.
- 0x082d is 29 bytes and advertises slots as normal/premium/billing/producible/valid = 9/9/0/9/9.
- The iRO init flow does not include legacy 0x006b.
- 0x09a0 carries `syncCount = 12`; this is not derived from the number of characters.
- Each 0x09a1 requests the next sync page. The response is 0x0b72 with a 4-byte header plus zero or more 175-byte CHARACTER_INFO records. An empty response is exactly `72 0B 04 00`.
- CHARACTER_INFO is 175 bytes. The successful creation response 0x0b6f is therefore exactly 177 bytes.
- 0x0187 is a 6-byte account check/keepalive and the official server echoes the same packet after validating the account ID.
- Character creation request 0x0a39 is 36 bytes: packet ID, name[24], slot[1], hair color[2], hair style[2], job[4], sex[1].
- Character select is 0x0066 (3 bytes): packet ID plus slot.
- Map handoff is 0x0071 (28 bytes): packet ID, character ID, map name[16], IPv4[4], port[2]. The captured endpoint was 128.241.92.42:4501.
- The iRO advertised map endpoint defaults to 128.241.92.42:4501 and can be overridden with `ATHENA_NET_CHAR_IRO_MAP_IP` and `ATHENA_NET_CHAR_IRO_MAP_PORT`; the internal MapServer endpoint is unchanged.
- No 0x020d was observed in this char-server flow. PIN-disabled sessions receive no 0x08b9.
- The first packet sent to the official map server is 0x0c1f with a total length of 1001 bytes.

NOG TE ONDERZOEKEN
- The authentication/token structure of 0x0c1f (1001 bytes).
- The subsequent stock iRO MapServer packet flow.
- In-game packet compatibility after map authentication.

Legacy references
- upstream/src/char/char.cpp
- upstream/src/char/char_clif.cpp
- upstream/src/char/char_mapif.cpp
- upstream/src/char/char_logif.cpp
- upstream/src/char/inter.cpp
- upstream/src/char/int_* (guild, party, storage, mail, homun, mercenary, etc.)
- upstream/conf/char_athena.conf
- upstream/conf/inter_athena.conf
- upstream/sql-files/main.sql
- upstream/sql-files/logs.sql

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
