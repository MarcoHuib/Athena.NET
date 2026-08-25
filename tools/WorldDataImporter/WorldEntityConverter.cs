using System.Globalization;
using System.Text.RegularExpressions;

internal sealed record ConversionFilter(string? SourceFile, string? Map, string? Name, string? Kind)
{
    public bool IsEmpty => SourceFile is null && Map is null && Name is null && Kind is null;
    public bool Matches(RathenaDeclaration value) =>
        (SourceFile is null || value.Source.File.EndsWith(SourceFile, StringComparison.OrdinalIgnoreCase)) &&
        (Map is null || string.Equals(value.Map, Map, StringComparison.OrdinalIgnoreCase)) &&
        (Name is null || string.Equals(value.Name, Name, StringComparison.Ordinal)) &&
        (Kind is null || string.Equals(Kind, "warp", StringComparison.OrdinalIgnoreCase) ||
            (string.Equals(Kind, "npc", StringComparison.OrdinalIgnoreCase) &&
             (value.Directive == "script" || value.Directive.StartsWith("duplicate(", StringComparison.Ordinal)) &&
             !value.Arguments.Contains("WARPNPC", StringComparison.Ordinal)));
}
internal sealed record UnsupportedConversion(string Name, string File, int Line, string Reason);
internal sealed record ConversionResult(IReadOnlyList<WorldEntityDefinition> Entities, IReadOnlyList<UnsupportedConversion> Unsupported);
internal sealed record NpcConversionResult(IReadOnlyList<NpcDefinition> Definitions, IReadOnlyList<NpcPlacement> Placements, IReadOnlyList<UnsupportedConversion> Unsupported);
internal sealed record WarpTriggerConversionResult(IReadOnlyList<WarpTriggerDefinition> Definitions, IReadOnlyList<WarpTriggerPlacement> Placements, IReadOnlyList<UnsupportedConversion> Unsupported);

internal static partial class WorldEntityConverter
{
    private const string Commit = "6e6bca69b8a2ee03cd744cbc7a78a054a6f376ca";

    public static ConversionResult Convert(IEnumerable<string> roots, ConversionFilter filter)
    {
        var declarations = RathenaSourceParser.Parse(roots);
        var templates = declarations.Where(item => item.Directive == "script").ToDictionary(item => item.Name, StringComparer.Ordinal);
        var entities = new List<WorldEntityDefinition>();
        var unsupported = new List<UnsupportedConversion>();
        foreach (var declaration in declarations.Where(filter.Matches))
        {
            if (declaration.Directive == "warp")
            {
                if (TryDeclarative(declaration, out var entity)) entities.Add(entity);
                else unsupported.Add(Unsupported(declaration, "Malformed declarative warp"));
            }
            else if (declaration.Directive == "script" && IsWarpNpc(declaration))
            {
                if (TryScripted(declaration, declaration, out var entity)) entities.Add(entity);
                else if (TryPreserveScript(declaration, declaration, out entity, unsupported)) entities.Add(entity);
                else unsupported.Add(Unsupported(declaration, "Malformed WARPNPC script"));
            }
            else if (TryDuplicateName(declaration.Directive, out var templateName) && IsWarpNpc(declaration) && templates.TryGetValue(templateName, out var template))
            {
                // Deliberately evaluate the template with the duplicate's own name.
                if (TryScripted(declaration, template, out var entity)) entities.Add(entity);
                else if (TryPreserveScript(declaration, template, out entity, unsupported)) entities.Add(entity);
                else unsupported.Add(Unsupported(declaration, "Malformed WARPNPC duplicate"));
            }
            else if (declaration.Directive == "script" && TryOrdinaryScript(declaration, roots, out var ordinary)) entities.Add(ordinary);
            else if (TryDuplicateName(declaration.Directive, out templateName) && templates.TryGetValue(templateName, out template) &&
                     TryOrdinaryScript(declaration, template, roots, out ordinary)) entities.Add(ordinary);
        }
        return new(entities.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(), unsupported);
    }

