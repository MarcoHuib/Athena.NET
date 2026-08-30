namespace Athena.Net.MapServer.World;

public sealed record WorldEntityDefinition(int SchemaVersion, string Id, string Kind, WorldActorComponent? Actor, IReadOnlyList<WorldTriggerDefinition> Triggers, IReadOnlyList<ScriptBehaviorDefinition> Scripts, WorldSourceInfo Source);
public sealed record WorldActorComponent(string Name, string Map, ushort X, ushort Y, byte Direction, ushort Class, uint EffectState = 0);
public sealed record WorldTriggerDefinition(string Type, string Map, ushort X, ushort Y, ushort RadiusX, ushort RadiusY, IReadOnlyList<WorldActionDefinition> Actions);
public sealed record ScriptBehaviorDefinition(string Trigger, string Map, ushort X, ushort Y, ushort RadiusX, ushort RadiusY, bool SourceParsed, bool RuntimeExecutable, IReadOnlyList<string> RequiredCapabilities, string NormalizedSource, IReadOnlyList<ScriptInstructionDefinition>? Instructions = null, string? BaseNpcName = null);
public abstract record ScriptInstructionDefinition;
public sealed record MessageInstruction(string Text) : ScriptInstructionDefinition;
public sealed record NextInstruction : ScriptInstructionDefinition;
public sealed record CloseInstruction : ScriptInstructionDefinition;
public sealed record Close2Instruction : ScriptInstructionDefinition;
public sealed record SelectInstruction(IReadOnlyList<SelectOptionDefinition> Options) : ScriptInstructionDefinition;
public sealed record SelectOptionDefinition(string Text, IReadOnlyList<ScriptInstructionDefinition> Instructions);
public sealed record SetQuestInstruction(uint QuestId) : ScriptInstructionDefinition;
public sealed record CompleteQuestInstruction(uint QuestId) : ScriptInstructionDefinition;
public sealed record IfQuestStateInstruction(uint QuestId, CharacterQuestStatus Expected, IReadOnlyList<ScriptInstructionDefinition> Then, IReadOnlyList<ScriptInstructionDefinition> Else) : ScriptInstructionDefinition;
public sealed record AssignmentInstruction(string Variable, ScriptExpressionDefinition Value) : ScriptInstructionDefinition;
public sealed record WarpInstruction(ScriptExpressionDefinition Map, ushort X, ushort Y) : ScriptInstructionDefinition;
public sealed record SavePointInstruction(ScriptExpressionDefinition Map, ushort X, ushort Y, ushort RadiusX = 0, ushort RadiusY = 0) : ScriptInstructionDefinition;
public abstract record ScriptExpressionDefinition;
public sealed record StringLiteralExpression(string Value) : ScriptExpressionDefinition;
public sealed record VariableExpression(string Name) : ScriptExpressionDefinition;
public sealed record ConcatExpression(ScriptExpressionDefinition Left, ScriptExpressionDefinition Right) : ScriptExpressionDefinition;
public sealed record StrNpcInfoExpression(int InfoType) : ScriptExpressionDefinition;
public sealed record ReplaceStringExpression(ScriptExpressionDefinition Value, ScriptExpressionDefinition Search, ScriptExpressionDefinition Replacement) : ScriptExpressionDefinition;
public abstract record WorldActionDefinition;
public sealed record WarpAction(string Map, ushort X, ushort Y) : WorldActionDefinition;
public sealed record SetSavePointAction(string Map, ushort X, ushort Y) : WorldActionDefinition;
public sealed record WorldSourceInfo(string Repository, string Commit, string File, int Line);

