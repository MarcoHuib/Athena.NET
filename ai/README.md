# Athena.NET AI development guide

## Mission
Athena.NET is an iRO-focused private server implementation in modern C#.

The only client-compatibility target is the current unmodified official International Ragnarok Online (iRO) client. kRO compatibility and generic rAthena protocol parity are not project goals.

rAthena remains an important engineering reference for architecture, server responsibilities, database concepts, gameplay rules, data formats, scripts, and mature implementation ideas. It is not the wire-protocol authority for iRO.

## Required reading order
For every task:
1. Read this file.
2. Read `ai/iro-2026-wire.md` for verified stock-iRO protocol facts.
3. Read the relevant server file: `ai/login-server.md`, `ai/char-server.md`, `ai/map-server.md`, or `ai/data-and-tools.md`.
4. Read the matching `*-parity.md` file only as an implementation/reference map. The historical `parity` filename is retained, but those files are no longer requests for rAthena/kRO parity.
5. Inspect the current code, tests, config, `git status`, and `git diff` before changing anything.

## Evidence priority
For stock iRO compatibility, use this order:
1. Successful official iRO Wireshark captures from the exact client generation being targeted.
2. Repeatable runtime behavior of the unmodified official iRO client against Athena.NET.
3. Athena.NET regression tests derived from those captures.
4. Current iRO-specific community evidence when it matches the observed client.
5. rAthena/OpenKore as structural/reference material.
6. Hypotheses, only when explicitly marked unproven.

If rAthena, OpenKore, kRO packet definitions, or old packet tables conflict with a verified stock-iRO capture, the verified iRO capture wins for Athena.NET.

## Development rules
- Never change the client to make the server work. No Ragexe patching, custom executable, custom `clientinfo.xml`, or data-folder protocol workaround is part of the target architecture.
- Preserve the official launcher/EAC/EOS path. Network redirection used in development is external to the client and must not be confused with server protocol behavior.
- Keep iRO protocol facts explicit and tested by exact packet ID, length, field offset, byte order, and state transition where known.
- Do not implement speculative packets because rAthena sends them. First establish iRO evidence or clearly mark the work as exploratory diagnostics.
- Do not keep generic kRO/rAthena protocol branches merely for parity. If they no longer serve iRO or internal server-to-server behavior, they may be removed after tests show they are unnecessary.
- Internal architecture may strongly follow rAthena where it is useful, but public client-facing behavior must be iRO-first.
- Preserve security checks: authenticated account/character ownership, occupied-slot validation, bounds/length checks, and safe logging.
- Never log passwords, PINs, JWTs, session tokens, auth tokens, or full sensitive packets.
- Keep capture-derived fixtures sanitized before committing them.
- Do not commit secrets or generated local config. Keep using templates/imports/gitignore.
- Aspire is the preferred local-development orchestrator. Docker/container deployment may remain supported for runtime/production use.

## Project index
- `ai/iro-2026-wire.md` - authoritative verified stock-iRO wire facts and disproven assumptions.
- `ai/login-server.md` - current iRO LoginServer state and next work.
- `ai/loginserver-parity.md` - LoginServer reference map from iRO requirements to useful rAthena concepts.
- `ai/char-server.md` - current iRO CharServer state and next work.
- `ai/charserver-parity.md` - CharServer reference map from iRO requirements to useful rAthena concepts.
- `ai/map-server.md` - current iRO MapServer state and next work.
- `ai/mapserver-parity.md` - MapServer reference map; generic CZ_ENTER behavior is reference-only unless iRO evidence matches it.
- `ai/data-and-tools.md` - rAthena-derived data/tooling strategy for an iRO server.

## Definition of project success
Athena.NET is successful when an unmodified supported stock iRO client can complete the full flow:

`launcher/login -> LoginServer -> CharServer -> create/select -> MapServer auth -> spawn -> gameplay`

and normal iRO gameplay systems work without requiring kRO compatibility as a separate target.
