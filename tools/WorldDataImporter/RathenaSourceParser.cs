using System.Globalization;

internal sealed record SourceLocation(string File, int Line);
internal sealed record RathenaDeclaration(string Map, ushort X, ushort Y, byte Direction, string Directive, string Name, string Arguments, string ScriptBody, SourceLocation Source);

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
                declarations.Add(new(map, x, y, direction, directive, columns[2].Trim(), arguments, body,
                    new(MakeRelative(absoluteRoots, file), sourceLine)));
            }
        }
        return declarations;
    }

    private static bool TryPosition(string text, out string map, out ushort x, out ushort y, out byte direction)
    {
        map = ""; x = y = 0; direction = 0;
        var parts = text.Split(',').Select(value => value.Trim()).ToArray();
        return parts.Length >= 4 && (map = parts[0]).Length > 0 &&
            ushort.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out x) &&
            ushort.TryParse(parts[2], NumberStyles.None, CultureInfo.InvariantCulture, out y) &&
            byte.TryParse(parts[3], NumberStyles.None, CultureInfo.InvariantCulture, out direction);
    }
    private static int Count(string value, char character) => value.Count(item => item == character);
    private static string MakeRelative(IEnumerable<string> roots, string file)
    {
        foreach (var root in roots)
            if (file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal)) return Path.GetRelativePath(Environment.CurrentDirectory, file);
        return file;
    }
}
