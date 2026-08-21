# Stock iRO 2026 verified wire protocol

## Purpose
This file is the protocol authority for the current stock iRO client targeted by Athena.NET.

Only add a statement to **Verified** when it is supported by an official successful capture, repeatable runtime evidence, or an exact regression fixture derived from that evidence. Keep unknowns under **Open**.

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

## Open MapServer questions
- Exact field layout of `0x0C1F`.
- Which fields correspond to account ID, character ID, login IDs, timestamps/nonces, world/session IDs, and external authentication material.
- Which parts must be validated versus treated as opaque.
- How the iRO MapServer entry maps to Athena.NET's existing CharServer auth-node handoff.
- Exact first server response(s) after successful `0x0C1F` authentication.
- Packets required before first map spawn, inventory/status synchronization, and movement.

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
