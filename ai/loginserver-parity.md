# LoginServer iRO reference map

> Historical filename: this is no longer a request for rAthena/kRO parity.

## Purpose
Map the iRO LoginServer requirements onto useful rAthena concepts so implementation can reuse proven server design without inheriting the wrong regional wire protocol.

## iRO client-facing contract
| Area | Athena.NET iRO requirement | rAthena use |
|---|---|---|
| Login request | `0x0064`, 55 bytes, version 18 observed | Reference validation/auth concepts only |
| Fixed strings | Stop at first NUL | Reference helper patterns only |
| Login success | `0x0A4D` | Do not substitute generic packet choice |
| Success header | 64 bytes | Compare field meaning where useful |
| World entry | 32 bytes | Reuse server-list concepts, not kRO struct size |
| Chaos handoff | Advertise configured iRO endpoint | Reuse server registration/config concepts |
| Auth nodes | Must preserve account/session ownership | rAthena architecture is useful |
| Failure/bans | Must be safe and iRO-compatible | rAthena rules/messages are useful references |

## Engineering guidance
- Start from the verified iRO packet fixture, then map each field to Athena.NET state.
- Use rAthena to understand *why* an auth/server-list field exists, not to decide its iRO byte layout.
- Keep packet parsing length-bounded and fixed strings NUL-terminated.
- Keep secrets out of diagnostics.
- Prefer exact serializer/parser tests over line-by-line parity comparisons with upstream.

## Anti-regression checks
- `0x0064` length remains 55.
- Username/password parsing stops at first NUL.
- Successful response remains `0x0A4D`.
- Server/world entries remain 32 bytes for this client target.
- No generic `0x0AC4` branch becomes the iRO default.

## Useful upstream concepts
- login authentication/account state
- IP bans and login logging
- auth-node expiration/cleanup
- CharServer registration and duplicate handling
- configuration/import patterns
- SQL schema concepts

Do not treat upstream packet IDs or `PACKETVER` regional branches as authoritative when they conflict with `ai/iro-2026-wire.md`.
