# Stock iRO 2026 verified wire protocol

## Purpose
This file is the protocol authority for the current stock iRO client targeted by Athena.NET.

Only add a statement to **Verified** when it is supported by an official successful capture, repeatable runtime evidence, or an exact regression fixture derived from that evidence. Keep unknowns under **Open**.


## Local reference projects
- `legacy/rathena/` is the local reference for server architecture, gameplay/domain behavior, data formats, scripts, database concepts, and implementation patterns.
- `legacy/openkore/` is the local reference for packet naming, iRO/community protocol clues, and interpretation of captured traffic.
- Both are reference-only and read-only by default. Neither is authoritative over verified stock-iRO capture/runtime evidence.
- Do not use a generic rAthena/kRO `PACKETVER` branch or an OpenKore packet table as proof of the iRO wire format without capture/runtime confirmation.

## Verified LoginServer flow
- Client login request: `0x0064`, 55 bytes.
- Observed client version field: `18`.
- Fixed username/password fields must be decoded through the first `0x00`; bytes after the first NUL are not part of the string.
- Successful login response: `0x0A4D`.
- Captured successful response length with three worlds: 160 bytes.
- `0x0A4D` consists of a 64-byte login header followed by 32-byte server/world entries.
- Captured official worlds included Chaos, Thor, and Freya.
- Captured Chaos CharServer endpoint: `128.241.92.43:4500`.
- The earlier 160-byte-per-server `0x0AC4` interpretation is not valid for this iRO flow.

## Verified CharServer flow
- Client enter: `0x0065`, 17 bytes.
- Server sends raw 4-byte account ID.
- Slot information: `0x082D`, 29 bytes.
- Captured iRO slot fields: normal/premium/billing/producible/valid = `9/9/0/9/9`.
- Legacy `0x006B` was not observed in the verified iRO flow.
- Sync announcement: `0x09A0`, 6 bytes, `syncCount = 12`.
- Client sync request: `0x09A1`, 2 bytes.
- Character page/list response: `0x0B72`.
- Empty `0x0B72`: exactly `72 0B 04 00`.
- `CHARACTER_INFO`: exactly 175 bytes for the targeted stock client.
- `CHARACTER_INFO.CharNum`: relative offset 138.
- In a `0x0B72` packet, first record `CharNum` is at absolute offset 142.
- Slots 0, 1, and 8 have regression coverage for the slot offset.
- Client/account keepalive/check: `0x0187`, 6 bytes. Official server echoes the same account ID packet.
- PIN disabled means no `0x08B9` is sent in the verified Athena.NET iRO path.
- Character create request: `0x0A39`, 36 bytes.
- `0x0A39` fields: packet ID[2], name[24], slot[1], hairColor[2], hairStyle[2], job[4], sex[1].
- Successful create response: `0x0B6F`, 177 bytes = packet ID[2] + `CHARACTER_INFO[175]`.
- Character creation correctly rejects an occupied slot; a free adjacent slot has been verified to create and persist successfully.
- Character select: `0x0066`, 3 bytes: packet ID + slot.
- Map handoff: `0x0071`, 28 bytes: packet ID[2], char ID[4], map name[16], IPv4[4], port[2].
- Captured official MapServer endpoint: `128.241.92.42:4501`.

## Verified MapServer entry evidence
- After `0x0071`, the stock client opens a new TCP connection to the advertised MapServer.
- First observed stock-iRO client packet to the official MapServer: `0x0C1F`.
- Observed `0x0C1F` length: 1001 bytes.
- The captured packet contains a large authentication/token payload.
- Capture-proven little-endian `0x0C1F` fields are account ID at offset `0x02`,
  selected character ID at `0x06`, and login ID 1 at `0x0A`. Each was correlated
  to the same successful LoginServer/CharServer session. Bytes `0x0E..0x3E8`
  remain opaque.
- The first captured server packets are `0x0B18/4`, `0x0283/6`, `0x0ADE/6`,
  `0x02EB/13`, then `0x0B32/19`.
- Captured `0x02EB` is accept-enter with a 32-bit tick, packed position/direction,
  `xSize=5`, `ySize=5`, and a 16-bit font.
- The first later client packet is captured as `0x007D/3`; its trailing byte is
  opaque and conflicts with generic upstream's 2-byte layout.
- Athena runtime now independently proves that the stock client enters the world
  and sends `0x007D/3` after accepting the four-packet Athena bootstrap.
- The official capture's `0x007D` bytes are `7D 00 BA`. Only the ID and total
  length are semantic evidence; `BA` remains opaque.