// Pinned rAthena monster capability bits (legacy/rathena/src/common/mmo.hpp enum e_mode,
// mmo.hpp:242-272; cross-checked against legacy/rathena/doc/mob_db_mode_list.txt, the pinned
// project's own authoritative bit-by-bit reference). This is now the COMPLETE pinned MD_* bitmask
// (26 named bits across the 32-bit range; two positions - 0x0000100 and 0x0800000 - are pinned
// "FREE"/unused slots with no MD_* constant and are correctly absent here). Every bit is
// REPRESENTED (a source Modes: entry for any of these names round-trips losslessly through
// MobDataCompiler/GenerateMobDefinition), independent of whether MapServer's runtime currently
// executes that bit's gameplay behavior - see RepositoryDomainAnalyzers.AnalyzeMobs' ModeData vs
// ModeRuntime component split, which is the analyzer-side expression of this same distinction.
// Only CanMove/NoRandomWalk/CanAttack/ChangeTargetMelee/ChangeTargetChase are consulted by any
// runtime call site today (MonsterRuntime/MobInstance/MonsterCombatCoordinator, via mode.HasFlag) -
// every other bit is deliberately inert at runtime while still being real, retained source data.
[Flags]
public enum MobMode : uint
{
    None = 0,
    // MD_CANMOVE (mmo.hpp:244, 0x0000001) - authorizes idle random walk (mob_randomwalk,
    // mob.cpp:1673) and chase movement. Without this bit a mob must never be scheduled to walk,
    // regardless of any other source-backed movement data (WalkSpeed, etc.) it happens to carry.
    // Runtime-executed.
    CanMove = 0x0000001,
    // MD_LOOTER (mmo.hpp:245, 0x0000002) - the mob loots nearby ground items while idle
    // (mob.cpp:2009/3859). Stored only; no loot runtime exists yet.
    Looter = 0x0000002,
    // MD_AGGRESSIVE (mmo.hpp:246, 0x0000004) - a normal aggressive mob actively seeks a nearby
    // player to attack (mob.cpp:2024). Stored only; no aggro/AI runtime exists yet.
    Aggressive = 0x0000004,
    // MD_ASSIST (mmo.hpp:247, 0x0000008) - joins a nearby same-class mob's attack (mob.cpp:1029).
    // Stored only; no assist runtime exists yet.
    Assist = 0x0000008,
    // MD_CASTSENSORIDLE (mmo.hpp:248, 0x0000010) - reacts to a nearby character starting to cast
    // while idle/walking. Stored only; no cast-sensor runtime exists yet.
    CastSensorIdle = 0x0000010,
    // MD_NORANDOMWALK (mmo.hpp:249, 0x0000020) - explicitly suppresses idle random walk
    // (mob_randomwalk's own early-return guard, mob.cpp:1687) even when MD_CANMOVE is also set.
    // Runtime-executed.
    NoRandomWalk = 0x0000020,
    // MD_NOCAST (mmo.hpp:250, 0x0000040) - the mob is unable to cast skills (mob.cpp:4286). Stored
    // only; no mob-skill runtime exists yet (mob-skill:runtime).
    NoCast = 0x0000040,
    // MD_CANATTACK (mmo.hpp:251, 0x0000080) - pinned mob_ai_sub_hard's own target-acquisition gate
    // ("if (md->attacked_id && mode&MD_CANATTACK)", mob.cpp:1937): a mob without this bit never
    // promotes an attacker into a combat target at all, regardless of MD_AGGRESSIVE. Consulted by
    // MonsterCombatCoordinator.Attack before calling MobInstance.TryAcquireTarget - see that call
    // site's own doc comment. Runtime-executed.
    CanAttack = 0x0000080,
    // 0x0000100 is a pinned "FREE" bit position (doc/mob_db_mode_list.txt) - no MD_* constant
    // claims it; deliberately absent here, not an omission.
    // MD_CASTSENSORCHASE (mmo.hpp:253, 0x0000200) - reacts to a nearby character starting to cast
    // while idle/chasing (switches chase target). Stored only.
    CastSensorChase = 0x0000200,
    // MD_CHANGECHASE (mmo.hpp:254, 0x0000400) - a chasing/rushing mob may switch to a different
    // player who comes within attack range. Stored only.
    ChangeChase = 0x0000400,
    // MD_ANGRY (mmo.hpp:255, 0x0000800) - "hyper-active" mob with distinct follow/angry
    // pre-attack states and its own skill-set selection (mob.cpp:1832). Stored only.
    Angry = 0x0000800,
    // MD_CHANGETARGETMELEE (mmo.hpp:256, 0x0001000) - pinned mob_can_changetarget's own MSS_BERSERK
    // case (mob.cpp:1242): whether a mob already attacking one target in melee range may switch to
    // a DIFFERENT attacker. Consulted by MobInstance.TryAcquireTarget when MobCombatState is
    // Berserk. Runtime-executed.
    ChangeTargetMelee = 0x0001000,
    // MD_CHANGETARGETCHASE (mmo.hpp:257, 0x0002000) - pinned mob_can_changetarget's own MSS_RUSH
    // case (mob.cpp:1252): whether a mob already chasing one target may switch to a DIFFERENT
    // attacker mid-chase. Consulted by MobInstance.TryAcquireTarget when MobCombatState is Rush -
    // this is the bit G_PORING's real generated mode LACKS, which is why item 6's own acceptance
    // criterion (a second attacker cannot steal an already-chasing G_PORING's target) holds without
    // any mob-ID special case. Runtime-executed.
    ChangeTargetChase = 0x0002000,
    // MD_TARGETWEAK (mmo.hpp:258, 0x0004000) - an aggressive mob only picks fights with characters
    // at least 5 levels below its own (mob.cpp:1330). Stored only.
    TargetWeak = 0x0004000,
    // MD_RANDOMTARGET (mmo.hpp:259, 0x0008000) - picks a new random in-range target per normal
    // attack (mob.cpp:1271). Stored only.
    RandomTarget = 0x0008000,
    // MD_IGNOREMELEE (mmo.hpp:261, 0x0010000) - takes 1 HP from physical melee attacks
    // (mob.cpp:1084, the "Plant type" branch). Stored only.
    IgnoreMelee = 0x0010000,
    // MD_IGNOREMAGIC (mmo.hpp:262, 0x0020000) - takes 1 HP from magic attacks. Stored only.
    IgnoreMagic = 0x0020000,
    // MD_IGNORERANGED (mmo.hpp:263, 0x0040000) - takes 1 HP from ranged attacks. Stored only.
    IgnoreRanged = 0x0040000,
    // MD_MVP (mmo.hpp:264, 0x0080000) - flags the mob as MVP: coma-immune, MVP sign, MVP EXP/item
    // rewards (mob.cpp:378/388). Stored only - see AnalyzeMvp for the dedicated MvpBehavior/
    // MvpDropsData components this bit feeds.
    Mvp = 0x0080000,
    // MD_IGNOREMISC (mmo.hpp:265, 0x0100000) - takes 1 HP from "misc"/none-type attacks. Stored
    // only.
    IgnoreMisc = 0x0100000,
    // MD_KNOCKBACKIMMUNE (mmo.hpp:266, 0x0200000) - cannot be knocked back. Stored only.
    KnockBackImmune = 0x0200000,
    // MD_TELEPORTBLOCK (mmo.hpp:267, 0x0400000) - pinned source's own doc: "Not implemented yet"
    // even in rAthena itself. Stored only.
    TeleportBlock = 0x0400000,
    // 0x0800000 is a pinned "FREE" bit position (doc/mob_db_mode_list.txt) - no MD_* constant
    // claims it; deliberately absent here, not an omission.
    // MD_FIXEDITEMDROP (mmo.hpp:269, 0x1000000) - the mob's drops are unaffected by item-drop-rate
    // modifiers (mob.cpp:5552, auto-applied to CLASS_EVENT mobs in loadingFinished). Stored only.
    FixedItemDrop = 0x1000000,
    // MD_DETECTOR (mmo.hpp:270, 0x2000000) - detects/attacks hidden or cloaked characters
    // (mob.cpp:5543, auto-applied to CLASS_BOSS mobs). Stored only.
    Detector = 0x2000000,
    // MD_STATUSIMMUNE (mmo.hpp:271, 0x4000000) - immune to status-change effects (mob.cpp:1078,
    // auto-applied to CLASS_BOSS/CLASS_GUARDIAN/CLASS_BATTLEFIELD). Stored only.
    StatusImmune = 0x4000000,
    // MD_SKILLIMMUNE (mmo.hpp:272, 0x8000000) - immune to being affected by skills (auto-applied to
    // CLASS_BATTLEFIELD). Stored only.
    SkillImmune = 0x8000000,
}

