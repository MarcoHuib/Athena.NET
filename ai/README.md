# Athena.NET AI development guide

## Mission
Athena.NET is an iRO-focused private server implementation in modern C#.

The only client-compatibility target is the current unmodified official International Ragnarok Online (iRO) client. kRO compatibility and generic rAthena protocol parity are not project goals.

The repository contains two local legacy reference projects: `legacy/rathena/` and `legacy/openkore/`. They are reference material only, not compatibility targets. `legacy/rathena/` is the main engineering reference for architecture, server responsibilities, database concepts, gameplay rules, data formats, scripts, tools, and mature implementation ideas. `legacy/openkore/` is a secondary reference for packet naming, iRO/community protocol clues, and interpreting observed client behavior. Neither is the wire-protocol authority for iRO.

## Required reading order
For every task:
1. Read this file.
2. Read `ai/iro-2026-wire.md` for verified stock-iRO protocol facts.
3. Read the relevant server file: `ai/login-server.md`, `ai/char-server.md`, `ai/map-server.md`, or `ai/data-and-tools.md`.
4. Read the matching `*-parity.md` file only as an implementation/reference map. The historical `parity` filename is retained, but those files are no longer requests for rAthena/kRO parity.
5. Inspect the current code, tests, config, `git status`, and `git diff` before changing anything.
6. When reference implementation details are needed, inspect `legacy/rathena/` and/or `legacy/openkore/` locally; do not assume a separate `upstream/` checkout.

## Evidence priority
For stock iRO compatibility, use this order:
1. Successful official iRO Wireshark captures from the exact client generation being targeted.
2. Repeatable runtime behavior of the unmodified official iRO client against Athena.NET.
3. Athena.NET regression tests derived from those captures.
4. Current iRO-specific community evidence when it matches the observed client.
5. `legacy/rathena/` and `legacy/openkore/` as structural/reference material.
6. Hypotheses, only when explicitly marked unproven.

If `legacy/rathena/`, `legacy/openkore/`, kRO packet definitions, or old packet tables conflict with a verified stock-iRO capture, the verified iRO capture wins for Athena.NET.

## Legacy reference repositories
- `legacy/rathena/` — read-only by default. Use for server architecture, mechanics, persistence/schema, data formats, scripts, maps, tools, and implementation patterns.
- `legacy/openkore/` — read-only by default. Use for packet naming, iRO/community protocol hints, regional differences, and cross-checking captured traffic.
- Do not make Athena.NET depend on building or running either legacy project.
- Do not copy a packet ID, struct size, `PACKETVER` branch, or field layout into the iRO path merely because it exists in either legacy tree.
- Do not edit files under `legacy/` unless the user explicitly requests work on those reference projects.

## Development rules
- Never change the client to make the server work. No Ragexe patching, custom executable, custom `clientinfo.xml`, or data-folder protocol workaround is part of the target architecture.
- Preserve the official launcher/EAC/EOS path. Network redirection used in development is external to the client and must not be confused with server protocol behavior.
- Keep iRO protocol facts explicit and tested by exact packet ID, length, field offset, byte order, and state transition where known.
- Do not implement speculative packets because `legacy/rathena/` sends them. First establish iRO evidence or clearly mark the work as exploratory diagnostics.
- Do not keep generic kRO/rAthena protocol branches merely for parity. If they no longer serve iRO or internal server-to-server behavior, they may be removed after tests show they are unnecessary.
- Internal architecture may strongly follow `legacy/rathena/` where it is useful, but public client-facing behavior must be iRO-first.
- Preserve security checks: authenticated account/character ownership, occupied-slot validation, bounds/length checks, and safe logging.
- Never log passwords, PINs, JWTs, session tokens, auth tokens, or full sensitive packets.
- Keep capture-derived fixtures sanitized before committing them.
- Do not commit secrets or generated local config. Keep using templates/imports/gitignore.
- Aspire is the preferred local-development orchestrator. Docker/container deployment may remain supported for runtime/production use.

## Project index
- `ai/iro-2026-wire.md` - authoritative verified stock-iRO wire facts and disproven assumptions.
- `ai/login-server.md` - current iRO LoginServer state and next work.
- `ai/loginserver-parity.md` - LoginServer reference map from iRO requirements to useful `legacy/rathena/` concepts.
- `ai/char-server.md` - current iRO CharServer state and next work.
- `ai/charserver-parity.md` - CharServer reference map from iRO requirements to useful `legacy/rathena/` concepts.
- `ai/map-server.md` - current iRO MapServer state and next work.
- `ai/mapserver-parity.md` - MapServer reference map; generic CZ_ENTER behavior is reference-only unless iRO evidence matches it.
- `ai/data-and-tools.md` - `legacy/rathena/`-derived data/tooling strategy for an iRO server.
- `ai/world-data.md` - generated warp-data, persistence, and static world-actor architecture.

## Definition of project success
Athena.NET is successful when an unmodified supported stock iRO client can complete the full flow:

`launcher/login -> LoginServer -> CharServer -> create/select -> MapServer auth -> spawn -> gameplay`

and normal iRO gameplay systems work without requiring kRO compatibility as a separate target.