- The first official server record after `0x007D` is frame 422, `0x0C20/28`.
  Its length field is 28, but neither current rAthena nor current iRO OpenKore
  provides a matching name or field layout. It must not be synthesized yet.
- Reanalysis with the walking capture proves that the apparent `0x0360/10` was
  two packets coalesced in one TCP segment: stock-iRO `0x0360/7`, followed by
  `0x08C9/3`. Both have one opaque trailing iRO byte beyond their generic layouts.
- The complete official server burst before that `0x007D` is 487 bytes and 26
  packets. After `0x02EB` it contains `0x0B32`, status/appearance packets,
  reputation/config packets, an inventory transaction, one unknown `0x0BF2/13`,
  more status packets, and `0x0A24`.
- Captured `0x0B32/19` is a four-byte header plus one 15-byte `NV_BASIC` skill-tree
  entry at level zero. It is not evidence that the character has learned the skill.
- Official `0x0071` client map names include `.gat`. Athena's internal map names
  remain extensionless; only the client-facing handoff is normalized to `.gat`.

## Open MapServer questions
- Meaning and validation requirements of the remaining opaque `0x0C1F` fields.
- Whether login ID 2, timestamps/nonces, world/session IDs, or external
  authentication material occur in the opaque remainder.
- Which parts must be validated versus treated as opaque.
- How the iRO MapServer entry maps to Athena.NET's existing CharServer auth-node handoff.
- How Athena's future MapServer skill-tree model should derive `0x0B32` entries,
  and which later packets are required before first map spawn, inventory/status
  synchronization, and movement.
- Which subset of the proven pre-`0x007D` state burst is mandatory for client
  world construction, and the name/layout of `0x0BF2/13`.
- The third byte and modern handling of client `0x007D/3`.
- The meaning/layout and required Athena state for official `0x0C20/28`, and
  whether omitting it affects later client initialization.
- The semantics of the trailing iRO bytes on otherwise known client packets.

## Verified MapServer movement and transitions

Capture `/Users/marco/Downloads/full-ragnarok-flow-with-walking.pcapng` proves:

- Initial handoff `0x0071` is `iz_int01.gat` on `128.241.92.42:4501`.
- Initial `0x02EB` position is `(22,37,direction 0)`.
- Client movement is `0x035F/6`: ID, three packed destination bytes, and one
  opaque trailing iRO byte. Generic upstream defines the first five bytes.
- Successful self-movement response is `0x0087/12`: ID, uint32 server tick, and
  six packed bytes containing source, destination, and subcell `8/8` in the
  normal completed-step samples.
- `0x0368/7` is an actor-info request: ID, uint32 actor ID, and one opaque trailing
  iRO byte. It is not movement.
- Same-MapServer change is `0x0091/22`: map[16], uint16 x, uint16 y. The capture
  changes to `iz_int01.gat (51,30)` on the existing `:4501` connection, then the
  client sends `0x007D/3` again without another `0x0C1F`.
- The capture-proven trigger sequence is client `0x035F` target `(26,30)`, server
  `0x0087 (25,22)->(26,30)`, then server `0x0091 iz_int01.gat (51,30)`. No other
  client Ragnarok packet intervenes. Matching rAthena mapdata defines an inclusive
  radius `(1,1)` around `(27,30)` to the same destination for `iz_int` and each
  `iz_int01..04` variant.
- Cross-MapServer change is `0x0092/28`: map[16], uint16 x/y, IPv4 bytes, and
  little-endian uint16 port. It sends the client to `int_land01.gat (85,107)` at
  `128.241.92.42:4506`.
- The client closes `:4501`, opens `:4506`, sends a second `0x0C1F/1001`, receives
  bootstrap with `0x02EB (85,107,0)`, sends `0x007D/3`, then moves successfully.
- The two `0x0C1F` packets have identical proven 14-byte headers. In their opaque
  area, `0x014..0x3DF` and byte `0x3E7` are equal; `0x00E..0x013`,
  `0x3E0..0x3E6`, and byte `0x3E8` differ. No semantics are assigned.
- Server `0x0AE2/7` is open-UI: uint8 type `7`, int32 data `0`. Matching rAthena
  identifies output UI type 7 as attendance. The capture does not prove that this
  is the only UI state responsible for the visually missing screen in Athena.

## Implemented world / warp state

- Athena models the real `iz_int` and `iz_int01..04` same-map doors as immutable
  map-scoped rectangular `WarpDefinition` records. No capture payload is replayed.
