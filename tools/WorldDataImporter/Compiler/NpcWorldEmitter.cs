using System.Text;
using Athena.WorldCompiler;
using Athena.WorldCompiler.Lowering;

namespace Athena.WorldCompiler.Generation;

// Emission-selection is a generic, non-NPC-specific mechanism applied strictly AFTER
// WorldEntityConverter.ConvertNpcDefinitions returns its complete, lossless semantic result.
// It expresses "this generated world slice uses only these placements/behaviors" without any
// name-based filtering inside the converter itself. Two independent knobs:
//   - IncludedPlacementIds: null means "include every placement the converter found" (the normal,
//     fully-reproducible-from-source case); a non-null set narrows to specific PlacementIds - used
//     by the current Academy migration to preserve today's exact 4-placement Captain Carocc/Lumin
//     shape while ConvertNpcDefinitions itself still computes the full lossless 5.
//   - DefinitionsWithoutEmittedBehavior: definitions whose script class must NOT be generated/registered
//     even though the converter found executable triggers for them (Captain Carocc/Lumin today, pending
//     real healing/EXP/status/inventory runtime support per ai/world-data.md). Their NpcDefinition is
//     still emitted with Behaviors: [] - proving the same model supports "definition + placements + no
//     behavior" without deleting real rAthena content from the semantic picture.
internal sealed record NpcWorldEmissionSelection(
    IReadOnlySet<string>? IncludedPlacementIds = null,
    IReadOnlySet<string>? DefinitionsWithoutEmittedBehavior = null)
{
    public static readonly NpcWorldEmissionSelection Unrestricted = new();
    public bool IncludesPlacement(string placementId) => IncludedPlacementIds is null || IncludedPlacementIds.Contains(placementId);
    public bool EmitsBehaviorFor(string definitionId) => DefinitionsWithoutEmittedBehavior is null || !DefinitionsWithoutEmittedBehavior.Contains(definitionId);
}

internal sealed record WarpTriggerEmissionSelection(IReadOnlySet<string>? IncludedPlacementIds = null)
{
    public static readonly WarpTriggerEmissionSelection Unrestricted = new();
    public bool IncludesPlacement(string placementId) => IncludedPlacementIds is null || IncludedPlacementIds.Contains(placementId);
}

internal sealed record NpcWorldEmissionResult(
    string AcademyWorldSource,
    string AcademyNpcsSource,
    string? AcademyWarpTriggersSource,
    IReadOnlyDictionary<string, string> ScriptSources); // className -> source, one file per class

