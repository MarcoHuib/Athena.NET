# Map server migration prompt

Goal
- Build the C# map server with incremental parity: core session, map load, basic NPC and battle flow.

Legacy references
- legacy/src/map/map.cpp
- legacy/src/map/clif.cpp, npc.cpp, battle.cpp, pc.cpp
- legacy/conf/map_athena.conf
- legacy/conf/script_athena.conf
- legacy/conf/packet_athena.conf
- legacy/db/ and legacy/npc/ data
- legacy/sql-files/main.sql

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