// Pinned e_size (legacy/rathena/src/map/mob.hpp:114-120). Only SZ_SMALL/SZ_MEDIUM/SZ_BIG are ever
// resolved by MobDatabase::parseBodyNode's own Size: parser (it clamps any parsed constant outside
// [SZ_SMALL, SZ_BIG] back to SZ_SMALL, mob.cpp:5244) - SZ_ALL/SZ_MAX are pinned-source runtime
// sentinels (e.g. skill/item size-filter wildcards), never a real mob_db.yml Size: value, so they
// are intentionally not members here. Numeric values match pinned source exactly.
public enum MobSize
{
    Small = 0,
    Medium = 1,
    Big = 2,
}

// Pinned e_race (legacy/rathena/src/map/map.hpp:324-339). RC_NONE_/RC_ALL/RC_MAX are pinned-source
// sentinels/wildcards (RC_NONE_ means "no bonus applies", RC_ALL is a skill/item race-filter
// wildcard) - CHK_RACE (the same bounds MobDatabase::parseBodyNode itself enforces, mob.cpp:5287,
// clamping any out-of-range parsed constant back to Formless) only ever accepts RC_FORMLESS..
// RC_PLAYER_DORAM as a real per-mob value, so only those are modeled here. Numeric values match
// pinned source exactly.
public enum MobRace
{
    Formless = 0,
    Undead = 1,
    Brute = 2,
    Plant = 3,
    Insect = 4,
    Fish = 5,
    Demon = 6,
    DemiHuman = 7,
    Angel = 8,
    Dragon = 9,
    PlayerHuman = 10,
    PlayerDoram = 11,
}

