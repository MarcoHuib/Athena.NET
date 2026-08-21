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
- Athena.NET parses the capture-proven `0x0C1F` header fields, authenticates them
  against the single-use `MapAuthNode`, and sends the proven bootstrap through `0x02EB`.

## Proven from stock iRO 2026 capture
- CharServer handoff is `0x0071`, 28 bytes.
- The official advertised MapServer endpoint is `128.241.92.42:4501`.
- After handoff the stock client opens a new MapServer TCP connection.
- Capture `/Users/marco/Downloads/full-login-flow.pcapng` uses CharServer TCP
  `192.168.178.55:60565 -> 128.241.92.43:4500` and MapServer TCP
  `192.168.178.55:64171 -> 128.241.92.42:4501`.
- Frame 389 contains `0x0071`: character ID, `iz_int01.gat`, and
  `128.241.92.42:4501`. Frame 402 contains the first MapServer client payload:
  `0x0C1F`, exactly 1001 bytes.
- The official `0x0071` map field uses the client-facing `.gat` form. Athena's
  internal/database map names remain extensionless, but `BuildIroZoneServerPacket`
  now appends `.gat` on the wire. This matches both frame 389 and rAthena's
  `mapindex_getmapname_ext` call in `src/char/char_clif.cpp`.
- The packet contains a large authentication/token payload.
- `1001` is the observed total packet size. There is no evidence that `0x0C1F` contains an internal length field.
- The capture proves these little-endian fields:

| Offset | Size | Type | Meaning | Same-session correlation |
|---:|---:|---|---|---|
| `0x00` | 2 | `uint16` | packet ID `0x0C1F` | MapServer framing, frame 402 |
| `0x02` | 4 | `uint32` | account ID | `0x0A4D` frame 77 offset 8; `0x0065` frame 139 offset 2; `0x0283` frame 411 offset 2 |
| `0x06` | 4 | `uint32` | selected character ID | `0x0B6F` frame 358 offset 2; `0x0071` frame 389 offset 2 |
| `0x0A` | 4 | `uint32` | login ID 1 / first session ID | `0x0A4D` frame 77 offset 4; `0x0065` frame 139 offset 6 |

- Bytes `0x0E..0x3E8` remain opaque. They include printable authentication material,
  but no token format or validation semantics are inferred and their contents are
  neither logged nor committed.
- Reassembled server-to-client bytes after frame 402 start as follows. Stream offsets
  are relative to the first MapServer server payload, not TCP segment boundaries:

| Order | Frame | Stream offset | Packet | Length | Capture value/role |
|---:|---:|---:|---|---:|---|
| 1 | 407 | 0 | `0x0B18` | 4 | inventory expansion size `0` |
| 2 | 411 | 4 | `0x0283` | 6 | current account ID |
| 3 | 411 | 10 | `0x0ADE` | 6 | red-weight threshold `70` |
| 4 | 411 | 16 | `0x02EB` | 13 | accept enter: tick, `(18,27,dir 0)`, sizes `5/5`, font `0` |
| 5 | 411 | 29 | `0x0B32` | 19 | one-entry skill list; not implemented without Athena skill state |

- The capture's `0x02EB` position is `(18,27)` on the character handed to
  `iz_int01.gat`; Athena serializes its own `MapAuthNode` map position rather than
  replaying these captured values.
- The first subsequent client packet in the official flow is frame 421,
  `0x007D`, 3 bytes. Generic upstream describes a 2-byte load-end acknowledgement;
  the extra iRO byte is still opaque, so this is the next implementation boundary.

### Complete official server burst before client 0x007D

The complete reassembled burst is 487 bytes and contains 26 packets. Variable
packet sizes below come from their captured internal length fields. Fixed sizes
are corroborated by the current OpenKore iRO table or rAthena structures unless
explicitly marked otherwise.

