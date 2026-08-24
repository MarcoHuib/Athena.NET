# Athena.NET world data

## Runtime architecture

MapServer's default world is compiled C#. It does not scan `data/world`, load
WorldEntity JSON, or overlay a legacy warp aggregate.

Current pipeline:

`pinned rAthena -> lexer/parser/semantics/lowering -> deterministic C# -> normal dotnet build -> WorldMapRegistry -> MapClientSession`

The intentionally supported runtime slice is:

- `iz_int/#room_out` and `#room_in`, pinned `izlude.txt:57,63`;
- active instance variants `iz_int03/#room_out03` and `#room_in03`, pinned
  `izlude.txt:60,66`;
- `iz_int03/#ship_out03`, pinned duplicate at `izlude.txt:80`, generated
  executable `OnTouch` with savepoint and transfer to `int_land03`;
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

## Authoritative character gameplay state

After MapAuth succeeds, MapServer performs a separate authenticated gameplay-state
read before sending the iRO bootstrap. `CharacterGameplayStateSession` is the one
state owner for the active session. Its immutable snapshot contains levels,
experience, HP/SP and their stored maxima, stat/skill points, and the six persistent
base stats. Temporary statuses and script locals are deliberately excluded.

CharServer remains the durable owner. Updates carry the expected and proposed
complete snapshot; CharServer checks authenticated character ownership and the
`gameplay_state_version` concurrency token, then commits all changed columns in one
EF transaction. MapServer replaces its local snapshot only after that commit is
acknowledged with the incremented authoritative version.

HP/SP and MaxHP/MaxSP already existed as relational `char` fields and remain stored.
Outside the generated Novice progression slice the maxima are transported unchanged;
unsupported jobs do not receive invented recalculation formulas.

## Novice progression

`compile-progression` reads pinned renewal `job_exp.yml`, `job_basepoints.yml`,
`job_stats.yml`, and `statpoint.yml` and emits deterministic strongly typed C#.
The generated table currently covers job class 0 (Novice), base levels 1-99 and
job levels 1-10. EXP entries are per-current-level costs, not cumulative totals.

`CharacterProgressionService` applies base and job EXP independently, loops over
all crossed thresholds, awards the difference between cumulative stat-point rows,
and awards one skill point per job level. One complete resulting gameplay snapshot
is persisted through the versioned CharServer transaction before MapServer publishes
it or sends client parameter updates. A failed/stale write sends no success updates.

Base-level recalculation uses the pinned Novice HP/SP base tables, persistent VIT/INT,
and generated Novice job bonuses. A base level fully restores recalculated HP/SP as
in `pc_checkbaselevelup`; job-only recalculation preserves current HP/SP within the
new maxima. Equipment/status modifiers remain outside this Novice-only slice.

## Regeneration

Generate the current minimal static warps:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile \
  --source-root legacy/rathena/npc/re/warps/cities \
  --source-file izlude.txt \
  --name '#room_out' --name '#room_in' \
  --name '#room_out03' --name '#room_in03' --kind warp \
  --output src/MapServer/Generated/World/Izlude/RequiredWarps.cs
```

Compiler audit/capability reports may still scan the complete pinned NPC tree;
their breadth does not imply runtime support.

## Still missing

The minimal `iz_int03` slice now also includes compiler-generated navigation targets, both Wounded Swordsman actor states/scripts, and actor-only definitions for the pinned `int_land03` Captain Carocc and Lumin duplicates. Captain/Lumin are visible, but their scripts deliberately remain unregistered until real healing, EXP, status-effect, inventory, and related semantics exist; no no-op gameplay commands are used.

- Remaining rAthena NPCs, warps, shops, monsters, items, and scripts.
- Poring spawn/combat/death and quest kill-progress synchronization.
- Broader event/state-machine lowering and persistent rAthena variable scopes.

Expand the runtime only through tested vertical slices; do not restore bulk JSON
runtime data as a shortcut.