    // Lossless semantic conversion: resolution always scans the FULL declaration index parsed from
    // roots, independent of filter. Only which template GROUPS get converted/emitted is scoped by
    // filter (matched against the template's own declaration). This keeps duplicate resolution correct
    // even if a duplicate ever lived in a different file than its template, and keeps emission-scope
    // decisions (e.g. limiting which placements a particular generated world slice uses) entirely
    // outside this method - callers narrow AFTER receiving the complete NpcConversionResult.
    public static NpcConversionResult ConvertNpcDefinitions(IEnumerable<string> roots, ConversionFilter filter)
    {
        var rootList = roots as IReadOnlyList<string> ?? roots.ToArray();
        var declarations = RathenaSourceParser.Parse(rootList);
        var templates = declarations.Where(item => item.Directive == "script").ToDictionary(item => item.Name, StringComparer.Ordinal);
        var definitions = new List<NpcDefinition>();
        var placements = new List<NpcPlacement>();
        var unsupported = new List<UnsupportedConversion>();

        var resolved = declarations
            .Where(declaration => !IsWarpNpc(declaration) && declaration.Directive != "warp")
            .Select(declaration => (declaration, template: ResolveTemplate(declaration, templates)))
            .Where(pair => pair.template is not null)
            .Select(pair => (pair.declaration, template: pair.template!));

        var scoped = resolved.Where(pair => filter.Matches(pair.template));

        foreach (var group in scoped.GroupBy(pair => pair.template))
        {
            var template = group.Key;
            if (!TryBuildDefinition(template, out var definition)) { unsupported.Add(Unsupported(template, "Malformed NPC template")); continue; }
            definitions.Add(definition);
            // cloakonnpc() is an OnInit-only rAthena command (see academy.txt:123) - it never appears inside the
            // OnClick/OnTouch slices captured by NpcTriggerBehavior.NormalizedSource, so the initial-cloak signal
            // must be read from the template's full raw ScriptBody, computed once per group and applied uniformly.
            var initialEffectState = template.ScriptBody.Contains("cloakonnpc();", StringComparison.Ordinal) ? 4u : (uint?)null;
            foreach (var (instance, _) in group)
            {
                if (!TryBuildPlacement(instance, definition, initialEffectState, rootList, out var placement)) { unsupported.Add(Unsupported(instance, "Malformed NPC duplicate placement")); continue; }
                placements.Add(placement);
            }
        }

        return new(
            definitions.OrderBy(item => item.DefinitionId, StringComparer.Ordinal).ToArray(),
            placements.OrderBy(item => item.PlacementId, StringComparer.Ordinal).ToArray(),
            unsupported);
    }

    private static RathenaDeclaration? ResolveTemplate(RathenaDeclaration declaration, Dictionary<string, RathenaDeclaration> templates) =>
        declaration.Directive == "script" ? declaration
        : TryDuplicateName(declaration.Directive, out var name) && templates.TryGetValue(name, out var template) ? template
        : null;

    // Mirrors ConvertNpcDefinitions exactly, scoped to WARPNPC (script+duplicate()) declarations only.
    // Lossless: resolution always scans the full declaration index; emission-scope decisions (which
    // placements a generated world slice actually uses) happen strictly outside this method.
    public static WarpTriggerConversionResult ConvertWarpTriggers(IEnumerable<string> roots, ConversionFilter filter)
    {
        var declarations = RathenaSourceParser.Parse(roots);
        var templates = declarations.Where(item => item.Directive == "script").ToDictionary(item => item.Name, StringComparer.Ordinal);
        var definitions = new List<WarpTriggerDefinition>();
        var placements = new List<WarpTriggerPlacement>();
        var unsupported = new List<UnsupportedConversion>();

        var resolved = declarations
            .Where(IsWarpNpc)
            .Select(declaration => (declaration, template: ResolveTemplate(declaration, templates)))
            .Where(pair => pair.template is not null)
            .Select(pair => (pair.declaration, template: pair.template!));

        var scoped = resolved.Where(pair => filter.Matches(pair.template));

        foreach (var group in scoped.GroupBy(pair => pair.template))
        {
            var template = group.Key;
            if (!TryBuildWarpTriggerDefinition(template, out var definition)) { unsupported.Add(Unsupported(template, "Malformed WARPNPC template")); continue; }
            definitions.Add(definition);
            foreach (var (instance, _) in group)
            {
                if (!TryBuildWarpTriggerPlacement(instance, definition, out var placement)) { unsupported.Add(Unsupported(instance, "Malformed WARPNPC duplicate placement")); continue; }
                placements.Add(placement);
            }
        }

        return new(
            definitions.OrderBy(item => item.DefinitionId, StringComparer.Ordinal).ToArray(),
            placements.OrderBy(item => item.PlacementId, StringComparer.Ordinal).ToArray(),
            unsupported);
    }

