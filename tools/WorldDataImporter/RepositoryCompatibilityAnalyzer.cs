using System.Globalization;
using System.Text;
using Athena.WorldCompiler;
using Athena.WorldCompiler.Lowering;
using Athena.WorldCompiler.Rathena.Syntax;

internal enum CompatibilityStatus { Compatible, Unsupported, NotYetAnalyzed, NotApplicable }
internal enum FailureStage { Discovery, Parsing, SemanticAnalysis, Lowering, RuntimeCapability, Dependency, Generation }
internal enum AnalysisScope { Runtime, All }
internal enum DefinitionCompatibilityStatus { FullyCompatible, PartiallyCompatible, Unsupported, NotApplicable }

internal sealed record AnalysisOptions(
    string RathenaRoot, string OutputDirectory, int SourceContextLines = 5,
    IReadOnlySet<string>? Types = null, string? Map = null, string? Source = null,
    AnalysisScope Scope = AnalysisScope.Runtime, IReadOnlySet<string>? Domains = null);
internal sealed record SourceContext(int StartLine, int EndLine, IReadOnlyList<string> Text);
internal sealed record CompatibilityBlocker(string DiagnosticCode, string Feature, string Category, FailureStage Stage, string CompilerConstruct, int Line, int Column, string Message);
internal sealed record CompatibilityEntity(
    string Id, string EntityType, string EntityName, string? Map, string? Event,
    string SourceFile, int SourceLine, CompatibilityStatus Status,
    IReadOnlyList<string> Features, IReadOnlyList<CompatibilityBlocker>? Blockers = null,
    SourceContext? SourceContext = null, IReadOnlyList<string>? Dependencies = null);
internal sealed record CategorySummary(string Category, int Discovered, int Compatible, int Unsupported, int NotYetAnalyzed, int NotApplicable);
internal sealed record EventSummary(string Event, int Compatible, int Unsupported);
internal sealed record DefinitionCompatibilitySummary(int Total, int FullyCompatible, int PartiallyCompatible, int Unsupported, int NotApplicable);
// Priority 9 (ai/world-data.md): these four fields are the NPC/warp-declaration scan's OWN
// counts, not global multi-domain totals - the report also covers tens of thousands of domain
// entities (items/mobs/quests/etc, see Domains below) that this record does not aggregate into
// one blended number. Named Npc*-prefixed deliberately so a reader of summary.json/report.md
// cannot mistake "Compatible: 41" for "41 out of everything in this repository is compatible".
// The per-domain picture lives in Domains (RepositoryDomainAnalyzers.Summaries) - that is the
// correct place to look for a multi-domain view; there is no single meaningful blended percentage.
internal sealed record AnalysisSummary(int NpcSourceFilesAnalyzed, int NpcEventsAnalyzed, int NpcCompatible, int NpcUnsupported,
    IReadOnlyList<CategorySummary> Categories, IReadOnlyList<EventSummary> Events,
    DefinitionCompatibilitySummary NpcDefinitions, DefinitionCompatibilitySummary WarpNpcDefinitions,
    IReadOnlyList<DomainSummary> Domains);
internal sealed record BlockerSummary(string Domain, string Feature, string Category, FailureStage Stage, int Occurrences, int AffectedEntities, int SoleBlockerFor, IReadOnlyList<string> RepresentativeSources);
internal sealed record WorkItem(int Priority, string Domain, string Feature, string Category, FailureStage Stage, int AffectedEntities, int EntitiesUnlocked, int Occurrences, IReadOnlyList<string> RepresentativeSources);
internal sealed record EntityDependencies(string Entity, IReadOnlyList<string> Dependencies);
internal sealed record RepositoryAnalysisResult(AnalysisSummary Summary, IReadOnlyList<CompatibilityEntity> Compatible,
    IReadOnlyList<CompatibilityEntity> Unsupported, IReadOnlyList<BlockerSummary> Blockers,
    IReadOnlyList<WorkItem> WorkItems, IReadOnlyList<EntityDependencies> Dependencies,
    IReadOnlyList<DomainEntity> DomainEntities);