| # | Frame | Stream offset | Packet/length | Proven meaning and captured state | Classification | Athena sends |
|---:|---:|---:|---|---|---|---|
| 1 | 407 | 0 | `0x0B18/4` | inventory expansion size `0` | inventory | yes |
| 2 | 411 | 4 | `0x0283/6` | account ID | character state | yes |
| 3 | 411 | 10 | `0x0ADE/6` | red-weight threshold `70` | config | yes |
| 4 | 411 | 16 | `0x02EB/13` | accept enter, tick and packed position | world critical | yes |
| 5 | 411 | 29 | `0x0B32/19` | skill list: one 15-byte `SKILLDATA` entry | skill state | no |
| 6 | 411 | 48 | `0x00B0/8` | `SP_PATK(225)=0` | status | no |
| 7 | 411 | 56 | `0x00B0/8` | `SP_SMATK(226)=0` | status | no |
| 8 | 411 | 64 | `0x01D7/15` | account actor, `LOOK_WEAPON(2)=1201`, secondary value `0` | character appearance | no |
| 9 | 411 | 79 | `0x013A/4` | attack range `1` | status | no |
| 10 | 411 | 83 | `0x00B0/8` | `SP_RES(227)=0` | status | no |
| 11 | 411 | 91 | `0x00B0/8` | `SP_MRES(228)=0` | status | no |
| 12 | 411 | 99 | `0x00B0/8` | `SP_SPEED(0)=150` | status | no |
| 13 | 411 | 107 | `0x013A/4` | attack range `1`, repeated | status | no |
| 14 | 411 | 111 | `0x0B8D/101` | success plus six `(uint64 type,int64 points)` reputation entries, types 1..6, all zero | character state | no |
| 15 | 411 | 212 | `0x02C9/3` | party invitations allowed (`0`) | config | no |
| 16 | 411 | 215 | `0x0ADC/6` | four zero misc-config flags | config | no |
| 17 | 416 | 221 | `0x0B08/5` | inventory start, type `0`, empty name | inventory | no |
| 18 | 416 | 226 | `0x0B09/5` | empty stackable inventory list, type `0` | inventory | no |
| 19 | 416 | 231 | `0x0B39/141` | non-stackable inventory list, type `0`, two entries | inventory/appearance source | no |
| 20 | 416 | 372 | `0x0B0B/4` | inventory end, type/flag `0/0` | inventory | no |
| 21 | 416 | 376 | `0x0BF2/13` | eleven zero payload bytes; name/layout unknown | unknown | no |
| 22 | 416 | 389 | `0x00B0/8` | `SP_ATK2(42)=17` | status | no |
| 23 | 416 | 397 | `0x00B0/8` | `SP_MATK1(43)=0` | status | no |
| 24 | 416 | 405 | `0x00B0/8` | `SP_DEF2(46)=10` | status | no |
| 25 | 416 | 413 | `0x00B0/8` | `SP_MDEF2(48)=0` | status | no |
| 26 | 416 | 421 | `0x0A24/66` | achievement update: total points 10, achievement 240000 completed | character state | no |

The first missing official packet is therefore `0x0B32/19`. This does not prove
that `0x0B32` alone causes the crash: the official client receives the entire
remaining 458-byte state burst in the same TCP payload sequence before it sends
`0x007D`.

### 0x0B32 proven layout

`0x0B32` is `ZC_SKILLINFO_LIST3`: `uint16 id`, `uint16 totalLength`, followed by
zero or more packed 15-byte entries. The captured length 19 is exactly a four-byte
header plus one entry:

| Entry offset | Type | Captured value | Meaning |
|---:|---|---:|---|
| 0 | `uint16` | 1 | skill ID `NV_BASIC` |
| 2 | `int32` | 0 | skill targeting/info flags |
| 6 | `uint16` | 0 | learned level |
| 8 | `uint16` | 0 | SP cost |
| 10 | `uint16` | 1 | range |
| 12 | `uint8` | 1 | upgradable |
| 13 | `uint16` | 0 | secondary/current level |

Athena's CharServer database has real `CharSkill` rows and inventory rows, but a
new Novice has no learned `CharSkill` row for this level-zero skill-tree entry.
The MapServer has neither a job skill-tree model nor access to the CharServer's
skill/inventory collections through `MapAuthNode`. Consequently it cannot yet
derive the captured `NV_BASIC` entry or the later inventory/appearance packets
without inventing state or changing the internal CharServer protocol.

### 0x02EB semantic comparison

| Offset | Type | Official | Athena runtime/source | Result |
|---:|---|---|---|---|
| 0 | `uint16` | `0x02EB` | `0x02EB` | match |
| 2 | `uint32` | `168864946` | unsigned `Environment.TickCount` | expected dynamic difference; both monotone milliseconds |
| 6 | packed 3 bytes | `04 81 B0` = `(18,27,0)` | `04 81 A0` = `(18,26,0)` | correct state-dependent difference |
| 9 | `uint8` | 5 | 5 | match; upstream says ignored |
| 10 | `uint8` | 5 | 5 | match; upstream says ignored |
| 11 | `uint16` | 0 | `MapAuthNode.Font`, runtime character default 0 | match |

