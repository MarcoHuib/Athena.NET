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

Captain Carocc's pinned source is `legacy/rathena/npc/re/jobs/novice/academy.txt:133`
(`int_land,78,103,5 script Captain Carocc#intro_npc03`). An earlier version of this
document claimed frames 3172-3186 proved Captain's `specialeffect2`/`heal`/
`skilleffect`/`sc_start`/`getexp` commands produced zero client-visible bytes. That
claim was based on a misattributed frame: frame 3186's dialogue text ("...I'll trust
you.") and its preceding lines do not appear anywhere in the pinned
`academy.txt:133` script, so frame 3186 is not proven to be Captain's real pinned
`case 0:` dialogue in the first place, and the "silence" observed there proves
nothing about Captain's actual heal/status/getexp commands. **The zero-byte claim is
retracted.**

Frame 3496 of `npc-interaction-heal-action.pcapng` (reassembled TCP stream, byte-
identical between the compact and fuller "all-gravity-traffic" exports; the target
`0x00B4` message spans frames 3485→3496 - the 8-byte header lands in 3485, the
17-byte remainder opens 3496) is the server burst immediately after
"[Captain Carocc] / All done now? / Hunt 2 Porings and get 2 pieces of Wood for the
sailor. / Good luck." (this exact text is likewise not found verbatim in pinned
`academy.txt`, so this specific in-game NPC's exact script correlation to the pinned
file remains unproven — matching the pre-existing disclosure a few paragraphs above
that Captain/Lumin/Sailor dialogue text could not be correlated to pinned
declarations). What IS conclusively proven, independent of exact script-line
attribution, is the complete wire behavior of a Blessing(val1=10)/Increase
AGI(val1=10)/Heal(9999) sequence applied to a player by an NPC actor, which is
exactly the runtime capability Captain's pinned `heal`/`skilleffect`/`sc_start`
commands require. The reassembled 461-byte burst (offset 0 = start of frame 3496,
continuing the split `0x00B4` from frame 3485) segments as:

| Offset | Packet | Fields |
|---:|---|---|
| 0x11 | `0x00B4` len=22 | "All done now?" |
| 0x27 | `0x00B4` len=64 | "Hunt 2 Porings and get 2 pieces of Wood for the sailor." |
| 0x67 | `0x00B4` len=19 | "Good luck." |
| 0x7A,0x82,0x8A,0xA0,0xA8 | `0x00B0` | var 45/50/53/53/0 (DEF2/FLEE2/ASPD/ASPD/SPEED - derived-stat cascade, not modeled) |
| 0x92 | `0x0141` | statusType=14(AGI) base=1 plus=**12** |
| 0xB0 | `0x0983` len=29 | type=**12(EFST_INC_AGI)** actorId=player state=1 total=remain=**240000** val1=**10** val2=0 val3=0 |
| 0xCD | `0x09CB` len=17 | SKID=**29(AL_INCAGI)** level=**10** target=player src=Captain's actor result=1 |
| 0xDE..0xF6 | `0x00B0`x3, `0x0141` | var 41/45/42(ATK2/DEF2/MATK1 cascade); statusType=13(STR) base=1 plus=**10** |
| 0x104..0x11C | `0x00B0`x3, `0x0141` | var 44/47/43(DEF1/MDEF2/MATK2 cascade); statusType=16(INT) base=1 plus=**10** |
| 0x12A..0x16A | `0x00B0`x7, `0x0141` | var 8/7/41/44/47/49/53/42 (MAXSP/SP/ATK2/DEF1/MDEF2/FLEE1/ASPD/MATK1 cascade); statusType=17(DEX) base=1 plus=**10** |
| 0x178 | `0x00B0` | var=49 (FLEE1 cascade) |
| 0x180 | `0x0983` len=29 | type=**10(EFST_BLESSING)** actorId=player state=1 total=remain=**240000** val1=**10** val2=0 val3=0 |
| 0x19D | `0x09CB` len=17 | SKID=**34(AL_BLESSING)** level=**10** target=player src=Captain's actor result=1 |
| 0x1AE | `0x00B0` | var=**5(HP)** val=**40** |
| 0x1B6 | `0x09CB` len=17 | SKID=**28(AL_HEAL)** level=**9999** target=player src=Captain's actor result=1 |
| 0x1C7 | `0x00B6` len=6 | dialogue close |

