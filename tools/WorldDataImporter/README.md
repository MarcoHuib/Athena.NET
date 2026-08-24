# WorldDataImporter

`WorldDataImporter` is the compatibility CLI for the emerging Athena world
compiler. Its pipeline is source loading -> hand-written lexer -> recursive-
descent syntax tree -> semantic analysis -> lowering -> deterministic C#. The
JSON `WorldEntity` interpreter remains temporarily available while runtime parity
is migrated vertically.

## Generate strongly typed C# (first vertical slice)

The first generated definition slice is declarative warp/WARPNPC data. Generation
is intentionally filter-scoped during migration:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile \
  --source-root legacy/rathena/npc/re/warps \
  --source-file cities/izlude.txt --map iz_int --kind warp \
  --output /tmp/Generated/Maps/Izlude.Warps.g.cs
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

Current generated executable coverage is one real rAthena entity:
`warp:int_land04:intro_to_izlude_d` (`OnTouch`). Its former runtime JSON file is
removed because generated registration and runtime integration tests cover the
same definition and behavior. The other five executable JSON entities still use
the legacy `ScriptExecutionSession` fallback. Compiler report JSON remains
diagnostic output and is not part of this removal.

| Runtime entity | Generated equivalent | Runtime consumer | Parity test | Safe to remove JSON |
|---|---|---|---|---|
| `int_land04/#intro_to_izlude_d` | `IntroToIzlude.g.cs` | Generated registry + `ScriptContext` | generated stock-iRO session integration | Yes; removed |
| `int_land/#intro_to_izlude` | None | JSON + `ScriptExecutionSession` | legacy registry/session tests | No |
| `int_land01/#intro_to_izlude_a` | None | JSON + `ScriptExecutionSession` | legacy registry/session tests | No |
| `int_land02/#intro_to_izlude_b` | None | JSON + `ScriptExecutionSession` | legacy registry/session tests | No |
| `int_land03/#intro_to_izlude_c` | None | JSON + `ScriptExecutionSession` | legacy registry/session tests | No |
| `int_land04/Athena Test NPC` | None | developer JSON + `ScriptExecutionSession` | dialogue/quest tests | No |

## Convert everything currently compatible

This is the normal command for regenerating all warp and WARPNPC definitions that
the current Athena runtime can execute completely:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- convert \
  --source-root legacy/rathena/npc/warps \
  --source-root legacy/rathena/npc/re/warps \
  --all-compatible true \
  --output data/world/entities \
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
  --output data/world/entities
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
