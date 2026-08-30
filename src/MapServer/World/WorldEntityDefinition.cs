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
// mmo.hpp:242-272). Only the bits this project's mob-movement/AI slice actually needs are modeled
// so far - this is NOT the complete pinned e_mode bitmask (e.g. MD_AGGRESSIVE, MD_ASSIST,
// MD_LOOTER, MD_MVP, etc. all exist in pinned source but have no Athena runtime behavior yet).
// Extend this enum (never a mob-specific bool like "PoringCanMove") when a future slice needs
// another bit - matching how MobDataCompiler already computes the FULL pinned mode value from
// source and simply narrows which bits Athena's generated model currently exposes.
[Flags]
public enum MobMode
{
    None = 0,
    // MD_CANMOVE (mmo.hpp:244, 0x0000001) - authorizes idle random walk (mob_randomwalk,
    // mob.cpp:1673) and chase movement. Without this bit a mob must never be scheduled to walk,
    // regardless of any other source-backed movement data (WalkSpeed, etc.) it happens to carry.
    CanMove = 0x0000001,
    // MD_NORANDOMWALK (mmo.hpp:249, 0x0000020) - explicitly suppresses idle random walk
    // (mob_randomwalk's own early-return guard, mob.cpp:1687) even when MD_CANMOVE is also set.
    NoRandomWalk = 0x0000020,
    // MD_CANATTACK (mmo.hpp:251, 0x0000080) - pinned mob_ai_sub_hard's own target-acquisition gate
    // ("if (md->attacked_id && mode&MD_CANATTACK)", mob.cpp:1937): a mob without this bit never
    // promotes an attacker into a combat target at all, regardless of MD_AGGRESSIVE. Consulted by
    // MonsterCombatCoordinator.Attack before calling MobInstance.TryAcquireTarget - see that call
    // site's own doc comment.
    CanAttack = 0x0000080,
    // MD_CHANGETARGETMELEE (mmo.hpp:256, 0x0001000) - pinned mob_can_changetarget's own MSS_BERSERK
    // case (mob.cpp:1242): whether a mob already attacking one target in melee range may switch to
    // a DIFFERENT attacker. Consulted by MobInstance.TryAcquireTarget when MobCombatState is
    // Berserk.
    ChangeTargetMelee = 0x0001000,
    // MD_CHANGETARGETCHASE (mmo.hpp:257, 0x0002000) - pinned mob_can_changetarget's own MSS_RUSH
    // case (mob.cpp:1252): whether a mob already chasing one target may switch to a DIFFERENT
    // attacker mid-chase. Consulted by MobInstance.TryAcquireTarget when MobCombatState is Rush -
    // this is the bit G_PORING's real generated mode LACKS, which is why item 6's own acceptance
    // criterion (a second attacker cannot steal an already-chasing G_PORING's target) holds without
    // any mob-ID special case.
    ChangeTargetChase = 0x0002000,
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
// List-shaped blocks (RaceGroups, Modes' unmodeled bits, MvpDrops, Drops) remain out of scope for
// this record: RaceGroups has no CHK_RACE-style fixed bound and no runtime consumer, and
// MvpDrops/Drops are each already their own dedicated analyzer component
// (ai/world-data.md "Two static-vs-runtime splits"), not a scalar field on this record.
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
    MobClass Class = MobClass.Normal);

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