Every non-cascade field matches pinned source exactly: `0x0983` = `ZC_MSG_STATE_CHANGE3`
(`clif.cpp:6461,6486-6509`, 29 bytes, `type.W actorId.L state.B totalMsec.L remainMsec.L
val1.L val2.L val3.L`); `EFST_BLESSING=10`/`EFST_INC_AGI=12` (`status.hpp:1456-1469`,
`EFST_BLANK=-1` origin); `0x09CB` = `ZC_USE_SKILL` (`packets_struct.hpp:4674-4683`,
17 bytes, PACKETVER_RE>=20130724 layout, built by `clif_skill_nodamage`); `0x0141` =
`ZC_COUPLESTATUS` (`clif.cpp:3608-3618`, 14 bytes, `statusType.L base.L plus.L`, sent
by `clif_updatestatus(SP_STR/SP_AGI/SP_INT/SP_DEX)` — `map.hpp:500-501` gives
`SP_STR=13, SP_AGI=14, SP_INT=16, SP_DEX=17`); the captured `plus` values (STR/INT/
DEX=+10, AGI=+12) exactly match `db/re/status.yml`'s `Blessing`/`Increaseagi`
`CalcFlags` (`Str/Int/Dex` and `Agi/Speed/Aspd` respectively) combined with
`status_change_start_post_delay`'s val-settings switch (`status.cpp:10844-10854`,
`val2 = 2 + val1` for `SC_INCREASEAGI`; `status.cpp:11566-11571`, `val2 = val1` for
`SC_BLESSING` on a `BL_PC` target) — independently confirming the server-side fix
already applied to `CharacterStatusEffectState`.

One field is a genuine, documented discrepancy against the pinned snapshot: pinned
`status_change_start_post_delay` (`status.cpp:13194`) sends `val1` only when the
status DB entry sets the `SendVal1` flag (`scdb->flag[SCF_SENDVAL1] ? val1 : 1`), and
neither `Blessing` nor `Increaseagi` sets it in `db/re/status.yml:445-481` — meaning
pinned source implies `val1=1` (hardcoded), yet the capture proves `val1=10` (the
real skill level) for both. This is treated as the capture's operator-side status DB
differing from this pinned snapshot for the `SendVal1` flag on these two entries;
per this project's evidence priority the capture's `val1` is used as-is.

The `0x09CB` packets' `src` actor (Captain, not the player) was likewise not
conclusively traced to a specific pinned call site: `skilleffect(id,lv)`'s own code
path (`script.cpp:15519-15556`, `script_skill_effect`) targets `bl=sd` (the player)
for both src and target when called with the 2-argument form Captain's script uses,
which would imply `src=player`, not `src=Captain`. The capture is used as-is per the
same evidence priority; the packet's existence, layout, and its skill-ID/level/
target-actor fields are unambiguous regardless of this open src-attribution question.

The heal visual (`0x09CB SKID=28 level=9999`) is likewise not attributable to
`specialeffect2 EF_HEAL2`, whose pinned path (`BUILDIN_FUNC(specialeffect2)` ->
`clif_specialeffect` -> `0x01F3`/`ZC_NOTIFY_EFFECT2`) produced **zero** `0x01F3` bytes
anywhere in the 461-byte burst — that part of the original zero-byte claim holds.
`level=9999` matches `heal 9999,0`'s exact HP argument (not the resulting clamped
HP=40, which is separately synced via the ordinary `0x00B0 var=5` parameter packet
immediately before it), so Athena attributes this visual to `heal`, not
`specialeffect2`.

### Natural status expiration (Blessing / Increase AGI)

