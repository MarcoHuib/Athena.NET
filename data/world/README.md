# Generated rAthena warp data

`warps.json` is deterministic normalized world data generated from the local
rAthena reference checkout at commit
`6e6bca69b8a2ee03cd744cbc7a78a054a6f376ca`.

Active Renewal source folders:

- `legacy/rathena/npc/warps`
- `legacy/rathena/npc/re/warps`

Regenerate from the repository root:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- \
  data/world/warps.json \
  legacy/rathena/npc/warps \
  legacy/rathena/npc/re/warps
```

The importer supports tab-separated declarative `warp` definitions and resolves
`duplicate(name)` only when `name` identifies a previously parsed static warp.
WARPNPC scripts and unresolved WARPNPC duplicates retain normalized visual
actor geometry but are classified as dynamic: no destination is inferred.

The source checkout and Athena.NET are GPL-3.0 licensed. The generated file
retains per-record source file and line provenance and is distributed under the
repository license. It must be regenerated and reviewed when the source commit
changes.
