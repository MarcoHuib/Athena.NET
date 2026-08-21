# iRO MapServer development prompt

## Goal
Make Athena.NET accept the current unmodified stock iRO client after `0x0071`, authenticate the selected character, spawn it on the map, and then implement iRO gameplay incrementally.

This is now the primary project milestone.

Generic kRO/rAthena client entry compatibility is not a goal. Existing CZ_ENTER/CZ_ENTER2 code is reference/legacy code unless verified iRO evidence later uses it.

## Current state
- MapServer process/config/secrets/logging and Aspire integration exist.
- MapServer registers with CharServer and existing internal char-map auth messaging exists.
- CharServer can now successfully create/select a character and send `0x0071` map handoff.
- The verified official MapServer endpoint in the capture is `128.241.92.42:4501`; development redirection can route that externally to Athena.NET's internal MapServer listener.
- Athena.NET recognizes `0x0C1F` framing as a fixed 1001-byte stock-iRO client packet but does not yet fully parse/authenticate it.

## Verified iRO evidence
- CharServer handoff is `0x0071`, 28 bytes.
- After handoff the stock client opens a new MapServer TCP connection.
- First official client packet is `0x0C1F`, 1001 bytes.
- The packet contains a large authentication/token payload.

## Immediate next milestone: 0x0C1F authentication
1. Confirm the redirected stock client reaches Athena.NET MapServer.
2. Capture/log only safe framing metadata: packet ID, length, connection/session correlation. Do not dump tokens.
3. Decode the sanitized official `0x0C1F` fixture field by field.
4. Identify stable IDs/fields that can be correlated with the LoginServer/CharServer session and selected character.
5. Determine which large token fields must be validated, reproduced, ignored as opaque, or replaced by Athena.NET-issued state.
6. Map the iRO request to the existing CharServer auth-node handoff without weakening account/character ownership checks.
7. Reconstruct the official first MapServer response sequence from the verified capture.
8. Add exact parser/serializer/state-machine tests before broad gameplay work.

## After MapServer authentication
Work in capture-driven slices:
- successful connection/accept sequence
- character/map spawn
- status and basic character data sync
- inventory/equipment sync
- movement and map switching
- NPC/script interaction
- items/skills/combat/mobs
- party/guild/storage/chat/quests and other iRO features as exercised

## Useful legacy reference areas
Both repositories live under `legacy/` and should be treated as read-only reference material unless explicitly asked otherwise. For this server, use `legacy/rathena/` primarily for architecture/domain behavior and `legacy/openkore/` for packet naming or iRO/community protocol clues.

Use heavily for architecture/game mechanics, not as iRO packet authority:
- `legacy/rathena/src/map/map.cpp`
- `legacy/rathena/src/map/clif.cpp`
- `legacy/rathena/src/map/pc.cpp`
- `legacy/rathena/src/map/npc.cpp`
- `legacy/rathena/src/map/battle.cpp`
- `legacy/rathena/src/map/skill.cpp`
- `legacy/rathena/src/map/status.cpp`
- `legacy/rathena/conf/map_athena.conf`
- `legacy/rathena/conf/script_athena.conf`
- `legacy/rathena/db/`
- `legacy/rathena/npc/`
- `legacy/rathena/sql-files/main.sql`

## Safety and diagnostics
- Never log the full 1001-byte `0x0C1F` at runtime if it contains live authentication material.
- Use sanitized fixtures in tests.
- Reject malformed lengths and impossible IDs safely.
- Do not bypass CharServer auth-node/ownership checks merely to enter the map.

## Definition of done for current milestone
A supported unmodified stock iRO client selects a character, connects to Athena.NET MapServer, authenticates through the verified iRO entry flow, and reaches a stable first-map state.
