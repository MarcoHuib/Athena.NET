using System.Globalization;
using System.Text.Json;

if (args.Length < 3)
{
    Console.Error.WriteLine("Usage: WorldDataImporter <output.json> <source-folder> <source-folder> [...]");
    return 2;
}

var outputPath = Path.GetFullPath(args[0]);
var sourceFolders = args.Skip(1).Select(Path.GetFullPath).ToArray();
var result = WarpImporter.Import(sourceFolders);
Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
var options = new JsonSerializerOptions { WriteIndented = true };
await File.WriteAllTextAsync(outputPath, JsonSerializer.Serialize(result, options) + Environment.NewLine);
Console.WriteLine(
    $"Imported static warps: {result.Summary.StaticWarps}; resolved duplicates: {result.Summary.ResolvedDuplicates}; " +
    $"dynamic/scripted: {result.Summary.DynamicWarps}; unsupported/malformed: {result.Summary.Unsupported}.");
return result.Summary.Unsupported == 0 ? 0 : 1;

internal static class WarpImporter
{
    public static GeneratedWorldData Import(IEnumerable<string> sourceFolders)
    {
        var folders = sourceFolders.OrderBy(path => path, StringComparer.Ordinal).ToArray();
        var files = folders
            .SelectMany(folder => Directory.EnumerateFiles(folder, "*.txt", SearchOption.AllDirectories))
            .Distinct(StringComparer.Ordinal)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();
        var staticWarps = new List<GeneratedWarp>();
        var dynamicWarps = new List<ClassifiedWarp>();
        var unsupported = new List<ClassifiedWarp>();
        var templates = new Dictionary<string, GeneratedWarp>(StringComparer.Ordinal);
        var duplicateCandidates = new List<ParsedLine>();

        foreach (var file in files)
        {
            var relativeFile = MakeRelative(folders, file);
            var lineNumber = 0;
            foreach (var rawLine in File.ReadLines(file))
            {
                lineNumber++;
                var line = rawLine.TrimStart('\uFEFF').Trim();
                if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal))
                {
                    continue;
                }

                var parsed = new ParsedLine(relativeFile, lineNumber, line, line.Split('\t'));
                if (parsed.Columns.Length >= 4 && parsed.Columns[1].Trim() == "warp")
                {
                    if (TryParseStatic(parsed, out var warp))
                    {
                        staticWarps.Add(warp);
                        templates[warp.Name] = warp;
                    }
                    else
                    {
                        unsupported.Add(new ClassifiedWarp(relativeFile, lineNumber, "MalformedStaticWarp", line, null, null, null, null, null));
                    }

                    continue;
                }

                if (parsed.Columns.Length >= 2 && parsed.Columns[1].Trim().StartsWith("duplicate(", StringComparison.Ordinal))
                {
                    duplicateCandidates.Add(parsed);
                    continue;
                }

                if (parsed.Columns.Length >= 4 &&
                    parsed.Columns[1].Trim() == "script" &&
                    parsed.Columns[3].Contains("WARPNPC", StringComparison.Ordinal))
                {
                    dynamicWarps.Add(ParseDynamicVisual(parsed, "ScriptedWarpNpc"));
                }
            }
        }

        var resolvedDuplicates = 0;
        foreach (var parsed in duplicateCandidates)
        {
            if (TryResolveDuplicate(parsed, templates, out var duplicate))
            {
                staticWarps.Add(duplicate);
                templates[duplicate.Name] = duplicate;
                resolvedDuplicates++;
            }
            else if (parsed.Line.Contains("WARPNPC", StringComparison.Ordinal))
            {
                dynamicWarps.Add(ParseDynamicVisual(parsed, "DynamicWarpDuplicate"));
            }
        }

        var ordered = staticWarps
            .OrderBy(warp => warp.SourceMap, StringComparer.Ordinal)
            .ThenBy(warp => warp.CenterX)
            .ThenBy(warp => warp.CenterY)
            .ThenBy(warp => warp.Name, StringComparer.Ordinal)
            .ToArray();
        return new GeneratedWorldData(
            1,
            "rAthena",
            "6e6bca69b8a2ee03cd744cbc7a78a054a6f376ca",
            folders.Select(folder => Path.GetRelativePath(Environment.CurrentDirectory, folder)).ToArray(),
            files.Length,
            ordered,
            dynamicWarps.OrderBy(item => item.File, StringComparer.Ordinal).ThenBy(item => item.Line).ToArray(),
            unsupported.OrderBy(item => item.File, StringComparer.Ordinal).ThenBy(item => item.Line).ToArray(),
            new ImportSummary(ordered.Length, resolvedDuplicates, dynamicWarps.Count, unsupported.Count));
    }

    private static bool TryParseStatic(ParsedLine line, out GeneratedWarp warp)
    {
        warp = default!;
        if (line.Columns.Length < 4 ||
            !TryParseSource(line.Columns[0], out var map, out var x, out var y) ||
            !TryParseDestination(line.Columns[3], out var radiusX, out var radiusY, out var destinationMap, out var destinationX, out var destinationY))
        {
            return false;
        }

        warp = new GeneratedWarp(
            line.Columns[2].Trim(), map, x, y, radiusX, radiusY,
            destinationMap, destinationX, destinationY, true,
            line.File, line.LineNumber);
        return true;
    }

    private static bool TryResolveDuplicate(
        ParsedLine line,
        IReadOnlyDictionary<string, GeneratedWarp> templates,
        out GeneratedWarp warp)
    {
        warp = default!;
        var action = line.Columns[1].Trim();
        var close = action.IndexOf(')');
        if (close <= "duplicate(".Length || line.Columns.Length < 3 ||
            !TryParseSource(line.Columns[0], out var map, out var x, out var y))
        {
            return false;
        }

        var templateName = action["duplicate(".Length..close];
        if (!templates.TryGetValue(templateName, out var template))
        {
            return false;
        }

        warp = template with
        {
            Name = line.Columns[2].Trim(),
            SourceMap = map,
            CenterX = x,
            CenterY = y,
            SourceFile = line.File,
            SourceLine = line.LineNumber,
        };
        return true;
    }

    private static ClassifiedWarp ParseDynamicVisual(ParsedLine line, string classification)
    {
        if (line.Columns.Length >= 4 &&
            TryParseSource(line.Columns[0], out var map, out var x, out var y))
        {
            var visual = line.Columns[3].Split(',').Select(field => field.Trim()).ToArray();
            if (visual.Length >= 3 && visual[0] == "WARPNPC" &&
                ushort.TryParse(visual[1], NumberStyles.None, CultureInfo.InvariantCulture, out var radiusX) &&
                ushort.TryParse(visual[2].TrimEnd('{'), NumberStyles.None, CultureInfo.InvariantCulture, out var radiusY))
            {
                return new ClassifiedWarp(
                    line.File,
                    line.LineNumber,
                    classification,
                    line.Line,
                    line.Columns[2].Trim(),
                    map,
                    x,
                    y,
                    new WarpRadius(radiusX, radiusY));
            }
        }

        return new ClassifiedWarp(line.File, line.LineNumber, classification, line.Line, null, null, null, null, null);
    }

    private static bool TryParseSource(string value, out string map, out ushort x, out ushort y)
    {
        map = string.Empty;
        x = y = 0;
        var fields = value.Split(',').Select(field => field.Trim()).ToArray();
        return fields.Length >= 3 &&
               (map = fields[0]).Length > 0 &&
               ushort.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out x) &&
               ushort.TryParse(fields[2], NumberStyles.None, CultureInfo.InvariantCulture, out y);
    }

    private static bool TryParseDestination(
        string value,
        out ushort radiusX,
        out ushort radiusY,
        out string map,
        out ushort x,
        out ushort y)
    {
        radiusX = radiusY = x = y = 0;
        map = string.Empty;
        var fields = value.Split(',').Select(field => field.Trim()).ToArray();
        return fields.Length == 5 &&
               ushort.TryParse(fields[0], NumberStyles.None, CultureInfo.InvariantCulture, out radiusX) &&
               ushort.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out radiusY) &&
               (map = fields[2]).Length > 0 &&
               ushort.TryParse(fields[3], NumberStyles.None, CultureInfo.InvariantCulture, out x) &&
               ushort.TryParse(fields[4], NumberStyles.None, CultureInfo.InvariantCulture, out y);
    }

    private static string MakeRelative(IEnumerable<string> roots, string file)
    {
        foreach (var root in roots)
        {
            if (file.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal))
            {
                return Path.GetRelativePath(Environment.CurrentDirectory, file);
            }
        }

        return file;
    }

    private sealed record ParsedLine(string File, int LineNumber, string Line, string[] Columns);
}

internal sealed record GeneratedWorldData(
    int SchemaVersion,
    string SourceRepository,
    string SourceCommit,
    string[] SourceFolders,
    int FilesAnalyzed,
    GeneratedWarp[] StaticWarps,
    ClassifiedWarp[] DynamicWarps,
    ClassifiedWarp[] Unsupported,
    ImportSummary Summary);

internal sealed record GeneratedWarp(
    string Name,
    string SourceMap,
    ushort CenterX,
    ushort CenterY,
    ushort RadiusX,
    ushort RadiusY,
    string DestinationMap,
    ushort DestinationX,
    ushort DestinationY,
    bool HasWarpActor,
    string SourceFile,
    int SourceLine);

internal sealed record ClassifiedWarp(
    string File,
    int Line,
    string Classification,
    string Definition,
    string? Name,
    string? SourceMap,
    ushort? CenterX,
    ushort? CenterY,
    WarpRadius? Radius);
internal sealed record WarpRadius(ushort X, ushort Y);
internal sealed record ImportSummary(int StaticWarps, int ResolvedDuplicates, int DynamicWarps, int Unsupported);
