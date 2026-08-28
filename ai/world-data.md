# Athena.NET world data

## Runtime architecture

MapServer's default world is compiled C#. It does not scan `data/world`, load
WorldEntity JSON, or overlay a legacy warp aggregate.

Current pipeline:

`pinned rAthena -> lexer/parser/semantics/lowering -> deterministic C# -> normal dotnet build -> WorldMapRegistry -> MapClientSession`

The intentionally supported runtime slice covers the COMPLETE `iz_int`/`int_land`
tutorial family — the generic/base maps (`iz_int`, `int_land`) and all four
instanced duplicates (`iz_int01..04`, `int_land01..04`) alike, since
`start_point` config can place a new character on any of the five `iz_int*`
variants and each must be a functionally equivalent tutorial slice. This was
NOT always true: an earlier `compile-npc-world`/`compile-navigation`
regeneration invocation used `--exclude-placement`/`--warp-exclude-placement`
to drop the generic/base template placements of several pinned
`duplicate(...)` families, leaving generic `iz_int` (one of the five valid
`start_point` destinations) without a visible Wounded Swordsman, without
`#ship_out`, and downstream without Captain Carocc/Lumin/`#intro_to_izlude`
on generic `int_land` either — a regeneration-selection bug, not an
intentional narrower slice. See `tools/WorldDataImporter/README.md`'s
`compile-npc-world`/`compile-navigation` sections for the corrected
invocations and `WorldMapRegistryFamilyTests`
(`tests/MapServer.Tests/World/`) for the regression coverage.

Representative examples (every one of these has a `01`/`02`/`03`/`04`
counterpart on the matching instanced map):

- `iz_int/#room_out` and `#room_in`, pinned `izlude.txt:57,63` (instanced
  variants e.g. `iz_int03/#room_out03`/`#room_in03`, pinned `izlude.txt:60,66`);
- `iz_int/#ship_out`, pinned template at `izlude.txt:69`, generated executable
  `OnTouch` with savepoint and transfer to `int_land` (instanced duplicate
  e.g. `iz_int03/#ship_out03` -> `int_land03`, pinned `izlude.txt:80`);
- `int_land/#intro_to_izlude`, pinned template at `izlude.txt:83`, generated
  executable `OnTouch` with transfer to `izlude` (instanced duplicates use the
  pinned lettered suffixes, e.g. `int_land04/#intro_to_izlude_d` -> `izlude_d`,
  NOT a numeric `04` suffix — `StrNpcInfo(2)`-derived at runtime, not a
  per-instance branch);
- `iz_int/Wounded Swordsman#intro_npc01_iz_int` (class 687, generated
  executable `OnClick`, initially visible — no `cloakonnpc()` in its pinned
  `OnInit`) and `iz_int/Wounded Swordsman#intro_npc02_iz_int` (class 688,
  generated executable `OnClick`, initially cloaked via pinned `OnInit:
  cloakonnpc()`).

`GeneratedScriptRegistry` owns explicit script factories and entity metadata.
Generated and custom scripts share `INpcScript`, `ScriptContext`, world entities,
and actor contracts. Duplicate custom registrations fail unless replacement is
explicitly requested.

## World Definition Model (NPCs)

