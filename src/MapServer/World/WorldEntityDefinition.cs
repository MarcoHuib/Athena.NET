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

// Immutable, source-backed monster data (pinned rAthena db/re/mob_db.yml).
// Renewal semantics: Attack -> rhw.atk (weapon-roll component when this mob
// is the ATTACKER, irrelevant when it is the target), Defense -> hard DEF,
// MagicDefense -> mdef (magic only). Soft physical DEF (def2/"vit_def") is
// NOT a YAML field: it is derived at combat time as floor((Level+Vit)/2)
// (status.cpp status_calc_misc, BL_MOB branch) from Vit here. BaseExp/JobExp
// are 0 when the pinned block omits them entirely (rAthena YAML loader
// default), matching a tutorial punching-bag mob - this is read from source,
// never assumed nonzero because CharacterProgressionService exists.
public sealed record MobDefinition(
    int Id, string AegisName, string Name, int Level, uint MaxHp,
    int Attack, int Attack2, int Defense, int MagicDefense,
    int Str, int Agi, int Vit, int Int, int Dex, int Luk,
    int AttackRange, int WalkSpeed, int AttackDelay,
    long BaseExp, long JobExp,
    WorldSourceInfo Source);

// One pinned `monster` spawn-line declaration (npc/re/mobs/*.txt), scoped to
// a single map. `Count` instances are maintained on that map; `RespawnDelayMs`
// is the pinned mob.delay1 (npc_parse_mob defaults to 5000 when unspecified).
public sealed record MobSpawnDefinition(MobDefinition Mob, string Map, int Count, int RespawnDelayMs, WorldSourceInfo Source);

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
