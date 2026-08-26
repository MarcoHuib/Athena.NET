# Athena.NET agent instructions

Athena.NET is an **iRO-only** private server implementation in C#. The supported client target is the current unmodified official International Ragnarok Online (iRO) client. Generic kRO/rAthena client compatibility is not a project goal.

For every request:
1. Read `ai/README.md` first.
2. Read `ai/iro-2026-wire.md` for verified client-facing protocol facts.
3. Read the relevant `ai/*.md` server/workstream file before changing code.
4. Inspect current code, tests, config, `git status`, and `git diff`; preserve compatible user changes.
5. When reference code is useful, inspect the local reference projects under `legacy/` instead of assuming an external or `upstream/` path.

Evidence rule:
- Verified stock-iRO captures/runtime/tests are authoritative for client-facing packet behavior.
- `legacy/rathena/` and `legacy/openkore/` are local **reference projects**. They are not Athena.NET compatibility targets and do not override verified iRO wire evidence.
- Use `legacy/rathena/` primarily for server architecture, mechanics, data structures, database/schema concepts, scripts, tools, and implementation ideas.
- Use `legacy/openkore/` primarily for packet naming, iRO/community protocol clues, field interpretations, and comparison with observed client behavior.
- Treat both legacy trees as read-only reference material unless the user explicitly asks to modify them.
- Do not add kRO/rAthena protocol behavior solely for parity.

Client rule:
- Do not patch or modify the official client to make Athena.NET work. Keep client compatibility server-side.

Authoritative gameplay rule:

- Treat all client-supplied gameplay data as untrusted intent, never as authoritative state.
- The client may request an action; Athena.NET must resolve and validate the actual state and outcome server-side.
- Never create, modify, award, equip, consume, move, trade, drop, or persist gameplay state solely because the client claims that state.
- Resolve referenced entities, inventory slots, items, amounts, equipment positions, actors, skills, quests, currencies, and other gameplay state from Athena.NET's authoritative server-side state.
- When implementing a client action using `legacy/rathena/` as reference, trace and preserve the relevant server-side validation/failure path as well as the success path. Do not port only the happy path.
- Invalid, stale, duplicated, forged, out-of-range, or conflicting client requests must fail safely without creating authoritative state.
- For durable mutations, prefer validate -> persist -> update authoritative runtime state -> notify the client. Do not report success to the client before required persistence succeeds.
- Client-visible state is a projection of authoritative server state. If the client locally displays a different state, Athena.NET's state remains authoritative and subsequent synchronization must restore the server truth.

Security rule:
- Never log or commit passwords, PINs, bearer/JWT/session/auth tokens, or unsanitized packet captures.