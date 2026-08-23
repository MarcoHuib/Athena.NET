using System.Globalization;
using System.Text.RegularExpressions;

internal sealed record ConversionFilter(string? SourceFile, string? Map, string? Name, string? Kind)
{
    public bool IsEmpty => SourceFile is null && Map is null && Name is null && Kind is null;
    public bool Matches(RathenaDeclaration value) =>
        (SourceFile is null || value.Source.File.EndsWith(SourceFile, StringComparison.OrdinalIgnoreCase)) &&
        (Map is null || string.Equals(value.Map, Map, StringComparison.OrdinalIgnoreCase)) &&
        (Name is null || string.Equals(value.Name, Name, StringComparison.Ordinal)) &&
        (Kind is null || string.Equals(Kind, "warp", StringComparison.OrdinalIgnoreCase));
}
internal sealed record UnsupportedConversion(string Name, string File, int Line, string Reason);
internal sealed record ConversionResult(IReadOnlyList<WorldEntityDefinition> Entities, IReadOnlyList<UnsupportedConversion> Unsupported);

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
        }
        return new(entities.OrderBy(item => item.Id, StringComparer.Ordinal).ToArray(), unsupported);
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