internal static class NpcWorldEmitter
{
    public static NpcWorldEmissionResult Emit(
        NpcConversionResult conversion,
        NpcWorldEmissionSelection selection,
        string worldNamespace,
        string scriptsNamespace,
        string sourceCommit,
        WarpTriggerConversionResult? warpConversion = null,
        WarpTriggerEmissionSelection? warpSelection = null,
        string prefix = "Academy")
    {
        var placementsByDefinition = conversion.Placements
            .Where(placement => selection.IncludesPlacement(placement.PlacementId))
            .GroupBy(placement => placement.DefinitionId)
            .ToDictionary(group => group.Key, group => group.OrderBy(p => p.PlacementId, StringComparer.Ordinal).ToArray());

        var definitions = conversion.Definitions
            .Where(definition => placementsByDefinition.ContainsKey(definition.DefinitionId))
            .OrderBy(definition => definition.DefinitionId, StringComparer.Ordinal)
            .ToArray();

        var byBaseName = definitions.ToLookup(definition => BaseName(definition.TemplateNpcName));
        var scriptSources = new Dictionary<string, string>(StringComparer.Ordinal);
        var npcFieldsByDefinition = new Dictionary<string, (string FieldName, string Literal)>(StringComparer.Ordinal);
        var registerCalls = new List<string>();

        foreach (var definition in definitions)
        {
            var emitsBehavior = selection.EmitsBehaviorFor(definition.DefinitionId) && definition.Triggers.Count > 0;
            var triggerFactories = new List<(string Trigger, string ClassName)>();

            if (emitsBehavior)
            {
                foreach (var trigger in definition.Triggers)
                {
                    // Not every rAthena trigger a definition claims is actually lowerable to generated C#
                    // today (e.g. sleep2/timer constructs) - a trigger that fails to lower is skipped for
                    // emission rather than aborting the whole invocation, matching the project's existing
                    // tolerant-partial-support philosophy (see TryPreserveScript for the warp equivalent).
                    if (!TryLowerTrigger(trigger.Trigger, definition.RawScriptBody, definition.Source, out var lowered, out var failureReason))
                    {
                        Console.Error.WriteLine($"Skipping unlowerable trigger '{trigger.Trigger}' for '{definition.TemplateNpcName}': {failureReason}");
                        continue;
                    }
                    var className = DefinitionClassName(byBaseName, definition, trigger.Trigger);
                    var definitionMetadata = new GeneratedDefinitionMetadata(scriptsNamespace, className, definition.DefinitionId,
                        definition.TemplateNpcName, definition.Source.File, definition.Source.Line, sourceCommit);
                    var triggerMetadata = new GeneratedTriggerMetadata(trigger.Trigger, definition.Source.File, definition.Source.Line + 1, definition.Source.Line);
                    scriptSources[className] = NpcScriptEmitter.EmitScriptClass(lowered, definitionMetadata, triggerMetadata, className);
                    triggerFactories.Add((trigger.Trigger, className));
                }
            }

            var fieldName = DefinitionFieldName(byBaseName, definition);
            npcFieldsByDefinition[definition.DefinitionId] = (fieldName, EmitDefinitionField(fieldName, definition, triggerFactories, scriptsNamespace, sourceCommit));

            var placements = placementsByDefinition[definition.DefinitionId];
            registerCalls.Add(EmitNpcRegisterCall(fieldName, placements, sourceCommit, prefix));
        }

        string? academyWarpTriggersSource = null;
        if (warpConversion is not null)
        {
            var effectiveWarpSelection = warpSelection ?? WarpTriggerEmissionSelection.Unrestricted;
            var warpPlacementsByDefinition = warpConversion.Placements
                .Where(placement => effectiveWarpSelection.IncludesPlacement(placement.PlacementId))
                .GroupBy(placement => placement.DefinitionId)
                .ToDictionary(group => group.Key, group => group.OrderBy(p => p.PlacementId, StringComparer.Ordinal).ToArray());

            var warpDefinitions = warpConversion.Definitions
                .Where(definition => warpPlacementsByDefinition.ContainsKey(definition.DefinitionId))
                .OrderBy(definition => definition.DefinitionId, StringComparer.Ordinal)
                .ToArray();

            var warpByBaseName = warpDefinitions.ToLookup(definition => BaseName(definition.TemplateNpcName));
            var warpFieldsByDefinition = new Dictionary<string, (string FieldName, string Literal)>(StringComparer.Ordinal);

            foreach (var definition in warpDefinitions)
            {
                var trigger = definition.OnTouch;
                if (!TryLowerTrigger(trigger.Trigger, definition.RawScriptBody, definition.Source, out var lowered, out var failureReason))
                {
                    Console.Error.WriteLine($"Skipping unlowerable trigger '{trigger.Trigger}' for '{definition.TemplateNpcName}': {failureReason}");
                    continue;
                }
                var className = WarpDisambiguatedBaseName(warpByBaseName, definition) + trigger.Trigger + "Script";
                var definitionMetadata = new GeneratedDefinitionMetadata(scriptsNamespace, className, definition.DefinitionId,
                    definition.TemplateNpcName, definition.Source.File, definition.Source.Line, sourceCommit);
                var triggerMetadata = new GeneratedTriggerMetadata(trigger.Trigger, definition.Source.File, definition.Source.Line + 1, definition.Source.Line);
                scriptSources[className] = NpcScriptEmitter.EmitScriptClass(lowered, definitionMetadata, triggerMetadata, className);

                var fieldName = WarpDisambiguatedBaseName(warpByBaseName, definition);
                warpFieldsByDefinition[definition.DefinitionId] = (fieldName, EmitWarpTriggerDefinitionField(fieldName, definition, className, scriptsNamespace, sourceCommit, prefix));

                var placements = warpPlacementsByDefinition[definition.DefinitionId];
                registerCalls.Add(EmitWarpTriggerRegisterCall(fieldName, placements, prefix));
            }

            academyWarpTriggersSource = EmitAcademyWarpTriggers(worldNamespace, warpFieldsByDefinition.Values.Select(v => v.Literal), prefix);
        }

        return new(
            EmitAcademyWorld(worldNamespace, registerCalls, prefix),
            EmitAcademyNpcs(worldNamespace, npcFieldsByDefinition.Values.Select(v => v.Literal), prefix),
            academyWarpTriggersSource,
            scriptSources);
    }