- Entering the door zone sends the proven `0x0087` movement response first, then
  updates in-memory map/position and serializes `0x0091/22` from the warp state.
- Movement checks a direct Bresenham grid-cell route, not only its requested end
  tile. The first route cell inside any map-matching warp area wins. `0x0087`
  terminates at that intersection, so old-map state never advances to a requested
  target beyond the warp. This is explicitly not collision/pathfinding parity.
- The same TCP connection remains active, accepts the next `0x007D/3`, and uses
  the destination position as the source of subsequent movement.
- Cross-server `0x0092` remains unimplemented pending real endpoint ownership,
  routing, persistence, and new single-use MapAuth ticket transfer.

## Verified NPC dialogue evidence

Capture `/Users/marco/Downloads/npc-interaction-npc's_v2.pcapng` additionally proves the tutorial presentation packets used by the current generated slice: `0x08E2/27` carries navigation type/flags, a 16-byte map, coordinates and monster ID; `hideWindow=1` still renders the ground arrows. NPC overhead speech is variable-length `0x008D` with length at offset 2, actor ID at 4 and NUL-terminated text at 8. The Wounded actor transition uses `0x0229/15`, with actor ID at 2 and the 32-bit option/effect state at 10. Athena serializes these from generated world/script state and does not replay capture payloads.

Capture `/Users/marco/Downloads/npc-interaction-heal-action.pcapng` (SHA-256
`fe0f9b260f3ee9e45c89e12efcc83c3817874b7b6f1db696ee58815bc67de88f`) proves
the following reassembled TCP dialogue on `192.168.178.55:63328` to
`128.241.92.42:4506`:

| Frame | Direction | Packet | Length | Proven meaning |
|---:|:---:|:---:|---:|---|
| 2971 | C->S | `0x0361` | 6 | Change direction before interaction; head direction at 2, padding at 3, body direction at 4, opaque iRO byte at 5. |
| 2973 | C->S | `0x0090` | 8 | Interact with actor 7963 (Captain Carocc); actor ID is uint32 at offset 2. |
| 2974, 2978 | S->C | `0x00B4` | dynamic | Reassembled dialogue messages for actor 7963; uint16 length at 2, actor ID at 4, NUL-terminated text at 8. |
| 2978 | S->C | `0x00B5` | 6 | Next boundary for actor 7963. |
| 3004 | C->S | `0x00B9` | 7 | Resume/next response for actor 7963. |
| 3005, 3013 | S->C | `0x00B7` | 78 | Reassembled menu for actor 7963: uint16 length at 2, actor ID at 4, text at 8; colon-separated choices end with a trailing colon and NUL. |
| 3122 | C->S | `0x00B8` | 8 | Actor 7963 selection value 2 at offset 6, followed by opaque byte `0x59`; the second branch follows. |
| 4780 | C->S | `0x00B8` | 8 | Actor 7965 selection value 1 at offset 6, followed by opaque byte `0xF6`; the first branch follows. |
| 4781, 4784 | S->C | `0x00B4`, `0x00B6` | dynamic, 6 | Sailor messages followed by dialogue close for actor 7965. |
| 4800 | C->S | `0x0146` | 7 | Client close acknowledgement for actor 7965. |

Client `0x0090`, `0x00B9`, `0x00B8`, and `0x0146` each contain one trailing byte
beyond the pinned rAthena layout. Its meaning remains unknown and Athena treats it
as opaque. The observed Captain sequence is four messages, a next boundary, a
client next response, then a menu. Multiple logical packets share TCP segments and
the first message spans frames, so implementations must use packet lengths after
TCP reassembly rather than TCP frame boundaries.

The frame 3005/3013 menu contains two option strings separated by `:`, plus a
final `:` immediately before the NUL terminator. Frame 3122's wire value 2 selects
the second displayed option and produces the second-branch response in frames
3123/3125. Together with frame 4780's value 1 and first-branch response, this
proves one-based selection indexing for the captured flow.

The captured Captain Carocc, Lumin, and Sailor names and exact dialogue text could
not be correlated to declarations in the pinned `legacy/rathena/npc` tree. Their
captured branches also include menus and gameplay/quest state. Athena therefore
does not create a production WorldEntity from this capture. The first runtime slice
implements the independently proven Message, Next suspension/resume, Close, and
one-based menu selection transport using a test WorldEntity. Quest traffic, combat,
and item acquisition in this capture remain future evidence without runtime support.

Captain Carocc's captured `0x09FF/105` actor record uses object type 6, actor ID
7963, class 873 at offset 23, and the same modern idle-unit layout as the proven
class-45 WARPNPC records. Athena uses that captured normal-NPC class for its
developer dialogue fixture, while allocating its own runtime actor ID.

