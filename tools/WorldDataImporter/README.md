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
  --source-file izlude.txt --map iz_int \
  --name '#room_out' --name '#room_in' --kind warp \
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

Current generated executable coverage is two real rAthena entities:
`warp:int_land04:intro_to_izlude_d` (`OnTouch`) and
`npc:iz_int:wounded swordsman#intro_npc02_iz_int` (`OnClick`). The ordinary NPC
definition carries its pinned position, direction, class 688, and initial cloak
option; its generated async behavior starts quest 21001 and emits the verified
iRO cutin packet through `ScriptContext`. Compiler report JSON remains diagnostic
output and is not runtime content.

| Runtime entity | Generated equivalent | Runtime consumer | Parity test | Safe to remove JSON |
|---|---|---|---|---|
| `int_land04/#intro_to_izlude_d` | `IntroToIzlude.g.cs` | Generated registry + `ScriptContext` | generated stock-iRO session integration | Yes; removed |
| `iz_int/Wounded Swordsman#intro_npc02_iz_int` | `WoundedSwordsman.cs` | Generated registry + `ScriptContext` | visible actor/click/dialogue/quest integration | Yes; no runtime JSON existed |
| `iz_int/#room_out`, `#room_in` | `RequiredWarps.cs` | compiled `WorldMapRegistry` | generated minimal-warp tests | Yes; aggregate removed |

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

## Verification

```bash
dotnet test tests/WorldDataImporter.Tests/WorldDataImporter.Tests.csproj
dotnet test tests/MapServer.Tests/MapServer.Tests.csproj
dotnet build Athena.NET.sln -m:1
git diff --check
```

Generated files are deterministic: identical source, filters, and importer code
must produce byte-identical JSON.
