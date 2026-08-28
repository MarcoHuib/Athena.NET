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
- The node contains account ID, selected character ID, the selected authoritative character name, login ID 1, login ID 2, sex, map/position metadata, expiration time, group ID, and map-change state. The name is read from CharServer's owned character row at selection time and is carried in the authenticated internal MapAuth response for generated `strcharinfo(0)`; it is not accepted from a MapServer client packet. The login IDs originate in LoginServer authentication and pass through the CharServer session.
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
| 3 | 423 | C1004 | C->S | `0x0360/7`, `0x08C9/3` | two coalesced client packets, each with an opaque trailing iRO byte |
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
- Generic OpenKore tables describe `0x0360/6`; the walking capture proves that
  the prior ten-byte TCP payload consists of `0x0360/7` and `0x08C9/3`, not one
  ten-byte Ragnarok packet. The final byte of each remains opaque.

### Implemented boundary and hard-gate result

The iRO framing table registers `0x007D/3`, `0x0360/7`, and `0x08C9/3`.
Both are non-terminal known boundaries. No server response is implemented after
`0x007D`: although `0x0C20/28` is the first official response, its field layout,
semantics, dynamic values, and Athena state source are unknown. Sending the
captured bytes would violate the no-replay and state-mapping rules.

Expected next diagnostics are:

```text
[iRO MAP DEBUG] Map client packet=0x007D len=3
[iRO MAP DEBUG] Received stock iRO map-loaded packet=0x007D len=3
[iRO MAP DEBUG] Map client packet=0x0360 len=7
[iRO MAP DEBUG] Reached next post-enter client boundary packet=0x0360 len=7
[iRO MAP DEBUG] Map client packet=0x08C9 len=3
[iRO MAP DEBUG] Received opaque stock iRO packet=0x08C9 len=3
```

## Walking capture: world, movement, and map transitions

Primary evidence is
`/Users/marco/Downloads/full-ragnarok-flow-with-walking.pcapng`. Relevant TCP
connections are LoginServer `192.168.178.55:63054 -> 128.241.92.36:6800`,
CharServer `:63056 -> 128.241.92.43:4500`, first MapServer
`:53249 -> 128.241.92.42:4501`, and second MapServer
`:53884 -> 128.241.92.42:4506`.

### Proven chronological flow

```text
frame 114  C->Char       0x0066 character select
frame 115  Char->C       0x0071 iz_int01.gat, 128.241.92.42:4501
frame 127  C->Map :4501  0x0C1F/1001
frame 150  Map->C        0x02EB, (22,37,0)
frame 156  C->Map        0x007D/3
frame 163  Map->C        0x0AE2/7, UI type 7, data 0
frames 250..405          four 0x035F/6 -> 0x0087/12 movement pairs
frame 407  Map->C        0x0091 iz_int01.gat, (51,30)
frame 408  C->Map        0x007D/3, same TCP; no new auth
frames 425..449          three more movement pairs
frame 455  Map->C        0x0092 int_land01.gat, (85,107), 128.241.92.42:4506
           client closes :4501 and opens :4506
frame 462  C->Map :4506  second 0x0C1F/1001
frame 466  Map->C        0x02EB, (85,107,0)
frame 471  C->Map        0x007D/3
frames 539..560          two movement pairs on int_land01
```

### Initial world state and UI

`0x0071` proves `iz_int01.gat`; `0x02EB` independently proves spawn
`(22,37,direction 0)`. Athena's `iz_int03 (18,26)` is selected from the configured
five-entry `start_point` list and persisted in character state. Both `iz_int01`
and `iz_int03` are real parallel intro instances and share client resources.
The capture concerns one character/tutorial instance and does not prove Athena's
configured selection is obsolete, so no start-point change is made.

Captured `0x0AE2` is exactly seven bytes: uint16 ID, uint8 UI type `7`, int32
data `0`, little-endian. OpenKore calls it `open_ui`; rAthena's matching enum calls
type 7 `OUT_UI_ATTENDANCE`. It occurs after the first `0x007D` during world-state
initialization. Athena has no attendance counter/state, so the packet is not sent
with a fabricated zero. It is a strong candidate for one missing official UI,
but the capture alone cannot prove which observed screen the user meant.

### Capture-proven movement

Request `0x035F/6` is:

| Offset | Type | Meaning |
|---:|---|---|
| 0 | uint16 | ID `0x035F` |
| 2 | byte[3] | packed destination x/y; low nibble is zero in all samples |
| 5 | byte | opaque stock-iRO trailing byte |

Samples across both maps:

| Frame | Packed destination | Decoded target | Opaque byte |
|---:|---|---|---:|
| 250 | `05 01 E0` | `(20,30)` | `79` |
| 260 | `06 41 70` | `(25,23)` | `B7` |
| 270 | `06 41 60` | `(25,22)` | `50` |
| 401 | `06 81 E0` | `(26,30)` | `1E` |
| 425 | `0E 81 C0` | `(58,28)` | `CF` |
| 539 | `13 46 60` | `(77,102)` | `40` |

Response `0x0087/12` is uint16 ID, uint32 little-endian server tick, then six
packed movement bytes. Those bytes encode source x/y, destination x/y, and two
four-bit subcell values. The first four responses correlate as
`(22,37)->(20,30)`, `(20,30)->(25,23)`, `(25,23)->(25,22)`, and
`(25,22)->(26,30)`, with subcell `8/8`. Later clicks made before prior movement
completed show official intermediate source/subcell values: frames 425/435/448
prove the official server's own next `0x0087` after a second click reports
source `(55,28)` — an intermediate cell on the *first* click's route, not the
first click's destination `(58,28)` and not the walk's original start. This is
independently confirmed by pinned rAthena (`unit_walktoxy_timer`, `unit.cpp:542`):
authoritative position advances exactly one cell per `speed` ms of real elapsed
time, and a mid-walk retarget (`unit_walktoxy`, `unit.cpp:894-899`) re-paths from
whichever cell has *actually* been reached by the time the retarget is processed,
never from the original start or the interrupted walk's destination.

