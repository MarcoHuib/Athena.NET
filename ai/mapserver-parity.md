# MapServer iRO reference map

> Historical filename: this is not a generic rAthena/kRO parity target.

## Critical protocol rule
The supported stock iRO client does **not** use the legacy/generic Athena.NET CZ_ENTER/CZ_ENTER2 path as its first observed MapServer packet.

Verified entry:
- `0x0C1F`
- 1001 bytes

Any old `HandleEnterAsync` implementation based on generic CZ_ENTER is reference-only until capture evidence proves that it is reused later.

## Reference mapping
| Concern | iRO requirement | rAthena value |
|---|---|---|
| TCP/session lifecycle | robust accept, framing, disconnect | useful architecture |
| Client entry packet | `0x0C1F/1001` | packet choice not authoritative |
| Account/character auth | correlate with CharServer handoff | auth-node design is highly useful |
| Duplicate login | reject stale/conflicting ownership safely | useful domain logic |
| Map load/index | required after auth | highly reusable concepts/tools |
| Player session object | needed for spawn/gameplay | highly reusable architecture |
| Spawn/status/inventory | must follow iRO wire behavior | game-state logic reusable, packets capture-driven |
| Movement/NPC/combat | implement for iRO | rAthena mechanics are primary reference |

## 0x0C1F investigation checklist
- exact packet framing and field boundaries
- account ID and character ID candidates
- login/session IDs and client timestamp/nonce candidates
- token/JWT-like sections and their encoding
- world/server identifiers
- client IP/session correlation
- fields also present in CharServer auth node
- required response packet(s)
- response ordering and timing

Use differential captures where possible: same account/different character, reconnect, different session, and controlled field changes. Sanitize all auth material before committing fixtures.

## What may be reused from existing generic map code
- socket/session infrastructure
- malformed-packet protection
- connection state machine concepts
- CharServer connector and auth-node lookup
- entity/session models
- map cache/index infrastructure

## What must not be assumed
- generic CZ_ENTER packet ID/length/offsets
- generic ZC_ACCEPT_ENTER sequence
- kRO `PACKETVER` packet table
- generic packet obfuscation/shuffle behavior
- server response order after entry

All of those must be confirmed against stock-iRO evidence before becoming the iRO path.