// Pinned e_element (legacy/rathena/src/map/map.hpp:390-407). ELE_NONE/ELE_ALL/ELE_MAX/ELE_WEAPON/
// ELE_ENDOWED/ELE_RANDOM are pinned-source sentinels/wildcards (skill/item element-filter or
// "use the weapon's own element" markers), never a real mob_db.yml Element: value - pinned
// MobDatabase::parseBodyNode's own CHK_ELEMENT bounds check (mob.cpp:5334) only ever accepts
// ELE_NEUTRAL..ELE_UNDEAD for a per-mob value, so only those are modeled here. Numeric values
// match pinned source exactly.
public enum MobElement
{
    Neutral = 0,
    Water = 1,
    Earth = 2,
    Fire = 3,
    Wind = 4,
    Poison = 5,
    Holy = 6,
    Dark = 7,
    Ghost = 8,
    Undead = 9,
}

// Pinned e_mob_class / CLASS_* (legacy/rathena/src/map/mob.hpp:186-192). MobDatabase::parseBodyNode
// only ever accepts CLASS_NORMAL..CLASS_EVENT for a per-mob Class: value (mob.cpp:5483, clamping
// anything outside that range back to Normal) - CLASS_ALL is a pinned-source wildcard sentinel,
// never a real mob_db.yml value, so it is intentionally not modeled here. Numeric values match
// pinned source exactly (note the pinned enum's own gap: Guardian=2, Battlefield=4 - no value 3).
public enum MobClass
{
    Normal = 0,
    Boss = 1,
    Guardian = 2,
    Battlefield = 4,
    Event = 5,
}