rAthena `duplicate(...)` chains are represented as one `NpcDefinition`
(reusable behavior, keyed by a source-position-derived `DefinitionId`) plus N
`NpcPlacement` records (per-instance map/x/y/direction/sprite/radius) instead
of N independent generated classes with duplicated dialogue. `WorldEntityConverter.ConvertNpcDefinitions`
performs this grouping losslessly at conversion time: duplicate resolution
always scans the complete parsed declaration index, and for a template with N
duplicates it always returns the complete semantic set of N+1 placements
(the template's own row plus its duplicates). It never special-cases an NPC by
name.

Which of those placements/behaviors actually reach a particular generated
world slice is a separate, later "emission selection" step (`compile-npc-world`'s
`--exclude-placement`/`--no-behavior` flags), applied strictly after the
converter returns its lossless result. Placement and behavior are
orthogonal: `--exclude-placement` drops a specific NPC/warp instance's
ACTOR entirely (only appropriate when that literal instance/map is not part
of the intended generated world at all — never merely because a placement is
a `duplicate(...)` family's generic/base template row), while `--no-behavior`
keeps a definition's actor(s) visible/interactable-looking but withholds its
script registration. The Academy slice no longer needs that suppression for
Lumin: both Captain Carocc and Lumin have generated click scripts, and both
NPCs' actors are placed on every `int_land`/`int_land01..04` map, not only the
instanced duplicates.

`WorldRegistryBuilder.AddNpc(NpcDefinition, IReadOnlyList<NpcPlacement>)` is
the runtime registration entry point generated `AcademyWorld.Register(builder)`
calls; it lowers the definition/placement pair back into today's
`WorldEntityDefinition`/`GeneratedScriptRegistration` shapes internally, so
`WorldMapRegistry`, `ScriptContext`, and `MapClientSession` require no changes.
Hand-written custom NPC content uses the identical `AddNpc` API. A definition
with zero behaviors still contributes its `WorldEntityDefinition`s to the
built world (proven by `WorldRegistryBuildResult.Entities`, independent of
whether `WorldRegistryBuildResult.Scripts` has any registration for it). This
remains available for genuinely actor-only generated content; Captain Carocc
and Lumin now both have script registrations.

Generated tree for this slice: `src/MapServer/Generated/World/Izlude/Academy/AcademyWorld.cs`,
`AcademyNpcs.cs`, and one `Scripts/*.cs` file per unique executable behavior.
See `tools/WorldDataImporter/README.md` for the `compile-npc-world` command.

The same conversion-time-grouping principle applies to rAthena `script`/`duplicate()`
WARPNPC chains (`#ship_out`, `#intro_to_izlude`) via a parallel, warp-scoped
`WarpTriggerDefinition`/`WarpTriggerPlacement` pair (mirroring `NpcDefinition`/
`NpcPlacement`, not modeled as NPCs — warp triggers have no sprite/class
concept). `WorldRegistryBuilder.AddWarpTrigger` lowers into the same
`WorldEntityDefinition{Kind: "Warp"}` shape today's runtime already consumes.
Generated output: `Academy/AcademyWarpTriggers.cs` (one `WarpTriggerDefinition`
field per template) plus the shared `Academy/Scripts/*.cs` class. Plain
declarative `warp` directives (`#room_out`/`#room_in`, no `duplicate()` chain)
have no shared behavior to extract and remain ordinary `WarpDefinition` records
in `Academy/AcademyWarps.cs` (renamed from `RequiredWarps.cs`, content
unchanged). Tutorial `navigateto` placement data lives in
`Academy/AcademyNavigation.cs` (renamed from `TutorialNavigation.cs`, content
unchanged — it has no rAthena duplicate relationship to model).

### Navigation lifecycle across the `iz_int` -> `int_land` transition

Pinned `iz_int#intro_start`/`iz_int01..04#intro_start` (`academy.txt:21-53`)
starts one navigation (`iz_int -> 52,30`); pinned `iz_int#intro_evt02`/
`iz_int01..04#intro_evt02` (`academy.txt:55-64`) starts a SECOND, separate
navigation once the player reaches `51,30` (`int_land -> 75,100`), which is
still active while the player walks through `#ship_out` and loads `int_land`
— pinned source contains no third `navigateto`/cancel call anywhere in this
path. `MapClientSession`'s `CzNotifyActorInit` handler (map-loaded) calls
`GetNavigationAt(_mapName, _x, _y)` again on every map load, including
`int_land`'s own load — `AcademyNavigation.All` has zero `int_land` entries,
so that lookup is empty there and no third navigation packet is ever sent.
Arrows visibly persisting after the `#ship_out` warp is therefore the
EXPECTED re-display of the second (`intro_evt02`) navigation's proven wire
packet (`0x08E2`, ground-arrow rendering — see `ai/iro-2026-wire.md`'s
capture-verified structure), not a duplication/accumulation bug. There is no
capture evidence anywhere in this repository of an official iRO
cancel/clear-navigation packet or of official iRO client behavior differing
from this pinned script here, so no such packet is synthesized — see
`WorldMapRegistryFamilyTests.NoThirdNavigation_IsSynthesizedWhenIntLandItselfLoads`
for the regression proof, and `MapClientSession`'s `[iRO MAP DEBUG] Sending
0x08E2 navigation ...` log line for runtime diagnosis of this exact sequence.

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
Unsupported jobs fail through the generated progression lookup; they do not receive
invented recalculation formulas.

## Generated progression registry and rate policy

`compile-progression` reads pinned renewal `job_exp.yml`, `job_basepoints.yml`,
`job_stats.yml`, and `statpoint.yml` and emits a deterministic strongly typed
`GeneratedProgressionRegistry` keyed by job class. It currently contains job class
0 (Novice), base levels 1-99 and job levels 1-10. EXP entries are per-current-level
costs, not cumulative totals. Runtime consumes `CharacterProgressionDefinition`,
not a Novice-specific static table.

`CharacterProgressionService` applies Base and Job EXP independently, awards the
difference between cumulative stat-point rows, and awards one skill point per Job
level. Pinned `conf/battle/exp.conf` defaults `multi_level_up: no`; matching
`pc_checkbaselevelup`/`pc_checkjoblevelup`, one award crosses at most one threshold
and caps overcarry to the crossed requirement minus one. One complete resulting
snapshot is persisted through the versioned CharServer transaction before MapServer
publishes it or sends packets. A failed/stale write sends no success updates.

Base-level recalculation uses the pinned Novice HP/SP base tables, persistent VIT/INT,
and generated Novice job bonuses. A base level fully restores recalculated HP/SP as
in `pc_checkbaselevelup`; job-only recalculation preserves current HP/SP within the
new maxima. Equipment/status modifiers remain outside this Novice-only slice.

`GameplayRateOptions` is loaded once and passed unchanged through `MapServerWorld`.
`base_exp_rate` and `job_exp_rate` multiply raw generated monster Base/Job EXP;
`quest_exp_rate` alone multiplies both generated script `getexp` arguments;
`mvp_exp_rate` is retained for the future MVP boundary. Positive multiplication is
exact integer `raw * rate / 100`, truncating the remainder and capping at pinned
`MAX_EXP` (`INT64_MAX`) without floating point.

Item rate families (common/heal/use/equip/card, boss and MVP normal-drop variants,
and direct-reward `item_rate_mvp`) are parsed and retained but not consumed because
Athena has no generic normal-drop/MVP-reward runtime. Tutorial Wood/Lumber
`QuestDropRule` handling is unchanged and receives no item-rate multiplier.

## Regeneration

Generate the current minimal static warps:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile \
  --source-root legacy/rathena/npc/re/warps/cities \
  --source-file izlude.txt \
  --name '#room_out' --name '#room_in' \
  --name '#room_out03' --name '#room_in03' --kind warp \
  --output src/MapServer/Generated/World/Izlude/Academy/AcademyWarps.cs
```

Generate the current Academy NPC definitions/placements/behaviors: see the
`compile-npc-world` command in `tools/WorldDataImporter/README.md`.

Compiler audit/capability reports may still scan the complete pinned NPC tree;
their breadth does not imply runtime support.

Regenerate progression from the current pinned SHA (never edit generated output):

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- \
  compile-progression --rathena-root legacy/rathena \
  --rathena-commit e985006171d2eb320ee512a653f4c83aea3d81b6 \
  --output src/MapServer/Generated/Progression/NoviceProgression.cs
```

## Still missing

The complete `iz_int`/`int_land` tutorial family (generic base maps plus all four instanced duplicates) includes compiler-generated navigation targets, both Wounded Swordsman actor states/scripts, and generated behavior for Captain Carocc and Lumin — not only the `iz_int03`/`int_land03` instanced variant. Captain Carocc's pinned dialogue/quest/heal/status/EXP script and Lumin's pinned dialogue/quest/cutin/cloak script are registered and executable on every map in the family. Lumin's `strcharinfo(0)` resolves the active character name carried through the authenticated CharServer map handoff; it is never sourced from a client dialogue packet or capture constant.

Separately, pinned `Wounded Swordsman#intro_npc02_iz_int`'s `OnInit: questinfo(...)` is a genuine, still-open capability gap: rAthena's `questinfo(QTYPE_QUEST, QMARK_YELLOW, ...)` drives a client-facing quest-marker icon above the NPC, which Athena does not yet emit any packet for. This is NOT implemented here — no packet has been synthesized without capture/wire evidence (see `ai/iro-2026-wire.md`'s evidence-priority rule) — and remains distinct from the navigation-arrow (`navigateto`) capability, which IS implemented and generated from pinned source via `compile-navigation`/`AcademyNavigation.cs`.

## Heal and temporary status effects

`heal` mutates the authoritative `CharacterGameplayState` HP/SP through the same
versioned `CharacterGameplayStateSession.MutateAsync` persistence path progression
uses, via `CharacterHealService`. It clamps to `[0, MaxHp]`/`[0, MaxSp]` per pinned
`status_heal`. The already-verified `0x00B0` parameter-change packet synchronizes
HP/SP, and is only sent for the field(s) that actually changed (e.g. an
already-full heal sends nothing) — the same policy `GrantExperienceAsync` already
uses for level-up fields, not new or NPC-specific behavior. A positive heal amount
also sends the capture-proven `0x09CB` (`ZC_USE_SKILL`) heal visual with
`SKID=AL_HEAL(28)` and `level=` the heal amount (see `ai/iro-2026-wire.md`).

`sc_start` starts a small generic temporary status foundation
(`CharacterStatusEffectState`), not the complete Ragnarok status system. Each
`MapClientSession` owns independent mutable status state in MapServer runtime
memory only — temporary statuses are never persisted to `CharacterGameplayState`.
Reads (`TryGet`/`Recalculate`) treat an already-expired status as inactive purely
by comparing against an injected `TimeProvider`, without removing it. Effective
stats are derived on demand from persisted base stats plus every currently active
(non-expired) status; persisted base stats themselves are never mutated by a
temporary status. Re-applying an already-active status (pinned `sc_start`
semantics) overwrites its stored values/duration outright rather than stacking,
which also moves its expiration deadline forward.

Natural expiration is driven by one expiration scheduler per `MapClientSession`
(not a `Task.Delay`/`Timer` per active status) that sleeps until
`CharacterStatusEffectState.NextExpiration` via the same `TimeProvider`, waking
early whenever `StartStatusAsync` adds or refreshes a status. When a status comes
due, `CharacterStatusEffectState.ExpireDue` explicitly removes and returns it (so
the transition is observed exactly once, never silently), effective stats are
recalculated before and after, and the client receives a `0x0196`
(`ZC_MSG_STATE_CHANGE`, the "off" form of the same builder used for activation)
plus only the `0x0141` fields whose effective value actually changed — matching
pinned `status_change_end`'s generic tail for both statuses (neither has a
type-specific case in that function's switch). See `ai/iro-2026-wire.md` for the
pinned line references and the one open gap (client-side countdown display is
inferred from the activation packet's duration fields, not independently
capture-verified, since Captain's dialogue never runs the full 240 seconds).

Only `Blessing`/`Increase AGI` are currently modeled, matching pinned
`legacy/rathena/src/map/status.cpp` and independently confirmed by the
`npc-interaction-heal-action.pcapng` frame-3496 burst (`ai/iro-2026-wire.md`):
Blessing adds `+val1` (its `val2`, which equals `val1` for a player target) to
STR/INT/DEX; Increase AGI adds `+(2 + val1)` (its `val2`, per
`status_change_start_post_delay`'s val-settings switch) to AGI itself, and
additionally grants a flat `+25` move-speed haste value and a `+val1`
attack-speed bonus. `StartStatusAsync` sends the capture-proven client
synchronization for both: a `0x0983` (`ZC_MSG_STATE_CHANGE3`) activation icon
(`EFST_BLESSING=10`/`EFST_INC_AGI=12`) and `0x0141` (`ZC_COUPLESTATUS`) for each
capture-proven affected base stat (STR/INT/DEX for Blessing, AGI for Increase
AGI). `SkillEffectAsync` sends the matching `0x09CB` skill-cast visual
(`SKID`/`level` from the script's own arguments). `specialeffect2` remains a
no-op: the capture proves zero `0x01F3` bytes for it in this same burst, so no
packet is synthesized for it pending independent wire proof. An earlier version
of this document incorrectly claimed all of `specialeffect2`/`skilleffect`/
`sc_start` produced zero client bytes in this capture; that claim was based on a
misattributed frame and has been retracted (see `ai/iro-2026-wire.md`).
`specialeffect2`'s own zero-byte finding, specifically, still holds.

- Remaining rAthena NPCs, warps, shops, items, and scripts.
- Broader event/state-machine lowering and persistent rAthena variable scopes.

## Monster combat and quest drops (G_PORING / quest 21008)

### Verified stock-iRO evidence

`kill-poring-heal-jobup.pcapng` (see `ai/iro-2026-wire.md`'s "Verified monster
combat wire evidence" section for the full frame-by-frame table) closes this
gap: a real stock iRO client kills G_PORING (actor `0x00001E9D`) on
`128.241.92.42:4506` and receives Wood. Proven: monster appearance (`0x09FF`,
object type 5, real HP fields), actor-info correlation (`0x0368`/`0x0ADF`),
attack request (`0x0437`), damage response (`0x08C8`, exact `ZC_NOTIFY_ACT3`
match), death (`0x0080` type=1 "died"), and item-pickup acknowledgement
(`0x0B41`, exact `ZC_ITEM_PICKUP_ACK` match). The earlier finding below (that
the WARPNPC `0x09FF` serializer must not be reused unchanged for a monster)
is confirmed by this capture and remains accurate: a monster's `0x09FF` uses
`objecttype=5` and a real, HP-state-dependent sentinel, not WARPNPC's
`objecttype=6`/always-`0xFFFFFFFF` shape - see `IroMonsterActorPackets`
(distinct from `IroWorldActorPackets.BuildWorldActor`). Respawn/reappearance
for this actor was not itself captured (the player moved away first); that
one behavior remains inferred from pinned source, not independently
capture-verified.

### Pinned rAthena semantics

- `npc/re/mobs/int_land.txt:11-15`: `int_land{,01,02,03,04},0,0 monster Poring
  2401,40,5000` — mob ID **2401** (`Aegis G_PORING`), not ordinary Poring
  (1002). `db/re/mob_db.yml` Id 2401 has **no `BaseExp`/`JobExp` fields at
  all** (both resolve to 0) and `Modes: FixedItemDrop: true` (a normal-drop
  rate-scaling flag, unrelated to aggression/passivity — this branch does not
  claim to know whether G_PORING retaliates; no combat AI is implemented
  either way, so the question is moot for this slice).
- `mob.cpp:1117` (`mob_spawn`) + `map.cpp:1798`
  (`map_search_freecell`): a spawn declaration with `x=0,y=0` and no
  `xs,ys` is **not** literal coordinate `(0,0)` and **not** "random within a
  small radius around `(0,0)`". `xs+ys<1` forces the search branch, which
  performs a map-wide randomized-candidate search (excluding a configurable
  edge margin, default 15 cells) checked against real GAT walkability
  (`CELL_CHKREACH`), retried up to 8 times then falling back to a 50-try
  whole-map search. Athena.NET has **no `.gat`/mapcache/collision data
  anywhere in this repository** — not in its own runtime, and not in the
  pinned `legacy/rathena` submodule (rAthena repos never ship the proprietary
  client map resources a mapcache is built from; only the mapcache-building
  *tool's* C++ source exists, `src/tool/mapcache.cpp`, with no generated
  output checked in anywhere). This is a genuine external data gap, not a
  scope choice; see `IMobSpawnCellSelector` /
  `UnverifiedFallbackMobSpawnCellSelector` in `src/MapServer/World/
  MobSpawnCellSelector.cs`, which isolates it behind one seam with a
  deterministic (not walkability-checked) placeholder, explicitly documented
  as non-authoritative rather than presented as production parity.
- `db/re/quest_db.yml:12538-12543`: quest 21008 ("The first battle") has
  **only** a `Drops:` rule (`G_PORING -> Wood @ Rate 10000`), **no `Targets:`
  block**. `quest.cpp:quest_update_objective` (lines 757-838) proves the drop
  loop is entirely independent of the kill-count/`Targets` objective loop:
  quest 21008 has zero objectives, so nothing there ever runs for it. The
  loop only ever iterates a character's own `quest_log` (`Q_COMPLETE` entries
  are explicitly skipped at line 762), so an absent-from-log or completed
  quest never reaches the drop-matching code at all. Rate `10000`
  (`quest.cpp:813`, `rnd_chance(rate,10000)`) is out of 10000 and never rolls
  at all when equal to 10000 (guaranteed). `Count` defaults to 1 when the
  pinned YAML omits it (`quest.cpp:409-410`).
- `npc/re/jobs/novice/academy.txt:133-199` (`Captain Carocc#intro_npc03`):
  `setquest 21008` (already implemented, `CaptainCaroccOnClickScript.cs`) is
  the only place quest 21008 is activated in the pinned tree; nothing sets a
  kill counter anywhere.
- `db/re/item_db_etc.yml:15582-15588`: Wood is item ID **6008**, `Type: Etc`,
  which `itemdb.cpp:item_data::isStackable` makes stackable (every type except
  Weapon/Armor/PetEgg/PetArmor/ShadowGear is stackable).
- Combat formula (`status.cpp:2424` `status_base_atk`, `:2600`
  `status_calc_misc`, `battle.cpp:2515` `battle_calc_base_damage`, `:4720`
  `battle_calc_defense_reduction`, `:6766` `battle_calc_attack`): traced
  exactly for the one case this slice supports — a fresh Novice, bare fists,
  no 4th-tier `POW` stat allocation (`POW=0` is the real reset default,
  `pc.cpp:9262`, not an invented value). `batk = floor((STR*10 + DEX*10/5 +
  LUK*10/3 + BaseLevel*10/4)/10)`; unarmed weapon-roll contributes 0; monster
  soft-DEF (`def2`) = `floor((Level+Vit)/2)` (mob's `Vit` defaults to 1 when
  the pinned block omits it, per the constructor default at `mob.cpp:4954`,
  not 0); `damage = batk*(4000+Defense)/(4000+10*Defense) - def2`; a result
  `<1` is a **miss (0 damage)**, not floored to 1 (`battle_calc_attack:6766`
  — there is no universal min-1-damage clamp for a normal attack). Hit/flee
  accuracy rolls are deliberately not implemented (disclosed simplification,
  not a silent one): this slice's only source of a miss is the damage-floor
  rule above.

### Athena.NET implementation

Generated (via new `WorldDataImporter` commands `compile-mob-spawn`,
`compile-quest-drop`, `compile-item`, mirroring the existing `compile-
progression` hand-rolled-scalar-parser pattern rather than adding a new YAML
library dependency). Global game data — "what is mob 2401", "what is item
6008", "quest 21008's drop rule" — is deliberately **not** placed under the
Academy world slice, since a `MobDefinition`/`ItemDefinition`/`QuestDropRule`
is referenceable from any map/world, not Academy-specific:
`src/MapServer/Generated/GameData/Mobs/GeneratedMobs.cs`,
`GameData/Items/GeneratedItems.cs`, `GameData/Quests/GeneratedQuestDrops.cs`.
Only the genuinely world-scoped placement data —
`MobSpawnDefinition` ("spawn `GeneratedMobs.GPoring` on this map with this
count/respawn") — stays under `src/MapServer/Generated/World/Izlude/Academy/
AcademyMobSpawns.cs` (`int_land01`-`int_land04`, `int_land` base excluded —
matching how Captain Carocc/Lumin already exclude it), referencing the global
mob definition rather than duplicating it. `AcademyMobSpawnRegistration.cs`'s
`world.AddMobSpawn(...)` loop over `AcademyMobSpawns.GPoringSpawns` is
hand-composed (not compiler output, no `<auto-generated>` header) and
deliberately kept out of `AcademyWorld.cs`: `compile-npc-world`'s
`NpcWorldEmitter` only knows NPC/warp source parsing today and is verified
byte-for-byte reproducible against the pinned source
(`WorldDataImporter.Tests.CompilerTests.
RealAcademyWorld_GenerationIsDeterministicAndMatchesCompiledAcademyTree`);
extending that emitter to also emit mob-spawn registrations, or putting a
hand-written line inside its otherwise-compiler-output file, was judged
disproportionate/would break that reproducibility guarantee for one
registration loop.

Runtime (`src/MapServer/World/`): `MobDefinition`/`MobSpawnDefinition`
(immutable, generated) vs. `MobInstance` (mutable runtime HP/lifecycle,
`Alive`/`Dead`, atomic/idempotent damage-and-death under one lock).
`WorldRegistryBuilder.AddMobSpawn` collects generated spawns into
`WorldRegistryBuildResult.MobSpawns` alongside the existing `Entities`/
`Scripts` fields, so `MonsterRegistry` spawns from the *same*
`WorldRegistryBuilder.Build()` result `WorldMapRegistry.Tutorial` itself is
built from (via `GeneratedScriptRegistry.MobSpawns`), rather than reading
generated data directly and bypassing the builder.

`MapServerWorld.Build()` (`src/MapServer/World/MapServerWorld.cs`) is the
explicit composition root: it constructs **one** `WorldActorIdAllocator` and
passes it to both `WorldMapRegistry.LoadGenerated(allocator)` and
`MonsterRegistry`'s constructor, so NPC/warp actors and monster actors share
one real actor-ID namespace — matching rAthena's own single NPC/monster
domain — instead of two independently-numbered allocators that could
collide. `MapServerApp.RunAsync` calls `MapServerWorld.Build()` once at
startup and threads the result through `MapTcpServer` into every
`MapClientSession` it constructs; that live path never falls back to
`WorldMapRegistry.Tutorial` (which remains available only for existing
tests/legacy standalone callers that don't combine world data with a monster
runtime, since it builds its own private, unshared allocator).
`MonsterRegistry` itself is **not** a static singleton like
`WorldMapRegistry.Tutorial`: unlike that class's genuinely immutable
definition data, a `MonsterRegistry` holds live mutable runtime state (each
`MobInstance`'s HP/alive-dead/respawn timers), so it is constructed once at
startup and passed down explicitly rather than hidden behind a lazy static
property.

`BasicAttackCalculator` is the pure damage-calculation function described
above. `MonsterCombatCoordinator` is the single authoritative
attack→damage→(exactly-once)death→quest-drop→respawn-scheduling transition.
When the authenticated session's accepted hit causes that transition, the session
uses the same `CharacterProgressionService` as generated NPC `getexp`: raw
`MobDefinition.BaseExp`/`JobExp` receive battle Base/Job rates, the one complete
state mutation is acknowledged by CharServer, then `IroCharacterProgressionPackets`
emits state/gain/level-up packets before the dead actor's `0x0080` removal. The
current combat slice has one authoritative attacker/session recipient and invents
no party or damage-contribution policy. Generated zero/zero EXP returns no mutation
and no progression packets; G_PORING therefore retains the capture-proven zero-EXP
death and quest-specific Wood path.
`QuestDropResolver` is generic/data-driven over `GeneratedQuestDrops.All`, not
quest-21008-specific code, and is kept **pure and synchronous**: it takes a
`Func<uint, CharacterQuestStatus>` — an already-resolved, in-memory snapshot
lookup for just the quest IDs its generated rules mention — rather than an
`ICharacterQuestPersistence` reference or a materialized "all active quest
IDs" collection. Athena's runtime has no such materialized set anywhere
(every real quest check in `MapClientSession` is single-quest-ID-scoped via
`ICharacterQuestPersistence.GetQuestStateAsync`), so the resolver's caller is
responsible for resolving each relevant quest ID through the real persistence
interface beforehand and closing over the result — keeping CharServer/
persistence I/O entirely outside the pure calculation.

`CharacterInventorySession`/`ICharacterInventoryPersistence` +
`MapInventoryAddProtocol` (new authenticated MapServer↔CharServer request/
response pair, `0x2b31`/`0x2b32`, mirroring `MapQuestStateProtocol` exactly)
add a real `CharInventory` row (find-existing-stack-or-create, then persist)
through the same "calculate proposed mutation → persist → only report success
on acknowledgement" rule `CharacterHealService`/`CharacterProgressionService`
already follow. Respawn uses `TimeProvider`-driven due-time checks
(`MonsterRegistry.ProcessDueRespawns`), not one `Timer`/`Task.Delay` per
monster.

Now wired end-to-end on the live client path (`feature/poring-live-wire`,
verified against `kill-poring-heal-jobup.pcapng` - see `ai/iro-2026-wire.md`).
`MapClientSession.HandleIroAttackRequestAsync` parses the proven `0x0437`
request, resolves the target through the existing `MonsterRegistry`, and
calls the existing unmodified `MonsterCombatCoordinator.Attack` - no damage
calculation happens in networking code. Results are represented (not
recalculated) by `IroMonsterCombatPackets` (`0x08C8` damage, `0x0080`
death, `0x0B41` item pickup) and `IroMonsterActorPackets` (`0x09FF` monster
appearance, distinct from the NPC/warp `IroWorldActorPackets` serializer -
see the evidence section above for why). `MapServerWorld.Build()` now also
composes one `MonsterCombatCoordinator` (over the same shared
`MonsterRegistry`/`WorldActorIdAllocator`) and threads it into
`MapClientSession` alongside the pre-existing `MonsterRegistry` field.
Monster visibility (`0x09FF`) is sent from the same call sites that already
send NPC/warp visibility (post-`0x007D` map-load, post-movement), sharing the
existing `_visibleActorIds` dedup set. Monster actor IDs are never routed
into NPC script dispatch (`_worldMapRegistry.TryGetInteraction` simply never
matches a monster ID, since monsters were never registered there - not a new
exclusion rule). Respawn re-visibility reuses the existing
`MonsterRegistry.ProcessDueRespawns`/`0x09FF`-emission path the next time a
session sends visibility to a map containing the respawned instance; no new
scheduler was added, and this specific behavior is pinned-source-inferred
rather than independently capture-verified (see evidence section above).

The captured `0x0B41`'s `Index` field required one small, deliberately
minimal protocol extension: pinned `client_index()` (`clif.cpp:122-124`) is
server-side inventory array position + 2, and neither Athena's
`CharInventory` schema nor real rAthena's own SQL schema persists that
position as a column - it is derived from row-insertion order at grant time.
The internal MapServer<->CharServer `0x2b31`/`0x2b32` protocol
(`MapInventoryAddProtocol`) and `ICharacterInventoryPersistence`/
`CharacterInventorySession`/`InventoryAddResult` now carry an additional
`SlotIndex` (server-side, 0-based) alongside the pre-existing
`Success`/`NewAmount` fields; the `+2` wire transform is applied only at the
point `MapClientSession` serializes `0x0B41`, not inside the persistence
layer.

### Inventory persistence guarantees — precise scope

Proven: `MobInstance.ApplyDamage`'s lock ensures at most one
`KilledByThisHit=true` per monster death, so at most one quest-drop-award
*attempt* originates from any single death (`MonsterCombatCoordinatorTests`).

NOT claimed, and explicitly out of scope for this branch:
- **General inventory-mutation idempotency.** `CharServerConnector`'s
  `_pendingInventoryAdds` dictionary is keyed by `(charId, itemId)` and its
  `TryAdd` rejects (returns failure to the caller) a second concurrent
  request for the same key while one is already in flight — this prevents
  silent corruption/double-counting for concurrent same-character-same-item
  requests, but it does so by **failing** the second request outright, not by
  queuing or merging it. In ordinary single-player play this situation does
  not arise from one character's own actions (`MapClientSession` processes
  one TCP session's packets sequentially, so two `Attack` calls for the same
  character never execute concurrently), but two *different* monsters dying
  at nearly the same real-world instant for the same character (e.g. from
  hypothetical future multi-monster-engagement gameplay) is a genuine,
  undefended concurrency edge case this branch does not add new handling for.
- **Commit-then-ack-loss.** If CharServer's DB commit for an inventory-add
  succeeds but the TCP response never reaches MapServer (e.g. a dropped
  connection), MapServer has no record the item was actually granted and no
  operation/idempotency key to reconcile on a retry. No distributed
  idempotency protocol is added in this branch for that case.
- **DB-integration testing.** `HandleInventoryAddRequestAsync`'s EF Core
  find-or-create/increment logic is exercised only indirectly, through
  `CharacterInventorySessionTests`' fake-persistence unit tests and
  `CharacterPositionPersistenceTests`' authorization-logic tests. This
  repository has no precedent anywhere for integration-testing a CharServer
  packet handler against a real (in-memory or SQLite) `CharDbContext` — the
  only DB-touching CharServer test file (`CharQuestModelTests.cs`) inspects EF
  compiled-model metadata and never executes a real query — so this branch
  does not introduce that pattern either; the EF query/mutation path itself
  is unverified against a real database.

Expand the runtime only through tested vertical slices; do not restore bulk JSON
runtime data as a shortcut.

## Map geometry: direct pinned rAthena map_cache.dat import (normal path)

Athena's NORMAL source of map geometry/collision data is pinned rAthena's own
`legacy/rathena/db/map_cache.dat`, read directly at MapServer startup —
**not** a manually extracted client `.gat` file, and **not** an offline
per-map conversion step. rAthena already ships this server-side dataset
(map dimensions + per-cell static terrain, itself built from client
GAT/RSW resources by rAthena's own offline `src/tool/mapcache.cpp`) inside
the pinned checkout, so a developer never needs an installed Ragnarok
client, a GRF, or `.gat` extraction to get real map geometry into Athena.

This pinned `map_cache.dat` is **server-side reference data from the pinned
rAthena version**, not a claim of authoritative current-iRO map content —
Athena's stock-iRO wire-authority rules (`ai/iro-2026-wire.md`) are
unchanged by this section; nothing here asserts a captured/verified iRO
fact.

### Runtime architecture

```
legacy/rathena/db/map_cache.dat  (pinned reference data, read as-is)
        |
RathenaMapCacheReader.ReadAll/ReadAllFromFile   (src/MapServer/World/RathenaMapCacheReader.cs)
        |  (parses the pinned container format directly into runtime types —
        |   no intermediate Athena artifact/container format for this path)
        v
MapCollisionMap[]  (existing runtime type, reused verbatim)
        |
MapCollisionStartupLoader.Load(artifacts, mapCachePath)
        v
IMapCollisionProvider  (MapCollisionProvider, keyed by each map's own real name)
```

`RathenaMapCacheReader` reads and decompresses the whole file exactly once,
at MapServer startup — never per session, never per lookup. The resulting
`MapCollisionMap` instances are immutable and shared for the server's
lifetime, exactly like every other startup-composed dependency
(`GameplayRuleServices`, `MonsterRegistry`, etc.). Gameplay code
(`MapClientSession`, `MonsterRegistry`, pathfinding) only ever sees the
generic `IMapCollisionProvider`/`MapCollisionMap` abstractions — nothing
outside `RathenaMapCacheReader`/`MapCollisionStartupLoader` knows the pinned
binary layout exists.

No alias mechanism is needed for this source: pinned `map_cache.dat` already
declares `int_land`/`int_land01`/`int_land02`/`int_land03`/`int_land04` as
five separate, independent, real records (confirmed by direct inspection of
the pinned file, not inferred from `legacy/openkore`'s client-side resource
table) — each logical Athena map name this project uses already has its own
row in the pinned cache with real geometry. The `.gat`/`.athmap` alias
mechanism described later in this document exists only for that secondary
path, where a single physical client resource genuinely is shared across
several logical map names; it does not apply here. (Not every map name
Athena might eventually want has a record in the shipped `map_cache.dat` —
e.g. plain `prontera` IS declared in pinned rAthena's own map list
(`conf/maps_athena.conf:201`, `map: prontera`), but has no corresponding
record in this pinned checkout's `db/map_cache.dat`; `izlude` likewise has no
bare-name record, only instanced variants such as `izlude_a`/`izlude_in`.
This is a gap in the shipped, prebuilt cache file specifically — a cache
rebuild (`mapcache` tool, out of scope here) would need to run against real
client resources to add such a map — not an alias gap Athena's reader needs
to bridge, and not evidence that rAthena itself never declares that map.)

### Pinned reader/writer trace

Traced against `legacy/rathena` at `e985006171d2eb320ee512a653f4c83aea3d81b6`,
independently cross-checked against the real pinned `map_cache.dat` (1288
maps) byte-for-byte:

- `map.cpp:156-159` (`struct map_cache_main_header`) + `map.cpp:3672-3717`
  (`map_readfromcache`) + `map.cpp:3640-3666` (`map_init_mapcache` — whole
  file read into memory, no streaming): main header is `file_size` (`uint32`
  LE) + `map_count` (`uint16` LE), but the first real record starts at byte
  offset **8**, not 6 — ordinary C structure-alignment padding pinned
  `map_readfromcache` already accounts for via `sizeof(struct
  map_cache_main_header)` (`map.cpp:3677`) on its target platform/compiler.
  Confirmed against the real pinned file: with `map_count=1288`, every
  declared record length only lines up exactly against `file_size` when the
  first record starts at offset 8.
- `map.cpp:162-167` (`struct map_cache_map_info`), repeated `map_count`
  times back-to-back, each immediately followed by that record's own
  compressed payload (`map.cpp:3679-3687` walks entries by jumping `len`
  bytes past each record — not a fixed stride): `name` (12-byte NUL-padded
  ASCII, `MAP_NAME_LENGTH` = `mmo.hpp:163`, never includes a `.gat`
  extension), `xs`/`ys` (`int16` LE each), `len` (`int32` LE, the
  COMPRESSED payload's byte length).
- `grfio.cpp:245-255` (`decode_zip`/`encode_zip`): standard zlib
  `compress()`/`uncompress()` — an RFC 1950 zlib-wrapped deflate stream, not
  raw deflate or gzip. .NET's `ZLibStream` speaks this container directly.
- `map.cpp:3710-3711`: decompresses to exactly `xs*ys` bytes, one raw GAT
  cell-type byte per cell, in `x + y*xs` row-major order — the same flat
  index `MapCollisionMap.GetCell` already uses, so no coordinate transform
  is needed.
- `map.cpp:3280-3299` (`map_gat2cell`): the raw GAT type byte → static bit
  mapping (see "Pinned trace" below for the exact per-type semantics) —
  IDENTICAL to the direct-`.gat` path's mapping, because `map_cache.dat`'s
  payload is nothing more than the same raw GAT type bytes
  `src/tool/mapcache.cpp`'s `read_map` already extracts from a client
  `.gat`, zlib-compressed and bundled with every other map into one
  container. Both import paths are alternate INPUT ENCODINGS of identical
  underlying cell semantics, not two independent formats.
- `map_readfromcache` (`map.cpp:3692-3693`) treats `xs<=0`/`ys<=0` as "skip
  this record, keep scanning" because its pinned caller linear-scans a
  shared file for one specific map name and a malformed OTHER entry must
  not abort that search. `RathenaMapCacheReader` instead fails the WHOLE
  load loudly on any malformed record: Athena loads every map in one pass
  at startup rather than probing per name, so a malformed record is
  definitionally a corrupt input file, never "some other map I don't care
  about."

### Configuration

`map_cache_path: <path>` (`MapConfig.MapCachePath`/`MapConfigLoader`) is the
normal key — e.g. `map_cache_path: legacy/rathena/db/map_cache.dat` for
local development. A missing or malformed configured file fails MapServer
startup loudly (`MapCollisionStartupLoader` throws `InvalidOperationException`),
never silently falling back to `EmptyMapCollisionProvider`. Configuring both
`map_cache_path` and one or more `map_collision_artifact` lines is itself a
startup configuration error — `MapConfigLoader.Load` throws rather than
picking an implicit precedence, since silently choosing one source over the
other could hide a real operator mistake. Configuring neither key preserves
the original default: `EmptyMapCollisionProvider.Instance`, exactly as
before this section's runtime existed.

No production packaging/copy step (e.g. bundling `map_cache.dat` into a
published MapServer output directory) exists yet — `map_cache_path` today
points at a local filesystem path (typically directly at the pinned
submodule checkout for development). Revisit packaging when a real
production deployment target requires MapServer to run without the
`legacy/rathena` submodule present.

### Still missing

Same as the "Still missing (explicitly deferred)" list at the end of this
document — this section only proves Athena *knows* real map geometry now.
Random monster spawn, pathfinding, movement, and wandering are unchanged and
still do not consume this data.

## Investigation (in progress): G_PORING spawns visually on water/mountain on generic `int_land`

Live testing against the unmodified current iRO client (PACKETVER 20220406) reported that some
generic-`int_land` G_PORING spawns visually appear on water/mountain/unreachable-looking terrain.
**This is not yet resolved** - the investigation below establishes one data point and the
diagnostic tooling needed to pin down the actual suspect instance; it does NOT yet establish
whether pinned `map_cache.dat` and the current iRO client's real geometry disagree.

### What is established so far

Ten G_PORING coordinates sampled from the 2026-08-27 runtime startup log (all visible spawns on
generic `int_land` at that moment, not confirmed to include the specific instance that looked wrong
on screen) all decode to raw GAT type **0** (plain `Walkable`, not `Water`, not a wall) in the real
pinned `legacy/rathena/db/map_cache.dat` `int_land` record: `(63,69)`, `(69,70)`, `(68,53)`,
`(74,58)`, `(65,61)`, `(75,71)`, `(68,60)`, `(56,61)`, `(70,72)`, `(77,53)`. None are type 3
(`Walkable|Water`). This rules out an Athena reader/coordinate bug for these ten specific
coordinates (the reported flags exactly match what `map_cache.dat` itself stores there) and rules
out "landed on a Water cell" for these ten specifically - but **it has not been proven that any of
these ten is the actual instance the tester saw standing on water/mountain**. A screenshot
correlates a visual position to a report, not to one of these ten sampled coordinates.

Separately, `int_land` itself is a small, mostly-blocked map: 19600 total cells, 16801 type 1
(wall, ~86%), only 2742 type 0 (walkable, ~14%), and 57 type 3 (walkable water) - so a uniform
random pick among all `IsTraversalCell` cells is inherently confined to that narrow ~14% walkable
footprint, which will look visually tight/scattered regardless of RNG behavior. This is
context, not a conclusion about the reported instance.

### What remains to be done

The next step is to use the diagnostic tooling below to identify the SPECIFIC actorId a tester
observes as visually wrong (by hovering/clicking it in the stock client) and inspect its exact
live cell state. Only once that specific instance's coordinate and cell flags are known can the
three-way classification (A: Athena bug / B: matches pinned source, including possibly Water / C:
pinned-source-vs-current-iRO-client mismatch) be applied to the actual reported instance rather
than to an unrelated sample. Settling case C specifically would additionally need either: the live
stock iRO client refusing/redirecting a click-to-move request onto that exact coordinate (a real
client pathing refusal onto a cell pinned `map_cache.dat` marks walkable would be concrete evidence
of a genuine source-version mismatch), or a real client-side `.gat`/`.rsw` extraction for
`int_land` compared cell-by-cell against the pinned record.

No blanket "forbid Water", "require grass", hardcoded region, or invented "same connected
component" spawn-legality rule has been added while this remains open, since pinned
`map_search_freecell`'s normal (non-`flag&2`) call path (used by ordinary `mob_spawn`,
`battle_config.no_spawn_on_player?4:0`, never `2`) only checks the individual candidate cell's own
`CELL_CHKREACH`, never `unit_can_reach_pos`/connected-component reachability from an anchor point
(`map.cpp:1798-1867`, `mob.cpp:1149`) - inventing a stronger rule here would itself be an unproven
deviation from pinned semantics, and would do so before the actual reported instance has even been
identified.

### Diagnostics added

- `RathenaCompatibleMobSpawnCellSelector.TrySelectCell` logs `[iRO MAP DEBUG][MONSTER CELL]` for
  every accepted initial-spawn or respawn cell (mob AegisName, map, x/y, raw `MapCellFlags`, and
  the four derived predicates) - useful for seeing what was CHOSEN at spawn/respawn time, but
  cannot answer "what is at actorId N right now": `WorldActorIdAllocator` assigns the real actorId
  in `MonsterRegistry`'s constructor only AFTER `TrySelectCell` already returned a position, so the
  selector itself never observes the actorId.
- `MonsterSpatialInspector` (`src/MapServer/World/MonsterSpatialInspector.cs`) is the small,
  reusable, READ-ONLY spatial-inspection capability that closes that gap: given an actorId and map
  name, it resolves the live `MobInstance` via `MonsterRegistry.TryGetInstance`, reads its CURRENT
  `GetPosition()` (reflecting the latest respawn, not the original spawn), and looks up that exact
  cell's static state via the already-composed `IMapCollisionProvider` - never re-parsing
  `map_cache.dat`, never threaded into `MonsterRegistry` itself merely for logging. Composed once
  in `MapServerWorld.Build` alongside the rest of the live world.
- `MapClientSession`'s existing proven `0x0368` actor-info-request handler (already used for
  monster-name lookup on click/hover) now calls `MonsterSpatialInspector.TryDescribe` for the
  clicked/hovered actorId and logs the same `[iRO MAP DEBUG][MONSTER CELL]` line - this is the live
  flow a tester uses to identify one specific visually-suspicious instance: stock-client
  hover/click -> `0x0368` -> actorId -> `MonsterSpatialInspector` -> position + cell flags -> log
  line, entirely independent of whether the player can physically walk there.
- All of the above are diagnostic-only: none change spawn eligibility, cell semantics, or any
  gameplay behavior.

## Map collision data import + runtime collision foundation (secondary/debug tooling)

This section documents an ALTERNATE, secondary collision-data import path —
a direct `.gat` → Athena artifact (`.athmap`) compiler — kept available for
debugging the format, synthetic tests, or a map genuinely absent from the
pinned `map_cache.dat`. **It is not the normal path**; see the
`map_cache.dat` section above for that. Nothing in current MapServer startup
requires an `.athmap` file: an operator configures either `map_cache_path`
or `map_collision_artifact` lines, never both.

### Pinned trace

Traced against `legacy/rathena` at `e985006171d2eb320ee512a653f4c83aea3d81b6`:

- `map.hpp:788-810` (`struct mapcell`): the pinned STATIC terrain state is exactly three bits —
  `walkable`, `shootable`, `water` — kept architecturally separate from eight DYNAMIC runtime bits
  on the same struct (`npc`, `basilica`, `landprotector`, `novending`, `nochat`, `maelstrom`,
  `icewall`, `nobuyingstore`). Athena's imported artifact/runtime model preserves only the static
  three; the dynamic bits remain an unmodeled MapServer runtime concern, never part of imported
  data.
- `map.cpp:3323-3395` (`map_getcellp`): every static `cell_chk` value used by spawn/pathfinding
  code is fully derivable from those three bits alone — `CELL_CHKWALL = !walkable && !shootable`,
  `CELL_CHKWATER = water`, `CELL_CHKCLIFF = !walkable && shootable`, `CELL_CHKPASS`/`CELL_CHKREACH
  = walkable`, `CELL_CHKNOPASS`/`CELL_CHKNOREACH = !walkable` (the `CELL_NOSTACK` build-time
  stacking-limit refinement on `CHKPASS`/`CHKNOPASS` is not modeled, matching a non-default rAthena
  build option). This proves one byte per cell (not one walkability bit) is the correct minimum —
  collapsing to a single bit would silently discard the water/shootable distinction real source
  code branches on.
- `map.cpp:3280-3299` (`map_gat2cell`/`map_cell2gat`): the raw GAT type ↔ static-bit mapping. GAT
  types 0/2/4/6 → `Walkable|Shootable` (2/4/6 are rAthena's own "???"/unused-but-present types,
  behaviorally identical to plain ground); type 1 → none (wall); type 3 →
  `Walkable|Shootable|Water`; type 5 → `Shootable` only (a snipable gap/cliff).
- `src/tool/mapcache.cpp:68-116` (`read_map`): the offline mapcache-building tool's own `.gat`
  reader — 6-byte file signature, `width`/`height` as little-endian `uint32` at offsets 6/10, then
  one 20-byte record per cell (4 unused corner-height floats + a little-endian `uint32` GAT type at
  the record's `+16` offset). rAthena's own reader never validates the signature bytes; Athena's
  importer does, as a "fail clearly on malformed input" strengthening, not a ported rAthena check.
  The mapcache tool also folds in a `.rsw` water-height adjustment (`mapcache.cpp:107-108`,
  promoting a walkable-but-below-water-level cell to type 3) that Athena's importer does not
  reproduce — a disclosed, narrow omission, not a scope failure (a `.gat` cell already encoded as
  type 3 is unaffected).

Direct `.gat` was chosen as the import input over rAthena's multi-map, zlib-compressed,
GRF-dependent `map_cache.dat` container: a single `.gat` file is self-contained, requires no
external GRF/RSW tooling, and every static semantic Athena needs (the three `mapcell` bits) is
present in it directly.

### Format/artifact boundary

```
local .gat file (never committed)
        |
tools/WorldDataImporter  compile-map-collision
        |  (MapCollisionCompiler: .gat -> CompiledMapCollision;
        |   MapCollisionArtifactWriter: CompiledMapCollision -> bytes)
        v
local Athena collision artifact, "*.athmap" (never committed)
        |
MapServer  (MapCollisionArtifact.Read: bytes -> MapCollisionMap)
        v
IMapCollisionProvider (runtime, immutable after load)
```

`WorldDataImporter` has no project reference to `MapServer` (an explicit constraint for this
slice), so the compiled-data type (`CompiledMapCollision`/`CompiledMapCellFlags`) and its writer
(`MapCollisionArtifactWriter`) live entirely in `tools/WorldDataImporter/Compiler/` and are
independent of the MapServer-side reader (`MapCollisionArtifact.Read`) and runtime type
(`MapCollisionMap`/`MapCellFlags` in `src/MapServer/World/MapCollision.cs`). The two sides agree on
one binary layout (magic `"AMC1"`, map-name length + UTF-8 name, `int32` width/height, then
`width*height` cell bytes) and are kept in sync by
`MapCollisionCompilerTests.MapCollisionRoundTrip_MatchesRuntimeReader`, which decodes the writer's
own output against the exact byte layout the runtime reader expects.

CLI usage:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile-map-collision \
    --input /local/path/int_land03.gat \
    --map int_land03 \
    --output /local/generated/int_land03.athmap
```

### Runtime API

`src/MapServer/World/MapCollision.cs`:

- `[Flags] MapCellFlags : byte { None, Walkable, Shootable, Water }`
- `MapCollisionMap` — immutable per-map grid: `MapName`, `Width`, `Height`, `IsInBounds(x,y)`,
  `GetCell(x,y)` (throws `ArgumentOutOfRangeException` for an out-of-bounds cell — this is
  deliberately NOT the same outcome as a genuinely blocked in-bounds cell), plus `IsWalkable`/
  `IsShootable`/`IsWater` convenience wrappers over `GetCell`.
- `IMapCollisionProvider { bool TryGetMap(string mapName, out MapCollisionMap map) }` —
  `TryGetMap` returning `false` for an unknown map is a distinct outcome from a known map's blocked
  cell; callers must not conflate "no data for this map" with "this cell is blocked".
- `EmptyMapCollisionProvider.Instance` — the current production default; resolves no map at all.
- `MapCollisionProvider` — simple immutable in-memory provider, case-insensitive-ordinal map-name
  lookup (matching every other map-name comparison in this codebase). Two constructors: one keyed
  directly by each map's own `MapName` (the common "each map is its own resource" case), and one
  taking an explicit logical-name → `MapCollisionMap` dictionary so several logical names can
  share exactly one loaded map/cell array (see "Logical map name -> physical client collision
  resource" below) — `MapCollisionStartupLoader` uses the latter.
- `MapCollisionStartupLoader.Load(IReadOnlyList<MapCollisionArtifactConfig>)`
  (`src/MapServer/World/MapCollisionStartupLoader.cs`) — the only place a real artifact file is
  read from disk; see "Composition/ownership" below.

### Logical map name -> physical client collision resource

Athena's Academy world declares five separate LOGICAL map names for the tutorial G_PORING slice -
`int_land`, `int_land01`, `int_land02`, `int_land03`, `int_land04` (`npc/re/mobs/int_land.txt:11-15`,
`AcademyMobSpawns.cs`, `AcademyNavigation.cs`). These are genuinely separate server-side map
instances (distinct NPC placements, distinct navigation targets, distinct monster spawn groups),
but they are NOT five separate physical client map resources. `legacy/openkore`'s
`resnametable.txt` - checked across every regional client table that carries an int_land entry
(bRO, tRO, kRO Zero, kRO Sakray, aRO, ROla, cRO, laRO, translated kRO_english; the iRO-specific
table has no int_land entry of its own, but every other regional table agrees) - unanimously
remaps `int_land01.gat`/`int_land02.gat`/`int_land03.gat`/`int_land04.gat` (and the matching
`.gnd`/`.rsw`) to the single physical resource `int_land.gat`. This independently confirms
`ai/map-server.md`'s own earlier finding for `int_land01` specifically ("resource tables alias it
to `int_land`") and extends it to all five logical names. Consequently: importing ONE `int_land.gat`
file produces collision data valid for all five logical Athena map names, and the runtime must
share that one loaded `MapCollisionMap`/cell array across all five logical registrations rather
than importing (or duplicating in memory) five copies.

### Composition/ownership

`MapServerWorld.Build` (`src/MapServer/World/MapServerWorld.cs`) takes an optional
`collisionProvider` parameter, defaulting to `EmptyMapCollisionProvider.Instance` (the same
behavior as before this section existed). `MapServerWorld` carries a `Collision` property
alongside `Maps`/`Monsters`/`Combat`. `MapClientSession` does not open files and has no path to
`.gat`/artifact parsing.

`MapServerApp.RunAsync` calls `MapCollisionStartupLoader.Load(mergedConfig.CollisionArtifacts,
mergedConfig.MapCachePath)` once, at the same composition point every other startup dependency
(gameplay rules, the monster registry, etc.) is built, and passes the result into
`MapServerWorld.Build`. `MapCollisionStartupLoader.Load` now branches on which of the two mutually
exclusive sources is configured (see the `map_cache.dat` section above for the normal
`map_cache_path` branch); this artifact-based branch is what runs when one or more
`map_collision_artifact: <path>|<map1>,<map2>,...` lines are configured instead. Zero configured
lines/path is still the default and still resolves to `EmptyMapCollisionProvider.Instance` -
unconfigured startup behavior is unchanged. This branch reads each configured artifact file exactly
once via `MapCollisionArtifact.ReadFile` and registers the SAME loaded `MapCollisionMap` instance
under every logical map name listed for that artifact (never re-parsing the file per alias, never
duplicating the cell array) - this is how the `int_land`/`int_land01..04` aliasing above is served
without five in-memory copies. A configured artifact that is missing, malformed, or whose logical
map name collides with another artifact's registration makes `MapCollisionStartupLoader.Load`
throw `InvalidOperationException`, failing MapServer startup outright rather than silently running
with partial/absent collision data an operator believed was loaded.

### Local/gitignored data strategy and licensing

Per this project's Gravity-asset rule, BOTH the proprietary source `.gat` file and any real
collision artifact derived from real client map data must stay local and gitignored — never
committed, in this branch or any future one, unless a separate, explicit licensing decision is
made. Nothing under version control in this repository is or references real Gravity map bytes;
every test fixture is a tiny synthetic byte array built in-test (see `MapCollisionCompilerTests`/
`MapCollisionTests`/`MapCollisionStartupLoaderTests`). No real `.gat`/GRF/client installation was
found anywhere in this development environment as of this writing (checked common local paths,
`~/Downloads`, and a full filesystem search for `.gat`/`.grf`) - the real-map validation this
section's runtime plumbing exists to support (real dimensions, cell counts, and known-coordinate
sanity checks against `int_land.gat`) remains an outstanding manual step for whoever has legitimate
access to a real client installation; see the developer-facing instructions in
`conf/templates/map_athena.conf`.

### Pinned traversal-boundary distinction (raw artifact bounds vs. gameplay bounds)

`MapCollisionMap.IsInBounds`/`GetCell` expose the RAW imported bounds: every `(x,y)` with
`0 <= x < Width` and `0 <= y < Height` is readable, including the final row/column, and returns
the map's real stored terrain byte there. This is deliberately narrower than nothing and wider
than pinned rAthena's own gameplay-traversal check: `map_getcellp` (`map.cpp:3329-3331`,
`"NOTE: this intentionally overrides the last row and column"`) treats `x >= xs-1` or `y >= ys-1`
as always `CELL_CHKNOPASS`/never-`CELL_CHKREACH` for traversal purposes, regardless of that cell's
real stored value. A future spawn-selection/pathfinding/`CELL_CHK*`-equivalent consumer built on
top of this artifact must apply that `x < Width-1` / `y < Height-1` restriction itself; narrowing
`MapCollisionMap`'s own bounds to hide the final row/column would silently discard genuine
imported terrain data from every caller, not just gameplay-traversal ones (see
`MapCollisionTests.RawArtifactBounds_IncludeTheFinalRowAndColumn_UnlikePinnedGameplayTraversalBounds`).

### Still missing (explicitly deferred)

- GAT-backed random monster spawn (`map_search_freecell`/`CELL_CHKREACH`) — `MobSpawnCellSelector`
  is unchanged; `UnverifiedFallbackMobSpawnCellSelector` remains the only spawn-cell source.
- A* pathfinding (`path_search`/`CELL_CHKNOPASS`) — `MovementPathProvider` is unchanged;
  `UnverifiedGridLineMovementPathProvider` remains the only path source.
- Collision-aware player movement.
- Passive monster wandering (`mob_randomwalk`/`CELL_CHKPASS`) — not implemented; `MobInstance.X/Y`
  remain immutable after construction.
- Monster chasing/combat AI/retaliation.
- Proactive world-to-session broadcast (a monster's own state change reaching an already-connected
  session without that session first acting) — still does not exist anywhere in MapServer.
- A real, developer-verified `.gat` import and coordinate sanity check for THIS (secondary)
  `.gat`/`.athmap` path specifically — its startup loading branch (`MapCollisionStartupLoader`/
  `map_collision_artifact` config) is implemented and tested against synthetic fixtures, but has
  not yet been exercised against a real client resource in this environment (none is available -
  see "Local/gitignored data strategy" above). This is no longer the blocking gap for real map
  geometry in general: the `map_cache.dat` section above proves real geometry (including
  `int_land`/`int_land01..04`) via the normal `map_cache_path` source, tested against the actual
  pinned `legacy/rathena/db/map_cache.dat`.