    private static bool TryBuildWarpTriggerDefinition(RathenaDeclaration template, out WarpTriggerDefinition definition)
    {
        definition = default!;
        var onTouch = template.ScriptBody.IndexOf("OnTouch:", StringComparison.Ordinal);
        if (onTouch < 0) return false;
        var touchSource = string.Join('\n', template.ScriptBody[(onTouch + "OnTouch:".Length)..]
            .Split("OnInit:", 2, StringSplitOptions.None)[0]
            .Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.TrimEnd())).Trim();
        var trigger = new NpcTriggerBehavior("OnTouch", true, true, ScriptCapabilities.Classify(touchSource), touchSource);
        definition = new(1, DeterministicId.ForDefinition(template.Source.File, template.Name), template.Name, trigger,
            new("rAthena", Commit, template.Source.File, template.Source.Line), template.ScriptBody);
        return true;
    }

    private static bool TryBuildWarpTriggerPlacement(RathenaDeclaration instance, WarpTriggerDefinition definition, out WarpTriggerPlacement placement)
    {
        placement = default!;
        var visual = instance.Arguments.Split(',').Select(value => value.Trim().TrimEnd('{')).ToArray();
        if (visual.Length < 3 || visual[0] != "WARPNPC" || !U16(visual[1], out var rx) || !U16(visual[2], out var ry)) return false;
        placement = new(DeterministicId.For("warp", instance.Map, instance.Name), definition.DefinitionId, instance.Name,
            instance.Map, instance.X, instance.Y, instance.Direction, rx, ry,
            new("rAthena", Commit, instance.Source.File, instance.Source.Line));
        return true;
    }

    private static bool TryBuildDefinition(RathenaDeclaration template, out NpcDefinition definition)
    {
        definition = default!;
        var clickSource = template.ScriptBody.Split("OnTouch:", 2, StringSplitOptions.None)[0].Split("OnInit:", 2, StringSplitOptions.None)[0].Trim();
        var triggers = new List<NpcTriggerBehavior>();
        if (clickSource.Length > 0)
            triggers.Add(new("OnClick", true, true, ScriptCapabilities.Classify(clickSource), clickSource));
        if (template.ScriptBody.Contains("OnTouch:", StringComparison.Ordinal))
        {
            var touchSource = template.ScriptBody.Split("OnTouch:", 2, StringSplitOptions.None)[1].Split("OnInit:", 2, StringSplitOptions.None)[0].Trim();
            triggers.Add(new("OnTouch", true, true, ScriptCapabilities.Classify(touchSource), touchSource));
        }
        definition = new(1, DeterministicId.ForDefinition(template.Source.File, template.Name), template.Name, triggers,
            new("rAthena", Commit, template.Source.File, template.Source.Line), template.ScriptBody);
        return true;
    }

    private static bool TryBuildPlacement(RathenaDeclaration instance, NpcDefinition definition, uint? initialEffectState, IEnumerable<string> roots, out NpcPlacement placement)
    {
        placement = default!;
        var visual = instance.Arguments.Split(',').Select(value => value.Trim().TrimEnd('{')).Where(value => value.Length > 0).ToArray();
        if (visual.Length == 0 || !NpcSpriteClassResolver.TryResolve(roots, visual[0], out var spriteClass)) return false;
        var rx = visual.Length > 1 && U16(visual[1], out var parsedRx) ? parsedRx : (ushort)0;
        var ry = visual.Length > 2 && U16(visual[2], out var parsedRy) ? parsedRy : (ushort)0;
        placement = new(DeterministicId.For("npc", instance.Map, instance.Name), definition.DefinitionId, instance.Name,
            instance.Map, instance.X, instance.Y, instance.Direction, spriteClass, rx, ry, initialEffectState,
            new("rAthena", Commit, instance.Source.File, instance.Source.Line));
        return true;
    }

    private static bool TryOrdinaryScript(RathenaDeclaration source, IEnumerable<string> roots, out WorldEntityDefinition entity)
        => TryOrdinaryScript(source, source, roots, out entity);

    private static bool TryOrdinaryScript(RathenaDeclaration instance, RathenaDeclaration template, IEnumerable<string> roots, out WorldEntityDefinition entity)
    {
        entity = default!;
        var visual = instance.Arguments.Split(',').Select(value => value.Trim().TrimEnd('{')).Where(value => value.Length > 0).ToArray();
        if (visual.Length == 0 || !NpcSpriteClassResolver.TryResolve(roots, visual[0], out var spriteClass)) return false;
        var rx = visual.Length > 1 && U16(visual[1], out var parsedRx) ? parsedRx : (ushort)0;
        var ry = visual.Length > 2 && U16(visual[2], out var parsedRy) ? parsedRy : (ushort)0;
        var clickSource = template.ScriptBody.Split("OnTouch:", 2, StringSplitOptions.None)[0].Split("OnInit:", 2, StringSplitOptions.None)[0].Trim();
        var scripts = new List<ScriptBehaviorDefinition>
        {
            new("OnClick", instance.Map, instance.X, instance.Y, rx, ry, true, true, ScriptCapabilities.Classify(clickSource), clickSource, null,
                ReferenceEquals(instance, template) ? null : template.Name)
        };
        if (template.ScriptBody.Contains("OnTouch:", StringComparison.Ordinal))
        {
            var touchSource = template.ScriptBody.Split("OnTouch:", 2, StringSplitOptions.None)[1].Split("OnInit:", 2, StringSplitOptions.None)[0].Trim();
            scripts.Add(new("OnTouch", instance.Map, instance.X, instance.Y, rx, ry, true, true, ScriptCapabilities.Classify(touchSource), touchSource, null,
                ReferenceEquals(instance, template) ? null : template.Name));
        }
        entity = new(1, DeterministicId.For("npc", instance.Map, instance.Name), "Npc",
            new(instance.Name, instance.Map, instance.X, instance.Y, instance.Direction, spriteClass, template.ScriptBody.Contains("cloakonnpc();", StringComparison.Ordinal) ? 4u : 0u), [],
            scripts,
            new("rAthena", Commit, instance.Source.File, instance.Source.Line));
        return true;
    }

    private static bool TryDeclarative(RathenaDeclaration source, out WorldEntityDefinition entity)
    {
        entity = default!;
        var arguments = BlockCommentRegex().Replace(source.Arguments, "");
        var lineComment = arguments.IndexOf("//", StringComparison.Ordinal);
        if (lineComment >= 0) arguments = arguments[..lineComment];
        var parts = arguments.Split(',').Select(value => value.Trim()).ToArray();
        if (parts.Length != 5 || !U16(parts[0], out var rx) || !U16(parts[1], out var ry) || !U16(parts[3], out var x) || !U16(parts[4], out var y)) return false;
        entity = Create(source, rx, ry, [new WarpAction(parts[2], x, y)]);
        return true;
    }

    private static bool TryScripted(RathenaDeclaration instance, RathenaDeclaration template, out WorldEntityDefinition entity)
    {
        entity = default!;
        var visual = instance.Arguments.Split(',').Select(value => value.Trim().TrimEnd('{')).ToArray();
        if (visual.Length < 3 || visual[0] != "WARPNPC" || !U16(visual[1], out var rx) || !U16(visual[2], out var ry)) return false;
        if (!DeterministicWarpScriptEvaluator.TryEvaluate(template.ScriptBody, instance.Name, out var actions)) return false;
        entity = Create(instance, rx, ry, actions);
        return true;
    }

    private static WorldEntityDefinition Create(RathenaDeclaration source, ushort rx, ushort ry, IReadOnlyList<WorldActionDefinition> actions) =>
        new(1, DeterministicId.For("warp", source.Map, source.Name), "Warp",
            new(source.Name, source.Map, source.X, source.Y, source.Direction, 45),
            [new("OnTouch", source.Map, source.X, source.Y, rx, ry, actions)],
            null,
            new("rAthena", Commit, source.Source.File, source.Source.Line));

    private static bool TryPreserveScript(RathenaDeclaration instance, RathenaDeclaration template, out WorldEntityDefinition entity, List<UnsupportedConversion> unsupported)
    {
        entity = default!;
        var visual = instance.Arguments.Split(',').Select(value => value.Trim().TrimEnd('{')).ToArray();
        if (visual.Length < 3 || visual[0] != "WARPNPC" || !U16(visual[1], out var rx) || !U16(visual[2], out var ry)) return false;
        var onTouch = template.ScriptBody.IndexOf("OnTouch:", StringComparison.Ordinal);
        if (onTouch < 0) return false;
        var normalized = string.Join('\n', template.ScriptBody[onTouch..].Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n').Select(line => line.TrimEnd())).Trim();
        var parsed = RathenaScriptParser.ParseOnTouch(template.ScriptBody);
        foreach (var issue in parsed.Issues)
            unsupported.Add(new(instance.Name, template.Source.File, template.Source.Line + issue.RelativeLine, $"Unsupported script construct '{issue.Construct}': {issue.SourceText}"));
        entity = new(1, DeterministicId.For("warp", instance.Map, instance.Name), "Warp",
            new(instance.Name, instance.Map, instance.X, instance.Y, instance.Direction, 45), [],
            [new("OnTouch", instance.Map, instance.X, instance.Y, rx, ry, true, parsed.Executable,
                ScriptCapabilities.Classify(normalized), normalized, parsed.Instructions, template.Name)],
            new("rAthena", Commit, instance.Source.File, instance.Source.Line));
        return true;
    }
    private static bool IsWarpNpc(RathenaDeclaration value) => value.Arguments.Contains("WARPNPC", StringComparison.Ordinal);
    private static bool TryDuplicateName(string directive, out string name)
    {
        name = ""; if (!directive.StartsWith("duplicate(", StringComparison.Ordinal)) return false;
        var close = directive.IndexOf(')'); if (close < 11) return false; name = directive[10..close]; return true;
    }
    private static bool U16(string value, out ushort result) => ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out result);
    private static UnsupportedConversion Unsupported(RathenaDeclaration value, string reason) => new(value.Name, value.Source.File, value.Source.Line, reason);
    [GeneratedRegex("/\\*.*?\\*/")] private static partial Regex BlockCommentRegex();
}

