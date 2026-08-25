using System.Text.Json.Serialization;

internal sealed record WorldEntityDefinition(int SchemaVersion, string Id, string Kind, WorldActorComponent? Actor, IReadOnlyList<WorldTriggerDefinition> Triggers, IReadOnlyList<ScriptBehaviorDefinition>? Scripts, WorldSourceInfo Source);
internal sealed record WorldActorComponent(string Name, string Map, ushort X, ushort Y, byte Direction, ushort Class, uint EffectState = 0);
internal sealed record WorldTriggerDefinition(string Type, string Map, ushort X, ushort Y, ushort RadiusX, ushort RadiusY, IReadOnlyList<WorldActionDefinition> Actions);
internal sealed record ScriptBehaviorDefinition(string Trigger, string Map, ushort X, ushort Y, ushort RadiusX, ushort RadiusY, bool SourceParsed, bool RuntimeExecutable, IReadOnlyList<string> RequiredCapabilities, string NormalizedSource, IReadOnlyList<ScriptInstructionDefinition>? Instructions = null, string? BaseNpcName = null);
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(MessageInstruction), "Message")]
[JsonDerivedType(typeof(NextInstruction), "Next")]
[JsonDerivedType(typeof(CloseInstruction), "Close")]
[JsonDerivedType(typeof(Close2Instruction), "Close2")]
[JsonDerivedType(typeof(SelectInstruction), "Select")]
[JsonDerivedType(typeof(CompleteQuestInstruction), "CompleteQuest")]
[JsonDerivedType(typeof(SetQuestInstruction), "SetQuest")]
[JsonDerivedType(typeof(IfQuestStateInstruction), "IfQuestState")]
[JsonDerivedType(typeof(AssignmentInstruction), "Assign")]
[JsonDerivedType(typeof(WarpInstruction), "Warp")]
[JsonDerivedType(typeof(SavePointInstruction), "SavePoint")]
internal abstract record ScriptInstructionDefinition;
internal sealed record MessageInstruction(string Text) : ScriptInstructionDefinition;
internal sealed record NextInstruction : ScriptInstructionDefinition;
internal sealed record CloseInstruction : ScriptInstructionDefinition;
internal sealed record Close2Instruction : ScriptInstructionDefinition;
internal sealed record SelectInstruction(IReadOnlyList<SelectOptionDefinition> Options) : ScriptInstructionDefinition;
internal sealed record SelectOptionDefinition(string Text, IReadOnlyList<ScriptInstructionDefinition> Instructions);
internal sealed record CompleteQuestInstruction(uint QuestId) : ScriptInstructionDefinition;
internal sealed record SetQuestInstruction(uint QuestId) : ScriptInstructionDefinition;
internal sealed record IfQuestStateInstruction(uint QuestId, string Expected, IReadOnlyList<ScriptInstructionDefinition> Then, IReadOnlyList<ScriptInstructionDefinition> Else) : ScriptInstructionDefinition;
internal sealed record AssignmentInstruction(string Variable, ScriptExpressionDefinition Value) : ScriptInstructionDefinition;
internal sealed record WarpInstruction(ScriptExpressionDefinition Map, ushort X, ushort Y) : ScriptInstructionDefinition;
internal sealed record SavePointInstruction(ScriptExpressionDefinition Map, ushort X, ushort Y, ushort RadiusX = 0, ushort RadiusY = 0) : ScriptInstructionDefinition;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(StringLiteralExpression), "String")]
[JsonDerivedType(typeof(VariableExpression), "Variable")]
[JsonDerivedType(typeof(ConcatExpression), "Concat")]
[JsonDerivedType(typeof(StrNpcInfoExpression), "StrNpcInfo")]
[JsonDerivedType(typeof(ReplaceStringExpression), "ReplaceString")]
internal abstract record ScriptExpressionDefinition;
internal sealed record StringLiteralExpression(string Value) : ScriptExpressionDefinition;
internal sealed record VariableExpression(string Name) : ScriptExpressionDefinition;
internal sealed record ConcatExpression(ScriptExpressionDefinition Left, ScriptExpressionDefinition Right) : ScriptExpressionDefinition;
internal sealed record StrNpcInfoExpression(int InfoType) : ScriptExpressionDefinition;
internal sealed record ReplaceStringExpression(ScriptExpressionDefinition Value, ScriptExpressionDefinition Search, ScriptExpressionDefinition Replacement) : ScriptExpressionDefinition;
[JsonPolymorphic(TypeDiscriminatorPropertyName = "Type")]
[JsonDerivedType(typeof(SetSavePointAction), "SetSavePoint")]
[JsonDerivedType(typeof(WarpAction), "Warp")]
internal abstract record WorldActionDefinition;
internal sealed record WarpAction(string Map, ushort X, ushort Y) : WorldActionDefinition;
internal sealed record SetSavePointAction(string Map, ushort X, ushort Y) : WorldActionDefinition;
internal sealed record WorldSourceInfo(string Repository, string Commit, string File, int Line);
internal static class DeterministicId
{
    public static string For(string kind, string map, string name) => $"{kind.ToLowerInvariant()}:{map.ToLowerInvariant()}:{name.TrimStart('#').ToLowerInvariant()}";
    public static string FileName(string id) => id[(id.LastIndexOf(':') + 1)..];
    public static string ForDefinition(string templateSourceFile, string templateNpcSymbol) =>
        $"npcdef:{CanonicalizeSourcePath(templateSourceFile).ToLowerInvariant()}:{templateNpcSymbol.TrimStart('#').ToLowerInvariant()}";

    // Mirrors Program.cs's CanonicalSourceFile: normalizes path separators and slices from the
    // legacy/rathena/ anchor when present, so DefinitionId is identical across Windows/macOS/Linux.
    public static string CanonicalizeSourcePath(string path)
    {
        var normalized = path.Replace('\\', '/');
        var legacy = normalized.IndexOf("legacy/rathena/", StringComparison.Ordinal);
        return legacy >= 0 ? normalized[legacy..] : normalized;
    }
}

internal sealed record NpcDefinition(
    int SchemaVersion, string DefinitionId, string TemplateNpcName,
    IReadOnlyList<NpcTriggerBehavior> Triggers, WorldSourceInfo Source, string RawScriptBody);

internal sealed record NpcTriggerBehavior(
    string Trigger, bool SourceParsed, bool RuntimeExecutable,
    IReadOnlyList<string> RequiredCapabilities, string NormalizedSource,
    IReadOnlyList<ScriptInstructionDefinition>? Instructions = null);

internal sealed record NpcPlacement(
    string PlacementId, string DefinitionId, string NpcName,
    string Map, ushort X, ushort Y, byte Direction, ushort Class,
    ushort RadiusX, ushort RadiusY,
    uint? InitialEffectState, WorldSourceInfo Source);

// Mirrors NpcDefinition/NpcPlacement for the rAthena WARPNPC script+duplicate() pattern
// (#ship_out, #intro_to_izlude): one shared OnTouch behavior, N placements. No sprite/class field -
// WARPNPC instances always resolve to class 45 (JT_WARPNPC), unlike ordinary NPC sprites.
internal sealed record WarpTriggerDefinition(
    int SchemaVersion, string DefinitionId, string TemplateNpcName,
    NpcTriggerBehavior OnTouch, WorldSourceInfo Source, string RawScriptBody);

internal sealed record WarpTriggerPlacement(
    string PlacementId, string DefinitionId, string NpcName,
    string Map, ushort X, ushort Y, byte Direction,
    ushort RadiusX, ushort RadiusY, WorldSourceInfo Source);
