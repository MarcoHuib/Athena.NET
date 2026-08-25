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
converter returns its lossless result. This is how the Academy slice keeps
Captain Carocc and Lumin actor-only today even though pinned rAthena contains
real, non-trivial click dialogue for both — their scripts deliberately remain
unregistered pending real healing/EXP/status-effect/inventory runtime support,
which is an emission-time decision, not something the converter encodes.

`WorldRegistryBuilder.AddNpc(NpcDefinition, IReadOnlyList<NpcPlacement>)` is
the runtime registration entry point generated `AcademyWorld.Register(builder)`
calls; it lowers the definition/placement pair back into today's
`WorldEntityDefinition`/`GeneratedScriptRegistration` shapes internally, so
`WorldMapRegistry`, `ScriptContext`, and `MapClientSession` require no changes.
Hand-written custom NPC content uses the identical `AddNpc` API. A definition
with zero behaviors still contributes its `WorldEntityDefinition`s to the
built world (proven by `WorldRegistryBuildResult.Entities`, independent of
whether `WorldRegistryBuildResult.Scripts` has any registration for it) — this
is how actor-only NPCs like Captain Carocc/Lumin remain visible without a
script registration.

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
  --output src/MapServer/Generated/World/Izlude/Academy/AcademyWarps.cs
```

Generate the current Academy NPC definitions/placements/behaviors: see the
`compile-npc-world` command in `tools/WorldDataImporter/README.md`.

Compiler audit/capability reports may still scan the complete pinned NPC tree;
their breadth does not imply runtime support.

## Still missing

The minimal `iz_int03` slice now also includes compiler-generated navigation targets, both Wounded Swordsman actor states/scripts, and definitions for the pinned `int_land03` Captain Carocc and Lumin duplicates. Captain Carocc's real pinned dialogue/quest/heal/status/EXP script is registered and executable, using the generic heal, temporary-status, and existing quest/progression runtime capabilities described below. Lumin remains actor-only: its script stays unregistered until real inventory runtime support exists; no no-op gameplay commands are used.

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
- Live client-facing monster spawn/attack/death/item-acquisition wire packets
  (domain runtime exists; the network path is not wired - see "Monster combat
  and quest drops" below).
- Broader event/state-machine lowering and persistent rAthena variable scopes.

## Monster combat and quest drops (G_PORING / quest 21008)

### Verified stock-iRO evidence

None found for combat itself. The supplied
`npc-interaction-npc's_v2.pcapng`/`npc-interaction-heal-action.pcapng`
captures (already cited above and in `ai/iro-2026-wire.md`) contain movement,
warps, and extensive NPC/tutorial dialogue, but **no attack-initiation
packet, no damage packet, no monster-death packet, and no item-acquisition
packet correlated to a monster actor**. `ai/iro-2026-wire.md`'s own "Verified
NPC dialogue evidence" section already states this explicitly: "Quest
traffic, combat, and item acquisition in this capture remain future evidence
without runtime support." This branch did not find a stronger capture. The
only reusable *proven* wire fact for monster visibility is `0x09FF`
(`ZC_NOTIFY_STANDENTRY`)'s field layout for a WARPNPC actor (object type `6`,
speed sentinel `300`, HP sentinels `0xFFFFFFFF`) — and that layout is
**object-type-specific**: pinned rAthena (`clif.cpp:1200` et al.) sends
`objecttype = 0x5` (`NPC_MOB_TYPE`) for a real monster, with real current/max
HP, not the `6`/sentinel-HP shape Athena's existing `IroWorldActorPackets.
BuildWorldActor` already sends for NPCs/warps. Reusing that serializer
unchanged for a monster would not be proven; a real monster-visibility packet
is left as an explicit, isolated evidence gap rather than invented.

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

Not wired: no client packet handler drives `MonsterCombatCoordinator.Attack`
from a live socket (no verified attack-request packet ID/layout exists), and
no monster-specific `0x09FF` variant is sent to make a spawned instance
visible to a real client (see evidence gap above). `MapClientSession` now
carries an optional `MonsterRegistry` field (populated on the live path via
`MapServerWorld`, `null` on the test-facing constructor's default), but no
packet handler reads it yet.

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
