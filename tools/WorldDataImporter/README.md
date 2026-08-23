# WorldDataImporter

`WorldDataImporter` converts the pinned rAthena source under `legacy/rathena/npc`
into Athena.NET `WorldEntity` JSON. Run commands from the repository root.

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

The report counts commands and marks current capabilities as supported or
unsupported. It is the roadmap for extending the existing parser/runtime.

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