    // Parses the full raw script body (all labels present), exactly matching Program.cs's proven
    // CompileScriptAsync approach - LowerEvent locates the requested trigger's label itself (or falls
    // back to the implicit-OnClick convention when no label exists) within the complete compilation unit.
    // Shared by both NPC and warp-trigger definitions - takes only the raw strings each needs, not either
    // definition type, since NpcDefinition and WarpTriggerDefinition intentionally don't share a base type.
    private static bool TryLowerTrigger(string triggerName, string rawScriptBody, WorldSourceInfo source, out LoweredNpcScript script, out string failureReason)
    {
        script = null!; failureReason = "";
        var (syntax, semantics) = RathenaEventCompiler.Parse(rawScriptBody, source);
        var compilation = RathenaEventCompiler.Compile(syntax, semantics, triggerName);
        if (!compilation.Success)
        {
            failureReason = string.Join(Environment.NewLine, compilation.Diagnostics.Where(d => d.Severity == "Error").Select(d => d.Message));
            return false;
        }
        script = compilation.Script!;
        return true;
    }

    private static string EmitDefinitionField(string fieldName, NpcDefinition definition, IReadOnlyList<(string Trigger, string ClassName)> behaviors, string scriptsNamespace, string sourceCommit)
    {
        var output = new StringBuilder();
        output.Append("    internal static readonly NpcDefinition ").Append(fieldName).Append(" = new(\n");
        output.Append("        \"").Append(E(definition.DefinitionId)).Append("\", \"").Append(E(definition.TemplateNpcName)).Append("\",\n");
        output.Append("        [");
        for (var index = 0; index < behaviors.Count; index++)
        {
            if (index > 0) output.Append(", ");
            output.Append("new(\"").Append(behaviors[index].Trigger).Append("\", static () => new ").Append(scriptsNamespace).Append('.').Append(behaviors[index].ClassName).Append("())");
        }
        output.Append("],\n");
        output.Append("        new(\"rAthena\", \"").Append(sourceCommit).Append("\", \"").Append(E(definition.Source.File)).Append("\", ").Append(definition.Source.Line).Append("));\n");
        return output.ToString();
    }

    private static string EmitNpcRegisterCall(string fieldName, IReadOnlyList<NpcPlacement> placements, string sourceCommit, string prefix)
    {
        var output = new StringBuilder();
        output.Append("        world.AddNpc(").Append(prefix).Append("Npcs.").Append(fieldName).Append(",\n        [\n");
        foreach (var placement in placements)
        {
            output.Append("            new(\"").Append(E(placement.PlacementId)).Append("\", ").Append(prefix).Append("Npcs.").Append(fieldName).Append(".DefinitionId, \"")
                .Append(E(placement.NpcName)).Append("\", \"").Append(E(placement.Map)).Append("\", ")
                .Append(placement.X).Append(", ").Append(placement.Y).Append(", ").Append(placement.Direction).Append(", ").Append(placement.Class).Append(", ")
                .Append(placement.RadiusX).Append(", ").Append(placement.RadiusY).Append(", ").Append(placement.InitialEffectState ?? 0).Append("),\n");
        }
        output.Append("        ]);\n");
        return output.ToString();
    }

