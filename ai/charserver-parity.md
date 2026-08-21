# CharServer iRO reference map

> Historical filename: this document no longer targets generic rAthena/kRO parity.

## Purpose
Map verified iRO CharServer behavior to useful rAthena architecture and domain logic. The iRO capture is authoritative for client-facing packets.

Reference projects are stored locally at `legacy/rathena/` and `legacy/openkore/`. Use them read-only by default; neither overrides verified iRO capture evidence.

## iRO contract versus reusable rAthena concepts
| iRO area | Verified Athena.NET target | Useful rAthena concepts |
|---|---|---|
| Enter | `0x0065/17` + raw AID | session/auth request architecture |
| Slots | `0x082D`, `9/9/0/9/9` | slot/account policy concepts |
| Sync | `0x09A0 syncCount=12`, `0x09A1` | state-machine organization |
| Character pages | `0x0B72`, 175-byte records | character data model only |
| Slot byte | `CharNum` offset 138 | field semantics |
| Keepalive | `0x0187` echo | account/session validation |
| Create | `0x0A39/36` | name/slot/job/start-data validation |
| Create success | `0x0B6F/177` | persistence/start items |
| Select | `0x0066/3` | ownership/online-state logic |
| Map handoff | `0x0071/28` | auth-node/map-selection architecture |

## Rules that must survive refactoring
- `CHARACTER_INFO` remains 175 bytes for the supported client until a newer verified capture says otherwise.
- `CharNum` remains relative offset 138; first-record `0x0B72` absolute offset 142.
- Occupied slots are rejected; do not overwrite or duplicate a slot to satisfy client UI behavior.
- Name-taken and slot-occupied remain distinct internal errors.
- `0x0187` validates the authenticated account before echoing.
- PIN-disabled iRO flow emits no PIN packet.
- No legacy `0x006B` or speculative `0x020D` in the verified startup flow.
- Map handoff is `0x0071`, not generic `0x0AC5`.

## What to borrow from rAthena
- character persistence schema and lifecycle ideas
- start map/status/zeny/item rules where they reflect desired iRO gameplay
- ownership and online-state checks
- deletion, rename, slot-move domain rules
- char-to-map auth-node design
- party/guild/storage/mail/inter-server subsystem architecture

## What not to borrow blindly
- packet IDs chosen by kRO `PACKETVER` branches
- character struct sizes from another regional client
- PIN flow
- sync/page counts
- map handoff packet choice
- client error-code assumptions

## Regression suite expectations
Tests should assert exact iRO byte layout for the verified flow and separately test domain behavior such as occupied slots, duplicate names, persistence, and authorization.
