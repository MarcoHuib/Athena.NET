# Athena.NET vibe prompts

Purpose
- These files are the running "vibe coding" prompts to continue the C# migration from legacy rAthena.
- Each file is scoped to one project or workstream so we can pick up where we left off.
- When a milestone is done, delete or collapse that section to keep context small.

How to use
- Pick the relevant file(s) for the next session.
- Start by reading the "Current state" and "Next tasks".
- Update the file as work finishes (remove done items, add new gaps).

Index
- ai/login-server.md
- ai/char-server.md
- ai/map-server.md
- ai/data-and-tools.md

Global constraints
- Do not commit secrets or generated local config; keep using templates and gitignore.
- Aspire is the local-dev orchestration; Docker Compose is the production/runtime option and may be documented.
- Use ASCII in new files unless the file already contains Unicode.

Legacy reference root
- upstream/ is the source of truth for feature parity. Use it for behavior, config, and DB schema.
