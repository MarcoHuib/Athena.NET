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
public sealed record MobDefinition(
    int Id, string AegisName, string Name, int Level, uint MaxHp,
    int Attack, int Attack2, int Defense, int MagicDefense,
    int Str, int Agi, int Vit, int Int, int Dex, int Luk,
    int AttackRange, int WalkSpeed, int AttackDelay,
    long BaseExp, long JobExp, MobMode Mode,
    WorldSourceInfo Source);

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
