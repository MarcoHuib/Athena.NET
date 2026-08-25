# WorldDataImporter

`WorldDataImporter` is the compatibility CLI for the emerging Athena world
compiler. Its pipeline is source loading -> hand-written lexer -> recursive-
descent syntax tree -> semantic analysis -> lowering -> deterministic C#. The
MapServer consumes compiled generated C#. JSON output remains useful for compiler
diagnostics or offline inspection, but is not runtime world data.

## Generate strongly typed C# (first vertical slice)

The first generated definition slice is declarative warp/WARPNPC data. Generation
is intentionally filter-scoped during migration:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile \
  --source-root legacy/rathena/npc/re/warps \
  --source-file izlude.txt \
  --name '#room_out' --name '#room_in' \
  --name '#room_out03' --name '#room_in03' --kind warp \
  --output src/MapServer/Generated/World/Izlude/RequiredWarps.cs
```

Output is ordinary deterministic C# with compact record-struct definitions and
`WorldBuildInfo` provenance (pinned rAthena commit, compiler version, world hash).
It does not use runtime Roslyn scripting or define a new runtime instruction VM.

## Generate executable NPC C# (migration slice)

`compile-script` runs the selected rAthena declaration through the tokenizer,
AST, semantic analysis, executable lowering, and deterministic C# emitter. The
current checked generated slice is reproduced with:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile-script \
  --source-root legacy/rathena/npc/re/warps \
  --source-file cities/izlude.txt \
  --map int_land04 --name '#intro_to_izlude_d' --kind warp \
  --output src/MapServer/Generated/World/Izlude/IntroToIzlude.g.cs
```

The generated class is compiled normally by the MapServer project. An explicit
generated registry supplies its actor/event definition and factory without
reflection. Generated scripts call the controlled `ScriptContext`; MapServer
continues to own client packets, continuations, map state, and authenticated
CharServer persistence.

Current generated executable coverage includes these real rAthena entities:
`warp:int_land04:intro_to_izlude_d` (`OnTouch`) and
`npc:iz_int:wounded swordsman#intro_npc02_iz_int` (`OnClick`). The ordinary NPC
definition carries its pinned position, direction, class 688, and initial cloak
option; its generated async behavior starts quest 21001 and emits the verified
iRO cutin packet through `ScriptContext`. Compiler report JSON remains diagnostic
output and is not runtime content.

The active `iz_int03/#ship_out03` exit is also generated executable `OnTouch`;
it derives `int_land03` from duplicate identity, persists the pinned savepoint,
and performs the existing same-server transfer through `ScriptContext`.

| Runtime entity | Generated equivalent | Runtime consumer | Parity test | Safe to remove JSON |
|---|---|---|---|---|
| `int_land04/#intro_to_izlude_d` + duplicates | `Academy/AcademyWorld.cs`, `Academy/AcademyWarpTriggers.cs`, `Academy/Scripts/IntroToIzludeOnTouchScript.cs` | `WorldRegistryBuilder` + `ScriptContext` | generated stock-iRO session integration | Yes; no runtime JSON existed |
| `iz_int/#ship_out` + duplicates | `Academy/AcademyWorld.cs`, `Academy/AcademyWarpTriggers.cs`, `Academy/Scripts/ShipOutOnTouchScript.cs` | `WorldRegistryBuilder` + `ScriptContext` | generated ShipOut03 warp/savepoint integration | Yes; no runtime JSON existed |
| `iz_int/Wounded Swordsman#intro_npc02_iz_int` + duplicates | `Academy/AcademyWorld.cs`, `Academy/AcademyNpcs.cs`, `Academy/Scripts/*.cs` | `WorldRegistryBuilder` + `ScriptContext` | visible actor/click/dialogue/quest integration | Yes; no runtime JSON existed |
| `int_land/Captain Carocc#intro_npc03`, `int_land/Lumin#new_ship` + duplicates | `Academy/AcademyWorld.cs`, `Academy/AcademyNpcs.cs` (actor-only, no behavior) | `WorldRegistryBuilder` | visible actor presence | Yes; no runtime JSON existed |
| `iz_int` and `iz_int03` room door pairs | `Academy/AcademyWarps.cs` | compiled `WorldMapRegistry` | generated minimal-warp/manual-login tests | Yes; aggregate removed |

## Generate an area's NPC/warp-trigger definitions + placements (duplicate-aware)

`compile-npc-world` groups a rAthena `script`/`duplicate(...)` chain into ONE
shared `NpcDefinition`/`WarpTriggerDefinition` + ONE shared `INpcScript` class
+ N `NpcPlacement`/`WarpTriggerPlacement` records, instead of emitting N
independent classes with duplicated dialogue. `--name` selects ordinary NPC
templates (`WorldEntityConverter.ConvertNpcDefinitions`); `--warp-name`
selects WARPNPC templates such as `#ship_out`/`#intro_to_izlude`
(`WorldEntityConverter.ConvertWarpTriggers`) — a parallel, warp-scoped type
pair mirroring `NpcDefinition`/`NpcPlacement` exactly, kept separate because
warp triggers have no sprite/class concept and never model NPC content.

Both conversions are always lossless: for a template with 4 duplicates they
always find the complete set of 5 placements (the template's own row plus its
4 duplicates), regardless of which of them a particular generated world slice
actually uses. Emission selection (`--exclude-placement`/`--warp-exclude-placement`,
`--no-behavior`) is applied strictly after that lossless conversion and never
special-cases a name inside either converter.