// Immutable, source-backed monster data (pinned rAthena db/re/mob_db.yml).
// Renewal semantics: Attack -> rhw.atk (weapon-roll component when this mob
// is the ATTACKER, irrelevant when it is the target), Defense -> hard DEF,
// MagicDefense -> mdef (magic only). Soft physical DEF (def2/"vit_def") is
// NOT a YAML field: it is derived at combat time as floor((Level+Vit)/2)
// (status.cpp status_calc_misc, BL_MOB branch) from Vit here. BaseExp/JobExp
// are 0 when the pinned block omits them entirely (rAthena YAML loader
// default), matching a tutorial punching-bag mob - this is read from source,
// never assumed nonzero because CharacterProgressionService exists.
// `Mode` is derived exactly like pinned MobDatabase::parseBodyNode (mob.cpp:5446-5519): the
// pinned `Ai:` field resolves to one of the MONSTER_TYPE_NN preset bitmasks (mob.hpp:151-164,
// e.g. Ai=02 -> MONSTER_TYPE_02=0x83), which becomes the mob's base status.mode; any pinned
// `Modes:` block entries then individually OR (true) or AND-NOT (false) additional bits on top of
// that preset - never one flat "the Modes: block IS the mode" assumption, since a real mob's
// effective mode is almost always dominated by its Ai preset, with Modes: only overriding specific
// bits (e.g. G_PORING/2401 has Ai=02=0x83=MD_CANMOVE|MD_LOOTER|MD_CANATTACK and a Modes: block
// that only sets FixedItemDrop=true - a bit this project's MobMode does not yet model).
// AttackMotion (amotion) and DamageMotion (dmotion) are pinned mob_db.yml scalars distinct from
// AttackDelay (adelay): AttackDelay controls attack CADENCE (this project's MobInstance.
// NextAttackAt scheduling), never animation/hit-reaction timing. AttackMotion is THIS mob's own
// attack-animation timing - used as clif_damage's srcSpeed when this mob is the ATTACKER
// (mob->player combat). DamageMotion is THIS mob's own hit-reaction/walk-delay timing - used as
// clif_damage's dstSpeed when this mob is the TARGET (player->mob combat). The two directions must
// never be conflated: a mob's own DamageMotion is never a valid dstSpeed when THAT SAME mob is the
// attacker (see MobBasicAttackCalculator/IroMonsterCombatPackets call sites).
// Fields added beyond the original combat/movement slice (JapaneseName, MaxSp, MvpExp,
// Resistance, MagicResistance, SkillRange, ChaseRange, Size, Race, Element, ElementLevel,
// ClientAttackMotion, DamageTaken, GroupId, Title, Class) are the remaining STATIC scalar/enum
// mob_db.yml fields with no runtime consumer yet - preserved losslessly (never dropped at compile
// time) matching this project's lossless-conversion convention for NPC/warp data (ai/world-data.md).
// RaceGroups/Drops/MvpDrops are each list-shaped pinned blocks, now retained as their own typed
// list fields below (never collapsed into a StaticData scalar) - each still has its own dedicated
// analyzer component distinguishing DATA representation (now losslessly complete) from RUNTIME
// support (still absent: no race-group gameplay consumer, no drop-table/MVP-reward runtime exists
// anywhere in this project outside the unrelated single-quest QuestDropDataCompiler slice).
// `Mode` on this record is SOURCE mode - the pinned `Ai:` preset plus `Modes:` block overrides
// ONLY (`MobDatabase::parseBodyNode`, mob.cpp:5446-5519), exactly what a reader of the YAML block
// itself would see. It deliberately does NOT include the class-derived bits pinned
// `MobDatabase::loadingFinished()` (mob.cpp:5536-5551) ORs on AFTERWARD, purely from `Class:` -
// MD_DETECTOR/MD_STATUSIMMUNE/MD_KNOCKBACKIMMUNE for CLASS_BOSS, MD_STATUSIMMUNE for
// CLASS_GUARDIAN, MD_STATUSIMMUNE/MD_SKILLIMMUNE for CLASS_BATTLEFIELD, MD_FIXEDITEMDROP for
// CLASS_EVENT - since mutating the stored source-backed `Mode` field with those would misrepresent
// what the pinned YAML block itself actually declared (a mob whose Modes: never mentions Detector
// would incorrectly look, from this record alone, like it explicitly set Detector). Call
// `EffectiveMode` for the mode value pinned rAthena ACTUALLY loads and runs combat against -
// `Mode | MobModeResolver.ClassDerivedBits(Class)`. Every real MapServer runtime call site today
// (MonsterRuntime/MobInstance/MonsterCombatCoordinator) only ever checks CanMove/NoRandomWalk/
// CanAttack/ChangeTargetMelee/ChangeTargetChase - none of which loadingFinished() ever derives from
// Class - so those call sites correctly keep reading source `Mode` directly; `EffectiveMode` exists
// for callers (chiefly RepositoryDomainAnalyzers' ModeRuntime component) that need the COMPLETE
// pinned-accurate mode a real rAthena server would hold at runtime.
public sealed record MobDefinition(
    int Id, string AegisName, string Name, int Level, uint MaxHp,
    int Attack, int Attack2, int Defense, int MagicDefense,
    int Str, int Agi, int Vit, int Int, int Dex, int Luk,
    int AttackRange, int WalkSpeed, int AttackDelay, int AttackMotion, int DamageMotion,
    long BaseExp, long JobExp, MobMode Mode,
    WorldSourceInfo Source,
    string? JapaneseName = null, uint MaxSp = 1, long MvpExp = 0,
    int Resistance = 0, int MagicResistance = 0, int SkillRange = 0, int ChaseRange = 0,
    MobSize Size = MobSize.Small, MobRace Race = MobRace.Formless,
    MobElement Element = MobElement.Neutral, int ElementLevel = 1,
    // ClientAttackMotion has NO fixed record default: pinned MobDatabase::parseBodyNode resolves an
    // absent ClientAttackMotion to THIS SAME mob's own resolved AttackMotion value when the block is
    // being seen for the first time (mob.cpp:5391-5397, the `else { if (!exists) ... }` branch) -
    // MobDataCompiler.ReadMobDefinition computes this derived default explicitly rather than the
    // record declaring a constant that would be wrong for every mob whose AttackMotion isn't 0.
    int ClientAttackMotion = 0, int DamageTaken = 100, int GroupId = 0, string? Title = null,
    MobClass Class = MobClass.Normal,
    // Pinned `RaceGroups:` (mob.cpp:5291-5317): a named RC2_* toggle collection, NOT a fixed
    // CHK_RACE-bounded enum - the pinned constant table is open-ended/content-defined (RC2_GOBLIN,
    // RC2_BIOLAB, RC2_MALANGDO, ...), so this project retains it as the pinned string name itself
    // (never re-encoded into a hand-maintained C# enum that would silently reject a future pinned
    // addition) rather than inventing a bound this project cannot actually enforce. Deterministic
    // pinned-source order (list, not a Dictionary) - see MobRaceGroupEntry.
    IReadOnlyList<MobRaceGroupEntry>? RaceGroups = null,
    // Pinned `Drops:`/`MvpDrops:` (mob.cpp:4844-4923, MobDatabase::parseDropNode, shared by both
    // blocks) - every meaningful per-entry field (Item/Rate/StealProtected/RandomOptionGroup) is
    // retained in pinned list order. StealProtected is meaningless for MvpDrops (pinned source never
    // reads it there - TF_STEAL never targets an MVP reward slot) so MvpDropEntries never sets it
    // true; it is still present on the shared record type rather than a second near-duplicate record,
    // since the pinned per-entry SHAPE is otherwise identical. See MobDropEntry.
    IReadOnlyList<MobDropEntry>? Drops = null,
    IReadOnlyList<MobDropEntry>? MvpDrops = null)
{
    // The pinned-accurate mode a real rAthena server actually holds/runs combat against - source
    // Mode plus this mob's own Class-derived bits. Computed on read (never stored/generated), so it
    // can never drift out of sync with Mode/Class and never needs its own generated-data field.
    public MobMode EffectiveMode => Mode | MobModeResolver.ClassDerivedBits(Class);
}

