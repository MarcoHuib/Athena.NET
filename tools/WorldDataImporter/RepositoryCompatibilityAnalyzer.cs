using System.Globalization;
using System.Text;
using Athena.WorldCompiler;
using Athena.WorldCompiler.Lowering;
using Athena.WorldCompiler.Rathena.Syntax;

internal enum CompatibilityStatus { Compatible, Unsupported, NotYetAnalyzed, NotApplicable }
internal enum FailureStage { Discovery, Parsing, SemanticAnalysis, Lowering, RuntimeCapability, Dependency, Generation }

internal sealed record AnalysisOptions(
    string RathenaRoot, string OutputDirectory, int SourceContextLines = 5,
    IReadOnlySet<string>? Types = null, string? Map = null, string? Source = null);
internal sealed record SourceContext(int StartLine, int EndLine, IReadOnlyList<string> Text);
internal sealed record CompatibilityBlocker(string DiagnosticCode, string Feature, string Category, FailureStage Stage, int Line, int Column, string Message);
internal sealed record CompatibilityEntity(
    string Id, string EntityType, string EntityName, string? Map, string? Event,
    string SourceFile, int SourceLine, CompatibilityStatus Status,
    IReadOnlyList<string> Features, IReadOnlyList<CompatibilityBlocker>? Blockers = null,
    SourceContext? SourceContext = null, IReadOnlyList<string>? Dependencies = null);
internal sealed record CategorySummary(string Category, int Discovered, int Compatible, int Unsupported, int NotYetAnalyzed, int NotApplicable);
internal sealed record EventSummary(string Event, int Compatible, int Unsupported);
internal sealed record AnalysisSummary(int FilesAnalyzed, int EntitiesAnalyzed, int Compatible, int Unsupported,
    IReadOnlyList<CategorySummary> Categories, IReadOnlyList<EventSummary> Events);
internal sealed record BlockerSummary(string Feature, string Category, FailureStage Stage, int Occurrences, int AffectedEntities, int SoleBlockerFor, IReadOnlyList<string> RepresentativeSources);
internal sealed record WorkItem(int Priority, string Feature, string Category, FailureStage Stage, int AffectedEntities, int EntitiesUnlocked, int Occurrences, IReadOnlyList<string> RepresentativeSources);
internal sealed record EntityDependencies(string Entity, IReadOnlyList<string> Dependencies);
internal sealed record RepositoryAnalysisResult(AnalysisSummary Summary, IReadOnlyList<CompatibilityEntity> Compatible,
    IReadOnlyList<CompatibilityEntity> Unsupported, IReadOnlyList<BlockerSummary> Blockers,
    IReadOnlyList<WorkItem> WorkItems, IReadOnlyList<EntityDependencies> Dependencies);