There are no remaining bytes or nullable pointer-like fields in `0x02EB`.
rAthena's `client_tick(gettick())` is a truncation to unsigned 32-bit milliseconds,
which is semantically equivalent to Athena's unchecked unsigned
`Environment.TickCount` for this packet.

### Map-state diagnosis

- The runtime `iz_int03`, `(18,26)` comes directly from the selected character's
  database `LastMap/LastX/LastY`, is copied unchanged into `MapAuthNode`, and is
  used by `0x02EB`.
- The configured stock start-point list explicitly includes `iz_int03,18,26`;
  the local reference tables also list `iz_int03` as a real intro map. This is
  positive evidence that the coordinate is intentional, not an invalid zero/null.
- Client resource tables commonly alias numbered `iz_intXX` map resources to the
  shared `iz_int` resources. The crash's shortened `iz_i.rsw` text is not enough
  to prove the exact client alias, but it is consistent with failure during
  client resource/world-name resolution.
- The successful capture's `iz_int01` is a different valid instance selected for
  that character. It does not justify changing Athena's stored `iz_int03` state.
- Before this task Athena serialized `iz_int03` instead of the capture/upstream
  client-facing `iz_int03.gat` in `0x0071`. That proven wire-format mismatch is
  corrected while keeping database and `MapAuthNode` names extensionless.

## Current Athena.NET entry and framing
- Before stock-iRO recognition was added, the client entry handlers expected legacy `CZ_ENTER` (`0x0072`, 19 bytes) or `CZ_ENTER2` (`0x0436`, 19 bytes), handled by `MapClientSession.HandleEnterAsync`.
- `MapClientSession` now has a stock-iRO-specific fixed-length registration: `0x0C1F = 1001`.
- Framing reads the two-byte packet ID, selects a fixed total size from `PacketLengths`, and loops until exactly the remaining bytes have arrived. It does not assume one TCP read is one packet.
- A single read containing multiple packets remains correctly framed because only the exact current packet length is consumed.
- `IroMapPacketFramingTests.ReadNextPacketAsync_Reassembles0c1fAcrossFragmentedReads` covers a 100/300/601-byte delivery.
- `IroMapPacketFramingTests.ReadNextPacketAsync_PreservesPacketBoundaryWhenReadContainsMultiplePackets` covers coalesced packets.
- `IroMapAuthPacket.TryParse` accepts only `0x0C1F/1001` and reads only offsets
  `0x02`, `0x06`, and `0x0A`. Its payload is never logged.
- The proven IDs are sent through the existing CharServer auth request. The request's
  existing trailing mode byte marks iRO authentication so CharServer validates
  account ID, character ID, and login ID 1 but does not pretend that sex was present
  in `0x0C1F`.
- `MapAuthManager.TryConsume` atomically consumes the exact matching node. Legacy
  authentication continues to validate sex as before.
- After auth success Athena sends `0x0B18`, `0x0283`, `0x0ADE`, and `0x02EB` as
  individually structured packets in the captured order. It then stops at/logs the
  captured `0x007D/3` boundary instead of applying the conflicting legacy layout.

## Proven from upstream (checked 2026-08-21)
- rAthena `master` commit `12624c21502ea1e62dfef6b1c9f80f1e49fe123b`: repository-wide search found no `0x0C1F` definition in current source, including `src/map/clif_packetdb.hpp` and `src/map/clif_shuffle.hpp`. Its current map-entry references remain other packet IDs/lengths and do not prove the iRO packet.
- OpenKore `master` commit `51de1ddfc4449ae5217f6886de702f87ca934030`: the sole relevant occurrence is `tables/ROla/recvpackets.txt:1699`, `0C1F 1000`. This is a Latin America table, differs from the captured iRO total of 1001 bytes, and supplies no packet name, direction, fields, offsets, types, token semantics, client version, or response.
- `legacy/rathena/src/map/packets_struct.hpp` and `clif.cpp` identify `0x0B18`
  (`PACKET_ZC_EXTEND_BODYITEM_SIZE`, `int16 expansionSize`), `0x0ADE`
  (`uint32 percentage`, the configured red-weight threshold), `0x02EB`
  (`ZC_ACCEPT_ENTER2`), and `0x0B32` (`ZC_SKILLINFO_LIST`, variable length).
