# Athena.NET world data

## Character position persistence

CharServer owns `CharDbContext` and persistent character rows. In schema order,
the location columns are `last_map`, `last_x`, `last_y`, `last_instanceid`, then
`save_map`, `save_x`, `save_y`. Thus a row projection such as
`iz_int 18 26 0 iz_int 18 26` contains current/login location first and savepoint
second. Character creation initializes both groups from `start_point`.

Character selection now resolves `LastMap/LastX/LastY`; only when `LastMap` is
empty does it use `SaveMap/SaveX/SaveY`. MapServer keeps current state in memory,
marks it dirty after movement, and sends internal `0x2B28/30` to CharServer on a
successful same-server warp and on session EOF/cancellation. CharServer accepts a
save only from an authenticated MapServer connection that consumed the matching
single-use `(accountId,charId)` auth node, then updates only `last_map/last_x/last_y`
for the matching non-deleted row. Savepoint columns are deliberately unchanged.

There is no per-movement database write and no periodic checkpoint yet. A normal
disconnect persists the latest dirty position; a sudden process crash can still
lose movement since the last warp.

## World-entity conversion pipeline

Athena's developer-facing world-data pipeline is:

`rAthena source -> source parser -> resolver/static evaluator -> Athena WorldEntity -> one JSON file per entity -> WorldRegistry -> derived runtime indexes`

One file under `data/world/entities/<map>/<entity>.json` is the source of truth for
one logical entity. Components that belong together stay together: an entity may
contain an optional Actor plus multiple Triggers, and every Trigger owns its
ordered Actions. An invisible trigger therefore needs no Actor. Runtime indexes
(`EntitiesById`, map actors, and OnTouch route lookups) are derived in memory;
there are no separately maintained actor/trigger/action files.

An entity may preserve a parsed script binding separately from executable
Triggers. The binding records trigger geometry, normalized source, required
runtime capabilities, and explicit `SourceParsed`/`RuntimeExecutable` state.
This lets Athena own the actor and source without registering unsafe behavior.

The typed extension points are intentionally narrow in the current slice:
`OnTouch`, `Warp`, and `SetSavePoint`. Class 45 actors preserve the verified
`JT_WARPNPC` visual. `SetSavePoint` data is loaded and ordered, but MapServer
execution is deferred until CharServer exposes a safe savepoint-persistence
contract. It is not faked by updating the character's current position.

`data/world/warps.json` remains a **temporary migration source**. WorldEntity
definitions load first and win over a legacy entry with the same map and entity
name, preventing duplicate actors/triggers. The legacy file will disappear only
after deliberate category-by-category migration.

### Importer commands

Audit the checked-in NPC source tree (counts/classifications only):

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- audit \
  --source-root legacy/rathena/npc \
  --output data/world/conversion-audit.json
```

Run the deliberately filtered tutorial conversion:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- convert \
  --source-root legacy/rathena/npc/warps \
  --source-root legacy/rathena/npc/re/warps \
  --source-file re/warps/cities/izlude.txt \
  --map iz_int03 \
  --kind warp \
  --output data/world/entities
```

Use `--map iz_int` with the same filters for the runtime-tested base tutorial
map. The checked-in slice contains only the three tutorial entities for
`iz_int` and the same three for `iz_int03`.

The stock iRO client has runtime-proven all six executable entities, including
the ordered `#ship_out` actions and same-MapServer transition to `int_land`.
The `int_land/#intro_to_izlude` actor and script are now WorldEntity-owned, but
its preserved `OnTouch` binding remains non-executable: it requires quest-state,
dialogue/player-selection, and quest-completion semantics Athena does not implement.

Developer-only runtime fixtures live separately under `data/world/dev`. They are
loaded through the same WorldRegistry and visibility indexes as converted content,
but are not rAthena provenance. The single `int_land04/Athena Test NPC` fixture exists
only to stock-client-test dialogue and quest state at `int_land04 (55,63)` and can be removed by deleting
its one file; it must never be treated as production world content.

