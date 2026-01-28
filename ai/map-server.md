# Map server migration prompt

Goal
- Build the C# map server with incremental parity: core session, map load, basic NPC and battle flow.

Current state
- Map server project scaffolded with config/secrets/logging and Aspire hookup.
- Inter-server handshake (map -> char) implemented: 0x2af8 login, 0x2afa map list (empty), 0x2b26 auth request, 0x2afd/0x2b27 responses.
- Map server accepts client connections and handles CZ_ENTER/CZ_ENTER2, then replies with ZC_ACCEPT_ENTER or ZC_REFUSE_ENTER after char auth.
- Unit tests added for config + secrets loading (MapServer.Tests).

Legacy references
- upstream/src/map/map.cpp
- upstream/src/map/clif.cpp, npc.cpp, battle.cpp, pc.cpp
- upstream/conf/map_athena.conf
- upstream/conf/script_athena.conf
- upstream/conf/packet_athena.conf
- upstream/db/ and upstream/npc/ data
- upstream/sql-files/main.sql

Phased build plan
1) MVP network core
- Map server start, client connection, basic packet loop.
- Inter-server handshake with char server.
- Load map cache (map_cache.dat) and map index.

2) Gameplay foundation
- Character spawn, movement, map switching.
- Basic NPC interaction and script execution.

3) Systems parity
- Battle, skills, items, mobs, quests, storage, guild/party, and chat.

Data and tooling
- Decide how to ingest legacy db and npc formats (YAML, TXT, scripts).
- Determine which legacy tools to port (yaml2sql, mapcache, etc.).

Definition of done
- Client can enter a map and interact with simple NPCs, with data loaded from legacy db.

Cleanup notes
- Remove completed phases to keep the prompt short.