internal static class NpcSpriteClassResolver
{
    public static bool TryResolve(IEnumerable<string> roots, string constant, out ushort value)
    {
        value = 0;
        var root = roots.Select(Path.GetFullPath).Select(FindRathenaRoot).FirstOrDefault(path => path is not null);
        var header = root is null ? null : Path.Combine(root, "src", "map", "npc.hpp");
        if (header is null || !File.Exists(header)) return false;
        var current = -1; var inside = false;
        foreach (var raw in File.ReadLines(header))
        {
            var line = raw.Split("//", 2)[0].Trim().TrimEnd(',');
            if (!inside) { if (line.StartsWith("enum e_job_types", StringComparison.Ordinal)) inside = true; continue; }
            if (line == "{") continue;
            if (line.StartsWith('}')) break;
            if (line.Length == 0) continue;
            var parts = line.Split('=', 2, StringSplitOptions.TrimEntries); current = parts.Length == 2 && int.TryParse(parts[1], out var assigned) ? assigned : current + 1;
            if (parts[0] == "JT_" + constant) { value = checked((ushort)current); return true; }
        }
        return false;
    }
    private static string? FindRathenaRoot(string path)
    {
        for (var current = new DirectoryInfo(path); current is not null; current = current.Parent)
            if (File.Exists(Path.Combine(current.FullName, "src", "map", "npc.hpp"))) return current.FullName;
        return null;
    }
}

