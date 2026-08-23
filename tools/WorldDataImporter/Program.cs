using System.Text.Json;

return await WorldDataImporterCli.RunAsync(args);

internal static class WorldDataImporterCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 2; }
        try
        {
            return args[0] switch
            {
                "audit" => await AuditAsync(args[1..]),
                "convert" => await ConvertAsync(args[1..]),
                "capabilities" => await CapabilitiesAsync(args[1..]),
                _ => throw new ArgumentException($"Unknown command '{args[0]}'."),
            };
        }
        catch (ArgumentException exception) { Console.Error.WriteLine(exception.Message); PrintUsage(); return 2; }
    }

    private static async Task<int> AuditAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var roots = options.All("source-root");
        if (roots.Count == 0) throw new ArgumentException("audit requires --source-root.");
        var report = ContentAuditor.Audit(roots);
        await DeterministicJson.WriteFileAsync(options.Required("output"), report);
        Console.WriteLine($"Audited {report.FilesAnalyzed} files and {report.TopLevel.Total} top-level declarations.");
        foreach (var item in report.TopLevel.Categories) Console.WriteLine($"  {item.Category,-24} {item.Count,8}");
        Console.WriteLine($"Embedded labels: OnTouch={report.EmbeddedBehavior.OnTouch}, OnTouch_={report.EmbeddedBehavior.OnTouchVariant}, OnInit={report.EmbeddedBehavior.OnInit}, timers/events={report.EmbeddedBehavior.TimerOrEvent}.");
        return 0;
    }

    private static async Task<int> ConvertAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var roots = options.All("source-root");
        if (roots.Count == 0) throw new ArgumentException("convert requires --source-root.");
        var filter = new ConversionFilter(options.Optional("source-file"), options.Optional("map"), options.Optional("name"), options.Optional("kind"));
        var allCompatible = string.Equals(options.Optional("all-compatible"), "true", StringComparison.OrdinalIgnoreCase);
        if (filter.IsEmpty && !allCompatible) throw new ArgumentException("convert requires a filter, or the explicit --all-compatible true safety switch.");
        if (allCompatible && options.Optional("report") is null) throw new ArgumentException("--all-compatible true requires --report so skipped content is never silent.");
        var result = WorldEntityConverter.Convert(roots, filter);
        var selected = allCompatible ? result.Entities.Where(IsRuntimeExecutable).ToArray() : result.Entities.ToArray();
        var duplicateIds = selected.GroupBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var writeEntities = selected.Where(entity => !duplicateIds.Contains(entity.Id)).ToArray();
        foreach (var entity in writeEntities)
        {
            var directory = Path.Combine(options.Required("output"), entity.Actor?.Map ?? entity.Triggers[0].Map);
            await DeterministicJson.WriteFileAsync(Path.Combine(directory, DeterministicId.FileName(entity.Id) + ".json"), entity);
        }
        var skipped = result.Entities.Count - selected.Length;
        if (options.Optional("report") is { } reportPath)
            await DeterministicJson.WriteFileAsync(reportPath, new CompatibilityConversionReport(writeEntities.Length, skipped, duplicateIds.Order(StringComparer.Ordinal).ToArray(), result.Unsupported));
        Console.WriteLine($"Converted {writeEntities.Length} executable entities; parsed but not executable: {skipped}; unsupported findings: {result.Unsupported.Count}; duplicate IDs skipped: {duplicateIds.Count}.");
        return allCompatible || (result.Unsupported.Count == 0 && duplicateIds.Count == 0) ? 0 : 1;
    }

    private static bool IsRuntimeExecutable(WorldEntityDefinition entity) =>
        entity.Triggers.Any(trigger => trigger.Actions.Any(action => action is WarpAction)) ||
        (entity.Scripts?.Any(script => script.RuntimeExecutable && script.Instructions is { Count: > 0 }) ?? false);

    private static async Task<int> CapabilitiesAsync(string[] args)
    {
        var options = CliOptions.Parse(args); var roots = options.All("source-root");
        if (roots.Count == 0) throw new ArgumentException("capabilities requires --source-root.");
        var report = CapabilityReporter.Scan(roots);
        await DeterministicJson.WriteFileAsync(options.Required("output"), report);
        Console.WriteLine($"Scanned {report.Files} files: NPC definitions={report.NpcDefinitions}, duplicates={report.Duplicates}, scripts={report.Scripts}.");
        foreach (var command in report.Commands.Take(20)) Console.WriteLine($"  {command.Command,-24} {command.Count,8} {command.Status}");
        return 0;
    }

    private static void PrintUsage()
    {
        Console.Error.WriteLine("WorldDataImporter audit --source-root <folder> [--source-root <folder>] --output <report.json>");
        Console.Error.WriteLine("WorldDataImporter convert --source-root <folder> --output <entities-folder> [--source-file <path>] [--map <map>] [--name <name>] [--kind warp]");
        Console.Error.WriteLine("WorldDataImporter convert --source-root <folder> --all-compatible true --output <entities-folder> --report <report.json>");
        Console.Error.WriteLine("WorldDataImporter capabilities --source-root <folder> [--source-root <folder>] --output <report.json>");
    }
}

internal sealed record CompatibilityConversionReport(int Converted, int ParsedButNotExecutable, IReadOnlyList<string> DuplicateIds, IReadOnlyList<UnsupportedConversion> Unsupported);

internal sealed class CliOptions
{
    private readonly Dictionary<string, List<string>> _values = new(StringComparer.Ordinal);
    public static CliOptions Parse(string[] args)
    {
        var result = new CliOptions();
        for (var index = 0; index < args.Length; index += 2)
        {
            if (!args[index].StartsWith("--", StringComparison.Ordinal) || index + 1 >= args.Length) throw new ArgumentException($"Invalid option near '{args[index]}'.");
            var key = args[index][2..];
            if (!result._values.TryGetValue(key, out var values)) result._values[key] = values = [];
            values.Add(args[index + 1]);
        }
        return result;
    }
    public IReadOnlyList<string> All(string key) => _values.TryGetValue(key, out var values) ? values : [];
    public string Required(string key) => Optional(key) ?? throw new ArgumentException($"Missing --{key}.");
    public string? Optional(string key) => _values.TryGetValue(key, out var values) ? values[^1] : null;
}