internal static class RepositoryCompatibilityAnalyzer
{
    public static RepositoryAnalysisResult Analyze(AnalysisOptions options)
    {
        var root = Path.GetFullPath(options.RathenaRoot);
        if (!Directory.Exists(root)) throw new ArgumentException($"rAthena root does not exist: {root}");
        if (options.SourceContextLines < 0 || options.SourceContextLines > 50) throw new ArgumentException("--source-context-lines must be between 0 and 50.");
        var contentRoot = options.Scope == AnalysisScope.Runtime && Directory.Exists(Path.Combine(root, "npc")) ? Path.Combine(root, "npc") : root;
        var files = Directory.EnumerateFiles(contentRoot, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();
        var declarations = RathenaSourceParser.Parse([contentRoot]);
        var warpConversion = WorldEntityConverter.Convert([contentRoot], new(null, null, null, "warp"));
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
                    ? rejectedWarps[(declaration.Source.File, declaration.Source.Line)].Select(item => new CompatibilityBlocker("RATD001", "world:warp", "world-converter", FailureStage.Discovery, "RathenaDeclaration", declaration.Source.Line, 1, item.Reason)).ToArray()
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
                    var eventBlockers = errors.Select(error => ToBlocker(error, syntax)).Distinct().OrderBy(item => item.Line).ThenBy(item => item.Column).ThenBy(item => item.Feature, StringComparer.Ordinal).ToArray();
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
        var npcBlockers = AggregateBlockers(unsupported);
        var categories = ordered.GroupBy(item => item.EntityType, StringComparer.Ordinal).OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => new CategorySummary(group.Key, group.Count(), group.Count(x => x.Status == CompatibilityStatus.Compatible), group.Count(x => x.Status == CompatibilityStatus.Unsupported), group.Count(x => x.Status == CompatibilityStatus.NotYetAnalyzed), group.Count(x => x.Status == CompatibilityStatus.NotApplicable))).ToArray();
        var eventSummary = ordered.Where(item => item.Event is not null).GroupBy(item => item.Event!, StringComparer.Ordinal).OrderBy(item => item.Key, StringComparer.Ordinal)
            .Select(group => new EventSummary(group.Key, group.Count(x => x.Status == CompatibilityStatus.Compatible), group.Count(x => x.Status == CompatibilityStatus.Unsupported))).ToArray();
        var npcDefinitions = DefinitionSummary(ordered, "npc");
        var warpNpcDefinitions = DefinitionSummary(ordered, "warpnpc");
        var domainEntities = RepositoryDomainAnalyzers.Analyze(root, options.Domains);
        var blockers = npcBlockers.Concat(AggregateDomainBlockers(domainEntities)).OrderByDescending(item => item.SoleBlockerFor)
            .ThenByDescending(item => item.AffectedEntities).ThenBy(item => item.Domain, StringComparer.Ordinal).ThenBy(item => item.Feature, StringComparer.Ordinal).ToArray();
        var work = blockers.OrderByDescending(item => item.SoleBlockerFor).ThenByDescending(item => item.AffectedEntities)
            .ThenByDescending(item => item.Occurrences).ThenBy(item => item.Domain, StringComparer.Ordinal).ThenBy(item => item.Feature, StringComparer.Ordinal)
            .Select((item, index) => new WorkItem(index + 1, item.Domain, item.Feature, item.Category, item.Stage, item.AffectedEntities, item.SoleBlockerFor, item.Occurrences, item.RepresentativeSources)).ToArray();
        var domainSummaries = RepositoryDomainAnalyzers.Summaries(domainEntities).ToList();
        AddDefinitionDomain("npc-definitions", npcDefinitions);
        AddDefinitionDomain("warpnpc-definitions", warpNpcDefinitions);
        AddEventDomain("warps", ordered.Where(item => item.EntityType == "warp"));
        domainSummaries = domainSummaries.OrderBy(item => item.Domain, StringComparer.Ordinal).ToList();
        // Cross-domain dependency graph: folds together the NPC/warp scan's own Dependencies
        // (setquest/getitem/warp resolved from lowered script commands) AND every domain entity's
        // Dependencies (mob-spawn -> map/mob, shop -> item, quest -> mob/item, item -> item via
        // Grants) into one deterministic array - see Priority 8/ai/world-data.md. Individual
        // dependency lists are already deduped/sorted (Entity()'s own helper for domain entities,
        // Dependencies() above for NPC/warp events); grouping by Entity id here additionally merges
        // the rare case where the same entity id is reported from both scans.
        var dependencies = ordered.Where(item => item.Dependencies is { Count: > 0 }).Select(item => (item.Id, Dependencies: item.Dependencies!))
            .Concat(domainEntities.Where(item => item.Dependencies.Count > 0).Select(item => (item.Id, item.Dependencies)))
            .GroupBy(item => item.Id, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => new EntityDependencies(group.Key, group.SelectMany(item => item.Dependencies).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray()))
            .ToArray();
        return new(new(files.Length, ordered.Length, compatible.Length, unsupported.Length, categories, eventSummary, npcDefinitions, warpNpcDefinitions, domainSummaries), compatible, unsupported, blockers, work, dependencies, domainEntities);

        void AddDefinitionDomain(string name, DefinitionCompatibilitySummary summary) => domainSummaries.Add(new(name, summary.Total, summary.FullyCompatible, summary.PartiallyCompatible, summary.Unsupported, 0, summary.NotApplicable));
        void AddEventDomain(string name, IEnumerable<CompatibilityEntity> values)
        {
            var array = values.ToArray(); domainSummaries.Add(new(name, array.Length, array.Count(x => x.Status == CompatibilityStatus.Compatible), 0,
                array.Count(x => x.Status == CompatibilityStatus.Unsupported), array.Count(x => x.Status == CompatibilityStatus.NotYetAnalyzed), array.Count(x => x.Status == CompatibilityStatus.NotApplicable)));
        }
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
        var domainsDirectory = Path.Combine(output, "domains"); Directory.CreateDirectory(domainsDirectory);
        foreach (var domain in result.DomainEntities.GroupBy(item => item.Domain, StringComparer.Ordinal).OrderBy(item => item.Key, StringComparer.Ordinal))
            await WriteJsonLines(Path.Combine(domainsDirectory, domain.Key + ".jsonl"), domain);
        await File.WriteAllTextAsync(Path.Combine(output, "report.md"), Markdown(result), new UTF8Encoding(false));
    }