The current checked Academy slice is reproduced with:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile-npc-world \
  --source-root legacy/rathena/npc/re/jobs/novice \
  --source-root legacy/rathena/npc/re/warps/cities \
  --name 'Wounded Swordsman#intro_npc02_iz_int' \
  --name 'Wounded Swordsman#intro_npc01_iz_int' \
  --name 'Captain Carocc#intro_npc03' \
  --name 'Lumin#new_ship' \
  --exclude-placement 'npc:iz_int:wounded swordsman#intro_npc01_iz_int' \
  --exclude-placement 'npc:int_land:captain carocc#intro_npc03' \
  --exclude-placement 'npc:int_land:lumin#new_ship' \
  --no-behavior 'Captain Carocc#intro_npc03' \
  --no-behavior 'Lumin#new_ship' \
  --warp-name '#ship_out' \
  --warp-name '#intro_to_izlude' \
  --warp-exclude-placement 'warp:iz_int:ship_out' \
  --warp-exclude-placement 'warp:int_land01:intro_to_izlude_a' \
  --warp-exclude-placement 'warp:int_land02:intro_to_izlude_b' \
  --warp-exclude-placement 'warp:int_land03:intro_to_izlude_c' \
  --warp-exclude-placement 'warp:int_land:intro_to_izlude' \
  --namespace Athena.Net.MapServer.Generated.World.Izlude.Academy \
  --output-dir src/MapServer/Generated/World/Izlude/Academy
```

`--exclude-placement`/`--warp-exclude-placement` preserve today's exact
vertical slice: the pinned `Wounded Swordsman#intro_npc01_iz_int` OnTouch body
isn't currently lowerable (a `sleep2` timer construct), so only its OnClick
("Lying"/cloak-toggle) behavior is emitted; `#ship_out`'s and
`#intro_to_izlude`'s own template placements (`iz_int`/`int_land`) and
`#intro_to_izlude`'s `_a/_b/_c` duplicates were never part of the original
hand-curated registry, matching `int_land`'s Captain Carocc/Lumin template
placements. `--no-behavior` keeps Captain Carocc and Lumin actor-only: both
have real, non-trivial rAthena click dialogue (the converter finds it
losslessly), but their scripts deliberately stay unregistered pending real
healing/EXP/status-effect/inventory runtime support (see `ai/world-data.md`)
— this is an explicit emission-time decision, not a converter limitation.

Omitting the exclusion/no-behavior flags entirely emits every placement and
behavior the converter finds for the selected `--name`/`--warp-name`
templates — the normal, fully-reproducible-from-source case for new content
that doesn't need to preserve a pre-existing narrower slice.

`compile-npc-world` writes one area-level `AcademyWorld.cs` (one
`world.AddNpc(...)`/`world.AddWarpTrigger(...)` call per definition), one
area-level `AcademyNpcs.cs` (one `NpcDefinition` field per definition), one
area-level `AcademyWarpTriggers.cs` when `--warp-name` is given (one
`WarpTriggerDefinition` field per definition), and one `Scripts/*.cs` file per
unique executable behavior — no per-NPC generated fragments, no hand-maintained
registration list to edit when new content is added within the same
invocation's scope.

## Offline JSON conversion

The converter can still emit JSON for offline diagnostics. Its output is not
copied or loaded by MapServer:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- convert \
  --source-root legacy/rathena/npc/warps \
  --source-root legacy/rathena/npc/re/warps \
  --all-compatible true \
  --output /tmp/athena-world-entities \
  --report data/world/conversion-unsupported.json
```

Bulk conversion requires both `--all-compatible true` and `--report`. Definitions
with an unsupported command or expression are not partially generated. They are
listed with their source location in `conversion-unsupported.json`. Conflicting
deterministic entity IDs are also reported and skipped.

This command currently converts only categories supported by the existing
WorldEntity converter. It does not claim that ordinary NPCs, shops, monsters, or
the complete rAthena scripting language are supported.

## Convert a narrow selection

Use any combination of `--source-file`, `--map`, `--name`, and `--kind`. At least
one filter is required unless the explicit bulk switch is present.

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- convert \
  --source-root legacy/rathena/npc/warps \
  --source-root legacy/rathena/npc/re/warps \
  --source-file re/warps/cities/izlude.txt \
  --map int_land04 \
  --name '#intro_to_izlude_d' \
  --kind warp \
  --output /tmp/athena-world-entities
```

## Capability report

Scan the complete NPC source without generating entities:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- capabilities \
  --source-root legacy/rathena/npc \
  --output data/world/conversion-capabilities.json
```

The report is derived from syntax and semantic analysis. It distinguishes parsed
constructs from fully runtime-supported commands, includes source locations and
blocking reasons, and does not classify labels or language keywords as commands.

## Top-level content audit

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- audit \
  --source-root legacy/rathena/npc \
  --output data/world/conversion-audit.json
```

The audit only produces counts and classifications; it does not convert content.

## Novice progression data

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile-progression \
  --rathena-root legacy/rathena \
  --output src/MapServer/Generated/Progression/NoviceProgression.cs
```

This deliberately generates only the currently supported renewal Novice base/job
EXP, HP/SP, stat-point, and relevant job-bonus tables. The pinned YAML remains the
source of truth.

## Verification

```bash
dotnet test tests/WorldDataImporter.Tests/WorldDataImporter.Tests.csproj
dotnet test tests/MapServer.Tests/MapServer.Tests.csproj
dotnet build Athena.NET.sln -m:1
git diff --check
```

Generated files are deterministic: identical source, filters, and importer code
must produce byte-identical JSON.