- `legacy/openkore/src/Network/Receive/ServerType0.pm` plus
  `tables/iRO/recvpackets.txt` corroborate `0x0283/6`, `0x0ADE/6`, `0x02EB/13`,
  `0x0B18/4`, and variable-length `0x0B32`. OpenKore's label for `0x0B18`
  conflicts with rAthena's modern structure; capture value and rAthena layout are used.
- These are generic/modern Ragnarok structural evidence matched to the captured
  iRO IDs and lengths. They do not override the capture and do not prove `0x0C1F`.

## Existing CharServer auth state (read-only diagnosis)
- On authenticated `0x0066`, `CharServer.Net.ClientSession.HandleSelectCharAsync` resolves the selected character by account and slot and stores a `MapAuthNode` keyed by account ID.
- The node contains account ID, selected character ID, login ID 1, login ID 2, sex, map/position metadata, expiration time, group ID, and map-change state. The login IDs originate in LoginServer authentication and pass through the CharServer session.
- `ExpirationTime` is currently stored as `0`; `MapAuthManager` performs no time-based expiry.
- The legacy map-entry handler reads account ID, character ID, login ID 1, and sex from its proven 19-byte legacy packet and asks CharServer to validate them. CharServer checks those values against the node, consumes it on success, and returns the stored node data.
- The iRO path validates the three capture-proven classic fields. Login ID 2, sex,
  and all modern/opaque authentication data are not claimed as validated.

## Required runtime diagnostics
For a redirected stock client, the implemented path logs:
```text
[iRO MAP DEBUG] Client connected: <ip>:<port>
[iRO MAP DEBUG] Map client packet=0x0C1F len=1001
[iRO MAP DEBUG] Received stock iRO map auth packet=0x0C1F len=1001
[iRO MAP DEBUG] Parsed 0x0C1F accountId=<id> charId=<id>
[iRO MAP DEBUG] 0x0C1F MapAuthNode authentication succeeded accountId=<id> charId=<id> sessionMatch=true
[iRO MAP DEBUG] Sending 0x0B18 len=4
[iRO MAP DEBUG] Sending 0x0283 len=6 accountId=<id>
[iRO MAP DEBUG] Sending 0x0ADE len=6 overweightPercent=70
[iRO MAP DEBUG] Sending 0x02EB len=13 map='<map>' x=<x> y=<y>
[iRO MAP DEBUG] Map client packet=0x007D len=3
[iRO MAP DEBUG] Received stock iRO map-loaded packet=0x007D len=3
```
No packet prefix or payload bytes are logged.

## Post-0x007D evidence

### Proven by Athena.NET runtime

The stock client visibly enters the game world and then sends `0x007D/3` after
Athena's minimal `0x0B18`, `0x0283`, `0x0ADE`, `0x02EB` bootstrap. The omitted
22 official pre-load packets are therefore not required to reach this first
map-loaded acknowledgement.

Before the lifecycle correction, the iRO handler called `_client.Close()` while
`RunAsync` still owned and iterated the `NetworkStream`. `TcpClient.Close()`
disposed that stream synchronously; the next read then raised
`ObjectDisposedException`. The handler no longer closes the socket. Deliberate
session termination now cancels the session read token; socket/stream disposal
happens only when the `MapTcpServer` scope exits after `RunAsync`. Session
disposal is idempotent.

### Proven from the official capture

Frame 421 contains `7D 00 BA`: `0x007D`, three total bytes, client to server.
The third byte remains opaque. Reassembled traffic from that boundary is:

