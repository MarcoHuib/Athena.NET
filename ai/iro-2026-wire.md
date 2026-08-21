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
- The first later client record is frame 423, `0x0360/10`. All eight bytes after
  its ID remain opaque for this iRO generation. Generic upstream's `0x0360/6`
  interpretation does not override the captured ten-byte boundary.
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
- The semantics of the eight payload bytes in captured client `0x0360/10`.

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
