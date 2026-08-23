using System.Text.RegularExpressions;

internal sealed record CapabilityLocation(string File, int Line);
internal sealed record CommandCapability(string Command, int Count, string Status, IReadOnlyList<CapabilityLocation> Locations);
internal sealed record ConversionCapabilityReport(int Files, int NpcDefinitions, int Duplicates, int Scripts, IReadOnlyList<CommandCapability> Commands, IReadOnlyList<string> ParseErrors);

internal static partial class CapabilityReporter
{
    private static readonly HashSet<string> Supported = new(StringComparer.OrdinalIgnoreCase)
    {
        "mes", "next", "select", "close", "close2", "setquest", "completequest", "isbegin_quest",
        "warp", "savepoint", "strnpcinfo", "replacestr", "end"
    };

    public static ConversionCapabilityReport Scan(IEnumerable<string> roots)
    {
        var files = roots.SelectMany(root => Directory.EnumerateFiles(Path.GetFullPath(root), "*.txt", SearchOption.AllDirectories))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var locations = new Dictionary<string, List<CapabilityLocation>>(StringComparer.OrdinalIgnoreCase);
        var npcDefinitions = 0; var duplicates = 0; var scripts = 0;
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(Environment.CurrentDirectory, file);
            var lines = File.ReadAllLines(file);
            var inScript = false; var depth = 0;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index]; var columns = line.Split('\t');
                if (!inScript && columns.Length >= 3 && columns[0].Count(character => character == ',') >= 3)
                {
                    npcDefinitions++;
                    if (columns[1].TrimStart().StartsWith("duplicate(", StringComparison.Ordinal)) duplicates++;
                    if (columns[1].Trim().Equals("script", StringComparison.Ordinal) && line.Contains('{'))
                    {
                        scripts++; depth = Braces(line); inScript = depth > 0; continue;
                    }
                }
                if (!inScript) continue;
                foreach (var command in CommandsIn(line))
                {
                    counts[command] = counts.GetValueOrDefault(command) + 1;
                    if (!Supported.Contains(command) && (!locations.TryGetValue(command, out var list) || list.Count < 1))
                    {
                        if (list is null) locations[command] = list = [];
                        list.Add(new(relative, index + 1));
                    }
                }
                depth += Braces(line);
                if (depth <= 0) inScript = false;
            }
        }
        var commands = counts.OrderByDescending(item => item.Value).ThenBy(item => item.Key, StringComparer.Ordinal)
            .Select(item => new CommandCapability(item.Key, item.Value, Supported.Contains(item.Key) ? "SUPPORTED" : "UNSUPPORTED", locations.GetValueOrDefault(item.Key) ?? [])).ToArray();
        return new(files.Length, npcDefinitions, duplicates, scripts, commands, []);
    }

    private static IEnumerable<string> CommandsIn(string source)
    {
        var line = source.Trim(); if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) yield break;
        var statement = LeadingCommandRegex().Match(line);
        if (statement.Success && !statement.Groups[1].Value.Equals("if", StringComparison.OrdinalIgnoreCase)) yield return statement.Groups[1].Value.ToLowerInvariant();
        foreach (Match match in FunctionRegex().Matches(line))
        {
            var name = match.Groups[1].Value.ToLowerInvariant();
            if (name is not "if" and not "switch") yield return name;
        }
    }
    private static int Braces(string line) => line.Count(character => character == '{') - line.Count(character => character == '}');
    [GeneratedRegex("^(?:if\\s*\\([^)]*\\)\\s*)?([A-Za-z_][A-Za-z0-9_]*)\\b")] private static partial Regex LeadingCommandRegex();
    [GeneratedRegex("\\b([A-Za-z_][A-Za-z0-9_]*)\\s*\\(")] private static partial Regex FunctionRegex();
}