    private static string EmitWarpTriggerDefinitionField(string fieldName, WarpTriggerDefinition definition, string className, string scriptsNamespace, string sourceCommit, string prefix)
    {
        var output = new StringBuilder();
        output.Append("    internal static readonly WarpTriggerDefinition ").Append(fieldName).Append(" = new(\n");
        output.Append("        \"").Append(E(definition.DefinitionId)).Append("\", \"").Append(E(definition.TemplateNpcName)).Append("\",\n");
        output.Append("        new(\"").Append(definition.OnTouch.Trigger).Append("\", static () => new ").Append(scriptsNamespace).Append('.').Append(className).Append("()),\n");
        output.Append("        new(\"rAthena\", \"").Append(sourceCommit).Append("\", \"").Append(E(definition.Source.File)).Append("\", ").Append(definition.Source.Line).Append("));\n");
        return output.ToString();
    }

    private static string EmitWarpTriggerRegisterCall(string fieldName, IReadOnlyList<WarpTriggerPlacement> placements, string prefix)
    {
        var output = new StringBuilder();
        output.Append("        world.AddWarpTrigger(").Append(prefix).Append("WarpTriggers.").Append(fieldName).Append(",\n        [\n");
        foreach (var placement in placements)
        {
            output.Append("            new(\"").Append(E(placement.PlacementId)).Append("\", ").Append(prefix).Append("WarpTriggers.").Append(fieldName).Append(".DefinitionId, \"")
                .Append(E(placement.NpcName)).Append("\", \"").Append(E(placement.Map)).Append("\", ")
                .Append(placement.X).Append(", ").Append(placement.Y).Append(", ").Append(placement.Direction).Append(", ")
                .Append(placement.RadiusX).Append(", ").Append(placement.RadiusY).Append("),\n");
        }
        output.Append("        ]);\n");
        return output.ToString();
    }

    private static string EmitAcademyWarpTriggers(string worldNamespace, IEnumerable<string> fields, string prefix)
    {
        var output = new StringBuilder();
        output.AppendLine("// <auto-generated>");
        output.AppendLine("// Generated by Athena.WorldCompiler.");
        output.AppendLine("// Do not edit this file directly.");
        output.AppendLine("// </auto-generated>");
        output.AppendLine("using Athena.Net.MapServer.World;");
        output.AppendLine();
        output.Append("namespace ").Append(worldNamespace).AppendLine(";");
        output.AppendLine();
        output.Append("internal static class ").Append(prefix).AppendLine("WarpTriggers");
        output.AppendLine("{");
        foreach (var field in fields) output.Append(field);
        output.AppendLine("}");
        return output.ToString();
    }

    private static string EmitAcademyNpcs(string worldNamespace, IEnumerable<string> fields, string prefix)
    {
        var output = new StringBuilder();
        output.AppendLine("// <auto-generated>");
        output.AppendLine("// Generated by Athena.WorldCompiler.");
        output.AppendLine("// Do not edit this file directly.");
        output.AppendLine("// </auto-generated>");
        output.AppendLine("using Athena.Net.MapServer.World;");
        output.AppendLine();
        output.Append("namespace ").Append(worldNamespace).AppendLine(";");
        output.AppendLine();
        output.Append("internal static class ").Append(prefix).AppendLine("Npcs");
        output.AppendLine("{");
        foreach (var field in fields) output.Append(field);
        output.AppendLine("}");
        return output.ToString();
    }

    private static string EmitAcademyWorld(string worldNamespace, IEnumerable<string> registerCalls, string prefix)
    {
        var output = new StringBuilder();
        output.AppendLine("// <auto-generated>");
        output.AppendLine("// Generated by Athena.WorldCompiler.");
        output.AppendLine("// Do not edit this file directly.");
        output.AppendLine("// </auto-generated>");
        output.AppendLine("using Athena.Net.MapServer.World;");
        output.AppendLine();
        output.Append("namespace ").Append(worldNamespace).AppendLine(";");
        output.AppendLine();
        output.Append("internal static class ").Append(prefix).AppendLine("World");
        output.AppendLine("{");
        output.AppendLine("    public static void Register(WorldRegistryBuilder world)");
        output.AppendLine("    {");
        foreach (var call in registerCalls) output.Append(call);
        output.AppendLine("    }");
        output.AppendLine("}");
        return output.ToString();
    }