Quest state is character gameplay data, not WorldEntity data. WorldEntity scripts
may contain typed quest instructions, but active/completed rows persist in the
character database's existing `quest` table through an authenticated MapServer to
CharServer contract. The developer fixture uses real client-known tutorial quest
21001 because no safe developer quest-ID namespace exists; it exercises only the
captured absent -> active -> completed lifecycle and should be used on development
characters.

The Athena MapServer-to-CharServer quest persistence contract is fixed and
explicit. Request `0x2B29/15` contains `accountId:u32` at offset 2,
`charId:u32` at 6, `questId:u32` at 10, and operation/state at 14 (`0` query,
`1` active, `2` completed). Response `0x2B2A/12` contains `charId:u32` at 2,
`questId:u32` at 6, resulting state at 10, and success at 11. The authenticated
CharServer session must own the account/character pair. Database errors return a
failure response without terminating that internal connection. With
`--auto-migrate`, CharServer applies its EF Core migrations; EF owns the complete
CharServer schema, including `quest`. Databases previously created by the removed
`EnsureCreated` bootstrap have no migration history and require a one-time rebuild
or an explicitly managed baseline before using the initial CharServer migration.

Filters may select `--source-file`, `--map`, `--name`, and/or `--kind`. At least
one is mandatory so an accidental unrestricted conversion cannot generate the
whole tree.

## Legacy rAthena warp aggregate

Source repository commit:
`6e6bca69b8a2ee03cd744cbc7a78a054a6f376ca`.

Renewal input is the combination enabled by rAthena's warp configs:

- `legacy/rathena/npc/warps` (81 files)
- `legacy/rathena/npc/re/warps` (58 files)

The retained legacy `warps.json` parses tab-separated declarative `warp` records into
unambiguous center/radius geometry. It supports comments, whitespace, same-map and
cross-map destinations, zero/larger radii, and statically resolvable
`duplicate(name)`. WARPNPC scripts and unresolved WARPNPC duplicates are reported
as dynamic/scripted. Their static visual name/map/center/radius is retained, but
no destination or execution is inferred.

Current deterministic output `data/world/warps.json` reports:

```text
files:                         139
static warps:                 3585
resolved static duplicates:      0
dynamic/scripted WARPNPC:       126
unsupported/malformed:            0
maps with static warps:          576
```

Regeneration command and GPL-3.0 provenance are in `data/world/README.md`.
MapServer copies the generated file to its output and loads it at startup; there
is no runtime dependency on the legacy checkout and no hand-coded tutorial list.

## Static and scripted warp actors

Every declarative `warp` has rAthena class `JT_WARPNPC`; dynamic WARPNPC scripts
also retain visual-only actor geometry. `WorldMapRegistry` creates stable logical
`WarpActor` instances from that same imported state. A thread-safe allocator uses
rAthena's separate NPC domain beginning at `110000000`, avoiding normal low
character IDs without copying official capture IDs.

On each stock-iRO `0x007D/3`, MapServer clears the current visibility cycle and
sends visible warp actors within the rAthena-compatible 14-cell square range.
Movement can add actors newly entering range, without duplicate spawn in the same
cycle. Same-map `0x0091` causes another `0x007D`, which begins the destination
visibility cycle. Explicit despawn is not synthesized because map reload resets
the client world in the captured flow.

The actor packet is capture-proven `0x09FF`: a fixed 84-byte modern idle-unit
prefix plus actor name. Dynamic values come from Athena actor state; NPC defaults
and constants match three official WARPNPC records and upstream layout. `0x0368`
actor-info response remains unimplemented because the capture did not require it
for portal visibility.

## Still missing

- periodic/debounced position checkpoints and crash recovery;
- generic NPC/script execution;
- dynamic scripted warp destinations;
- actor despawn/view-range exit handling;
- actor-info response semantics;
- collision/GAT pathfinding;
- cross-MapServer `0x0092` routing and auth transfer.
