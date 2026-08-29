using System.Globalization;

internal sealed record SourceLocation(string File, int Line);
internal sealed record RathenaDeclaration(string Map, ushort X, ushort Y, byte Direction, string Directive, string Name, string Arguments, string ScriptBody, SourceLocation Source, string? Alias = null);

internal static class RathenaSourceParser
{
    public static IReadOnlyList<RathenaDeclaration> Parse(IEnumerable<string> roots)
    {
        var absoluteRoots = roots.Select(Path.GetFullPath).Order(StringComparer.Ordinal).ToArray();
        var files = absoluteRoots.SelectMany(root => Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal);
        var declarations = new List<RathenaDeclaration>();
        foreach (var file in files)
        {
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var raw = lines[index].TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                var columns = raw.Split('\t');
                if (columns.Length < 3 || !TryPosition(columns[0], out var map, out var x, out var y, out var direction)) continue;
                var sourceLine = index + 1;
                var directive = columns[1].Trim();
                var arguments = columns.Length > 3 ? string.Join('\t', columns[3..]).Trim() : "";
                var body = "";
                if (directive == "script" && arguments.Contains('{'))
                {
                    var bodyLines = new List<string>();
                    var depth = Count(arguments, '{') - Count(arguments, '}');
                    while (depth > 0 && ++index < lines.Length)
                    {
                        bodyLines.Add(lines[index]);
                        depth += Count(lines[index], '{') - Count(lines[index], '}');
                    }
                    body = string.Join('\n', bodyLines);
                }
                var rawName = columns[2].Trim();
                // Two independent rAthena "::" conventions share the same delimiter:
                //   "::Name"    - a floating (map "-") global-label script; the whole name IS the alias
                //                 duplicate(...) references, with no separate display-name segment.
                //   "Name::Alias" - a normally self-placed script whose OWN display name differs from the
                //                 shorter alias other files' duplicate(...) directives use to reference it
                //                 (e.g. "Guide#01prontera::GuideProntera"). Only Name is the display name;
                //                 Alias is registered as an additional templates-dictionary key downstream.
                string name; string? alias = null;
                if (rawName.StartsWith("::", StringComparison.Ordinal)) name = rawName[2..];
                else
                {
                    var aliasIndex = rawName.IndexOf("::", StringComparison.Ordinal);
                    if (aliasIndex > 0) { name = rawName[..aliasIndex]; alias = rawName[(aliasIndex + 2)..]; }
                    else name = rawName;
                }
                declarations.Add(new(map, x, y, direction, directive, name, arguments, body,
                    new(MakeRelative(absoluteRoots, file), sourceLine), alias));
            }
        }
        return declarations;
    }

    // A bare "-" map field is rAthena's floating/global-label script: it carries no placement of its
    // own and exists only to be referenced by duplicate(...) instances elsewhere. Map is preserved as
    // "-" (never consumed as a real placement - only duplicate() instances' own Map/X/Y/Direction are
    // used downstream) so the declaration round-trips instead of being silently dropped by the parser.
    private static bool TryPosition(string text, out string map, out ushort x, out ushort y, out byte direction)
    {
        map = ""; x = y = 0; direction = 0;
        if (text.Trim() == "-") { map = "-"; return true; }
        var parts = text.Split(',').Select(value => value.Trim()).ToArray();
        return parts.Length >= 4 && (map = parts[0]).Length > 0 &&
            ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out x) &&
            ushort.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out y) &&
            byte.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out direction);
    }
    private static int Count(string value, char character) => value.Count(item => item == character);
    private static string MakeRelative(IEnumerable<string> roots, string file)
    {
        var normalized = file.Replace('\\', '/');
        var pinned = normalized.IndexOf("legacy/rathena/", StringComparison.Ordinal);
        if (pinned >= 0) return normalized[pinned..];
        foreach (var root in roots)
            if (file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return Path.GetRelativePath(Environment.CurrentDirectory, file);
        return file;
    }
}
