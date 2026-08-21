# iRO data and tooling development prompt

## Goal
Reuse the mature `legacy/rathena/` data ecosystem where practical while producing a clean, reproducible data pipeline for an iRO-only Athena.NET server.

The project does not need generic kRO client protocol support, but `legacy/rathena/` remains the main reference for gameplay data, scripts, map cache concepts, databases, and server mechanics.

## Legacy reference projects
- `legacy/rathena/` — primary source for gameplay data, scripts, database/schema concepts, map-cache tooling, and server mechanics.
- `legacy/openkore/` — protocol/community reference only; generally not a source for authoritative gameplay datasets.

## Primary rAthena reference sources
- `legacy/rathena/db/` - items, mobs, skills, jobs, maps, constants, and related YAML/TXT data.
- `legacy/rathena/npc/` - NPC and script content.
- `legacy/rathena/sql-files/` - schema and upgrade concepts.
- `legacy/rathena/src/tool/` - mapcache/yaml conversion/tooling ideas.
- `legacy/rathena/src/map/` - runtime interpretation of the data.

## iRO-first data policy
- Prefer iRO-correct values/content when they differ from rAthena defaults.
- Do not assume rAthena renewal/kRO data is identical to iRO merely because the schema matches.
- Separate *format compatibility* from *content correctness*: Athena.NET may parse rAthena YAML while overriding data with iRO-specific values.
- Track the provenance/version of imported datasets so an iRO client/server mismatch can be diagnosed.
- Keep protocol reverse engineering out of the data layer unless a packet requires a specific numeric ID mapping.

## Recommended architecture
- Reuse/parse rAthena YAML and scripts where licensing and project requirements permit.
- Build C# loaders with validation and deterministic startup diagnostics.
- Keep iRO overrides in explicit files/tables instead of hidden code constants.
- Generate/cache derived assets such as map indexes reproducibly.
- Preserve source identifiers when the iRO protocol/game data expects them.

## Immediate priorities after MapServer auth
- map cache/index sufficient to load the character's start map
- job/class/status definitions required for spawn
- item/inventory definitions required for initial sync
- skill/status data required by the first gameplay packets
- minimal NPC/script support only when the client can already enter and move

## Later work
- mobs, drops, combat, quests, shops, storage, guild/party, instances, achievements, mail, pets/homunculus/mercenary systems as needed by iRO behavior
- tooling for diffing rAthena data against iRO-specific overrides
- repeatable import/update process with tests

## Definition of done
A clean checkout can build or import the required game data deterministically, start Athena.NET, and serve iRO-correct map/gameplay state without manual one-off data edits.
