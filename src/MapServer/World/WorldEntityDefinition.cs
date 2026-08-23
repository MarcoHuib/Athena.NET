namespace Athena.Net.MapServer.World;

public sealed record WorldEntityDefinition(int SchemaVersion, string Id, string Kind, WorldActorComponent? Actor, IReadOnlyList<WorldTriggerDefinition> Triggers, IReadOnlyList<ScriptBehaviorDefinition> Scripts, WorldSourceInfo Source);
public sealed record WorldActorComponent(string Name, string Map, ushort X, ushort Y, byte Direction, ushort Class);
public sealed record WorldTriggerDefinition(string Type, string Map, ushort X, ushort Y, ushort RadiusX, ushort RadiusY, IReadOnlyList<WorldActionDefinition> Actions);
public sealed record ScriptBehaviorDefinition(string Trigger, string Map, ushort X, ushort Y, ushort RadiusX, ushort RadiusY, bool SourceParsed, bool RuntimeExecutable, IReadOnlyList<string> RequiredCapabilities, string NormalizedSource, IReadOnlyList<ScriptInstructionDefinition>? Instructions = null);
public abstract record ScriptInstructionDefinition;
public sealed record MessageInstruction(string Text) : ScriptInstructionDefinition;
public sealed record NextInstruction : ScriptInstructionDefinition;
public sealed record CloseInstruction : ScriptInstructionDefinition;
public sealed record SelectInstruction(IReadOnlyList<SelectOptionDefinition> Options) : ScriptInstructionDefinition;
public sealed record SelectOptionDefinition(string Text, IReadOnlyList<ScriptInstructionDefinition> Instructions);
public sealed record SetQuestInstruction(uint QuestId) : ScriptInstructionDefinition;
public sealed record CompleteQuestInstruction(uint QuestId) : ScriptInstructionDefinition;
public sealed record IfQuestStateInstruction(uint QuestId, CharacterQuestStatus Expected, IReadOnlyList<ScriptInstructionDefinition> Then, IReadOnlyList<ScriptInstructionDefinition> Else) : ScriptInstructionDefinition;
public abstract record WorldActionDefinition;
public sealed record WarpAction(string Map, ushort X, ushort Y) : WorldActionDefinition;
public sealed record SetSavePointAction(string Map, ushort X, ushort Y) : WorldActionDefinition;
public sealed record WorldSourceInfo(string Repository, string Commit, string File, int Line);