| Sequence | Frame | Stream offset | Direction | Packet(s) | Evidence/role |
|---:|---:|---:|---|---|---|
| 1 | 421 | C1001 | C->S | `0x007D/3` | map-loaded/load-end acknowledgement; ID/direction correlated with upstream, iRO length capture-proven |
| 2 | 422 | S487 | S->C | `0x0C20/28` | first response; variable length and bytes capture-proven, semantics unknown |
| 3 | 423 | C1004 | C->S | `0x0360/10` | first later client boundary; payload opaque |
| 4 | 426 | S515 | S->C | `2 x 0x0ACB/12`, then `12 x 0x00B0/8` | long and 32-bit parameter/status updates |
| 5 | 427 | C1014 | C->S | `0x0C21/29`, declared length 28 plus one opaque byte | semantics unknown |
| 6 | 428-429 | S627 | S->C | 76 packets through S2143 | base stats, parameter/status updates, appearance, map properties, actor/world records, navigation, broadcast, and cash-shop data |
| 7 | 432 | C1043 | C->S | `0x0447/3` | generic ID is blocking-play cancel at length 2; iRO trailing byte opaque |
| 8 | 433 | S2143 | S->C | `0x08CA/200` | scheduler cash-item list |
| 9 | 435 | S2343 | S->C | `0x0C20/28`, `0x08CA/216` | unknown `0x0C20`, then cash-item list |
| 10 | 436 | C1046 | C->S | `0x0C21/29`, declared 28 plus opaque byte | unknown |
| 11 | 437-445 | S2587 | S->C | six `0x08CA` packets, lengths 192/256/320/520/48, then `0x0C20/52` | scheduler data plus unknown variable response |
| 12 | 446 | C1075 | C->S | `0x0C21/29`, declared 28 plus opaque byte | unknown |
| 13 | 449 | S3975 | S->C | `0x08CA/192`, `0x08CA/32`, `0x09E7/3` | scheduler data and unread-RodEx flag |
| 14 | 450 | C1104 | C->S | `0x0C21/377`, declared 376 plus opaque byte | opaque bulk request/state |
| 15 | 451 | S4202 | S->C | `0x08CA/520`, `0x08CA/80` | scheduler cash-item data |
| 16 | 453 | S4802 | S->C | `0x0C20/28` | final captured unknown response |

The 76-packet frame 428-429 subsection begins with `0x00BD/44` base stats and
contains repeated `0x00B0/8`, `0x0141/14`, and `0x00BE/5` status updates followed
by `0x0229/15`, `0x099B/8`, `0x01D6/4`, `0x01D7/15`, `0x097B/42`, `0x0AE2/7`,
`0x0446/14`, `0x0BBB/5`, `0x007F/6`, two actor records `0x09FF/98,93`,
`0x08E2/27`, `0x01C3/76`, and `0x08CA/520`. Their exact boundaries come from
fixed upstream sizes or capture length fields. This catalog is diagnostic; none
is replayed.

### Proven from upstream

- rAthena `clif_packetdb.hpp` defines generic `0x007D/2` and dispatches it to
  `clif_parse_LoadEndAck`; `clif.cpp` describes it as the client finishing map
  loading before displaying its actor.
- OpenKore `Network/Send/ServerType0.pm` names generic `0x007D/2` `map_loaded`.
- Current rAthena contains no `0x0C20`/`0x0C21` definitions. Current OpenKore's
  iRO table contains neither; its ROla table only marks both variable-length,
  without names or layouts. This is not sufficient evidence to implement them.
- Generic OpenKore tables describe `0x0360/6`; the stock-iRO capture instead
  proves one ten-byte client record. The additional bytes remain opaque.

### Implemented boundary and hard-gate result

The iRO framing table registers `0x007D/3` and capture-observed `0x0360/10`.
Both are non-terminal known boundaries. No server response is implemented after
`0x007D`: although `0x0C20/28` is the first official response, its field layout,
semantics, dynamic values, and Athena state source are unknown. Sending the
captured bytes would violate the no-replay and state-mapping rules.

Expected next diagnostics are:

```text
[iRO MAP DEBUG] Map client packet=0x007D len=3
[iRO MAP DEBUG] Received stock iRO map-loaded packet=0x007D len=3
[iRO MAP DEBUG] Map client packet=0x0360 len=10
[iRO MAP DEBUG] Reached next post-enter client boundary packet=0x0360 len=10
```

## Hypotheses / unknown
- Semantics and validation requirements for `0x0C1F` bytes `0x0E..0x3E8`,
  including its opaque modern authentication material.
- Whether a later implementation must validate that material independently of the
  existing single-use CharServer ticket.
- `0x0B32` contents and subsequent status/inventory/bootstrap packets require real
  Athena character state; no captured skill entry is replayed.
- Meaning of the third byte in captured client `0x007D/3`, which conflicts with the
  generic 2-byte upstream packet.

## Immediate next milestone: load-end acknowledgement
1. Differentially prove the third byte in captured `0x007D/3`.
2. Model the selected character's real skill state and serialize `0x0B32` from it.
3. Continue reconstructing the state-driven response order after `0x0B32`.

The next work item is the captured `0x007D/3` load-end acknowledgement and the
state-driven bootstrap beginning with `0x0B32`; neither should use capture replay.

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