The pre-interaction sequence repeats at frames 3544/3547, 4049/4054, and
4504/4506: client `0x0361/6`, then client `0x0090/8`. Pinned rAthena identifies
the classic fields at offsets 2 and 4 as head direction and body direction; the
capture is authoritative for the additional opaque sixth byte.

### Verified tutorial quest state

Frame 3186 contains the captured quest transition inside the Captain dialogue
burst: `0x0B0C/155` adds quest 21001 with uint32 quest ID at offset 2 and active
state `1` at offset 6; `0x02B4/6` then removes quest 21001 from the client log;
another `0x0B0C/155` adds quest 21008 active. The add packet's remaining fields
are zero for these two objective-free client-log entries. All three packets are
sent immediately in the dialogue stream, not in a later batch. Quest 21008 is not
completed in this capture. Frame 3651 independently contains an add for quest
7471 and is outside this slice.

Pinned rAthena identifies `0x0B0C` as the modern fixed-size quest-add packet and
`0x02B4` as quest removal. Its completion path persists completed state server-side
but sends `0x02B4`, so disappearance from the client log does not mean absence in
Athena's character state. Quest titles/descriptions are not present in these wire
packets; the stock client resolves them by quest ID.

## Verified tutorial portal actor evidence

- Frame 163 contains `0x09FF/93` for `#room_out`: object type 6, dynamic actor ID
  2304, class 45, position `(27,30)`, size `1/1`. The position equals the first
  warp's real center.
- After `0x0091`, frame 413 contains the equivalent `#room_in` actor at `(47,30)`
  and frame 428 contains `#ship_out` at `(56,15)`; both are class 45.
- OpenKore iRO names `0x09FF` `actor_exists`. rAthena defines class 45 as
  `JT_WARPNPC` and emits its modern idle-unit structure for visible NPCs.
- This is strong evidence that the portal visual is a server-spawned warp-NPC
  actor rendered by the client, rather than a proven standalone effect packet.
- Athena does not yet send it because dynamic actor-ID allocation, world actor
  lifecycle/visibility, complete field state, and actor-info response state are
  absent. Captured actor IDs are not stable state and must not be hardcoded.
- Captured `0x0368/7` targets a different ordinary NPC (`Lumin#new01_ship`, class
  639 at `(73,100)`) and provides no portal-actor correlation.
- Athena's `0x09FF` WARPNPC serializer uses the capture/upstream-matched layout:
  ID/length at 0/2, object type 6 at 4, Athena actor ID at 5, speed 300 at 13,
  class 45 at 23, packed position at 63, trigger radii at 66/67, NPC max/current
  HP sentinels `0xFFFFFFFF` at 73/77, and the variable actor name from offset 84.
  Remaining NPC appearance/status fields are zero as in all three captured warp
  actors and the upstream NPC idle-unit state.
- WARPNPC actors are sent after `0x007D`, matching initial and post-`0x0091`
  capture timing. Actor IDs are allocated by Athena and never copied from capture.

## Verified NPC cutin evidence

The official `npc-interaction-heal-action.pcapng` capture contains fixed 67-byte
server packet `0x01B3`. Bytes 2..65 are the NUL-padded ASCII image filename and
byte 66 is the position: `tutorial03.BMP` with position 4 is followed later by an
empty filename with position 255 to clear the image. Athena uses this exact layout
for generated rAthena `cutin`; it does not invent a packet.

## Explicitly disproven assumptions for this target
Do not reintroduce these without newer verified iRO evidence:
- Login success is `0x0AC4` with 160-byte world entries. **Disproven.**
- iRO `CHARACTER_INFO` is 155 bytes. **Disproven.**
- iRO paged character response is `0x099D`. **Disproven.**
- `0x09A0` count should be derived from current character count. **Disproven for the captured flow.**
- `0x020D` is required in the captured initial CharServer flow. **Not observed; do not add speculatively.**
- `0x0187` requires no response. **Disproven; official server echoed it.**
- iRO map handoff uses `0x0AC5`. **Disproven; capture used `0x0071`.**
- Generic kRO/rAthena `CZ_ENTER/CZ_ENTER2` is the current stock-iRO MapServer entry packet. **Disproven by observed `0x0C1F/1001`.**

## Capture handling
Official captures can contain credentials, account/session identifiers, bearer/JWT-like tokens, and other sensitive authentication material. Never commit unsanitized PCAPs or raw token dumps to the repository.