internal static class ScriptCapabilities
{
    public static IReadOnlyList<string> Classify(string source)
    {
        var capabilities = new List<string>();
        Add("Conditional", "if ("); Add("QuestState", "isbegin_quest("); Add("Dialogue", "mes ");
        Add("DialogueNext", "next;"); Add("Selection", "select("); Add("CompleteQuest", "completequest ");
        Add("Close", "close"); Add("VariableAssignment", "="); Add("NpcIdentity", "strnpcinfo(");
        Add("StringReplace", "replacestr("); Add("Warp", "warp "); Add("SavePoint", "savepoint ");
        return capabilities;
        void Add(string capability, string token) { if (source.Contains(token, StringComparison.Ordinal)) capabilities.Add(capability); }
    }
}

internal static partial class DeterministicWarpScriptEvaluator
{
    [GeneratedRegex("(?m)^\\s*(?<var>\\.@\\w+\\$)\\s*=\\s*replacestr\\(\\s*strnpcinfo\\(2\\)\\s*,\\s*\"(?<old>[^\"]*)\"\\s*,\\s*\"(?<new>[^\"]*)\"\\s*\\)\\s*;")]
    private static partial Regex ReplaceNpcNameRegex();
    [GeneratedRegex("(?m)^\\s*(?<var>\\.@\\w+\\$)\\s*=\\s*\"(?<prefix>[^\"]*)\"\\s*\\+\\s*(?<other>\\.@\\w+\\$)\\s*;")]
    private static partial Regex ConcatRegex();
    [GeneratedRegex("(?m)^\\s*(?<action>savepoint|warp)\\s+(?<map>\\.@\\w+\\$|\"[^\"]+\"),(?<x>\\d+),(?<y>\\d+)(?:,[^;]+)?\\s*;")]
    private static partial Regex ActionRegex();