// Pinned `MobDatabase::loadingFinished()`'s class-derived mode-bit resolution (mob.cpp:5536-5551) -
// applied to EVERY mob after its own Ai/Modes: source mode is resolved, purely from `Class:`, with
// no corresponding `Modes:` entry required or expected. Not a `Modes:` override in the pinned
// source's own sense (it never appears in the YAML, and pinned's own per-Modes-entry
// invalidWarning/skip logic is not involved) - a separate, unconditional post-processing pass.
internal static class MobModeResolver
{
    public static MobMode ClassDerivedBits(MobClass mobClass) => mobClass switch
    {
        MobClass.Boss => MobMode.Detector | MobMode.StatusImmune | MobMode.KnockBackImmune,
        MobClass.Guardian => MobMode.StatusImmune,
        MobClass.Battlefield => MobMode.StatusImmune | MobMode.SkillImmune,
        MobClass.Event => MobMode.FixedItemDrop,
        _ => MobMode.None, // CLASS_NORMAL (and any other value) adds nothing.
    };
}

// One pinned `RaceGroups:` entry - `Name` is the bare pinned key (e.g. "Goblin", "Biolab",
// "Malangdo"), matched case-sensitively against the pinned `RC2_<Name>` constant table by rAthena
// itself; `Value` is that entry's own `true`/`false` toggle (mob.cpp:5309-5314: `true` adds the
// race2 flag, `false` explicitly removes it - both are meaningful source intent, not merely
// "true entries exist"). Never collapsed to a bare name list: an explicit `false` entry is
// different pinned intent from the entry being entirely absent.
public sealed record MobRaceGroupEntry(string Name, bool Value);

