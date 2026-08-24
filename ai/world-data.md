# Athena.NET world data

## Runtime architecture

MapServer's default world is compiled C#. It does not scan `data/world`, load
WorldEntity JSON, or overlay a legacy warp aggregate.

Current pipeline:

`pinned rAthena -> lexer/parser/semantics/lowering -> deterministic C# -> normal dotnet build -> WorldMapRegistry -> MapClientSession`

The intentionally supported runtime slice is:

- `iz_int/#room_out`, pinned `izlude.txt:57`, generated in `RequiredWarps.cs`;
- `iz_int/#room_in`, pinned `izlude.txt:63`, generated in `RequiredWarps.cs`;
- `int_land04/#intro_to_izlude_d`, generated executable `OnTouch`;
- `iz_int/Wounded Swordsman#intro_npc02_iz_int`, generated executable `OnClick`.

`GeneratedScriptRegistry` owns explicit script factories and entity metadata.
Generated and custom scripts share `INpcScript`, `ScriptContext`, world entities,
and actor contracts. Duplicate custom registrations fail unless replacement is
explicitly requested.

The former `data/world/entities`, `data/world/dev`, and `data/world/warps.json`
runtime datasets are removed. JSON files remaining under `data/world` are compiler
reports only. Pinned `legacy/rathena` is authoritative and remains available for
progressive regeneration.

## Generated gameplay execution

Generated scripts use existing MapClientSession dialogue continuations, iRO packet
serializers, and authenticated CharServer persistence. They never instantiate
`ScriptExecutionSession`. That interpreter remains only for explicitly constructed
legacy unit/integration fixtures while migration cleanup continues.

The generated Wounded Swordsman resolves `4_TOWER_02` from pinned `npc.hpp`, emits
direction and cloak option state, executes the real dialogue, starts quest 21001,
and uses the capture-verified cutin packet through `ScriptContext`.

## Character position and savepoint persistence

CharServer owns persistent character rows. MapServer keeps current position in
memory and sends authenticated position/savepoint requests to CharServer after
warps, savepoint commands, and normal disconnect handling. Generated scripts do
not access EF Core or MSSQL directly.

## Regeneration

Generate the current minimal static warps:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile \
  --source-root legacy/rathena/npc/re/warps/cities \
  --source-file izlude.txt --map iz_int \
  --name '#room_out' --name '#room_in' --kind warp \
  --output src/MapServer/Generated/World/Izlude/RequiredWarps.cs
```

Compiler audit/capability reports may still scan the complete pinned NPC tree;
their breadth does not imply runtime support.

## Still missing

- Remaining rAthena NPCs, warps, shops, monsters, items, and scripts.
- Poring spawn/combat/death and quest kill-progress synchronization.
- Broader event/state-machine lowering and persistent rAthena variable scopes.

Expand the runtime only through tested vertical slices; do not restore bulk JSON
runtime data as a shortcut.