    private static IReadOnlyList<string> EventNames(CompilationUnitSyntax syntax)
    {
        var labels = syntax.Statements.OfType<LabelStatementSyntax>().Where(item => item.IsEvent).Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var firstEvent = syntax.Statements.ToList().FindIndex(item => item is LabelStatementSyntax { IsEvent: true });
        if ((firstEvent < 0 || syntax.Statements.Take(firstEvent).Any(item => item is not EmptyStatementSyntax)) && !labels.Contains("OnClick", StringComparer.OrdinalIgnoreCase)) labels.Insert(0, "OnClick");
        return labels;
    }

    private static CompatibilityBlocker ToBlocker(CompilerDiagnostic diagnostic, CompilationUnitSyntax syntax)
    {
        var normalized = CompatibilityDiagnosticNormalizer.Normalize(diagnostic, syntax);
        return new(diagnostic.Code, normalized.CapabilityId, normalized.Category, normalized.Stage, normalized.CompilerConstruct,
            diagnostic.Span.Start.Line, diagnostic.Span.Start.Column, diagnostic.Message);
    }

    private static IReadOnlyList<BlockerSummary> AggregateBlockers(IReadOnlyList<CompatibilityEntity> unsupported)
    {
        var occurrences = unsupported.SelectMany(entity => entity.Blockers!.Select(blocker => (entity, blocker)));
        return occurrences.GroupBy(item => (item.blocker.Feature, item.blocker.Category, item.blocker.Stage))
            .Select(group => new BlockerSummary("npc", group.Key.Feature, group.Key.Category, group.Key.Stage, group.Count(),
                group.Select(item => item.entity.Id).Distinct(StringComparer.Ordinal).Count(),
                group.Select(item => item.entity).DistinctBy(item => item.Id).Count(entity => entity.Blockers!.Select(b => b.Feature).Distinct(StringComparer.Ordinal).Count() == 1),
                group.Select(item => $"{item.entity.SourceFile}:{item.blocker.Line}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5).ToArray()))
            .OrderByDescending(item => item.SoleBlockerFor).ThenByDescending(item => item.AffectedEntities).ThenBy(item => item.Feature, StringComparer.Ordinal).ToArray();
    }

    private static IReadOnlyList<BlockerSummary> AggregateDomainBlockers(IReadOnlyList<DomainEntity> entities)
    {
        return entities.SelectMany(entity => entity.Blockers.Select(blocker => (entity, blocker)))
            .GroupBy(item => (item.entity.Domain, item.blocker), StringTupleComparer.Ordinal)
            .Select(group => new BlockerSummary(group.Key.Domain, group.Key.blocker, DomainCategory(group.Key.blocker), FailureStage.RuntimeCapability,
                group.Count(), group.Select(item => item.entity.Id).Distinct(StringComparer.Ordinal).Count(),
                group.Select(item => item.entity).DistinctBy(item => item.Id).Count(entity => entity.Blockers.Distinct(StringComparer.Ordinal).Count() == 1),
                group.Select(item => $"{item.entity.SourceFile}:{item.entity.SourceLine}").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).Take(5).ToArray()))
            .ToArray();
    }

    private static string DomainCategory(string capability) => capability.Split(':', 2)[0];

    private sealed class StringTupleComparer : IEqualityComparer<(string Domain, string blocker)>
    {
        public static readonly StringTupleComparer Ordinal = new();
        public bool Equals((string Domain, string blocker) x, (string Domain, string blocker) y) => StringComparer.Ordinal.Equals(x.Domain, y.Domain) && StringComparer.Ordinal.Equals(x.blocker, y.blocker);
        public int GetHashCode((string Domain, string blocker) value) => HashCode.Combine(StringComparer.Ordinal.GetHashCode(value.Domain), StringComparer.Ordinal.GetHashCode(value.blocker));
    }

    private static DefinitionCompatibilitySummary DefinitionSummary(IReadOnlyList<CompatibilityEntity> records, string entityType)
    {
        var statuses = records.Where(item => item.EntityType == entityType)
            .GroupBy(item => (item.SourceFile, item.SourceLine, item.EntityName))
            .Select(group =>
            {
                var events = group.Where(item => item.Event is not null).ToArray();
                if (events.Length == 0) return DefinitionCompatibilityStatus.NotApplicable;
                var compatible = events.Count(item => item.Status == CompatibilityStatus.Compatible);
                return compatible == events.Length ? DefinitionCompatibilityStatus.FullyCompatible :
                    compatible > 0 ? DefinitionCompatibilityStatus.PartiallyCompatible : DefinitionCompatibilityStatus.Unsupported;
            }).ToArray();
        return new(statuses.Length,
            statuses.Count(item => item == DefinitionCompatibilityStatus.FullyCompatible),
            statuses.Count(item => item == DefinitionCompatibilityStatus.PartiallyCompatible),
            statuses.Count(item => item == DefinitionCompatibilityStatus.Unsupported),
            statuses.Count(item => item == DefinitionCompatibilityStatus.NotApplicable));
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
        text.AppendLine("_The counts below are scoped to the NPC/warp declaration scan only - they are NOT a global compatibility percentage. See \"Conversion overview\" for the multi-domain picture (items, mobs, quests, maps, and more)._\n");
        text.AppendLine($"NPC source files analyzed: {result.Summary.NpcSourceFilesAnalyzed}");
        text.AppendLine($"NPC entities/events analyzed: {result.Summary.NpcEventsAnalyzed}");
        text.AppendLine($"NPC compatible: {result.Summary.NpcCompatible}"); text.AppendLine($"NPC unsupported: {result.Summary.NpcUnsupported}");
        text.AppendLine("\n## Conversion overview\n\n| Domain | Total | Full | Partial | Unsupported | Not analyzed | N/A |\n|---|---:|---:|---:|---:|---:|---:|");
        foreach (var domain in result.Summary.Domains) text.AppendLine($"| {domain.Domain} | {domain.Total} | {domain.FullyCompatible} | {domain.PartiallyCompatible} | {domain.Unsupported} | {domain.NotYetAnalyzed} | {domain.NotApplicable} |");
        AppendDefinitionSummary(text, "NPC definitions", result.Summary.NpcDefinitions);
        AppendDefinitionSummary(text, "WARPNPC definitions", result.Summary.WarpNpcDefinitions);
        text.AppendLine("\n## Content categories\n\n| Category | Discovered | Compatible | Unsupported | Not yet analyzed | Not applicable |\n|---|---:|---:|---:|---:|---:|");
        foreach (var item in result.Summary.Categories) text.AppendLine($"| {item.Category} | {item.Discovered} | {item.Compatible} | {item.Unsupported} | {item.NotYetAnalyzed} | {item.NotApplicable} |");
        text.AppendLine("\n## Event compatibility\n\n| Event | Compatible | Unsupported |\n|---|---:|---:|");
        foreach (var item in result.Summary.Events) text.AppendLine($"| {item.Event} | {item.Compatible} | {item.Unsupported} |");
        text.AppendLine("\n## Top compatibility blockers\n\n| Rank | Feature | Stage | Affected | Sole blocker / unlocked |\n|---:|---|---|---:|---:|");
        foreach (var item in result.WorkItems.Take(25)) text.AppendLine($"| {item.Priority} | {item.Feature} | {item.Stage} | {item.AffectedEntities} | {item.EntitiesUnlocked} |");
        return text.ToString();
    }

    private static void AppendDefinitionSummary(StringBuilder text, string heading, DefinitionCompatibilitySummary summary)
    {
        text.AppendLine($"\n## {heading}\n\n| Status | Count | Percentage |\n|---|---:|---:|");
        Append("Fully compatible", summary.FullyCompatible);
        Append("Partially compatible", summary.PartiallyCompatible);
        Append("Unsupported", summary.Unsupported);
        Append("Not applicable", summary.NotApplicable);
        void Append(string status, int count)
        {
            var percentage = summary.Total == 0 ? 0 : count * 100d / summary.Total;
            text.AppendLine($"| {status} | {count} | {percentage.ToString("0.00", CultureInfo.InvariantCulture)}% |");
        }
    }
}
