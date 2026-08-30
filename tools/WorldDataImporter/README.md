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
| `int_land/Captain Carocc#intro_npc03`, `int_land/Lumin#new_ship` + duplicates | `Academy/AcademyWorld.cs`, `Academy/AcademyNpcs.cs`, `Academy/Scripts/*.cs` | `WorldRegistryBuilder` + `ScriptContext` | generated dialogue/quest/runtime integration | Yes; no runtime JSON existed |
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
  --name 'Sailor#intro_npc04' \
  --warp-name '#ship_out' \
  --warp-name '#intro_to_izlude' \
  --namespace Athena.Net.MapServer.Generated.World.Izlude.Academy \
  --rathena-commit e985006171d2eb320ee512a653f4c83aea3d81b6 \
  --output-dir src/MapServer/Generated/World/Izlude/Academy
```

No `--exclude-placement`/`--warp-exclude-placement` flags: every generic/base
template placement (`iz_int`/`int_land`, not just their `01`-`04` instanced
duplicates) is a genuinely valid member of the same pinned `duplicate(...)`
family and must be emitted. An earlier version of this command excluded the
generic `iz_int`/`int_land` template placements entirely (treating them as if
only the numbered duplicates were "real" content) — this made the generic
`iz_int` tutorial variant (one of five maps `start_point` can place a new
character on) silently incomplete: no visible Wounded Swordsman, no
`#ship_out` exit, and downstream no `#intro_to_izlude`/Captain
Carocc/Lumin on generic `int_land` either. That was a regeneration-selection
bug, not an intentional narrower slice — see
`WorldMapRegistryFamilyTests` (`tests/MapServer.Tests/World/`) for the
regression coverage across all five `iz_int`/`int_land` family members.

Lumin's real pinned click behavior is registered on every
`int_land`/`int_land01..04` placement. Its generated script uses the generic
dialogue/continuation, quest, cutin, cloak-state, and `strcharinfo(0)`
capabilities; the active character name comes from authenticated CharServer
state. The pinned `Wounded Swordsman#intro_npc01_iz_int` OnTouch body still
isn't lowerable (a `sleep2` timer construct) — the compiler itself skips it
automatically (no exclusion flag needed) and only its OnClick
("Lying"/cloak-toggle) behavior is emitted; this is a separate, still-open
lowering-capability gap, not a placement issue.

`--rathena-commit` must be the value of the pinned `legacy/rathena` gitlink
(`git submodule status`), never a stale/unrelated SHA — this value is stamped
into generated file comments and `WorldSourceInfo`/`NpcDefinition.Source`
provenance data for traceability, so it must always match what was actually
read from disk during this invocation.

Omitting `--exclude-placement`/`--warp-exclude-placement`/`--no-behavior`
entirely emits every placement and behavior the converter finds for the
selected `--name`/`--warp-name` templates — the normal, fully-reproducible-
from-source case. Use `--exclude-placement`/`--warp-exclude-placement` only
when a specific individual duplicate is NOT a valid member of the intended
generated slice (e.g. a map instance this MapServer build doesn't serve at
all) — never merely because a placement happens to be the generic/base
template of a `duplicate(...)` family.

`compile-npc-world` writes one area-level `AcademyWorld.cs` (one
`world.AddNpc(...)`/`world.AddWarpTrigger(...)` call per definition), one
area-level `AcademyNpcs.cs` (one `NpcDefinition` field per definition), one
area-level `AcademyWarpTriggers.cs` when `--warp-name` is given (one
`WarpTriggerDefinition` field per definition), and one `Scripts/*.cs` file per
unique executable behavior — no per-NPC generated fragments, no hand-maintained
registration list to edit when new content is added within the same
invocation's scope.