    public static bool TryEvaluate(string body, string npcName, out IReadOnlyList<WorldActionDefinition> actions)
    {
        actions = [];
        var onTouch = body.IndexOf("OnTouch:", StringComparison.Ordinal);
        if (onTouch < 0) return false;
        var script = body[onTouch..];
        if (script.Contains("if (", StringComparison.Ordinal) || script.Contains("select(", StringComparison.Ordinal)) return false;
        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (Match match in ReplaceNpcNameRegex().Matches(script))
            variables[match.Groups["var"].Value] = npcName.TrimStart('#').Replace(match.Groups["old"].Value, match.Groups["new"].Value, StringComparison.Ordinal);
        foreach (Match match in ConcatRegex().Matches(script))
            if (variables.TryGetValue(match.Groups["other"].Value, out var suffix)) variables[match.Groups["var"].Value] = match.Groups["prefix"].Value + suffix;
        var result = new List<WorldActionDefinition>();
        foreach (Match match in ActionRegex().Matches(script))
        {
            var mapToken = match.Groups["map"].Value;
            var map = mapToken.StartsWith('"') ? mapToken.Trim('"') : variables.GetValueOrDefault(mapToken);
            if (map is null || !ushort.TryParse(match.Groups["x"].Value, out var x) || !ushort.TryParse(match.Groups["y"].Value, out var y)) return false;
            result.Add(match.Groups["action"].Value == "warp" ? new WarpAction(map, x, y) : new SetSavePointAction(map, x, y));
        }
        if (result.Count == 0 || result.All(action => action is not WarpAction)) return false;
        actions = result;
        return true;
    }
}