Captain's dialogue never runs long enough to observe a real 240-second expiry in
this capture, so nothing below is capture-proven — it is derived entirely from
pinned `status_change_end` (`status.cpp:13433-14123`) and applied to the already
capture-proven `0x0196`/`0x0141` serializers, per the same evidence rules used for
the rest of this document (capture wins when it exists; pinned source is the
fallback when it doesn't).

Neither `SC_BLESSING` nor `SC_INCREASEAGI` has a case in `status_change_end`'s
per-type switch (`status.cpp:13433-14045`) — both fall through to the function's
generic tail (`status.cpp:14085-14109`), which unconditionally does, in order:
`clif_status_change(bl, status_icon, 0, 0, 0, 0, 0)` (the "off" form of the same
builder used for activation — `state=0`, `flag=0` for this PACKETVER, i.e. `0x0196`
`ZC_MSG_STATE_CHANGE`), then, if `calc_flag.any()`, `status_calc_bl_(bl, calc_flag)`
(recalculates and emits `clif_updatestatus`/`0x0141` for only the stats whose
recalculated value actually changed). This exactly matches "send status-end, then
resync only the changed stats" — no new packet shape was needed.

Athena implements this with one expiration scheduler per `MapClientSession`
(`RunStatusExpirationLoopAsync`/`ProcessDueStatusExpirationsAsync` in
`MapClientSession.cs`) rather than a timer per active status: it sleeps until
`CharacterStatusEffectState.NextExpiration` (via the session's `TimeProvider`, so
tests drive it with a fake clock instead of real 240-second waits), waking early
whenever `StartStatusAsync` moves the next deadline. On a due status it snapshots
effective stats immediately before removal (`RecalculateBeforeExpiration`), removes
the status (`ExpireDue`), recalculates after, sends one `0x0196` per expired status
(actor=player, `EFST_BLESSING=10`/`EFST_INC_AGI=12`), then sends only the `0x0141`
fields whose before/after effective value actually differs (STR/INT/DEX for
Blessing, AGI for Increase AGI) — mirroring `status_calc_bl_`'s "only what changed"
behavior rather than resending every stat unconditionally. Re-applying an
already-active status (`sc_start` semantics, matching pinned `status_change_start`'s
overwrite-not-stack behavior already documented above) replaces `ExpiresAt`
outright, so the old deadline cannot fire for the refreshed status — the scheduler
always re-reads the current stored value, never a stale captured one.

Remaining gap, explicitly not addressed here: the client's own `0x0983`
`totalMsec`/`remainMsec` fields almost certainly drive a client-side countdown that
clears its icon locally regardless of server behavior (standard RO client
convention), but this was not, and cannot be, capture-verified from Captain's short
dialogue window. Server/client convergence after natural expiration therefore rests
on pinned-source semantics for the packet sequence, not on independent wire proof —
flagged the same way the `SendVal1`/`src`-actor discrepancies above are flagged.

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

### Focused Lumin interaction evidence

The sanitized `lumin-packet-export.json` export from `Full-izlude.pcapng`
(SHA-256 `ee3bcbf2429d944c512d2ced10ce9c8db099dec79ad499f23b977462a0af2ec9`)
proves the existing dialogue lifecycle also applies to Lumin. Frame 6029 is
client `0x0090` interaction; frames 6030/6032 split one `0x00B4` across TCP
payloads; frames 6289/6292 split `0x00B7`; frame 6586 selects one-based menu
index 2 with an opaque trailing byte; frames 6587/6589 include the active
character's actual name in dialogue; frame 6610 sends structured `0x0229`
effect state 4 for Lumin's actor before subsequent dialogue in the same TCP
payload; and frame 6720 is client `0x0146` close. Frame 6275 independently
uses `0x01B3` for `nov_lumin01.bmp`. The capture's actor ID, character name,
dialogue wording, and opaque trailing bytes are evidence only and are never
replayed or copied into generated content.

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

## Verified monster combat wire evidence (G_PORING)

Capture `kill-poring-heal-jobup.pcapng` (exported as a sanitized text form, never committed)
proves the client-facing wire for killing a real monster on the map connection
`192.168.178.55:63501 <-> 128.241.92.42:4506`. Target actor `0x00001E9D` (class 2401,
`G_PORING`, name "Poring"). Reassembly used real packet-length fields, never raw TCP frame
boundaries; two TCP segments in this capture each coalesce two logical Ragnarok packets.

| Frame | Direction | Packet | Length | Proven meaning |
|---:|:---:|:---:|---:|---|
| 566 | S->C | `0x09FF` | 90 | `ZC_NOTIFY_STANDENTRY11` (`clif_set_unit_idle`, `clif.cpp:1041`), struct `packet_idle_unit` (`packets_struct.hpp:832`), gated `PACKETVER>=20141022`. objecttype=5 (`NPC_MOB_TYPE`) at offset 4, actorId at 5, speed=400 at 13, class=2401 at 23, packed position (75,51,dir 0) at 63, HP sentinel 0xFFFFFFFF/0xFFFFFFFF at 73/77 (full HP), name "Poring" at 84. Identical field-offset shape to the existing WARPNPC `0x09FF` (`IroWorldActorPackets.BuildWorldActor`, `FixedLength=84`); differs only in objecttype (5 vs 6) and in carrying a real, state-dependent HP pair rather than an unconditional sentinel. |
| 566 | S->C | `0x09FD` | 96 | `ZC_NOTIFY_MOVEENTRY11` (`clif_set_unit_walking`, `clif.cpp:1369`), struct `packet_unit_walking` (`packets_struct.hpp:758`). Same header shape as `0x09FF` plus a `moveStartTime` tick at offset 37 and a 6-byte packed src/dst/subcell movement field at offset 67 (decoded (75,51)->(75,57) subcell 8/8) in place of the idle variant's 3-byte static position. Not implemented in Athena's serializer: this slice only sends the standing form, since monster wander/AI movement is not modeled. |
| 600 | C->S | `0x0368` | 7 | Actor-info request for actor `0x1E9D`; reuses the already-proven generic layout. |
| 601 | S->C | `0x0ADF` | 58 | Name response "Poring" for actor `0x1E9D`; reuses the already-proven generic layout/serializer unchanged. |
| 614 | C->S | `0x0437` | 8 | `clif_parse_ActionRequest` (`clif.cpp:11818`), pinned generic length 7 (`clif_packetdb.hpp:1149/1222`), iRO adds one opaque trailing byte matching the established pattern. Fields: id(2), targetActorId(4, offset 2), actionType(1, offset 6, captured value `0x07` = `e_damage_type::DMG_REPEAT`, `clif.hpp:699`, "continuous attack"), opaque trailing byte(1, offset 7). |
| 620, 659 | S->C | `0x08C8` | 34 | `ZC_NOTIFY_ACT3` (`clif.cpp:5220`): `srcId.L dstId.L tick.L srcSpeed.L dstSpeed.L damage.L isSpDamage.B div.W type.B damage2.L`. Both captured hits deal exactly 37 damage (2x37=74 >= Poring's 55 HP), `div=1`, `type=0` (`DMG_NORMAL`), `damage2=0`. Exact structural match, zero opaque bytes. |
| 674 | S->C | `0x0088` | 10 | `ZC_STOPMOVE` (`clif.cpp:2204`): `id.L x.W y.W`. Poring stops at (75,57), matching `0x09FD`'s walk destination. |
| 674 | S->C | `0x02E1` | 33 | `ZC_NOTIFY_ACT2` (`clif.cpp:5219`), same field family as `0x08C8` minus `isSpDamage`. srcId=Poring, dstId=player, damage=0 - the monster's own zero-damage "attack back" visual. Coalesced in the same TCP segment as the preceding `0x0088`. |
| 694 | S->C | `0x0080` | 7 | `ZC_NOTIFY_VANISH` (`clif.cpp:945`): `id.L type.B`. Captured `type=1`, explicitly documented in pinned source as "died" (distinct from `0`=out of sight, `2`=logged out, `3`=teleport, `4`=trickdead). |
| 699 | S->C | `0x0B41` | 70 | `ZC_ITEM_PICKUP_ACK` (`packets_struct.hpp:540`, pinned RE `PACKETVER_RE_NUM>=20200723` branch). Exact match: `Index=2, count=1, nameid=6008 (Wood), IsIdentified=1, type=3 (Etc), result=0`. `Index` is the pinned `client_index()` transform (`clif.cpp:122-124`: server-side inventory array position + 2) - captured `Index=2` means Wood landed in server-side array position 0, i.e. the character's first inventory row. Neither Athena's `CharInventory` schema nor real rAthena's own `inventory` SQL table persists a slot/position column; the server-side position is derived from stable row-insertion order among a character's own rows at grant time (`MapServerSession.HandleInventoryAddRequestAsync`), returned through the internal `0x2b31`/`0x2b32` protocol as a new `slotIndex` field, and the wire `+2` transform is applied only at the point of serializing `0x0B41`. |

Respawn/reappearance for this specific actor is **not captured** in this export - the player
moved away before any second `0x09FF`/`0x09FD` for `0x1E9D` would have appeared. Athena's
respawn-visibility behavior (reusing the same `0x09FF` standing-entry emission once
`MonsterRegistry.ProcessDueRespawns` reports the instance alive again) is inferred from pinned
source (`clif_spawn_unit`/`clif_set_unit_idle` share one code path for first spawn and respawn),
not independently capture-verified.

A length-field transcription pitfall was found and corrected during this analysis: an early hex
transcription of frame 566 dropped one 16-byte all-zero row, producing a false apparent
16-byte "overlap" between `0x09FF` and `0x09FD`. The corrected byte-for-byte transcription
(verified against both the hex and ASCII-sidebar columns of the source export) shows a fully
self-consistent 90-byte `0x09FF` immediately followed by a 96-byte `0x09FD`, summing exactly to
the captured 186-byte TCP payload. There is no length-field anomaly in the real capture.

## Verified Full-izlude progression evidence

Sanitized companion trace `athena_full_izlude_packet_trace.txt` documents
`Full-izlude.pcapng` (SHA-256
`ee3bcbf2429d944c512d2ced10ce9c8db099dec79ad499f23b977462a0af2ec9`).
Three tutorial G_PORING deaths use `0x0080/7` reason 1 and subsequent Wood/Lumber
pickups use `0x0B41/70` with item 6008, amount 1, result 0. No `0x0ACB` or
`0x0ACC` progression packet is adjacent to any death. This agrees with generated
G_PORING BaseExp/JobExp both being 0; it is not a mob-ID special case.

The Captain Carocc burst proves `0x0ACC/18` (`ZC_NOTIFY_EXP2`) as
`packetType.W actorId.L amount.Q parameterId.W gainFlag.W`: amount 150 with
parameter 1 for Base EXP and amount 150 with parameter 2 for Job EXP; both flags
are 0. It proves `0x019B/10` as `packetType.W actorId.L effectType.L`, with type 1
for Job level-up. Reference source identifies type 0 as Base level-up; Athena's
serializer accepts only these two known types.

Captured order: current Base EXP (`0x0ACB param 1 = 150`), Base gain (`0x0ACC`),
Skill Points (`0x00B0 param 12 = 1`), Job Level (`0x00B0 param 55 = 2`), next Job
EXP (`0x0ACB param 23 = 18`), next Base EXP (`0x0ACB param 22 = 548`), Job
level-up visual (`0x019B type 1`), current Job EXP (`0x0ACB param 2 = 9`), Job
gain (`0x0ACC`), then quest 21008 activation (`0x0B0C`). No Base-level update or
type-0 visual occurs in this burst.

The capture's 150/150 gain differs from pinned
`npc/re/jobs/novice/academy.txt` and generated Captain content (`getexp 600,600`).
Capture remains wire authority and pinned rAthena remains Athena's content source;
the generated reward intentionally remains 600/600.

## Verified equip/unequip request framing (0x0998, 0x00AB)

Live stock-iRO client session, map flow `192.168.178.55 -> 128.241.92.42:4506`. **These two
C->S request lengths intentionally diverge from pinned rAthena** and are the current
authoritative shape per the evidence-priority rule (capture overrides pinned source for
current-client wire behavior).

| Frame | Direction | Packet | Pinned rAthena length | Actual stock-iRO length | Bytes |
|---:|:---:|:---:|---:|---:|---|
| 388 | C->S | `0x0998` (`CZ_REQ_WEAR_EQUIP_V5`) | 8 (`packets.hpp:1502-1509`) | **9** | `98 09 02 00 02 00 00 00 5B` |
| 449 | C->S | `0x0998` | 8 | **9** | `98 09 03 00 10 00 00 00 88` |
| 370 | C->S | `0x00AB` (`CZ_REQ_TAKEOFF_EQUIP`) | 4 (`clif_packetdb.hpp:59`) | **5** | `AB 00 02 00 4F` |
| 395 | C->S | `0x00AB` | 4 | **5** | `AB 00 03 00 85` |

Known fields (both packets): `packetType.W`, `index.W` (client inventory index,
`client_index()` = server slot + 2). `0x0998` additionally carries `position.L` (the requested
equip-position bitmask) per pinned source, matching the captured `02 00 00 00`=`EQP_HAND_R` and
`10 00 00 00`=`EQP_ARMOR` values exactly. Each packet's final byte (offset 8 for `0x0998`,
offset 4 for `0x00AB`) is **not present in pinned rAthena's struct at all** and its semantics
are unverified - it is consumed (required for correct framing of the next packet) but left
explicitly opaque, never assigned an invented meaning (checksum/token/anti-cheat, etc.).

**Impact if unmodeled**: parsing these packets at the pinned 8/4-byte lengths leaves exactly
one real payload byte unconsumed in the receive stream. That byte becomes the leading byte of
the next packet's 2-byte opcode header, producing a corrupted opcode - live-observed as
`0xAB98` (leftover `0x98` from an under-read `0x0998` + leading `0xAB` of the following
`0x00AB`) and `0x6025` (a further cascading desync). Athena now reads
`IroCzReqWearEquipLength=9` / `IroCzReqTakeoffEquipLength=5`, eliminating the residue.

## Capture handling
Official captures can contain credentials, account/session identifiers, bearer/JWT-like tokens, and other sensitive authentication material. Never commit unsanitized PCAPs or raw token dumps to the repository.
