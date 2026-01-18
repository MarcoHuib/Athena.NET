# Char server migration prompt

Goal
- Build the C# char server to match legacy behavior and integrate with login + map servers.

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
- Define schema mapping for char-related tables from main.sql.
- Implement minimum packet handlers from char_clif for login-to-char flow.
- Establish inter-server packet protocol (login <-> char <-> map).

Definition of done
- Same client flow as legacy: login -> char select -> map transfer.
- Core DB tables and in-memory tracking match legacy behavior.

Cleanup notes
- Remove sections when each phase is complete.