// One pinned `Drops:`/`MvpDrops:` list entry (mob.cpp:4844-4923 MobDatabase::parseDropNode).
// `Item` is the pinned AegisName item reference (this project has no cross-domain Id resolution
// step here - see ai/world-data.md's item/mob domain independence - so it is retained as the
// source string, not resolved to a numeric item Id). `Rate` is the raw pinned per-10000 drop-rate
// scalar (asUInt16Rate, 0-10000, matching item_rate_* semantics already documented for GENERIC
// drop-rate resolution elsewhere - this field is NOT itself scaled by any rate here, only stored).
// `StealProtected`/`RandomOptionGroup` are the pinned per-entry optional fields, defaulting to
// false/null exactly like pinned parseDropNode's own local defaults when the entry omits them.
public sealed record MobDropEntry(string Item, int Rate, bool StealProtected = false, string? RandomOptionGroup = null);

// One pinned `monster` spawn-line declaration (npc/re/mobs/*.txt), scoped to
// a single map. `Count` instances are maintained on that map; `RespawnDelayMs`
// is the pinned mob.delay1 (npc_parse_mob defaults to 5000 when unspecified).
// X/Y/Xs/Ys are the pinned declaration's own `<map>,<x>,<y>[,<xs>,<ys>]` fields
// (mob.cpp mob_spawn / npc_parse_mob), preserved losslessly rather than
// discarded at compile time - see IMobSpawnCellSelector for how a
// map-wide-random declaration (X=0, Y=0, Xs=0, Ys=0, i.e. "xs+ys<1" per
// pinned mob_spawn) is distinguished from a fixed/rectangular spawn area.
// Xs/Ys default to 0 (not 1) when the pinned line omits them, matching the
// pinned parser leaving spawn->xs/ys at their zero-initialized default in
// that case (npc_parse_mob only assigns them when the optional 4th/5th
// columns are present).
public sealed record MobSpawnDefinition(MobDefinition Mob, string Map, int Count, int RespawnDelayMs, WorldSourceInfo Source, short X = 0, short Y = 0, short Xs = 0, short Ys = 0);

// One pinned quest_db.yml `Drops:` entry (quest.cpp QuestDatabase::parseBodyNode
// / quest_update_objective's drop-processing loop). This is intentionally NOT a
// kill-count objective: quest_update_objective only ever increments
// sd->quest_log[i].count[j] for a quest's `Targets:` objectives, and quest
// 21008 in the pinned source has none - it has only this Drops rule. Rate is
// out of 10000 (rnd_chance(rate, 10000)); Count defaults to 1 when the pinned
// YAML omits it (quest.cpp: "if (!targetExists) target->count = 1;").
public sealed record QuestDropRule(uint QuestId, int MobId, int ItemId, int Count, int Rate, WorldSourceInfo Source);
public sealed record NavigationDefinition(string EntityId, string SourceMap, ushort X, ushort Y, ushort RadiusX, ushort RadiusY, string DestinationMap, ushort DestinationX, ushort DestinationY, string SourceFile, int SourceLine)
{
    public bool Contains(string map, ushort x, ushort y) => string.Equals(SourceMap, map, StringComparison.OrdinalIgnoreCase) && Math.Abs((int)X - x) <= RadiusX && Math.Abs((int)Y - y) <= RadiusY;
}
