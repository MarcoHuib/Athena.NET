# Athena.NET agent instructions

Athena.NET is an **iRO-only** private server implementation in C#. The supported client target is the current unmodified official International Ragnarok Online (iRO) client. Generic kRO/rAthena client compatibility is not a project goal.

For every request:
1. Read `ai/README.md` first.
2. Read `ai/iro-2026-wire.md` for verified client-facing protocol facts.
3. Read the relevant `ai/*.md` server/workstream file before changing code.
4. Inspect current code, tests, config, `git status`, and `git diff`; preserve compatible user changes.

Evidence rule:
- Verified stock-iRO captures/runtime/tests are authoritative for client-facing packet behavior.
- rAthena/OpenKore are references for architecture, mechanics, data structures, database/schema concepts, and implementation ideas, but they do not override verified iRO wire evidence.
- Do not add kRO/rAthena protocol behavior solely for parity.

Client rule:
- Do not patch or modify the official client to make Athena.NET work. Keep client compatibility server-side.

Security rule:
- Never log or commit passwords, PINs, bearer/JWT/session/auth tokens, or unsanitized packet captures.