    // Deterministic, identity-derived - never a positional/batch-order counter. Unique display names
    // (the common case) get the plain PascalCase name. A collision first tries a readable suffix drawn
    // from the template's own '#'-qualifier (e.g. "IntroNpc01IzInt" from "...#intro_npc01_iz_int"),
    // since that's real rAthena-authored disambiguating text, not an artifact of this compiler. Only if
    // that ALSO collides (two templates sharing both display name and qualifier) does it fall back to a
    // short hash of the definition's own stable DefinitionId - the fallback, not the default.
    private static string DefinitionClassName(ILookup<string, NpcDefinition> byBaseName, NpcDefinition definition, string trigger) =>
        DisambiguatedBaseName(byBaseName, definition) + trigger + "Script";

    private static string DefinitionFieldName(ILookup<string, NpcDefinition> byBaseName, NpcDefinition definition) =>
        DisambiguatedBaseName(byBaseName, definition);

    private static string DisambiguatedBaseName(ILookup<string, NpcDefinition> byBaseName, NpcDefinition definition)
    {
        var baseName = BaseName(definition.TemplateNpcName);
        var colliding = byBaseName[baseName].OrderBy(item => item.DefinitionId, StringComparer.Ordinal).ToArray();
        if (colliding.Length <= 1) return baseName;

        var qualified = colliding.Select(item => baseName + QualifierSuffix(item.TemplateNpcName)).ToArray();
        var qualifiersAreUnique = qualified.Distinct(StringComparer.Ordinal).Count() == qualified.Length;
        if (qualifiersAreUnique)
        {
            var index = Array.IndexOf(colliding, definition);
            return qualified[index];
        }
        return baseName + IdentityQualifier(definition.DefinitionId);
    }

    private static string WarpDisambiguatedBaseName(ILookup<string, WarpTriggerDefinition> byBaseName, WarpTriggerDefinition definition)
    {
        var baseName = BaseName(definition.TemplateNpcName);
        var colliding = byBaseName[baseName].OrderBy(item => item.DefinitionId, StringComparer.Ordinal).ToArray();
        if (colliding.Length <= 1) return baseName;

        var qualified = colliding.Select(item => baseName + QualifierSuffix(item.TemplateNpcName)).ToArray();
        var qualifiersAreUnique = qualified.Distinct(StringComparer.Ordinal).Count() == qualified.Length;
        if (qualifiersAreUnique)
        {
            var index = Array.IndexOf(colliding, definition);
            return qualified[index];
        }
        return baseName + IdentityQualifier(definition.DefinitionId);
    }

    // The template's '#'-qualifier, PascalCased into a symbol-safe suffix - e.g. "#intro_npc01_iz_int" -> "IntroNpc01IzInt".
    private static string QualifierSuffix(string templateNpcName)
    {
        var hashIndex = templateNpcName.IndexOf('#');
        if (hashIndex < 0) return "";
        return string.Concat(templateNpcName[(hashIndex + 1)..]
            .Split(['_', ' ', '-'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string IdentityQualifier(string definitionId) =>
        Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes(definitionId)))[..6];

    // NPC template names carry their qualifier after a '#' (e.g. "Wounded Swordsman#intro_npc02_iz_int" ->
    // "Wounded Swordsman"). Warp trigger names ARE the '#'-prefixed symbol itself (e.g. "#ship_out") with no
    // separate display-name segment, so the pre-'#' slice is empty and the whole trimmed name is the base.
    private static string BaseName(string templateNpcName)
    {
        var display = templateNpcName.Split('#')[0];
        // NPC display names use spaces ("Wounded Swordsman"); warp trigger symbols use underscores
        // ("#ship_out", "#intro_to_izlude") since the whole '#'-prefixed name IS the symbol, not a
        // separate human-readable display segment - split on both so each PascalCases correctly.
        var source = display.Length > 0 ? display : templateNpcName.TrimStart('#');
        return string.Concat(source.Split([' ', '-', '_'], StringSplitOptions.RemoveEmptyEntries)
            .Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
    }

    private static string E(string value) => value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal);
}