internal static class RepositoryCompatibilityAnalyzer
{
    public static RepositoryAnalysisResult Analyze(AnalysisOptions options)
    {
        var root = Path.GetFullPath(options.RathenaRoot);
        if (!Directory.Exists(root)) throw new ArgumentException($"rAthena root does not exist: {root}");
        if (options.SourceContextLines < 0 || options.SourceContextLines > 50) throw new ArgumentException("--source-context-lines must be between 0 and 50.");
        var files = Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var declarations = RathenaSourceParser.Parse([root]);
        var warpConversion = WorldEntityConverter.Convert([root], new(null, null, null, "warp"));
        var convertedWarps = warpConversion.Entities.Select(item => (CanonicalSource(root, item.Source.File), item.Source.Line)).ToHashSet();
        var rejectedWarps = warpConversion.Unsupported.Select(item => (File: CanonicalSource(root, item.File), item.Line, item.Reason)).ToLookup(item => (item.File, item.Line));
        var records = new List<CompatibilityEntity>();

        foreach (var parsedDeclaration in declarations.OrderBy(item => item.Source.File, StringComparer.Ordinal).ThenBy(item => item.Source.Line))
        {
            var declaration = parsedDeclaration with { Source = parsedDeclaration.Source with { File = CanonicalSource(root, parsedDeclaration.Source.File) } };
            var type = Classify(declaration);
            if (!Included(options, type, declaration)) continue;
            if (declaration.Directive != "script")
            {
                var status = type == "warp" && convertedWarps.Contains((declaration.Source.File, declaration.Source.Line)) ? CompatibilityStatus.Compatible :
                    type == "warp" ? CompatibilityStatus.Unsupported :
                    type == "duplicate" ? CompatibilityStatus.NotApplicable : CompatibilityStatus.NotYetAnalyzed;
                IReadOnlyList<CompatibilityBlocker>? conversionBlockers = status == CompatibilityStatus.Unsupported
                    ? rejectedWarps[(declaration.Source.File, declaration.Source.Line)].Select(item => new CompatibilityBlocker("RATD001", "warp", "world-converter", FailureStage.Discovery, declaration.Source.Line, 1, item.Reason)).ToArray()
                    : null;
                records.Add(Entity(declaration, type, null, status, [], conversionBlockers, null));
                continue;
            }

            var source = new WorldSourceInfo("rAthena", "pinned", declaration.Source.File, declaration.Source.Line);
            var (syntax, semantics) = RathenaEventCompiler.Parse(declaration.ScriptBody, source);
            var events = EventNames(syntax);
            if (events.Count == 0)
            {
                records.Add(Entity(declaration, type, null, CompatibilityStatus.NotApplicable, [], null, null));
                continue;
            }
            foreach (var eventName in events)
            {
                var compilation = RathenaEventCompiler.Compile(syntax, semantics, eventName);
                var errors = compilation.Diagnostics.Where(item => item.Severity == "Error").ToArray();
                var features = compilation.Features.Select(item => item.Name.ToLowerInvariant()).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                if (errors.Length == 0)
                    records.Add(Entity(declaration, type, eventName, CompatibilityStatus.Compatible, features, null, null) with { Dependencies = Dependencies(compilation.Script) });
                else
                {
                    var eventBlockers = errors.Select(error => ToBlocker(error, compilation.Features)).Distinct().OrderBy(item => item.Line).ThenBy(item => item.Column).ThenBy(item => item.Feature, StringComparer.Ordinal).ToArray();
                    var firstLine = eventBlockers.Min(item => item.Line);
                    records.Add(Entity(declaration, type, eventName, CompatibilityStatus.Unsupported, features, eventBlockers,
                        Context(root, declaration.Source.File, firstLine, options.SourceContextLines)) with { Dependencies = Dependencies(compilation.Script) });
                }
            }
        }

        // These top-level forms are reliably discoverable but do not use the positioned declaration grammar.
        records.AddRange(DiscoverNonPositioned(root, files, options));
        var ordered = records.OrderBy(item => item.SourceFile, StringComparer.Ordinal).ThenBy(item => item.SourceLine)
            .ThenBy(item => item.EntityName, StringComparer.Ordinal).ThenBy(item => item.Event, StringComparer.Ordinal).ToArray();
        var compatible = ordered.Where(item => item.Status == CompatibilityStatus.Compatible).ToArray();
        var unsupported = ordered.Where(item => item.Status == CompatibilityStatus.Unsupported).ToArray();
        var blockers = AggregateBlockers(unsupported);
        var work = blockers.OrderByDescending(item => item.SoleBlockerFor).ThenByDescending(item => item.AffectedEntities)
            .ThenByDescending(item => item.Occurrences).ThenBy(item => item.Feature, StringComparer.Ordinal)
            .Select((item, index) => new WorkItem(index + 1, item.Feature, item.Category, item.Stage, item.AffectedEntities, item.SoleBlockerFor, item.Occurrences, item.RepresentativeSources)).ToArray();
        var categories = ordered.GroupBy(item => item.EntityType, StringComparer.Ordinal).OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => new CategorySummary(group.Key, group.Count(), group.Count(x => x.Status == CompatibilityStatus.Compatible), group.Count(x => x.Status == CompatibilityStatus.Unsupported), group.Count(x => x.Status == CompatibilityStatus.NotYetAnalyzed), group.Count(x => x.Status == CompatibilityStatus.NotApplicable))).ToArray();
        var eventSummary = ordered.Where(item => item.Event is not null).GroupBy(item => item.Event!, StringComparer.Ordinal).OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => new EventSummary(group.Key, group.Count(x => x.Status == CompatibilityStatus.Compatible), group.Count(x => x.Status == CompatibilityStatus.Unsupported))).ToArray();
        var dependencies = ordered.Where(item => item.Dependencies is { Count: > 0 }).Select(item => new EntityDependencies(item.Id, item.Dependencies!)).ToArray();
        return new(new(files.Length, ordered.Length, compatible.Length, unsupported.Length, categories, eventSummary), compatible, unsupported, blockers, work, dependencies);
    }

    public static async Task WriteAsync(AnalysisOptions options, RepositoryAnalysisResult result)
    {
        var output = Path.GetFullPath(options.OutputDirectory); Directory.CreateDirectory(output);
        await DeterministicJson.WriteFileAsync(Path.Combine(output, "summary.json"), result.Summary);
        await WriteJsonLines(Path.Combine(output, "compatible.jsonl"), result.Compatible);
        await WriteJsonLines(Path.Combine(output, "unsupported.jsonl"), result.Unsupported);
        await DeterministicJson.WriteFileAsync(Path.Combine(output, "blockers.json"), result.Blockers);
        await DeterministicJson.WriteFileAsync(Path.Combine(output, "work-items.json"), result.WorkItems);
        await DeterministicJson.WriteFileAsync(Path.Combine(output, "dependencies.json"), result.Dependencies);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), Markdown(result), new UTF8Encoding(false));
    }

    private static IReadOnlyList<string> EventNames(CompilationUnitSyntax syntax)
    {
        var labels = syntax.Statements.OfType<LabelStatementSyntax>().Where(item => item.IsEvent).Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var firstEvent = syntax.Statements.ToList().FindIndex(item => item is LabelStatementSyntax { IsEvent: true });
        if ((firstEvent < 0 || syntax.Statements.Take(firstEvent).Any(item => item is not EmptyStatementSyntax)) && !labels.Contains("OnClick", StringComparer.OrdinalIgnoreCase)) labels.Insert(0, "OnClick");
        return labels;
    }

    private static CompatibilityBlocker ToBlocker(CompilerDiagnostic diagnostic, IReadOnlyList<Athena.WorldCompiler.Semantics.SemanticOccurrence> features)
    {
        var occurrence = features.OrderBy(item => Math.Abs(item.Span.Start.Offset - diagnostic.Span.Start.Offset)).FirstOrDefault();
        var feature = diagnostic.Construct ?? occurrence?.Name ?? diagnostic.Code;
        if (feature.EndsWith("StatementSyntax", StringComparison.Ordinal) && occurrence is not null) feature = occurrence.Name;
        var stage = diagnostic.Code.StartsWith("RAT1", StringComparison.Ordinal) || diagnostic.Code.StartsWith("RAT2", StringComparison.Ordinal) ? FailureStage.Parsing
            : diagnostic.Code.StartsWith("RAT3", StringComparison.Ordinal) ? FailureStage.SemanticAnalysis
            : diagnostic.Code.StartsWith("RAT4", StringComparison.Ordinal) ? FailureStage.Lowering : FailureStage.Generation;
        return new(diagnostic.Code, feature.ToLowerInvariant(), Category(feature), stage, diagnostic.Span.Start.Line, diagnostic.Span.Start.Column, diagnostic.Message);
    }

    private static string Category(string feature) => feature.ToLowerInvariant() switch
    {
        "sleep" or "sleep2" or "addtimer" or "deltimer" or "initnpctimer" => "timer",
        "callfunc" or "callsub" => "control-flow",
        _ => "script-compiler"
    };

    private static IReadOnlyList<BlockerSummary> AggregateBlockers(IReadOnlyList<CompatibilityEntity> unsupported)
    {
        var occurrences = unsupported.SelectMany(entity => entity.Blockers!.Select(blocker => (entity, blocker)));
        return occurrences.GroupBy(item => (item.blocker.Feature, item.blocker.Category, item.blocker.Stage))
            .Select(group => new BlockerSummary(group.Key.Feature, group.Key.Category, group.Key.Stage, group.Count(),
                group.Select(item => item.entity.Id).Distinct(StringComparer.Ordinal).Count(),
                group.Select(item => item.entity).DistinctBy(item => item.Id).Count(entity => entity.Blockers!.Select(b => (b.Feature, b.Stage)).Distinct().Count() == 1),
                group.Select(item => $"{item.entity.SourceFile}:{item.blocker.Line}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5).ToArray()))
            .OrderByDescending(item => item.SoleBlockerFor).ThenByDescending(item => item.AffectedEntities).ThenBy(item => item.Feature, StringComparer.Ordinal).ToArray();
    }

    private static CompatibilityEntity Entity(RathenaDeclaration d, string type, string? eventName, CompatibilityStatus status, IReadOnlyList<string> features, IReadOnlyList<CompatibilityBlocker>? blockers, SourceContext? context)
    {
        var id = string.Join(":", type, d.Source.File.Replace('\\', '/').ToLowerInvariant(), d.Source.Line.ToString(CultureInfo.InvariantCulture), d.Name.ToLowerInvariant(), eventName?.ToLowerInvariant() ?? "entity");
        return new(id, type, d.Name, d.Map == "-" ? null : d.Map, eventName, d.Source.File.Replace('\\', '/'), d.Source.Line, status, features, blockers, context);
    }

    private static string Classify(RathenaDeclaration d) => d.Directive switch
    {
        "warp" => "warp", "script" when d.Arguments.Contains("WARPNPC", StringComparison.OrdinalIgnoreCase) => "warpnpc",
        "script" => "npc", var value when value.StartsWith("duplicate(", StringComparison.Ordinal) => "duplicate",
        var value when value.Contains("shop", StringComparison.OrdinalIgnoreCase) => "shop",
        "monster" => "mob", "boss_monster" => "boss", "mapflag" => "mapflag", _ => d.Directive.ToLowerInvariant()
    };

    private static string CanonicalSource(string root, string source)
    {
        var normalized = source.Replace('\\', '/');
        var pinned = normalized.IndexOf("legacy/rathena/", StringComparison.Ordinal);
        if (pinned >= 0) return normalized[pinned..];
        var absolute = Path.GetFullPath(source);
        return Path.GetRelativePath(root, absolute).Replace('\\', '/');
    }

    private static bool Included(AnalysisOptions options, string type, RathenaDeclaration d) =>
        (options.Types is null || options.Types.Count == 0 || options.Types.Contains(type)) &&
        (options.Map is null || d.Map.Equals(options.Map, StringComparison.OrdinalIgnoreCase)) &&
        (options.Source is null || d.Source.File.Contains(options.Source, StringComparison.OrdinalIgnoreCase));

    private static IEnumerable<CompatibilityEntity> DiscoverNonPositioned(string root, IEnumerable<string> files, AnalysisOptions options)
    {
        foreach (var file in files)
        {
            var relative = Path.GetRelativePath(root, file).Replace('\\', '/');
            if (options.Source is not null && !relative.Contains(options.Source, StringComparison.OrdinalIgnoreCase)) continue;
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                var value = lines[index].Trim();
                var columns = value.Split('\t');
                string? type = null; string name; string map = "-";
                if (value.StartsWith("function\tscript\t", StringComparison.OrdinalIgnoreCase) || value.StartsWith("function script ", StringComparison.OrdinalIgnoreCase))
                {
                    type = "function"; name = value.Split(['\t', ' ', '{'], StringSplitOptions.RemoveEmptyEntries).Skip(2).FirstOrDefault() ?? "anonymous";
                }
                else if (columns.Length >= 2 && columns[1].Trim().Equals("mapflag", StringComparison.OrdinalIgnoreCase))
                {
                    type = "mapflag"; map = columns[0].Trim(); name = columns.Length > 2 ? columns[2].Trim() : "mapflag";
                }
                else continue;
                if (options.Types is { Count: > 0 } && !options.Types.Contains(type)) continue;
                if (options.Map is not null && !map.Equals(options.Map, StringComparison.OrdinalIgnoreCase)) continue;
                var declaration = new RathenaDeclaration(map, 0, 0, 0, type, name, "", "", new(relative, index + 1));
                yield return Entity(declaration, type, null, CompatibilityStatus.NotYetAnalyzed, [], null, null);
            }
        }
    }

    private static SourceContext? Context(string root, string sourceFile, int line, int radius)
    {
        var candidates = new[] { Path.Combine(root, sourceFile), Path.Combine(root, sourceFile.Replace("legacy/rathena/", "", StringComparison.Ordinal)) };
        var path = candidates.FirstOrDefault(File.Exists); if (path is null) return null;
        var lines = File.ReadAllLines(path); var start = Math.Max(1, line - radius); var end = Math.Min(lines.Length, line + radius);
        return new(start, end, lines[(start - 1)..end]);
    }

    private static IReadOnlyList<string> Dependencies(LoweredNpcScript? script)
    {
        if (script is null) return [];
        var found = new HashSet<string>(StringComparer.Ordinal);
        foreach (var command in Commands(script.Statements))
        {
            var prefix = command.Name switch
            {
                "setquest" or "completequest" => "quest",
                "getitem" or "delitem" => "item",
                "warp" or "savepoint" => "map",
                _ => null
            };
            if (prefix is null || command.Arguments.Count == 0 || command.Arguments[0] is not LoweredLiteral literal) continue;
            var value = Convert.ToString(literal.Value, CultureInfo.InvariantCulture);
            if (!string.IsNullOrWhiteSpace(value)) found.Add(prefix + ":" + value);
        }
        return found.Order(StringComparer.Ordinal).ToArray();
    }

    private static IEnumerable<LoweredCommand> Commands(IEnumerable<LoweredScriptStatement> statements)
    {
        foreach (var statement in statements)
        {
            if (statement is LoweredCommand command) yield return command;
            foreach (var nested in statement switch
            {
                LoweredBlock block => Commands(block.Statements),
                LoweredIf conditional => Commands(new[] { conditional.Then }.Concat(conditional.Else is null ? [] : [conditional.Else])),
                LoweredSwitch selection => Commands(selection.Cases.SelectMany(item => item.Statements)),
                _ => []
            }) yield return nested;
        }
    }

    private static async Task WriteJsonLines<T>(string path, IEnumerable<T> values) =>
        await File.WriteAllTextAsync(path, string.Concat(values.Select(value => DeterministicJson.SerializeLine(value) + "\n")), new UTF8Encoding(false));

    private static string Markdown(RepositoryAnalysisResult result)
    {
        var text = new StringBuilder("# Athena.NET rAthena Compatibility Report\n\n## Summary\n\n");
        text.AppendLine($"Files analyzed: {result.Summary.FilesAnalyzed}");
        text.AppendLine($"Entities/events analyzed: {result.Summary.EntitiesAnalyzed}");
        text.AppendLine($"Compatible: {result.Summary.Compatible}"); text.AppendLine($"Unsupported: {result.Summary.Unsupported}");
        text.AppendLine("\n## Content categories\n\n| Category | Discovered | Compatible | Unsupported | Not yet analyzed | Not applicable |\n|---|---:|---:|---:|---:|---:|");
        foreach (var item in result.Summary.Categories) text.AppendLine($"| {item.Category} | {item.Discovered} | {item.Compatible} | {item.Unsupported} | {item.NotYetAnalyzed} | {item.NotApplicable} |");
        text.AppendLine("\n## Event compatibility\n\n| Event | Compatible | Unsupported |\n|---|---:|---:|");
        foreach (var item in result.Summary.Events) text.AppendLine($"| {item.Event} | {item.Compatible} | {item.Unsupported} |");
        text.AppendLine("\n## Top compatibility blockers\n\n| Rank | Feature | Stage | Affected | Sole blocker / unlocked |\n|---:|---|---|---:|---:|");
        foreach (var item in result.WorkItems.Take(25)) text.AppendLine($"| {item.Priority} | {item.Feature} | {item.Stage} | {item.AffectedEntities} | {item.EntitiesUnlocked} |");
        return text.ToString();
    }
}