Athena previously did not reproduce this: `HandleIroMovementAsync` parsed the
destination and immediately assigned the full in-memory position to it, so a
second movement request before the client had visually finished the first walk
would retarget from a position the client was never shown reaching — producing
a visible client/server desync (reported as walking "stutter"). This is fixed:
`CharacterMovementState` (`src/MapServer/World/CharacterMovementState.cs`) now
models per-cell walk timing (`AdvanceTo`, called before deriving `from` for a
new request and on every `CurrentX`/`CurrentY` read) separately from *which*
cells a walk passes through, which comes from an injected
`IMovementPathProvider`. Production uses
`UnverifiedGridLineMovementPathProvider`, an explicitly disclosed placeholder:
pinned rAthena's real `path_search` (`path.cpp:269`) is A* pathfinding against
real GAT collision data ("We always use A* ... because it is what game client
uses. Easy pathfinding cuts corners of non-walkable cells, but client always
walks around it." — `path.cpp` comment), which only visually degenerates to a
direct line when the intervening cells happen to be obstacle-free — something
Athena cannot currently determine at all (same confirmed no-GAT-data gap as
`IMobSpawnCellSelector`). The straight-line provider is therefore NOT claimed as
rAthena/client path parity, only as the best available disclosed approximation
until real GAT/collision data exists; `IMovementPathProvider` isolates that gap
so the timing/lifecycle model above does not need to change when a real
path-search capability becomes available.

Per-cell walk speed (`CharacterMovementState`'s `cellDurationMs`) is derived by
`MovementSpeedCalculator` from the pinned `status_calc_speed` formula
(`status.cpp:8018-8223`) for the one subset this codebase currently models — a
PC with only Increase AGI's status-derived haste (already computed as
`EffectiveCharacterStats.MoveSpeedHaste`) and no other haste/slow modifier:
`speedRate = max(100 - haste, 40)`, `speed = 150 * speedRate / 100`, clamped to
rAthena's `MIN_WALK_SPEED`/`MAX_WALK_SPEED` (`20`/`1000`, `mmo.hpp:95-96`). Not
derived from Increase AGI merely because that status happens to touch movement
speed: `MoveSpeedHaste` already *is* the accumulated haste value
`status_calc_speed` itself would compute for this slice; the calculator performs
only the remaining, separately-traced steps. Mounts, carts, other haste/slow
statuses, and permanent item speed bonuses are not modeled and would need a
broader calculator.

This still has no collision, GAT-verified pathfinding, or per-tile database
persistence claim — only the per-cell *timing* lifecycle is now source-backed;
which cells a walk visits remains the disclosed placeholder above.

### 0x0368 correction

Frame 586 proves `0x0368/7`: uint16 ID, uint32 actor ID, one opaque trailing byte.
OpenKore iRO maps it to `actor_info_request`; rAthena's generic six-byte handler
reads the actor ID. Official responses correlate it with actor/name data. It is
not a movement request. Athena previously logged length 2 because unknown framing
had read only the ID before returning an unsupported boundary. The iRO table now
uses seven bytes and keeps the packet non-terminal; no actor response is invented.

### Map transitions

`0x0091/22` layout is uint16 ID, map[16], uint16 x, uint16 y. Frame 407 sends
`iz_int01.gat (51,30)`. TCP `:4501` stays open, no new `0x0C1F` occurs, and the
client sends `0x007D/3` again.

`0x0092/28` layout is uint16 ID, map[16], uint16 x/y, IPv4[4] in network byte
order, and uint16 little-endian port. Frame 455 sends `int_land01.gat (85,107)`
and `128.241.92.42:4506`. The old connection closes; the new connection starts
with `0x0C1F/1001`, bootstrap, and `0x007D/3`. Local iRO map tables label
`int_land01.rsw` `Remote Island`, and resource tables alias it to `int_land`.

The two map-auth packets have equal packet/account/character/login-ID header
bytes. Opaque equality ranges are `0x014..0x3DF` and `0x3E7`; changed ranges are
`0x00E..0x013`, `0x3E0..0x3E6`, and `0x3E8`. No scope or token semantics are
inferred from those changes.

### World / warp state

The first tutorial door now has capture evidence and matching real warp data:

```text
frame 401  C->S  0x035F/6 target (26,30)
frame 405  S->C  0x0087/12 (25,22) -> (26,30)
frame 407  S->C  0x0091/22 iz_int01.gat (51,30)
frame 408  C->S  0x007D/3 on the same TCP connection
```

No other client Ragnarok packet occurs between frames 401 and 407. The movement
response precedes the map change. The request-to-map-change interval is about
1.223 seconds and the response-to-map-change interval about 1.048 seconds; Athena
does not claim to reproduce walking-time interpolation.

Local rAthena data in `npc/re/warps/cities/izlude.txt` independently defines the
door at center `(27,30)`, radius `(1,1)`, to `(51,30)` for `iz_int` and every
`iz_int01..04` variant. `npc.cpp:npc_touch_areanpc` proves these radii are an
inclusive area, so captured target `(26,30)` is inside the real warp zone. It also
defines the reverse door at `(47,30)`, radius `(1,1)`, to `(22,30)` for all five
maps. The reverse route is upstream-mapdata proven but was not exercised in this
capture.

Athena now has an immutable `WorldMapRegistry` of data-driven `WarpDefinition`
records for those real same-map tutorial doors. After parsing movement it sends
the already proven `0x0087`, updates map/position, and sends a structured
`0x0091`. The serializer emits ID at offset 0, ASCII map[16] at offset 2 with
client-facing `.gat`, and little-endian x/y at offsets 18/20. The TCP session
stays open; the next `0x007D/3` and movement are accepted from `(51,30)`.

Runtime subsequently proved the first version only matched the requested target:
a click ending at `(28,30)` warped successfully, while a route crossing the door
and ending outside it could miss. Warp matching now enumerates a direct integer
grid line from the session position to the requested target using the standard
Bresenham error-step algorithm. Each traversed cell is checked in travel order,
so the first intersected warp wins independently of registry ordering. When a
route intersects, `0x0087` ends at that first intersection cell rather than the
far-side requested target; old-map state is never advanced beyond the portal.
Then the existing `0x0091` transition applies destination state. This is a small
world approximation, not official pathfinding: GAT collision, obstacle detours,
walking interpolation, and timing remain unimplemented.

This minimal model has no collision, pathfinding, NPC, actor, or loaded map-cache
state. Normal movement and same-server warps are currently in-memory only. The
MapServer has no character-position persistence command to CharServer, so a
disconnect/restart can restore the prior database location; expanding that
internal protocol was kept outside this client-protocol task.

The later route is separately proven:

```text
frame 425  target (58,28), frame 426 movement from (51,30)
frame 435  target (56,19), frame 436 movement from intermediate (55,28)
frame 448  target (57,14), frame 449 movement from intermediate (56,20)
frame 455  0x0092/28 int_land01.gat (85,107), 128.241.92.42:4506
```

rAthena's `#ship_out01` script is centered at `(56,15)` with radius `(1,1)` and
warps to `int_land01 (85,107)`, correlating the captured target `(57,14)` and
handoff. `0x0092` remains unimplemented. Supporting it correctly requires map
ownership/routing, a configured destination endpoint, persistence, transfer of a
fresh single-use auth ticket, closure of the old connection, and validation of a
new `0x0C1F` on the destination server. Athena can host many logically owned maps
in one process and use `0x0091` between them; it should reserve `0x0092` for an
explicitly configured cross-endpoint ownership boundary rather than rewriting an
official handoff accidentally.

### Portal visual investigation

The glow is not merely suggested by a packet name. Capture frame 163 contains a
variable `0x09FF/93` actor-exists record for `#room_out` with object type `6`,
dynamic actor ID `2304`, class/job `45`, packed position `(27,30)`, and x/y sizes
`1/1`. That position is exactly the real forward warp center. OpenKore iRO names
`0x09FF` `actor_exists`; rAthena's matching structure is the modern idle-unit
record, and `npc.hpp` proves class 45 is `JT_WARPNPC`.

The correlation repeats after the room transition:

| Frame | Packet | Name | Actor ID | Class | Position | Size |
|---:|---|---|---:|---:|---|---|
| 163 | `0x09FF/93` | `#room_out` | 2304 | 45 | `(27,30)` | `1/1` |
| 413 | `0x09FF/92` | `#room_in` | 2305 | 45 | `(47,30)` | `1/1` |
| 428 | `0x09FF/93` | `#ship_out` | 2309 | 45 | `(56,15)` | `1/1` |

This strongly supports a server-spawned warp-NPC actor as the visible portal,
with the client rendering class `JT_WARPNPC`; it is not evidence for a separate
special-effect packet. Athena does not synthesize it yet. A correct implementation
still needs a world-actor registry, collision-free dynamic actor-ID allocation,
map-load visibility lifecycle, the exact state mapping for all iRO `0x09FF`
fields, despawn behavior, and actor-info responses. Replaying actor ID 2304 or a
captured record would be incorrect.

Captured `0x0368/7` does not establish a portal link. On the second MapServer its
proved actor request targets actor ID 7966, whose preceding `0x09FF` record is the
NPC `Lumin#new01_ship` at `(73,100)`, class 639—not a `JT_WARPNPC`. The periodic
`0x0360/7` is likewise left unrelated to warp or portal state.

### Persistent position and generated world data

CharServer remains persistence owner. MapServer now sends authenticated internal
`0x2B28/30` containing account ID, character ID, map[16], x and y after `0x0091`
and when a dirty authenticated session reaches EOF or graceful cancellation.
CharServer only accepts it when that same authenticated MapServer session consumed
the `(accountId,charId)` auth node, and updates `last_map/last_x/last_y` on the
matching non-deleted row. `save_map/save_x/save_y` remain respawn/savepoint state.
Normal movement is in-memory and dirty; no per-tile write or timed checkpoint is
performed.

The runtime no longer reads `data/world/warps.json` or entity JSON. The default
compiled world intentionally contains the pinned room transitions for `iz_int`
and the actively used `iz_int03` instance plus the two generated executable
entities used by the gameplay slice. The complete pinned rAthena tree remains available to
regenerate additional definitions as their runtime capabilities are implemented.

Static and visual-only WARPNPC definitions now produce stable `WarpActor` state.
Actor IDs come from a thread-safe rAthena NPC domain beginning at 110000000, not
captured IDs. On `0x007D`, the server emits visible actors in a 14-cell square via
structured `0x09FF`; movement emits newly in-range actors once per visibility
cycle. This makes the two currently supported room actors available from the
same compiled definitions that drive their geometry.

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

## Weapon-aware basic melee combat (Knife vs G_PORING)

The starter tutorial character is not actually unarmed - it has a persisted Knife
(itemId 1201, Attack 17, WeaponLevel 1, WeaponType Dagger) equipped in the right
hand by default. Live 0x0437 attacks against G_PORING must resolve through the
pinned RENEWAL PC combat path, using whatever is CURRENTLY equipped in the
right hand, not a hardcoded unarmed assumption.

### Pinned source trace: armed and unarmed share ONE PC pipeline

`battle.cpp:4140-4142` - `if (sd) battle_calc_damage_parts(...) else
battle_calc_base_damage(...)`. This branch is gated purely on "is the attacker a
PC" (`sd`), **never** on whether a weapon is equipped. `battle_calc_base_damage`
is exclusively the non-PC/monster branch and is never reached by any PC normal
attack in RENEWAL, armed or unarmed. An earlier implementation incorrectly used
`battle_calc_base_damage` for the unarmed case (a distinct `BasicAttackCalculator`
class, since removed) - that bug is now fixed as part of unifying armed/unarmed
into one `RenewalBasicAttackRules`/`WeaponAttackCalculator` pipeline with an
optional weapon term, per the pinned source's own structure.

Full call chain, traced field-by-field in `RenewalBasicAttackRules`'s own doc
comment (`src/MapServer/Gameplay/Rules/Renewal/RenewalBasicAttackRules.cs`):

1. `status_base_atk` (`status.cpp:2424`) - `batk` formula (Dagger/fists are not
   DEX-flagged weapon types).
2. `battle_calc_damage_parts` (`battle.cpp:3889`) - `statusAtk = 2*batk`
   (doubled, unconditionally - unarmed included), `weaponAtk` from
   `battle_calc_base_weapon_attack`, `equipAtk`/`masteryAtk` both correctly 0
   for a fresh Novice with no eatk-granting items or weapon-mastery skills.
3. `battle_calc_base_weapon_attack` (`battle.cpp:2443`) - when no weapon is
   equipped, its own `if (sd && sd->equip_index[type] >= 0 ...)` guard
   (`battle.cpp:2453`) is false, leaving `atkmin=atkmax=status->watk=0` (an
   unarmed PC's `rhw.atk` is never populated by the equipment-parse loop) - so
   `weaponAtk` collapses to exactly 0 through the SAME function, not a separate
   code path. When a weapon IS equipped: `atkmin/atkmax` from `wa.atk` (= item
   Attack at refine 0) +/- variance (`5*atk*wlv/100`) + STR-based
   `base_stat_bonus` (`atk*STR/200`), then `rnd_value(atkmin,atkmax)`, then
   `battle_calc_sizefix` (`battle.cpp:2427`): `damage * atkmods[size] / 100`.
4. `wd.damage = statusAtk + weaponAtk + equipAtk + percentAtk`, `+= masteryAtk`.
5. `battle_calc_defense_reduction` (`battle.cpp:4720`) - RE DEF formula and
   monster soft-DEF (`def2 = floor((Level+Vit)/2)`), identical for both cases.
6. `battle_calc_attack` (`battle.cpp:6766`) - damage < 1 is a miss (0 damage).

**Size-fix ambiguity, resolved by pinned data default, not by capture-matching**:
`db/re/size_fix.yml` has no `Dagger` row (only Knuckle/Whip carry entries). The
pinned C++ initialization path for `atkmods[]` when a weapon type has no
size_fix.yml row could not be fully traced through `TypesafeYamlDatabase`
plumbing in the pinned snapshot; the YAML file's own header comment states the
column default is 100 for every unlisted weapon/size pair, which is also the
only value consistent with real gameplay (a Dagger dealing zero damage to every
target of an unlisted size would be an obvious live-game bug). `atkmods[SZ_*]`
is therefore treated as 100 (no-op) for Dagger against any target size -
`MobDefinition` gets no `Size` field for this slice since the modifier is a
no-op regardless of target size for this weapon type.

### Fields Athena already had vs. required

Already present and reused unchanged: `EffectiveCharacterStats` (STR/AGI/VIT/
INT/DEX/LUK), `MobDefinition.Defense/Level/Vit`, the RE DEF-reduction formula,
`WeaponItemDefinition.Attack/WeaponLevel`, `CharacterEquipmentSnapshot`
(live-maintained per session, rebuilt only after confirmed persistence),
`EquippedWeaponResolver` (previously only consumed by
`SendSelfWeaponAppearanceAsync` for the 0x01D7 LOOK_WEAPON packet - now also
consumed by the attack path). No new authoritative input was missing; this was
purely a missing combat-formula/dispatch gap, not a data gap.

## Gameplay ruleset selection (Renewal / PreRenewal composition boundary)

Athena.NET currently implements **RENEWAL gameplay only**, because the current
official iRO client is the only live target combat mechanics can be validated
against. Renewal-specific formulas must not leak into general MapServer
orchestration (`MapClientSession`, `MonsterCombatCoordinator`, inventory/
equipment ownership, monster HP/death handling, quest/drop handling) - those
classes depend only on ruleset-agnostic interfaces under
`src/MapServer/Gameplay/Rules/`.

### Folder/namespace layout

```text
src/MapServer/Gameplay/Rules/
    RagnarokRuleSet.cs        - enum { Renewal, PreRenewal } (domain value only)
    GameplayOptions.cs        - RuleSet selection, sourced from MapConfig
    GameplayRulesFactory.cs   - the ONE place ruleset -> implementations is decided
    GameplayRuleServices.cs   - the composed bundle MapServerWorld.Build receives
                                 (currently just BasicAttackRules; future independently
                                 scoped rule interfaces are added here, not folded into
                                 one giant IGameRules interface)
    IBasicAttackRules.cs      - ruleset-agnostic basic-melee-attack contract
    BasicAttackContext.cs     - authoritative inputs (attacker stats/level, optional
                                 equipped weapon, target) - never client-supplied state
    BasicAttackDamageResult.cs
    Renewal/
        RenewalBasicAttackRules.cs   - IBasicAttackRules impl; owns the pinned-source trace
        WeaponAttackCalculator.cs    - internal pure-math helper RenewalBasicAttackRules uses
    PreRenewal/
        README.md                    - documents the convention; NO C# implementation yet
```

`PreRenewal/` holds only a `README.md` - git does not track empty directories,
and an empty placeholder folder would need a stub C# file to force tracking,
which is explicitly not wanted. The README documents that any future Pre-Renewal
implementation belongs there, registered from `GameplayRulesFactory.Create`'s
`RagnarokRuleSet.PreRenewal` branch, without touching `IBasicAttackRules`,
`GameplayRuleServices`, `MonsterCombatCoordinator`, or `MapClientSession`.

### Composition root and configuration

This codebase has no `Microsoft.Extensions.DependencyInjection` container -
`MapServerApp.RunAsync` (`src/MapServer/Startup/MapServerApp.cs`) is the ONE
composition root that decides gameplay ruleset selection. `MapServerWorld.Build`
(`src/MapServer/World/MapServerWorld.cs`) receives an already-composed
`GameplayRuleServices` bundle as a **required** parameter - it never constructs
`GameplayOptions`, never references `RagnarokRuleSet`, and never calls
`GameplayRulesFactory.Create` itself, so it stays entirely unaware of which
ruleset produced the bundle it was handed:

```text
map_athena.conf "gameplay_ruleset: Renewal"
    -> MapConfigLoader (RagnarokRuleSet.TryParse; key ABSENT -> Renewal default;
       key PRESENT but unrecognized -> throws InvalidOperationException, config load fails)
    -> MapConfig.GameplayRuleSet
    -> MapServerApp.RunAsync builds GameplayOptions { RuleSet = mergedConfig.GameplayRuleSet }
    -> GameplayRulesFactory.Create(options) -> GameplayRuleServices
    -> MapServerWorld.Build(gameplayRules: services)
    -> new MonsterCombatCoordinator(monsters, questDrops, gameplayRules.BasicAttackRules)
```

`GameplayRulesFactory.Create` is a plain `switch` on `RagnarokRuleSet`:
`Renewal` returns `new GameplayRuleServices(new RenewalBasicAttackRules())`;
`PreRenewal` throws `NotSupportedException("Pre-Renewal gameplay rules are not
implemented.")`. `PreRenewal` DOES parse successfully as a config value (it is a
real, valid `RagnarokRuleSet` member) - the failure happens at composition, not
at config-parse time. There is no silent fallback anywhere in this chain:
- An absent `gameplay_ruleset` key defaults to Renewal (the same "use the field
  default" convention every other optional key in `map_athena.conf` follows).
- A PRESENT but unrecognized value (a typo, or a future enum member the running
  binary doesn't know about) throws `InvalidOperationException` out of
  `MapConfigLoader.Load` itself - config loading fails outright, it does not
  quietly resolve to Renewal or any other value.
- Selecting the real, valid `PreRenewal` value fails MapServer composition
  loudly via `GameplayRulesFactory.Create`'s `NotSupportedException` (composition
  happens before the TCP listener starts accepting connections).

`MonsterCombatCoordinator` depends on `IBasicAttackRules` only and forwards a
`BasicAttackContext` (attacker stats/level, the CURRENT authoritative equipped
weapon or null, target) into `Calculate` - it never asks which ruleset is active.
`MapClientSession.HandleIroAttackRequestAsync` resolves the equipped weapon
through the same `EquippedWeaponResolver` path `SendSelfWeaponAppearanceAsync`
already used (never `ClientViewId`/LOOK_WEAPON, never cached across attacks), so
a same-session equip/unequip changes the very next attack's calculation with no
reconnect and no coordinator-side cache to invalidate - `MapClientSession` itself
selects nothing Renewal-specific. `EquippedWeaponResolution.UnknownItem` and
`NonWeaponInWeaponSlot` are authoritative-state/data invariant FAILURES (an
equipped item id absent from the generated item registry, or a non-weapon item
resolved into the weapon slot) - `HandleIroAttackRequestAsync` treats either as
grounds to reject/abort the attack outright (logged, no combat calculation runs,
no wire response is sent at all), never as a legitimate unarmed state. Only
`EquippedWeaponResolution.Unarmed` may enter the unarmed `RenewalBasicAttackRules`
path.

The weapon-ATK roll is injectable (`RenewalBasicAttackRules`'s constructor takes
an optional `Func<int,int,int> rollWeaponAtk`, forwarded into
`WeaponAttackCalculator`, same pattern as `QuestDropResolver`'s injectable RNG)
so tests can pin it deterministically; production defaults to `Random.Shared`.

Live stock-iRO validation is now PROVEN: equipped starter Knife vs G_PORING dealt
19/18/18 damage across three consecutive live hits, the target's HP reached 0,
death notification and quest Wood reward/persistence/0x0B41 pickup all worked.
Unequipping the Knife during the same MapServer session immediately returned
subsequent attacks to the genuine unarmed RENEWAL calculation (observed damage
0 for this character/target state); re-equipping immediately restored
weapon-aware damage (18/19) without reconnecting, and a second G_PORING died
with the Wood stack amount increasing. The stock capture's originally observed
37/36 damage remains validation evidence only, not an input to the
implementation - the stock capture and this Athena run are not yet proven to
share identical runtime status/buff state, so that exact-value comparison is
separate future work.

## Base/Job progression composition and presentation

MapServer configuration constructs one immutable `GameplayRateOptions` policy
(`Athena.Net.MapServer.Gameplay.Rates`). ATHENA.NET SERVER POLICY: global rates
(`base_exp_rate`, `job_exp_rate`, `item_drop_rate`) always have a value (100 =
1x when unset); every other rate key (`quest_base_exp_rate`,
`quest_job_exp_rate`, `mvp_base_exp_rate`, `mvp_job_exp_rate`,
`card_drop_rate`, `boss_item_drop_rate`, `mvp_item_drop_rate`,
`quest_item_drop_rate`, the `item_rate_*` family, `item_rate_mvp`) is an
OPTIONAL override: unset means "inherit the relevant global rate", never an
independent default. Explicit malformed, negative, or out-of-range values fail
configuration loading. The same immutable object flows through `MapServerWorld`
to every `MapClientSession`.

All reward sources inherit global server rates by default. Source-specific
rates are optional overrides and REPLACE, rather than multiply, the inherited
rate - there is exactly one place this is decided:
`Athena.Net.MapServer.Gameplay.Rates.GameplayRateResolver`. Monster kills
always resolve `base_exp_rate`/`job_exp_rate` directly (no monster-specific
override exists). Generated NPC/script `getexp` and future quest rewards
resolve `quest_base_exp_rate ?? base_exp_rate` and `quest_job_exp_rate ??
job_exp_rate` - e.g. with `base_exp_rate: 500`/`job_exp_rate: 500` and no
quest override configured, Captain Carocc's generated `getexp 600,600`
resolves to 3000/3000 (inherits 5x), not 600 unrated; if
`quest_base_exp_rate: 1000` were also configured, the same call resolves to
6000 (the override REPLACES 500), never 30000 (it never stacks with the
inherited global). `Athena.Net.MapServer.Gameplay.Rates.ExperienceRewardService`
is the only place a raw Base/Job EXP reward is turned into this final rated
value, tagged by `ExperienceSource` (Monster/Quest/Script/Mvp/Event); its
output is the only thing `CharacterProgressionService` ever receives.

`CharacterProgressionService` itself has no rate/source concept at all - it
receives only already-rated final Base/Job EXP, resolves the active job
through generated `GeneratedProgressionRegistry`, performs the one atomic
versioned state mutation, and publishes no local state or packets until
CharServer acknowledges it. On a player-caused Alive→Dead monster transition,
raw generated mob EXP is resolved through `ExperienceRewardService`
(`ExperienceSource.Monster`) before this service is called, then the dead
actor is removed. G_PORING's generated zero/zero raw award resolves to
zero/zero at any rate and therefore causes no persistence and no progression
packets; a nonzero generated mob uses the identical path. Party attribution
and MVP bonus EXP remain unimplemented.

Drop-rate policy follows the identical inherit-unless-overridden shape via the
same resolver's `ResolveDropRate`, modeled with `DropSource` (Monster/Boss/
Mvp/Quest/Script/Event), `ItemCategory` (Common/Heal/Use/Equip/Card), and
`RewardKind` (NormalDrop vs the MVP's own direct-reward `MvpReward` - distinct
from the `item_rate_*_mvp` family, which is a normal-drop-table item merely
dropped BY an MVP monster). ATHENA.NET SERVER POLICY: resolution uses the most
specific configured override, each level REPLACING (never stacking with) the
level below it: (1) the exact source+category override, e.g.
`item_rate_card_boss`; (2) the source-level override, e.g.
`boss_item_drop_rate`/`mvp_item_drop_rate`/`quest_item_drop_rate`; (3) the
generic category override - currently only `card_drop_rate`, the sole
category with a source-independent override; other categories
(Common/Heal/Use/Equip) fall straight from their source-level override to (4)
the global `item_drop_rate`. Example: `item_drop_rate: 200`,
`card_drop_rate: 100`, `boss_item_drop_rate: 300`, `item_rate_card_boss`
unset resolves Boss Card to 300 (source-level beats the generic category and
global); setting `item_rate_card_boss: 50` instead resolves it to 50. The
direct MVP reward (`RewardKind.MvpReward`) resolves
`item_rate_mvp ?? mvp_item_drop_rate ?? item_drop_rate` - a separate rate
family from the `item_rate_*_mvp` normal-drop-table categories above. This PR
only makes the rate policy correct and extensible for drops - it does not add
a generic monster drop/MVP-reward runtime. The tutorial Wood/Lumber
`QuestDropRule` continues to roll its own probability unchanged; drop rate
would only ever scale a roll CHANCE, never an item count, and a guaranteed
100% drop must remain capped at 100% regardless of any configured rate.

`IroCharacterProgressionPackets` owns `0x00B0`, `0x0ACB`, capture-proven
`0x0ACC/18`, and `0x019B/10`. Its API receives the authenticated actor/account ID
explicitly and serializes from the persisted `CharacterProgressionResult`; raw
packet writes and captured payload replay do not occur in session logic. See
`ai/iro-2026-wire.md` for the Full-izlude field layouts and ordering.

## Authoritative inventory SlotIndex consistency

The live weapon-combat validation above also exposed a genuine authoritative-
state consistency bug in the inventory pipeline, now fixed. This section
documents the resulting invariants.

### The bug

There is exactly ONE authoritative server-side inventory `SlotIndex` namespace:
a character's own `CharInventory` rows, ordered stably by `Id` ascending
(`CharInventoryOrdering.InStableSlotOrder`, `src/CharServer/Db/
CharInventoryOrdering.cs`) - matching pinned rAthena's own load-order-derived
`sd->inventory.u.items_inventory[]` array position, since neither Athena's
schema nor real rAthena's own `inventory` SQL table persists a slot column at
all. **Equipped and unequipped rows share this same namespace** - equip state
is not a slot-partitioning concern.

`MapServerSession.HandleInventoryListGetAsync` and `HandleInventoryEquipUpdateAsync`
always used this full ordering. `HandleInventoryAddRequestAsync` previously
computed its returned `SlotIndex` via `CountAsync(item.Equip == 0 && item.Id <
row.Id)` - filtering OUT equipped rows - producing a second, incompatible
namespace: a character with a Knife and Cotton Shirt equipped (2 rows) plus an
unequipped First Aid Box (1 row) would see the inventory-list read assign
slots 0/1/2, while an inventory-add for a new Wood row would undercount by the
2 equipped rows and return the wrong slot. All three handlers now share the
same `InStableSlotOrder` extension method - there is exactly one place this
ordering is defined.

Separately, `MapClientSession`'s quest-drop reward path computed the client-facing
`0x0B41` index from the returned `SlotIndex` but never updated the session's own
authoritative `_inventory`/`_equipment` runtime state, so the MapServer runtime
snapshot stayed stale (still reflecting login-time state) after a successful
pickup, even though CharServer's database was correctly updated and the client
had already been told the pickup succeeded.

### Fixed invariants

- **One stable server-side inventory `SlotIndex` namespace.** Equipped and
  unequipped rows occupy the same ordering; equip state never partitions or
  removes a row from it.
- **`client_index = server SlotIndex + 2`** is applied ONLY at the wire
  serialization boundary (pinned `clif.cpp:122-124`), never earlier.
- **CharServer owns durable inventory state**; MapServer owns the confirmed
  runtime snapshot of that state (`CharacterInventorySnapshot`, held in
  `MapClientSession._inventory`).
- **`CharacterEquipmentSnapshot` is always derived from `CharacterInventorySnapshot`**
  (`CharacterEquipmentSnapshot.FromInventory`) - never a second, independently
  mutable copy.
- **Persisted mutations update MapServer's runtime inventory snapshot BEFORE
  client notification.** `MapClientSession`'s reward path now calls
  `_inventory = inventory.WithItem(addedItem)` and re-derives `_equipment`
  immediately after a successful `CharacterInventorySession.AddItemAsync`,
  before sending `0x0B41` - matching the same
  validate -> persist -> update runtime state -> notify ordering
  `HandleEquipRequestAsync`/`HandleUnequipRequestAsync` already used.
- **A failed persistence never mutates the runtime snapshot and never notifies
  the client** (no fake pickup success).

### Internal CharServer <-> MapServer protocol extension

`MapInventoryAddProtocol`'s response (both `CharServer.Net` and `MapServer.Net`
copies) now carries the persisted row's own authoritative `Equip`/`Identified`/
`Refine`/`Favorite`/`Bound` fields alongside `newAmount`/`slotIndex` (27 bytes,
up from 19) - CharServer is the only side that knows these values (e.g.
`Identify=1` is set at insert time), so MapServer never invents, assumes, or
duplicates CharServer's persistence rules to reconstruct the authoritative
`CharacterInventoryItem` this add produced or updated. No `IsNewRow` flag was
added: `CharacterInventorySnapshot.WithItem` derives new-row-vs-replace purely
from the returned `SlotIndex` compared against the runtime snapshot's own
current row count (`SlotIndex == Items.Count` -> append; `SlotIndex <
Items.Count` -> require the same `ItemId` already at that slot, then replace).
Any other case - `SlotIndex > Items.Count`, or a slot occupied by a different
`ItemId` - is treated as an authoritative-state invariant violation and throws
rather than guessing/repairing, matching this codebase's existing "never
silently resolve a data invariant violation" convention.

The full chain: `ICharacterInventoryPersistence.AddStackableItemAsync` returns
a single named `InventoryAddPersistenceResult` record (not a growing tuple) ->
`CharacterInventorySession.AddItemAsync` builds the authoritative
`CharacterInventoryItem` from it and returns it via `InventoryAddResult.Item` ->
`MapClientSession` applies it through `CharacterInventorySnapshot.WithItem`.

### DMG_REPEAT (documented future work, not implemented)

The stock iRO capture strongly suggests one `0x0437` request carrying
`actionType=DMG_REPEAT` starts a continuing attack sequence from which multiple
server-side damage events can follow. Athena currently performs exactly one hit
per `0x0437` request. Implementing a continuing auto-attack loop is a separate
future combat capability, out of scope for the inventory-consistency fix.

### Quest-state logging (documented cleanup, not addressed)

Some existing quest-state logging describes a `GetQuestStateAsync` read
operation in terms that read similarly to a persistence mutation. This is a
pre-existing logging-clarity issue, unrelated to the inventory fix; noted here
as future cleanup rather than addressed in this task.

## Item-use request (0x00A7) and the First Aid Box container vertical slice

### Live capture evidence

Using the starter First Aid Box from the stock iRO client previously caused the
session to disconnect: `[WARN] Unsupported map client packet=0x00A7 len=2`. The
logged length (2) was an artifact of the framing bug below, not the real packet
length - Athena had never registered `0x00A7` in `PacketLengths`, so
`ReadPacketAsync` consumed only the 2-byte opcode and returned, and `RunAsync`
treated that as EOF and disconnected.

Pinned rAthena's generic `clif_packetdb.hpp` table is genuinely ambiguous for
`0x00A7` across `PACKETVER` branches - it has been `clif_parse_UseItem`
(`CZ_USE_ITEM`, 8 bytes), `clif_parse_SolveCharName`, `clif_parse_UseSkillToPos`,
and `clif_parse_WalkToXY` in different historical branches, and the most recent
branch in the pinned tree maps it to `WalkToXY` - so the generic table alone
could not prove current-iRO semantics. A targeted, temporary diagnostic
instrumentation (since removed) drained and logged whatever bytes the client had
already queued immediately after the opaque 2-byte header, without guessing a
length. The live capture proved:

```text
A7 00 04 00 80 84 1E 00 D2
```

`opcode.W(0x00A7) clientIndex.W(4) accountId.L(2,000,000) opaqueByte.B(0xD2)` -
the classic `CZ_USE_ITEM` shape (`index.W accountId.L`, `clif.cpp:12077-12078`)
plus one opaque trailing byte, matching the exact pattern already proven for
attack/equip/unequip/movement/NPC packets. The `accountId` field exactly matched
the authenticated session's own account, confirming field identity. Per this
project's evidence-priority rule, this live capture wins over the ambiguous
pinned generic table. `PacketConstants.IroCzUseItem = 0x00a7`,
`IroCzUseItemLength = 9`, registered in `MapClientSession.PacketLengths` exactly
like every other iRO packet.

### Resolved item and pinned behavior

`clientIndex 4` -> `SlotIndex = clientIndex - 2 = 2` (the same convention every
other equip/unequip/pickup path already uses) -> the tutorial character's third
starter row (`char_athena.conf start_items: 1201,1,2:2301,1,16:23484,1,0` -
Knife equipped, Cotton Shirt equipped, First Aid Box unequipped) -> **ItemId
23484, "Firstaid_Box_5"**, `GeneratedItems.FirstAidBox` (`UsableItemDefinition`).

Its pinned `db/re/item_db_usable.yml` entry is a container/item-group opener,
**not** a healing effect - its `Script` is five constant `getitem` statements:

```text
getitem 11518,10;   // N_Blue_Potion, Healing
getitem 11614,20;   // Fresh_Milk, Healing
getitem 12325,15;   // N_Magnifier, DelayConsume
getitem 22542,1;    // Center_Potion_B, Usable (sc_start effect, not itself a container)
getitem 23485,1;    // Firstaid_Box_10, Usable (a bigger box, same container pattern)
```

Traced call chain: `clif_parse_UseItem` (`clif.cpp:12077-12106`) resolves
`n = server_index(index)` and calls `pc_useitem` (`pc.cpp:6450-6576`), which
validates via `pc_isUseitem` (`pc.cpp:6276-...`, gate: `type == IT_HEALING ||
IT_USABLE || IT_CASH` - First Aid Box's `IT_USABLE` passes trivially), then for
an immediate-consume item (`delay_consume == 0`, no `expire_time`) sends
`clif_useitemack(sd, n, amount-1, true)` **before** `pc_delitem(sd, n, 1, ...)`,
then `run_script` executes the item's script. `pc_delitem` (`pc.cpp:6103-6128`)
does **not** shift/renumber the in-memory array on removal - it `memset`s the
row to zero in place, still occupying that array index.

### `amount == 1` / row-removal semantics

Athena's `SlotIndex` is derived from stable row-Id ordering
(`CharInventoryOrdering.InStableSlotOrder`), not a fixed persisted array column,
so there is no equivalent "zeroed placeholder row" to leave behind. The smallest
correct translation: **CharServer deletes the row** when its amount reaches zero
(`MapServerSession.HandleInventoryConsumeAsync`), and MapServer applies that via
`CharacterInventorySnapshot.WithoutSlot`, which renumbers every later row's
`SlotIndex` down by one - exactly reproducing what a fresh full inventory reload
would produce, so a subsequent reconnect and the live runtime snapshot always
agree. This project has no live-verified evidence of what a real client expects
mid-session when a LOWER slot is deleted while un-reloaded higher-numbered UI
elements keep stale indices; the narrow case this task targets (consuming the
character's only First Aid Box, the LAST occupied slot at time of use) never
exercises that gap, and it is deliberately left unaddressed rather than guessed
at.

### Container item data (source-derived, not hardcoded)

`ItemDataCompiler` gained two new concrete `ItemDefinition` subtypes -
`HealingItemDefinition` (`IT_HEALING`) and `DelayConsumeItemDefinition`
(`IT_DELAYCONSUME`) - so pinned rows of those types are representable as
authoritative inventory data without collapsing them into `UsableItemDefinition`
or `EtcItemDefinition`. Neither type's real gameplay effect (`itemheal`,
`itemskill`, etc.) is implemented - using any of the five granted items is a
separate, unimplemented future vertical slice.

`UsableItemDefinition` gained an optional `Grants` field
(`IReadOnlyList<ItemGrantDefinition>`), populated only when
`ItemDataCompiler.TryParseGetItemScript` recognizes the item's pinned `Script`
as a sequence of constant `getitem <id>,<amount>;` statements - the ONLY script
shape this project models; this project has no general rAthena script
interpreter. The recognizer only commits to "this is a container" once the
script's first statement is `getitem` - a Usable item whose script is something
else entirely (e.g. Center_Potion_B's `sc_start`) simply has no `Grants` (that
effect stays unmodeled, matching Healing's own unmodeled `itemheal`), but once a
script DOES start with `getitem`, every remaining statement must also be a
constant `getitem` or generation fails loudly - never silently representing
only a getitem prefix and dropping an unrecognized suffix.

`GeneratedItems.FirstAidBox.Grants` is generated data (`compile-item --item-id
23484 --item-db-file db/re/item_db_usable.yml`) exactly matching the pinned
script's five `getitem` calls. All five granted items (`BluePotion`,
`FreshMilk`, `NoviceMagnifier`, `CenterPotionB`, `FirstaidBox10`) are likewise
real generated item data, registered in `GeneratedItems.ById`, so they exist as
authoritative inventory rows once granted - `MapClientSession` never invents a
fake `ItemDefinition` for a granted item id.

### Acknowledgement and architecture

`ZC_USE_ITEM_ACK2` (`0x01C8`, pinned `clif.cpp:4468-4497` /
`packets_struct.hpp:2577-2589`, `PACKETVER_RE_NUM >= 20180704` branch, 15 bytes:
`index.W itemId.L accountId.L amount.W result.B`) is the pinned-source layout
for the current `PACKETVER` branch - not yet independently capture-verified on
the response side (only the request side has a live capture so far). Pinned
`clif_useitemack` sends to `AREA` on success (`SELF` only on failure); Athena
has no cross-session/multi-client broadcast infrastructure at all yet, so this
slice sends to `SELF` only in both cases - a disclosed, real limitation, not an
invented simplification.

`MapClientSession.HandleIroUseItemRequestAsync` follows this project's
validate -> persist -> update runtime state -> notify rule (`AGENTS.md`), which
is also consistent with pinned `pc_useitem`'s own real ordering for this exact
immediate-consume case (ack sent from the row's pre-delete state, before
`pc_delitem` runs) - persisting first and building the ack from the confirmed
post-persist state produces the same wire values without needing to
special-case send-before-persist. After the ack, each `getitem` grant executes
through the SAME `CharacterInventorySession`/runtime-snapshot-update path the
quest-drop reward loop already uses, and each produces its own `0x0B41` pickup
notification - matching the pinned script's five independent `getitem` calls,
not one atomic operation (a grant referencing an unregistered item id is logged
and skipped, not fatal to the remaining grants).

The internal `MapInventoryConsumeRequest`/`Response` protocol
(`0x2b37`/`0x2b38`) is new: CharServer resolves the target row from `SlotIndex`
through the SAME `CharInventoryOrdering.InStableSlotOrder` the list/add/
equip-update handlers already share, decrements or deletes it (pinned
`pc_delitem`), and reports `RowDeleted` so MapServer knows whether to replace or
remove that slot in its own runtime snapshot.