The current checked `AcademyNavigation.cs` (`navigateto(...)` targets for the
tutorial's opening/room-transition NPCs) is reproduced with:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile-navigation \
  --source-root legacy/rathena/npc/re/jobs/novice \
  --name 'iz_int#intro_start' --name 'iz_int01#intro_start' --name 'iz_int02#intro_start' \
  --name 'iz_int03#intro_start' --name 'iz_int04#intro_start' \
  --name 'iz_int#intro_evt02' --name 'iz_int01#intro_evt02' --name 'iz_int02#intro_evt02' \
  --name 'iz_int03#intro_evt02' --name 'iz_int04#intro_evt02' \
  --namespace Athena.Net.MapServer.Generated.World.Izlude.Academy \
  --output src/MapServer/Generated/World/Izlude/Academy/AcademyNavigation.cs
```

`--name` must list every instance whose navigation should be emitted,
including the generic/base `iz_int#intro_start`/`iz_int#intro_evt02` template
declarations, not just their `01`-`04` duplicates — an earlier version of
this invocation omitted the generic instances, silently leaving the generic
`iz_int` tutorial variant without navigation arrows (see the
`compile-npc-world` note above for the matching placement-side bug).
`--namespace` defaults to `Athena.Net.MapServer.Generated.World.Izlude` if
omitted; the checked-in file uses the `.Academy` sub-namespace to match every
other Academy-generated file. `compile-navigation` does not stamp a
`rathena-commit` value into its output (no `--rathena-commit` flag exists for
it) since `AcademyNavigation.cs` carries no provenance/commit metadata at all.

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

## Repository-wide compatibility analysis

The `analyze` command discovers pinned world declarations and evaluates each NPC
event through the same lexer, parser, semantic analyzer, and lowerer used by C#
generation. It is a read-only dry run: it does not emit runtime C#, modify the
pinned source, update a database, or contact a server.

The default `--scope runtime` analyzes the real runtime NPC tree (`npc/` when the
supplied root contains it). Documentation and samples therefore do not distort the
official compatibility baseline or roadmap. Use `--scope all` deliberately when
including `doc/` and other text sources for parser stress analysis:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- analyze \
  --rathena-root legacy/rathena \
  --scope all \
  --output artifacts/world-analysis-all
```

Run the complete analysis manually (it is intentionally not part of normal tests):

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- analyze \
  --rathena-root legacy/rathena \
  --output artifacts/world-analysis
```

Narrow investigations can repeat `--type` and can use `--map`, `--source`, and
`--source-context-lines`:

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- analyze \
  --rathena-root legacy/rathena \
  --output artifacts/world-analysis-izlude \
  --type npc --map izlude --source npc/re --source-context-lines 5
```

The output contains:

- `summary.json`: NPC/warp-scan totals (`NpcSourceFilesAnalyzed`/`NpcEventsAnalyzed`/`NpcCompatible`/
  `NpcUnsupported` - deliberately NPC-scope-prefixed, see "Multi-domain architecture" below) plus the
  full `Domains` table (one row per domain, including every multi-domain entry described below);
- `compatible.jsonl`: one fully compatible logical NPC/warp entity/event per line;
- `unsupported.jsonl`: unsupported NPC/warp events, all known blockers, and bounded source context;
- `blockers.json`: feature/stage aggregates and representative sources, across BOTH the NPC/warp
  scan and every domain entity's own blockers;
- `work-items.json`: roadmap ordered by the amount of content a capability alone unlocks - stable
  semantic capability IDs only (see "Work-item meaning" below), never a raw exception type name;
- `dependencies.json`: the cross-domain dependency graph - literal quest/item/map/mob references
  proven by lowered NPC source AND every domain entity's own dependencies (mob-spawn -> map/mob,
  shop -> item, quest -> mob/item, item -> item via `Grants`), deduplicated and deterministically
  sorted by entity id;
- `domains/<domain>.jsonl`: one file per domain (`maps`, `mobs`, `mvp`, `items`, `mob-spawns`,
  `quests`, `shops`, `mapflags`, `functions`, `map-world`), each line one `DomainEntity`;
- `report.md`: concise human-readable summary.

`analyze` evaluates two independent layers that are composed into one report, never blended into
one meaningless percentage: the NPC/warp event scan (`RepositoryCompatibilityAnalyzer`,
`CompatibilityEntity`, the original trusted compiler boundary, unmodified by the work below) and
domain analysis (`RepositoryDomainAnalyzers`, `DomainEntity` - `maps`, `mobs`, `mvp`, `items`,
`mob-spawns`, `quests`, `shops`, `mapflags`, `functions`, `map-world`). See "Multi-domain analysis"
below for the full domain-by-domain breakdown, including static-vs-runtime compatibility (items,
mobs), map geometry vs map-world completeness, mob definition vs spawn vs skill, MVP
classification, and quest drop-rule vs full quest definition. Every `DomainEntity` decomposes into
named `Components`, each deriving its status ONLY from its own blockers - one component being
unsupported never taints a sibling.

Raw-line domain scanners (`AnalyzeMapFlags`, `AnalyzeFunctions` - the domains that read pinned
`*.txt` content directly rather than through `RathenaSourceParser`/`RathenaEventCompiler`) exclude
commented-out (`//`, after trimming leading whitespace) and blank lines via a shared
`IsCommentedOrBlank` helper before treating a line as a declaration - a commented-out
`//map	mapflag	flag` line is never discovered as an active mapflag, and never produces a false
`dependency:map` blocker referencing the literal `//map` text as a map name.

`functions` domain entity ids are source-qualified (`function:<relative-source>:<line>:<name>`),
not the bare function name, so two distinct pinned `function script` bodies that happen to share a
name (pinned rAthena has several, e.g. `Job_Change`, `Chk`, `Catwarp`) remain separate entities and
separate `dependencies.json` graph nodes instead of silently collapsing.

A mob's `RaceGroups:`/`Drops:`/`MvpDrops:` blocks are each classified exclusively by their own
dedicated component (`RaceGroups`/`Drops`/`MvpDrops`, each reporting its own `*:runtime` capability
id when the block is non-empty); all three are excluded from the generic unknown-top-level-field
`StaticData` scan so the same source construct is never double-counted as both a `mob-field:*`
StaticData blocker and a dedicated-component blocker. `Modes:` gets a parallel two-component split
instead (`ModeData`/`ModeRuntime` - see below), since a mode's REPRESENTATION and its RUNTIME
EXECUTION are independent axes in a way the list-shaped blocks are not.

`MobDataCompiler.ReadMobDefinition`/`GenerateMobDefinition` and `MobDefinition`
(`src/MapServer/World/WorldEntityDefinition.cs`) losslessly model every documented pinned
`db/re/mob_db.yml` top-level field. Scalars: `JapaneseName`, `Sp`, `MvpExp`, `Resistance`,
`MagicResistance`, `SkillRange`, `ChaseRange`, `Size`, `Race`, `Element`, `ElementLevel`,
`ClientAttackMotion`, `DamageTaken`, `GroupId`, and `Title` all round-trip alongside the original
combat/movement field set, each using the same documented pinned default (e.g. `Sp` -> 1,
`DamageTaken` -> 100, `ClientAttackMotion` -> the SAME mob's own resolved `AttackMotion` when absent
- see `mob.cpp:5391-5397`) rather than a blanket zero. `Size`/`Race`/`Element`/`Class` are
strongly-typed generated enums (`MobSize`/`MobRace`/`MobElement`/`MobClass`) mirroring the pinned
`e_size`/`e_race`/`e_element`/`e_mob_class` numeric values exactly, resolved case-insensitively
against the fixed pinned string table (matching `script_get_constant`'s own `strcasecmp` lookup)
with the documented fallback default on an unrecognized value - never a thrown error for one bad
enum-shaped field.

List-shaped blocks: `Modes:` retains the COMPLETE pinned 22-bit `MD_*` bitmask (`MobMode`/
`MobModeData`, `[Flags]`) - every valid mode NAME is representable, independent of whether
MapServer's runtime executes that bit (only 5 of the 22 bits are runtime-executed today - see
`ai/world-data.md`'s "Mob Modes" section for the full list and the `ModeData`/`ModeRuntime`
component split this enables). `RaceGroups:` retains each entry as `MobRaceGroupEntry(string Name,
bool Value)` - a pinned-NAME list, not a fixed C# enum, since the pinned `RC2_*` constant table is
open-ended/content-defined. `Drops:`/`MvpDrops:` both retain every entry as `MobDropEntry(string
Item, int Rate, bool StealProtected, string? RandomOptionGroup)` (pinned `parseDropNode` parses both
blocks identically). All three are `null` (never an empty-but-present list) when the pinned block is
entirely absent.

`MobSupportedKeys` in `RepositoryDomainAnalyzers` is kept in sync with the compiler's actual scalar
field coverage. As of this hardening pass, `analyze`'s `mob-field:*` StaticData blockers are **zero**
across the complete pinned `db/re/mob_db.yml` - every meaningful top-level field is either a modeled
scalar or one of the four dedicated components (`ModeData`/`RaceGroups`/`Drops`/`MvpDrops`), and a
real, pinned-file-scanning test
(`MobDataCompilerTests.PinnedMobDbSchema_EveryTopLevelKeyActuallyPresentInRealData_IsExplicitlyClassified`)
fails closed if a future pinned revision adds a genuinely new, unclassified top-level key.

Structural completeness counts (`map-world`'s `MobSpawns`/`MapFlags` components) are carried on an
optional `DomainComponent.Metric` (`{ "Compatible": N, "Total": M }`), never as a formatted
`"N/M"` string inside `Blockers` - `Blockers` holds only genuine semantic blocker/capability ids.

`Compatible` means the complete event passes the current real compilation boundary;
unsupported statements are never omitted. `soleBlockerFor` counts events whose
distinct normalized capability set contains only that feature, rather than estimating
from command frequency. Parsing failures, semantic failures, lowering gaps,
runtime capability gaps, dependencies, and generation failures are separate stages
because they require different work. Reliably discoverable categories without an
actual converter are reported as `NotYetAnalyzed`, never as compatible.

Roadmap blockers use stable semantic capability IDs such as
`control-flow:while`, `function:callfunc`, `variable:account`, and
`operator:logical-and`. The unsupported JSONL retains `compilerConstruct`, the
diagnostic code, message, and location separately so compiler implementation detail
remains available without becoming the roadmap identity. Attribution is based on
the syntax node owning the diagnostic span; a nearby supported command is never
used as a guess.

Event compatibility and NPC-definition compatibility are distinct. A definition is
`FullyCompatible` only when every executable event is compatible,
`PartiallyCompatible` when compatible and unsupported events coexist,
`Unsupported` when no executable event is compatible, and `NotApplicable` only
when it has no executable behavior. A future safe `--compatible-only` bulk mode
must generate only fully compatible definitions by default. It must not silently
drop an unsupported `OnTouch`, `OnInit`, or other event from a partially compatible
NPC.

## Top-level content audit

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- audit \
  --source-root legacy/rathena/npc \
  --output data/world/conversion-audit.json
```

The audit only produces counts and classifications; it does not convert content.

## Generated character data

```bash
dotnet run --project tools/WorldDataImporter/WorldDataImporter.csproj -- compile-character-data \
  --rathena-root legacy/rathena \
  --rathena-commit e985006171d2eb320ee512a653f4c83aea3d81b6 \
  --output src/MapServer/Generated
```

The command requires pinned `mmo.hpp`, `script_constants.hpp`, Renewal
`job_exp.yml`, `job_basepoints.yml`, `job_stats.yml`, `statpoint.yml`,
`skill_db.yml`, and `skill_tree.yml`. It parses, resolves, validates, computes
effective trees, then replaces only the owned `Generated/Jobs`,
`Generated/Progression`, and `Generated/Skills` directories. Generation is
staged before replacement and rejects missing files or unresolved relationships.

The six checked-in files provide job/skill identity (a generated `JobClass` enum
plus `JobClassNames`, not a dictionary of records), progression, and direct plus
effective skill-tree registries. Runtime consumes generated C# only. Current
coverage is 194 exported numeric identities, 175 generated jobs, 175 progression
mappings (89 unique value sets), 1,635 skills, and 175 direct/effective trees -
every generated job now has complete progression data, since HP/SP resolve
through pinned `JobDatabase::calc_basehp`/`calc_basesp` for any base level a
job's `db/re/job_basepoints.yml` table doesn't cover explicitly (see
`ai/world-data.md`'s "Generated character-data registries and rate policy"
section for the full formula and mapid-category adjustments). `JobClass`
member names are readable PascalCase identifiers (`Rune_Knight` ->
`RuneKnight`) computed and collision-checked at compile time;
`JobClassNames.GetRathenaName(JobClass)` recovers the canonical pinned source
name verbatim. Skill spending, `CharSkill` mutation, `0x0B32`, skill execution,
and job changing remain out of scope. `compile-progression` remains available
when only the owned progression directory needs regeneration; the command
above is the canonical complete sequence.

## Verification

```bash
dotnet test tests/WorldDataImporter.Tests/WorldDataImporter.Tests.csproj
dotnet test tests/MapServer.Tests/MapServer.Tests.csproj
dotnet build Athena.NET.sln -m:1
git diff --check
```

Generated files are deterministic: identical source, filters, and importer code
must produce byte-identical JSON.
