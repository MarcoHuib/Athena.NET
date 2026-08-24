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
