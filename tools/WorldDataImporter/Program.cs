using System.Globalization;
using System.IO.Compression;
using System.Text.Json;
using Athena.WorldCompiler.Generation;
using Athena.WorldCompiler.Lowering;
using Athena.WorldCompiler.Rathena;
using Athena.WorldCompiler.Semantics;

return await WorldDataImporterCli.RunAsync(args);

internal static partial class WorldDataImporterCli
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0) { PrintUsage(); return 2; }
        try
        {
            return args[0] switch
            {
                "audit" => await AuditAsync(args[1..]),
                "analyze" => await AnalyzeAsync(args[1..]),
                "convert" => await ConvertAsync(args[1..]),
                "compile" => await CompileAsync(args[1..]),
                "compile-script" => await CompileScriptAsync(args[1..]),
                "compile-npc-world" => await CompileNpcWorldAsync(args[1..]),
                "compile-actors" => await CompileActorsAsync(args[1..]),
                "compile-navigation" => await CompileNavigationAsync(args[1..]),
                "compile-character-data" => await CompileCharacterDataAsync(args[1..]),
                "compile-progression" => await CompileProgressionAsync(args[1..]),
                "compile-mob-definitions" => await CompileMobDefinitionsAsync(args[1..]),
                "generate-mobs" => await GenerateMobsAsync(args[1..]),
                "compile-mob-spawn" => await CompileMobSpawnAsync(args[1..]),
                "generate-mob-spawns" => await GenerateMobSpawnsAsync(args[1..]),
                "generate-maps" => await GenerateMapsAsync(args[1..]),
                "generate-warps" => await GenerateWarpsAsync(args[1..]),
                "compile-quest-drop" => await CompileQuestDropAsync(args[1..]),
                "compile-item" => await CompileItemAsync(args[1..]),
                "compile-map-collision" => await CompileMapCollisionAsync(args[1..]),
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

    private static async Task<int> AnalyzeAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var context = options.Optional("source-context-lines") is { } raw
            ? int.Parse(raw, CultureInfo.InvariantCulture) : 5;
        var types = options.All("type").Select(item => item.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var domains = options.All("domain").Select(item => item.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var scope = (options.Optional("scope") ?? "runtime").ToLowerInvariant() switch
        {
            "runtime" => AnalysisScope.Runtime,
            "all" => AnalysisScope.All,
            var value => throw new ArgumentException($"Unknown analysis scope '{value}'; expected runtime or all.")
        };
        var analysisOptions = new AnalysisOptions(options.Required("rathena-root"), options.Required("output"), context,
            types.Count == 0 ? null : types, options.Optional("map"), options.Optional("source"), scope, domains.Count == 0 ? null : domains);
        var result = RepositoryCompatibilityAnalyzer.Analyze(analysisOptions);
        await RepositoryCompatibilityAnalyzer.WriteAsync(analysisOptions, result);
        Console.WriteLine($"NPC scan: analyzed {result.Summary.NpcSourceFilesAnalyzed} files and {result.Summary.NpcEventsAnalyzed} entities/events: {result.Summary.NpcCompatible} compatible, {result.Summary.NpcUnsupported} unsupported. See the domain table in report.md for the multi-domain (items/mobs/quests/maps/...) picture.");
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

    private static async Task<int> CompileAsync(string[] args)
    {
        var options=CliOptions.Parse(args); var roots=options.All("source-root");
        if(roots.Count==0) throw new ArgumentException("compile requires --source-root.");
        var filter=new ConversionFilter(options.Optional("source-file"),options.Optional("map"),options.Optional("name"),options.Optional("kind"));
        if(filter.IsEmpty) throw new ArgumentException("compile requires a narrow source/map/name/kind filter in this migration slice.");
        var names = options.All("name");
        var results = names.Count <= 1
            ? [WorldEntityConverter.Convert(roots, filter)]
            : names.Select(name => WorldEntityConverter.Convert(roots, filter with { Name = name })).ToArray();
        var lowered=WorldLowerer.Lower(results.SelectMany(result => result.Entities));
        var compileNamespace = options.Optional("namespace");
        var compileClassName = options.Optional("class-name");
        var source = compileNamespace is null && compileClassName is null
            ? CSharpWorldEmitter.Emit(lowered,options.Required("rathena-commit"))
            : CSharpWorldEmitter.Emit(lowered,options.Required("rathena-commit"),
                @namespace: compileNamespace ?? "Athena.Net.MapServer.Generated.World.Izlude",
                className: compileClassName ?? "GeneratedWarps");
        var output=Path.GetFullPath(options.Required("output")); Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output,source,new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Generated {lowered.Warps.Count} strongly typed warp definitions into {output}."); return results.All(result => result.Unsupported.Count == 0)?0:1;
    }

    private static async Task<int> CompileScriptAsync(string[] args)
    {
        var options = CliOptions.Parse(args); var roots = options.All("source-root");
        if (roots.Count == 0) throw new ArgumentException("compile-script requires --source-root.");
        var filter = new ConversionFilter(options.Optional("source-file"), options.Optional("map"), options.Optional("name"), options.Optional("kind"));
        if (filter.IsEmpty) throw new ArgumentException("compile-script requires a narrow source/map/name/kind filter.");
        var entity = AssertSingle(WorldEntityConverter.Convert(roots, filter).Entities, "generated script entity");
        var declarations = RathenaSourceParser.Parse(roots);
        var instance = declarations.FirstOrDefault(declaration => declaration.Map == entity.Actor?.Map && declaration.Name == entity.Actor.Name)
            ?? throw new ArgumentException($"Generated script declaration '{entity.Actor?.Name}' was not found.");
        var duplicateClose = instance.Directive.StartsWith("duplicate(", StringComparison.Ordinal) ? instance.Directive.IndexOf(')') : -1;
        var inferredBaseName = duplicateClose > 10 ? instance.Directive[10..duplicateClose] : null;
        var requestedTrigger = options.Optional("trigger") ?? (entity.Kind.Equals("Warp", StringComparison.OrdinalIgnoreCase) ? "OnTouch" : "OnClick");
        var matchingBindings = entity.Scripts?.Where(script => script.Trigger.Equals(requestedTrigger, StringComparison.OrdinalIgnoreCase)).ToArray();
        var binding = matchingBindings is { Length: > 0 }
            ? AssertSingle(matchingBindings, "generated script binding")
            : new ScriptBehaviorDefinition("OnTouch", entity.Actor!.Map, entity.Actor.X, entity.Actor.Y,
                entity.Triggers.FirstOrDefault()?.RadiusX ?? 0, entity.Triggers.FirstOrDefault()?.RadiusY ?? 0,
                true, true, [], "Generated executable binding", null, inferredBaseName);
        var templateName = binding.BaseNpcName ?? entity.Actor?.Name ?? throw new ArgumentException("Generated script entity has no actor.");
        var template = declarations.FirstOrDefault(declaration => declaration.Directive == "script" && declaration.Name == templateName)
            ?? throw new ArgumentException($"Script template '{templateName}' was not found.");
        var source = template.ScriptBody.TrimEnd(); if (source.EndsWith('}')) source = source[..^1];
        var syntax = new RathenaParser(source, template.Source.File, template.Source.Line + 1).ParseCompilationUnit();
        var semantics = SemanticAnalyzer.Analyze(syntax);
        if (semantics.Diagnostics.Any(diagnostic => diagnostic.Severity == "Error"))
            throw new ArgumentException(string.Join(Environment.NewLine, semantics.Diagnostics.Where(diagnostic => diagnostic.Severity == "Error").Select(diagnostic => $"{diagnostic.Span.Start.File}:{diagnostic.Span.Start.Line}:{diagnostic.Span.Start.Column} {diagnostic.Code}: {diagnostic.Message}")));
        var lowered = RathenaScriptLowerer.LowerEvent(syntax, binding.Trigger);
        if (!lowered.Success) throw new ArgumentException(string.Join(Environment.NewLine, lowered.Diagnostics.Where(diagnostic => diagnostic.Severity == "Error").Select(diagnostic => $"{diagnostic.Span.Start.File}:{diagnostic.Span.Start.Line}:{diagnostic.Span.Start.Column} {diagnostic.Code}: {diagnostic.Message}")));
        var actor = entity.Actor!; var className = ClassName(entity.Id, binding.Trigger);
        var metadata = new GeneratedNpcMetadata("Athena.Net.MapServer.Generated.World.Izlude", className, entity.Id, entity.Kind, actor.Name, actor.Map, actor.X, actor.Y, actor.Direction, actor.Class,
            binding.RadiusX, binding.RadiusY, binding.Trigger, binding.BaseNpcName, CanonicalSourceFile(template.Source.File), template.Source.Line + 1, template.Source.Line, options.Required("rathena-commit"), actor.EffectState);
        var generated = NpcScriptEmitter.Emit(lowered.Script!, metadata); var output = Path.GetFullPath(options.Required("output")); Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, generated, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Generated executable {binding.Trigger} script '{entity.Id}' into {output}."); return 0;
    }

    // compile-npc-world groups rAthena duplicate(...) chains into shared NpcDefinition/NpcPlacement data
    // (and, via --warp-name, WarpTriggerDefinition/WarpTriggerPlacement data for WARPNPC script+duplicate()
    // chains such as #ship_out/#intro_to_izlude) and emits one area-level AcademyWorld.cs + AcademyNpcs.cs
    // (+ AcademyWarpTriggers.cs when warp names are given) plus one Scripts/*.cs per unique behavior.
    // Emission selection (--name/--warp-name, --exclude-placement/--warp-exclude-placement, --no-behavior)
    // is applied strictly AFTER the converter returns its complete, lossless semantic result - the
    // converter itself never special-cases a name or narrows what it finds in pinned source.
    private static async Task<int> CompileNpcWorldAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var roots = options.All("source-root");
        if (roots.Count == 0) throw new ArgumentException("compile-npc-world requires --source-root.");
        var names = options.All("name");
        var warpNames = options.All("warp-name");
        if (names.Count == 0 && warpNames.Count == 0) throw new ArgumentException("compile-npc-world requires at least one --name or --warp-name (the templates in scope for this emission).");
        var sourceFile = options.Optional("source-file");
        var map = options.Optional("map");
        var worldNamespace = options.Required("namespace");
        var scriptsNamespace = worldNamespace + ".Scripts";
        var outputDir = Path.GetFullPath(options.Required("output-dir"));

        var definitions = new List<NpcDefinition>();
        var placements = new List<NpcPlacement>();
        foreach (var name in names)
        {
            var filter = new ConversionFilter(sourceFile, map, name, "npc");
            var result = WorldEntityConverter.ConvertNpcDefinitions(roots, filter);
            if (result.Unsupported.Count > 0)
                throw new ArgumentException($"Template '{name}' has unsupported declarations: " + string.Join("; ", result.Unsupported.Select(item => $"{item.File}:{item.Line} {item.Reason}")));
            definitions.AddRange(result.Definitions);
            placements.AddRange(result.Placements);
        }
        var conversion = new NpcConversionResult(definitions, placements, []);

        var excludedPlacements = options.All("exclude-placement").ToHashSet(StringComparer.Ordinal);
        var includedPlacementIds = excludedPlacements.Count == 0 ? null
            : (IReadOnlySet<string>)conversion.Placements.Select(p => p.PlacementId).Where(id => !excludedPlacements.Contains(id)).ToHashSet(StringComparer.Ordinal);
        var noBehaviorTemplateNames = options.All("no-behavior").ToHashSet(StringComparer.Ordinal);
        var definitionsWithoutEmittedBehavior = noBehaviorTemplateNames.Count == 0 ? null
            : (IReadOnlySet<string>)conversion.Definitions.Where(d => noBehaviorTemplateNames.Contains(d.TemplateNpcName)).Select(d => d.DefinitionId).ToHashSet(StringComparer.Ordinal);
        var selection = new NpcWorldEmissionSelection(includedPlacementIds, definitionsWithoutEmittedBehavior);

        WarpTriggerConversionResult? warpConversion = null;
        WarpTriggerEmissionSelection? warpSelection = null;
        if (warpNames.Count > 0)
        {
            var warpDefinitions = new List<WarpTriggerDefinition>();
            var warpPlacements = new List<WarpTriggerPlacement>();
            foreach (var name in warpNames)
            {
                var filter = new ConversionFilter(sourceFile, map, name, "warp");
                var result = WorldEntityConverter.ConvertWarpTriggers(roots, filter);
                if (result.Unsupported.Count > 0)
                    throw new ArgumentException($"WARPNPC template '{name}' has unsupported declarations: " + string.Join("; ", result.Unsupported.Select(item => $"{item.File}:{item.Line} {item.Reason}")));
                warpDefinitions.AddRange(result.Definitions);
                warpPlacements.AddRange(result.Placements);
            }
            warpConversion = new WarpTriggerConversionResult(warpDefinitions, warpPlacements, []);

            var excludedWarpPlacements = options.All("warp-exclude-placement").ToHashSet(StringComparer.Ordinal);
            var includedWarpPlacementIds = excludedWarpPlacements.Count == 0 ? null
                : (IReadOnlySet<string>)warpConversion.Placements.Select(p => p.PlacementId).Where(id => !excludedWarpPlacements.Contains(id)).ToHashSet(StringComparer.Ordinal);
            warpSelection = new WarpTriggerEmissionSelection(includedWarpPlacementIds);
        }

        var prefix = options.Optional("prefix") ?? "Academy";
        var emission = NpcWorldEmitter.Emit(conversion, selection, worldNamespace, scriptsNamespace, options.Required("rathena-commit"), warpConversion, warpSelection, prefix);

        Directory.CreateDirectory(outputDir);
        var scriptsDir = Path.Combine(outputDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        var encoding = new System.Text.UTF8Encoding(false);
        await File.WriteAllTextAsync(Path.Combine(outputDir, prefix + "World.cs"), emission.AcademyWorldSource, encoding);
        await File.WriteAllTextAsync(Path.Combine(outputDir, prefix + "Npcs.cs"), emission.AcademyNpcsSource, encoding);
        if (emission.AcademyWarpTriggersSource is { } warpTriggersSource)
            await File.WriteAllTextAsync(Path.Combine(outputDir, prefix + "WarpTriggers.cs"), warpTriggersSource, encoding);
        foreach (var (className, source) in emission.ScriptSources)
            await File.WriteAllTextAsync(Path.Combine(scriptsDir, className + ".cs"), source, encoding);

        var emittedPlacementCount = conversion.Placements.Count(placement => selection.IncludesPlacement(placement.PlacementId));
        var emittedWarpPlacementCount = warpConversion?.Placements.Count(placement => warpSelection!.IncludesPlacement(placement.PlacementId)) ?? 0;
        Console.WriteLine($"Generated {conversion.Definitions.Count} NPC definitions, {emittedPlacementCount} placements ({conversion.Placements.Count} in complete semantic conversion); " +
            $"{warpConversion?.Definitions.Count ?? 0} warp trigger definitions, {emittedWarpPlacementCount} placements ({warpConversion?.Placements.Count ?? 0} in complete semantic conversion); " +
            $"{emission.ScriptSources.Count} script classes into {outputDir}.");
        return 0;
    }

    private static async Task<int> CompileActorsAsync(string[] args)
    {
        var options = CliOptions.Parse(args); var roots = options.All("source-root");
        if (roots.Count == 0) throw new ArgumentException("compile-actors requires --source-root.");
        var names = options.All("name");
        if (names.Count == 0) throw new ArgumentException("compile-actors requires one or more --name filters.");
        var sourceFile = options.Optional("source-file");
        var map = options.Optional("map");
        var entities = names.SelectMany(name => WorldEntityConverter.Convert(roots, new(sourceFile, map, name, "npc")).Entities).ToArray();
        if (entities.Length != names.Count) throw new ArgumentException($"Expected {names.Count} actor definitions, found {entities.Length}.");
        var generated = CSharpActorEmitter.Emit(entities, options.Required("rathena-commit"));
        var output = Path.GetFullPath(options.Required("output")); Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, generated, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Generated {entities.Length} actor definitions into {output}."); return 0;
    }

    private static async Task<int> CompileNavigationAsync(string[] args)
    {
        var options = CliOptions.Parse(args); var roots = options.All("source-root"); var names = options.All("name");
        if (roots.Count == 0 || names.Count == 0) throw new ArgumentException("compile-navigation requires --source-root and --name.");
        var targetNamespace = options.Optional("namespace") ?? "Athena.Net.MapServer.Generated.World.Izlude";
        var declarations = RathenaSourceParser.Parse(roots);
        var templates = declarations.Where(value => value.Directive == "script").ToDictionary(value => value.Name, StringComparer.Ordinal);
        var rows = new List<string>();
        foreach (var name in names)
        {
            var instance = declarations.Single(value => value.Name == name);
            var close = instance.Directive.StartsWith("duplicate(", StringComparison.Ordinal) ? instance.Directive.IndexOf(')') : -1;
            var template = close > 10 ? templates[instance.Directive[10..close]] : instance;
            var match = System.Text.RegularExpressions.Regex.Match(template.ScriptBody, "navigateto\\(\\s*\"(?<map>[^\"]+)\"\\s*,\\s*(?<x>\\d+)\\s*,\\s*(?<y>\\d+)");
            if (!match.Success) throw new ArgumentException($"Navigation source '{name}' has no statically supported navigateto command.");
            var visual = instance.Arguments.Split(',').Select(value => value.Trim().TrimEnd('{')).ToArray();
            var rx = visual.Length > 1 && ushort.TryParse(visual[1], out var parsedRx) ? parsedRx : (ushort)0;
            var ry = visual.Length > 2 && ushort.TryParse(visual[2], out var parsedRy) ? parsedRy : (ushort)0;
            var destinationMap = match.Groups["map"].Value;
            if (instance.Map.StartsWith(destinationMap, StringComparison.Ordinal) && instance.Map.Length > destinationMap.Length &&
                instance.Map[destinationMap.Length..].All(char.IsAsciiDigit)) destinationMap = instance.Map;
            else
            {
                var suffix = new string(instance.Map.Reverse().TakeWhile(char.IsAsciiDigit).Reverse().ToArray());
                if (suffix.Length > 0 && !char.IsAsciiDigit(destinationMap[^1])) destinationMap += suffix;
            }
            rows.Add($"        new(\"{DeterministicId.For("npc", instance.Map, instance.Name)}\", \"{instance.Map}\", {instance.X}, {instance.Y}, {rx}, {ry}, \"{destinationMap}\", {match.Groups["x"].Value}, {match.Groups["y"].Value}, \"{CanonicalSourceFile(instance.Source.File)}\", {instance.Source.Line}),");
        }
        var source = "// <auto-generated>\n// Generated by Athena.WorldCompiler from pinned rAthena navigateto commands.\n// Do not edit this file directly.\n// </auto-generated>\nusing Athena.Net.MapServer.World;\nnamespace " + targetNamespace + ";\ninternal static class GeneratedTutorialNavigation\n{\n    internal static readonly NavigationDefinition[] All =\n    [\n" + string.Join('\n', rows.Order(StringComparer.Ordinal)) + "\n    ];\n}\n";
        var output = Path.GetFullPath(options.Required("output")); Directory.CreateDirectory(Path.GetDirectoryName(output)!); await File.WriteAllTextAsync(output, source, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Generated {rows.Count} navigation definitions into {output}."); return 0;
    }

    private static async Task<int> CompileProgressionAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var compilation = await CompileCharacterDataModelAsync(options);
        var output = Path.GetFullPath(options.Required("output"));
        var staging = Path.Combine(Path.GetTempPath(), $"athena-progression-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(staging);
            foreach (var artifact in compilation.Artifacts.Where(item => item.RelativePath.StartsWith("Progression/", StringComparison.Ordinal)))
                await File.WriteAllTextAsync(Path.Combine(staging, Path.GetFileName(artifact.RelativePath)), artifact.Source, new System.Text.UTF8Encoding(false));
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
            Directory.Move(staging, output);
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
        Console.WriteLine($"Regenerated {compilation.Counts.JobIdsWithProgression} progression mappings. Use compile-character-data for jobs and skills too.");
        return 0;
    }

    private static async Task<int> CompileCharacterDataAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var compilation = await CompileCharacterDataModelAsync(options);
        var commit = options.Required("rathena-commit");
        var output = Path.GetFullPath(options.Required("output"));
        var staging = Path.Combine(Path.GetTempPath(), $"athena-character-data-{Guid.NewGuid():N}");
        try
        {
            var encoding = new System.Text.UTF8Encoding(false);
            foreach (var artifact in compilation.Artifacts)
            {
                var path = Path.Combine(staging, artifact.RelativePath); Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, artifact.Source.Replace("\r\n", "\n", StringComparison.Ordinal), encoding);
            }
            Directory.CreateDirectory(output);
            foreach (var owned in new[] { "Jobs", "Progression", "Skills" })
            {
                var destination = Path.Combine(output, owned); if (Directory.Exists(destination)) Directory.Delete(destination, recursive: true);
                Directory.Move(Path.Combine(staging, owned), destination);
            }
        }
        finally { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }

        var counts = compilation.Counts;
        Console.WriteLine($"Compiled character data from pinned rAthena {commit}.");
        Console.WriteLine($"Jobs discovered: {counts.NumericJobIdentitiesDiscovered}; generated jobs: {counts.GeneratedJobDefinitions}; job mappings with progression: {counts.JobIdsWithProgression}; unique progression data sets: {counts.UniqueProgressionDefinitions}.");
        Console.WriteLine($"Canonical skills: {counts.CanonicalSkills}; direct skill trees: {counts.DirectSkillTrees}; effective skill trees: {counts.EffectiveSkillTrees}; generated files: {compilation.Artifacts.Count}; intentionally excluded jobs: {compilation.Exclusions.Count}.");
        foreach (var exclusion in compilation.Exclusions) Console.WriteLine($"  Excluded {exclusion}");
        return 0;
    }

    private static async Task<CharacterDataCompilation> CompileCharacterDataModelAsync(CliOptions options)
    {
        var root = Path.GetFullPath(options.Required("rathena-root"));
        var commit = options.Required("rathena-commit");
        var required = new[]
        {
            "src/common/mmo.hpp", "src/map/script_constants.hpp", "db/re/job_exp.yml", "db/re/job_basepoints.yml",
            "db/re/job_stats.yml", "db/re/statpoint.yml", "db/re/skill_db.yml", "db/re/skill_tree.yml",
            "conf/battle/player.conf",
        };
        foreach (var relative in required) if (!File.Exists(Path.Combine(root, relative))) throw new ArgumentException($"Required pinned source file is missing: {relative}.");
        return CharacterDataCompiler.Compile(new(
            await File.ReadAllTextAsync(Path.Combine(root, required[0])), await File.ReadAllTextAsync(Path.Combine(root, required[1])),
            await File.ReadAllTextAsync(Path.Combine(root, required[2])), await File.ReadAllTextAsync(Path.Combine(root, required[3])),
            await File.ReadAllTextAsync(Path.Combine(root, required[4])), await File.ReadAllTextAsync(Path.Combine(root, required[5])),
            await File.ReadAllTextAsync(Path.Combine(root, required[6])), await File.ReadAllTextAsync(Path.Combine(root, required[7])),
            await File.ReadAllTextAsync(Path.Combine(root, required[8]))), commit);
    }

    // Stateless/deterministic: fully regenerates the shared global GeneratedMobs partial class from
    // pinned mob_db.yml for exactly the --mob-id/--constant-name pairs given on THIS invocation - it
    // never reads back or merges any existing output file. Extending coverage (a new map's mobs)
    // means adding that mob's --mob-id/--constant-name to the invocation, not editing generated
    // output. Mob definitions are global game data ("what is mob <id>") shared by every map that
    // spawns it; this command owns that concern exclusively. Map-specific placement (where/how
    // many/how often) is a completely separate concern owned by compile-mob-spawn below.
    //
    // Output is sharded by fixed 1000-MobId buckets within the given --category (e.g.
    // "Monsters"), one file per NON-EMPTY bucket: GeneratedMobs.<Category>.<lo>-<hi>.cs (lo/hi are
    // the bucket's own fixed boundaries, e.g. 1000-1999, never derived from which IDs happen to be
    // present) - a fixed range grid, not item-count chunking, so a mob's file membership never
    // shifts as unrelated IDs are added/removed elsewhere in the same category. The full output
    // directory is treated as one generation unit for this category: every GeneratedMobs.<Category>.*.cs
    // file already in --output-dir is deleted before writing, so a bucket that no longer has any
    // selected mob does not silently survive from a previous run.
    private static async Task<int> CompileMobDefinitionsAsync(string[] args)
    {
        const int BucketSize = 1000;
        var options = CliOptions.Parse(args);
        var root = Path.GetFullPath(options.Required("rathena-root"));
        var mobIds = options.All("mob-id").Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var constantNames = options.All("constant-name");
        if (mobIds.Length == 0) throw new ArgumentException("compile-mob-definitions requires at least one --mob-id.");
        if (constantNames.Count != mobIds.Length) throw new ArgumentException("compile-mob-definitions requires the same number of --mob-id and --constant-name flags (one pair per mob, in order).");
        var commit = options.Required("rathena-commit");
        var className = options.Required("class-name");
        var category = options.Required("category");
        var outputDir = Path.GetFullPath(options.Required("output-dir"));

        var mobDbYaml = await File.ReadAllTextAsync(Path.Combine(root, "db/re/mob_db.yml"));
        var definitions = mobIds.Zip(constantNames, (id, constantName) => (Mob: MobDataCompiler.ReadMobDefinition(mobDbYaml, id), ConstantName: constantName)).ToArray();
        var sourceFile = CanonicalSourceFile(Path.Combine(root, "db/re/mob_db.yml"));

        Directory.CreateDirectory(outputDir);
        var stalePrefix = $"{className}.{category}.";
        foreach (var stale in Directory.EnumerateFiles(outputDir, $"{stalePrefix}*.cs")) File.Delete(stale);

        var buckets = definitions.GroupBy(item => item.Mob.Id / BucketSize).OrderBy(group => group.Key);
        var encoding = new System.Text.UTF8Encoding(false);
        var fileCount = 0;
        foreach (var bucket in buckets)
        {
            var lo = bucket.Key * BucketSize;
            var hi = lo + BucketSize - 1;
            var path = Path.Combine(outputDir, $"{className}.{category}.{lo}-{hi}.cs");
            await File.WriteAllTextAsync(path, MobDataCompiler.GenerateMobDefinitions(bucket.ToArray(), commit, className, sourceFile, 0), encoding);
            fileCount++;
        }

        Console.WriteLine($"Generated {definitions.Length} mob definition(s) across {fileCount} range-sharded file(s): {string.Join(", ", definitions.Select(d => $"{d.Mob.AegisName} ({d.Mob.Id})"))}.");
        return 0;
    }

    private static async Task<int> GenerateMobsAsync(string[] args)
    {
        const int BucketSize = 1000;
        const string ClassName = "GeneratedMobs";
        const string Category = "Monsters";
        var options = CliOptions.Parse(args);
        var root = Path.GetFullPath(options.Required("rathena-root"));
        var outputDir = Path.GetFullPath(options.Required("output"));
        var commit = options.Optional("rathena-commit") ?? ReadPinnedCommit(root);
        var sourcePath = Path.Combine(root, "db/re/mob_db.yml");
        var sourceFile = CanonicalSourceFile(sourcePath);
        var definitions = MobDataCompiler.ReadAllMobDefinitions(await File.ReadAllTextAsync(sourcePath));
        var symbols = MobDataCompiler.CreateGeneratedSymbols(definitions);

        Directory.CreateDirectory(outputDir);
        foreach (var path in Directory.EnumerateFiles(outputDir, "*.cs").Where(path => MobDataCompiler.IsOwnedGeneratedMobFile(path, ClassName, Category)))
            File.Delete(path);

        var encoding = new System.Text.UTF8Encoding(false);
        var fileCount = 0;
        foreach (var bucket in symbols.GroupBy(item => item.Mob.Id / BucketSize).OrderBy(group => group.Key))
        {
            var lo = bucket.Key * BucketSize;
            var path = Path.Combine(outputDir, $"{ClassName}.{Category}.{lo}-{lo + BucketSize - 1}.cs");
            await File.WriteAllTextAsync(path,
                MobDataCompiler.GenerateMobDefinitions(bucket.Select(item => (item.Mob, item.Symbol)).ToArray(), commit, ClassName, sourceFile, 0), encoding);
            fileCount++;
        }
        await File.WriteAllTextAsync(Path.Combine(outputDir, $"{ClassName}.Registry.cs"),
            MobDataCompiler.GenerateMobRegistry(symbols, commit, sourceFile), encoding);
        Console.WriteLine($"Mob generated-production coverage: {symbols.Count} / {definitions.Count} definitions across {fileCount} range shard(s) plus registry.");
        return 0;
    }

    private static string ReadPinnedCommit(string rathenaRoot)
    {
        var gitFile = Path.Combine(rathenaRoot, ".git");
        if (Directory.Exists(gitFile))
            throw new ArgumentException("generate-mobs requires --rathena-commit when the rAthena root is not a pinned submodule gitfile.");
        if (!File.Exists(gitFile))
            throw new ArgumentException("generate-mobs requires --rathena-commit because the pinned rAthena commit could not be discovered.");
        var pointer = File.ReadAllText(gitFile).Trim();
        if (!pointer.StartsWith("gitdir: ", StringComparison.Ordinal))
            throw new ArgumentException("Pinned rAthena .git file has an unsupported format; pass --rathena-commit explicitly.");
        var gitDir = pointer[8..];
        if (!Path.IsPathRooted(gitDir)) gitDir = Path.GetFullPath(Path.Combine(rathenaRoot, gitDir));
        var head = File.ReadAllText(Path.Combine(gitDir, "HEAD")).Trim();
        if (!head.StartsWith("ref: ", StringComparison.Ordinal)) return head;
        return File.ReadAllText(Path.Combine(gitDir, head[5..])).Trim();
    }

    // Spawn-only: references an ALREADY-generated global mob-definition class (via
    // --definition-class, e.g. "GeneratedMobs") rather than generating one itself - keeps this
    // command's sole responsibility "where/how many/how often does mob X spawn", matching
    // MobSpawnDefinition's own map-scoped shape. Supports one-or-many mobs per invocation via
    // repeated --mob-id/--name (parallel lists), so one invocation can cover every mob a pinned
    // spawn-declaration family shares.
    //
    // Two output shapes, chosen by whether --family-name is supplied:
    //
    // MAP-CENTRIC (default, no --family-name): one file per real source map, never bundled under
    // one "primary" map's folder merely because only that map is currently served -
    // --output-root/<PascalMap>/<PascalMap>MobSpawns.cs, each exposing a single
    // `MobSpawnDefinition[] All` covering every requested mob's rows for that one map. The output
    // root is treated as one generation unit: every existing <PascalMap>MobSpawns.cs under it is
    // deleted before writing.
    //
    // DUPLICATE-FAMILY (--family-name/--family-array-name given, one pair per concrete map in
    // order): for an EXPLICIT pinned rAthena duplicate family (e.g. prt_fild08/a/b/c/d - same
    // content pattern repeated per instanced duplicate), consolidates every concrete map into ONE
    // file (--output-root/<FamilyName>MobSpawns.cs) with one array per concrete map (named by
    // --family-array-name, e.g. "PrtFild08"/"PrtFild08A"/...) plus a composed `All` array
    // concatenating them - never collapsing the concrete maps into one runtime/template identity,
    // every entry still carries its own exact map string and source provenance. This is an
    // organizational rule for EXPLICIT source-backed duplicate families only, never a general
    // "combine unrelated maps" mechanism - callers choose it deliberately per invocation.
    //
    // Either shape: MapServerHostingScope.ServedMaps remains the sole runtime decision for which
    // concrete map populations are actually instantiated - generation never excludes a pinned
    // family member merely because it is not currently served.
    private static async Task<int> CompileMobSpawnAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var root = Path.GetFullPath(options.Required("rathena-root"));
        var spawnFile = options.Required("spawn-file");
        var mobNames = options.All("name");
        var mobIds = options.All("mob-id").Select(value => int.Parse(value, CultureInfo.InvariantCulture)).ToArray();
        var constantNames = options.All("constant-name");
        if (mobNames.Count == 0) throw new ArgumentException("compile-mob-spawn requires at least one --name.");
        if (mobIds.Length != mobNames.Count || constantNames.Count != mobNames.Count)
            throw new ArgumentException("compile-mob-spawn requires the same number of --mob-id, --name, and --constant-name flags (one set per mob, in order).");
        var commit = options.Required("rathena-commit");
        var definitionClassName = options.Required("definition-class");

        var spawnPath = Path.Combine(root, spawnFile);
        var spawnText = await File.ReadAllTextAsync(spawnPath);
        var excludedMaps = options.All("exclude-map").ToHashSet(StringComparer.Ordinal);
        var outputRoot = Path.GetFullPath(options.Required("output-root"));

        var allSpawns = new List<(MobDataCompiler.MobSpawnData Spawn, string MobDefinitionExpression)>();
        for (var index = 0; index < mobNames.Count; index++)
        {
            var mobId = mobIds[index];
            var mobName = mobNames[index];
            var spawns = MobDataCompiler.ReadMobSpawns(spawnText, CanonicalSourceFile(spawnPath), mobName, excludedMaps);
            var mismatched = spawns.Where(spawn => spawn.MobId != mobId).ToArray();
            if (mismatched.Length > 0) throw new ArgumentException($"Spawn declaration for '{mobName}' at line {mismatched[0].SourceLine} uses mob id {mismatched[0].MobId}, expected {mobId}.");
            foreach (var spawn in spawns) allSpawns.Add((spawn, $"{definitionClassName}.{constantNames[index]}"));
        }

        var encoding = new System.Text.UTF8Encoding(false);
        var byMap = allSpawns.GroupBy(item => item.Spawn.Map, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal).ToArray();

        var familyName = options.Optional("family-name");
        if (familyName is not null)
        {
            var familyMaps = options.All("family-map");
            var familyArrayNames = options.All("family-array-name");
            if (familyMaps.Count == 0 || familyArrayNames.Count != familyMaps.Count)
                throw new ArgumentException("--family-name requires matching --family-map/--family-array-name pairs (one per concrete map, in order).");
            var mapEntries = new List<(string ArrayName, IReadOnlyList<(MobDataCompiler.MobSpawnData Spawn, string MobDefinitionExpression)> Entries)>();
            for (var index = 0; index < familyMaps.Count; index++)
            {
                var group = byMap.FirstOrDefault(g => string.Equals(g.Key, familyMaps[index], StringComparison.Ordinal))
                    ?? throw new ArgumentException($"--family-map '{familyMaps[index]}' has no spawn declarations among the requested mobs.");
                mapEntries.Add((familyArrayNames[index], group.Select(item => (item.Spawn, item.MobDefinitionExpression)).ToArray()));
            }
            var familyNamespace = options.Required("namespace-root");
            var familyClassName = $"{familyName}MobSpawns";
            var familySource = MobDataCompiler.GenerateMobSpawnFamily(mapEntries, commit, familyClassName, familyNamespace);
            Directory.CreateDirectory(outputRoot);
            await File.WriteAllTextAsync(Path.Combine(outputRoot, $"{familyClassName}.cs"), familySource, encoding);
            Console.WriteLine($"Generated {allSpawns.Count} total spawn declarations for {mobNames.Count} mob(s) across {familyMaps.Count} family map(s) into one consolidated file.");
            return 0;
        }

        var namespaceRoot = options.Required("namespace-root");
        Directory.CreateDirectory(outputRoot);
        foreach (var staleDir in Directory.EnumerateDirectories(outputRoot))
        foreach (var stale in Directory.EnumerateFiles(staleDir, "*MobSpawns.cs"))
            File.Delete(stale);

        var fileCount = 0;
        foreach (var mapGroup in byMap)
        {
            var pascalMap = MapModuleNaming.PascalCase(mapGroup.Key);
            var mapNamespace = $"{namespaceRoot}.{pascalMap}";
            var className = $"{pascalMap}MobSpawns";
            var source = MobDataCompiler.GenerateMobSpawnsForMap(mapGroup.Select(item => (item.Spawn, item.MobDefinitionExpression)).ToArray(), commit, className, mapNamespace);
            var mapDir = Path.Combine(outputRoot, pascalMap);
            Directory.CreateDirectory(mapDir);
            await File.WriteAllTextAsync(Path.Combine(mapDir, $"{className}.cs"), source, encoding);
            fileCount++;
        }

        Console.WriteLine($"Generated {allSpawns.Count} total spawn declarations for {mobNames.Count} mob(s) across {fileCount} map file(s).");
        return 0;
    }

    // Deterministic PascalCase from a rAthena map name (e.g. "prt_fild08d" -> "PrtFild08d",
    // "int_land03" -> "IntLand03") - splits on '_' only, since rAthena map names never contain
    // other separators; a numeric suffix glued to the preceding word (e.g. "fild08") is NOT
    // further split, matching how "prt_fild08d" is one logical instanced-duplicate token.
    // Task's own generation summary shape (task section 38): "Pinned ordinary mob-spawn
    // declarations discovered" / "Generated" / "Mob definitions resolved" / "Valid map
    // dependencies" / "Invalid map dependencies" / "Generated files". The three known evt_zombie
    // declarations (npc/events/halloween_2008.txt:267-269, task section 40) are the ONLY map
    // dependency this command tolerates as invalid - ANY other unresolved map is a fail-closed
    // generation error, matching "unexpected invalid dependencies should fail closed" (task 38).
    // Similarly every discovered MobId must resolve against the freshly-parsed mob_db.yml symbol
    // table (mirrors generate-mobs' own ReadAllMobDefinitions/CreateGeneratedSymbols pipeline,
    // since this project cannot reference the already-generated MapServer assembly - see
    // WorldDataImporter.csproj's own doc comment on why only two isolated map-cache files are
    // compiled in, not the whole MapServer project) - an unresolvable MobId is a hard generation
    // error (task section 10), never a silently-dropped or null MobDefinition reference.
    private static readonly IReadOnlySet<string> KnownInvalidMapDependencies = new HashSet<string>(StringComparer.Ordinal) { "evt_zombie" };

    // Explicit family placement for maps that already belong to an established generated
    // World-family module with its own Npcs/Warps/World content (never inferred - a new family
    // must be added here deliberately). (FolderPath relative to the World output root, ClassName
    // prefix, Namespace suffix appended to "Athena.Net.MapServer.Generated.World."). PrtFild08
    // covers the pinned prt_fild08{,a,b,c,d} duplicate family; Izlude/Academy covers the pinned
    // int_land{,01,02,03,04} tutorial family (both already host real NPC/warp/script content under
    // these exact folders - see ai/world-data.md's "Generated mob spawns" section).
    // ArrayName is an explicit per-map override (never re-derived from PascalCaseMapName, which
    // would produce "PrtFild08a" - lowercase - for the trailing instanced-duplicate letter):
    // matches the pinned "prt_fild08{,a,b,c,d}" family's established capitalization convention,
    // the SAME one the retired hand-authored PrtFild08MobSpawns.cs used
    // ("PrtFild08"/"PrtFild08A"/"PrtFild08B"/"PrtFild08C"/"PrtFild08D").
    // Map-oriented placement (ai/world-data.md's "Generated mob spawns" section): one canonical
    // spawn file per map/world-family module, never per pinned source file - if several source
    // files target the same map, their declarations are merged into that one map's file
    // (WorldSourceInfo still carries the EXACT per-declaration file/line, so no provenance is
    // lost). Classification, in order:
    //   1. map resolves through the canonical map-cache layers -> its MapFamilies entry if one
    //      exists, otherwise a fresh single-map folder named after the map itself;
    //   2. map does NOT resolve, but every declaration for it originates from a source file under
    //      npc/events/ AND the map token starts with "evt_" -> World/Events/<PascalMap>/ (source
    //      declaration/mob-reference stay valid; map dependency/runtime activation stay invalid -
    //      this is an organizational placement only, never a claim the map is loadable);
    //   3. anything else unresolved is a hard generation failure - "evt_" is never a blanket
    //      escape hatch on its own; the source-context requirement (events directory) must also
    //      hold, and any genuinely new unresolved map fails closed rather than silently guessing a
    //      classification for it.
    private readonly record struct MapPlacement(string FolderPath, string ClassName, string Namespace, string ArrayName, bool RuntimeValid);

    private static MapPlacement ClassifyMap(string map, bool mapResolves, bool everyDeclarationIsFromEventsDirectory)
    {
        if (mapResolves)
        {
            if (MapModuleNaming.TryGetFamily(map, out var family))
                return new MapPlacement(family.FolderPath, family.ClassName, family.Namespace, family.ArrayName, true);
            var pascal = MapModuleNaming.PascalCase(map);
            return new MapPlacement(pascal, pascal, $"Athena.Net.MapServer.Generated.World.{pascal}", pascal, true);
        }
        if (map.StartsWith("evt_", StringComparison.Ordinal) && everyDeclarationIsFromEventsDirectory)
        {
            var pascal = MapModuleNaming.PascalCase(map);
            return new MapPlacement($"Events/{pascal}", pascal, $"Athena.Net.MapServer.Generated.World.Events.{pascal}", pascal, false);
        }
        throw new ArgumentException($"generate-mob-spawns found spawn declaration(s) targeting unresolved map '{map}' that do not qualify for the known event-map placement (source file under npc/events/ AND a map token starting with \"evt_\") - this is a genuinely new unresolved map dependency and must be investigated, not silently classified.");
    }

    private static async Task<int> GenerateMobSpawnsAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var root = Path.GetFullPath(options.Required("rathena-root"));
        var outputDir = Path.GetFullPath(options.Required("output"));
        var commit = options.Optional("rathena-commit") ?? ReadPinnedCommit(root);
        const string RegistryFileName = "GeneratedMobSpawnRegistry.cs";
        const string RegistryNamespace = "Athena.Net.MapServer.Generated.World";
        const string RegistryClassName = "GeneratedMobSpawnRegistry";
        const string ProfilesFileName = "GeneratedMobSpawnLoadProfiles.cs";

        // Mob-definition resolution: same source/pipeline generate-mobs itself uses, kept
        // independent of any already-generated MapServer output.
        var mobDbPath = Path.Combine(root, "db/re/mob_db.yml");
        var mobDefinitions = MobDataCompiler.ReadAllMobDefinitions(await File.ReadAllTextAsync(mobDbPath));
        var mobSymbols = MobDataCompiler.CreateGeneratedSymbols(mobDefinitions);
        var mobSymbolById = mobSymbols.ToDictionary(item => item.Mob.Id, item => item.Symbol);
        var aegisNameToId = MobDataCompiler.BuildAegisNameLookup(mobDefinitions);

        // Renewal source-load classification (ai/world-data.md's "Generated mob spawns" section) -
        // resolved once, from the real pinned config graph, and used ONLY to build the
        // GeneratedMobSpawnLoadProfiles views below; never stored onto MobSpawnDefinition itself
        // (task section 4/5: a spawn's source representation stays profile-neutral).
        // ResolveActiveNpcFiles returns paths relative to `root` (e.g. "npc/re/mobs/towns.txt");
        // MobSpawnLoadClassifier.Classify normalizes a canonical "legacy/rathena/npc/..." source
        // file (WorldSourceInfo.File/MobSpawnData.SourceFile) down to this same root-relative form
        // before comparing, so renewalActiveFiles stays in ResolveActiveNpcFiles' own natural shape.
        var renewalActiveFiles = RathenaScriptConfigGraph.ResolveActiveNpcFiles(root).ToHashSet(StringComparer.Ordinal);

        // Map resolution: the SAME canonical map-cache layering RepositoryDomainAnalyzers.AnalyzeMaps
        // and MapCollisionStartupLoader use (RathenaMapCacheLayers.Merge) - generated spawn map
        // validation and analyzer map validation must agree (task section 39), never a third
        // independent interpretation of "which maps exist".
        var baseCachePath = Path.Combine(root, "db/map_cache.dat");
        var renewalCachePath = Path.Combine(root, "db/re/map_cache.dat");
        var importCachePath = Path.Combine(root, "db/import/map_cache.dat");
        var baseCacheBytes = await File.ReadAllBytesAsync(baseCachePath);
        var renewalCacheBytes = File.Exists(renewalCachePath) ? await File.ReadAllBytesAsync(renewalCachePath) : null;
        var importCacheBytes = File.Exists(importCachePath) ? await File.ReadAllBytesAsync(importCachePath) : null;
        var resolvedMaps = Athena.Rathena.Data.RathenaMapCacheLayers.Merge(baseCacheBytes, renewalCacheBytes, importCacheBytes);
        var mapNames = resolvedMaps.Select(item => item.Entry.Name).ToHashSet(StringComparer.Ordinal);

        // Deterministic ordinal source-file enumeration (task section 19) - identical to
        // RepositoryDomainAnalyzers.AnalyzeMobSpawns' own enumeration, so both analyzer and
        // generator agree on which files exist and in what order. Declarations are collected here
        // in (file, line) order but immediately regrouped BY MAP below - source-file order only
        // matters as the deterministic tie-break within one map's own merged declaration list.
        var npcRoot = Path.Combine(root, "npc");
        var sourceFiles = Directory.EnumerateFiles(npcRoot, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal).ToArray();

        var discovered = new List<MobDataCompiler.MobSpawnData>();
        foreach (var path in sourceFiles)
        {
            var relative = CanonicalSourceFile(path);
            var text = await File.ReadAllTextAsync(path);
            discovered.AddRange(MobDataCompiler.ReadAllMobSpawns(text, relative, aegisNameToId));
        }

        // Fail closed: every discovered MobId must resolve (task section 10).
        var unresolvedMobIds = discovered.Select(item => item.MobId).Distinct().Where(id => !mobSymbolById.ContainsKey(id)).OrderBy(id => id).ToArray();
        if (unresolvedMobIds.Length > 0)
            throw new ArgumentException($"generate-mob-spawns found {unresolvedMobIds.Length} spawn MobId(s) with no resolvable generated MobDefinition: {string.Join(", ", unresolvedMobIds)}. Regenerate mob definitions (generate-mobs) first, or confirm these are genuinely absent from pinned mob_db.yml.");

        // Group by map (deterministic source-file-then-line order within each map - task section
        // 18) and classify each map's placement. A map's declarations may span multiple source
        // files (task section 42) - they still resolve to ONE physical file/array.
        var byMap = discovered.GroupBy(spawn => spawn.Map, StringComparer.Ordinal).OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (Map: group.Key, Spawns: (IReadOnlyList<MobDataCompiler.MobSpawnData>)group.OrderBy(spawn => spawn.SourceFile, StringComparer.Ordinal).ThenBy(spawn => spawn.SourceLine).ToArray()))
            .ToArray();

        var placements = new List<(string Map, MapPlacement Placement, IReadOnlyList<MobDataCompiler.MobSpawnData> Spawns)>();
        var invalidMapDeclarationCount = 0;
        // Two DISTINCT real maps can still collide onto the same PascalCase folder/class name for a
        // fresh single-map placement - a real pinned case: "gl_cas02" and "gl_cas02_" (trailing
        // underscore, championmobs.txt:99-100) are both genuinely resolvable maps and both
        // PascalCaseMapName to "GlCas02" (empty split segments are dropped). Family-member maps
        // never hit this (they already have a distinct ArrayName per map within one shared file),
        // so this only guards fresh single-map folders: a colliding folder/class name gets a
        // deterministic numeric suffix in stable (already map-name-ordered) iteration order -
        // each raw map string still gets its OWN file/array, never silently merged.
        var usedSingleMapFolders = new HashSet<string>(StringComparer.Ordinal);
        foreach (var (map, spawns) in byMap)
        {
            var resolves = mapNames.Contains(map);
            if (!resolves) invalidMapDeclarationCount += spawns.Count;
            var everyDeclarationFromEvents = spawns.All(spawn => spawn.SourceFile.Contains("/npc/events/", StringComparison.Ordinal));
            var placement = ClassifyMap(map, resolves, everyDeclarationFromEvents);
            if (!MapModuleNaming.TryGetFamily(map, out _))
            {
                var folder = placement.FolderPath;
                var suffix = 2;
                while (!usedSingleMapFolders.Add(folder))
                {
                    folder = $"{placement.FolderPath}_{suffix}";
                    suffix++;
                }
                if (folder != placement.FolderPath)
                    placement = placement with { FolderPath = folder, ClassName = folder, Namespace = placement.Namespace + "_" + (suffix - 1), ArrayName = folder };
            }
            placements.Add((map, placement, spawns));
        }

        // Group individual map placements into physical FILES: family folders (PrtFild08, Academy)
        // combine several maps' arrays into one file with a composed `All`; every other map gets
        // its own single-map file (still emitted via the same family-shaped generator, with one
        // named array plus a trivial composed `All`, for a uniform generated shape everywhere).
        var byFile = placements.GroupBy(item => (item.Placement.FolderPath, item.Placement.ClassName, item.Placement.Namespace), item => item)
            .OrderBy(group => group.Key.FolderPath, StringComparer.Ordinal)
            .ToArray();

        Directory.CreateDirectory(outputDir);
        // Stale-file cleanup spans the ENTIRE output tree (every existing map/family folder may
        // contain a previously-generated *MobSpawns.cs alongside hand-maintained *Npcs.cs/
        // *Warps.cs/*World.cs/Scripts/ siblings - task's safe-cleanup requirement) - filename suffix
        // ("...MobSpawns.cs") plus the auto-generated header are BOTH required before deletion
        // (MobDataCompiler.IsOwnedGeneratedMobSpawnFile), so a hand-maintained file can never be
        // swept up even if it coincidentally ends in "MobSpawns.cs". Also removes any leftover
        // "*Spawn.cs" file from this generator's own earlier (now-renamed) naming convention -
        // IsOwnedGeneratedMobSpawnFile recognizes both suffixes specifically for this one-time
        // migration cleanup (see that method's own doc comment).
        foreach (var stale in Directory.EnumerateFiles(outputDir, "*.cs", SearchOption.AllDirectories).Where(path => MobDataCompiler.IsOwnedGeneratedMobSpawnFile(path, RegistryFileName, ProfilesFileName)))
            File.Delete(stale);
        // Remove the RETIRED source-file-sharded layout's directory entirely if it still exists
        // from a prior generation (this branch's own earlier iteration used
        // Generated/World/MobSpawns/GeneratedMobSpawns.<SourceFileSuffix>.cs - never kept alongside
        // the new map-oriented layout).
        var staleSourceShardedDir = Path.Combine(outputDir, "MobSpawns");
        if (Directory.Exists(staleSourceShardedDir) && Directory.EnumerateFiles(staleSourceShardedDir, "*.cs").All(path => MobDataCompiler.IsOwnedGeneratedMobSpawnFile(path, RegistryFileName, ProfilesFileName)))
            Directory.Delete(staleSourceShardedDir, recursive: true);

        var encoding = new System.Text.UTF8Encoding(false);
        var arrayExpressions = new List<string>();
        // Tracks, per emitted array EXPRESSION, its own flattened MobSpawnData in the EXACT order
        // that expression's generated `All` array holds them - needed below to reconstruct
        // GeneratedMobSpawnRegistry.All's own flattened element order (a plain concatenation of
        // arrayExpressions in the SAME OrderBy(expr) order used at the registry-source-generation
        // call site) so GeneratedMobSpawnLoadProfiles can reference registry elements BY INDEX
        // rather than re-`new`-ing a second copy of any MobSpawnDefinition (task section 22).
        var spawnsByExpression = new Dictionary<string, IReadOnlyList<MobDataCompiler.MobSpawnData>>(StringComparer.Ordinal);
        foreach (var fileGroup in byFile)
        {
            var (folderPath, className, ns) = fileGroup.Key;
            var mapEntries = fileGroup.Select(item => (
                item.Placement.ArrayName,
                (IReadOnlyList<(MobDataCompiler.MobSpawnData Spawn, string MobDefinitionExpression)>)item.Spawns.Select(spawn => (spawn, $"GeneratedMobs.{mobSymbolById[spawn.MobId]}")).ToArray()
            )).OrderBy(item => item.ArrayName, StringComparer.Ordinal).ToArray();

            var source = MobDataCompiler.GenerateMobSpawnFamily(mapEntries, commit, $"{className}MobSpawns", ns);
            var fileDir = Path.Combine(outputDir, folderPath.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(fileDir);
            var filePath = Path.Combine(fileDir, $"{className}MobSpawns.cs");
            await File.WriteAllTextAsync(filePath, source, encoding);
            var arrayExpression = $"{ns}.{className}MobSpawns.All";
            arrayExpressions.Add(arrayExpression);
            spawnsByExpression[arrayExpression] = mapEntries.SelectMany(entry => entry.Item2.Select(pair => pair.Spawn)).ToArray();
        }

        var orderedArrayExpressions = arrayExpressions.OrderBy(expr => expr, StringComparer.Ordinal).ToArray();
        var registrySource = GenerateMobSpawnRegistry(orderedArrayExpressions, RegistryClassName, RegistryNamespace, commit, "npc/**/*.txt (all pinned ordinary monster declarations)");
        await File.WriteAllTextAsync(Path.Combine(outputDir, RegistryFileName), registrySource, encoding);

        // GeneratedMobSpawnRegistry.All's exact flattened element order (reconstructed here, not
        // re-scanned from the emitted source) - the authority GeneratedMobSpawnLoadProfiles indexes
        // into by position, guaranteeing true CLR reference identity to the SAME canonical
        // MobSpawnDefinition instances (task section 22 - views/indices, never duplicate copies).
        var registryAllOrder = orderedArrayExpressions.SelectMany(expr => spawnsByExpression[expr]).ToArray();
        var renewalIndices = new List<int>();
        var overlayOnlyIndices = new List<int>();
        var loadClassCounts = new Dictionary<MobSpawnLoadClass, int>();
        for (var i = 0; i < registryAllOrder.Length; i++)
        {
            var loadClass = MobSpawnLoadClassifier.Classify(registryAllOrder[i].SourceFile, renewalActiveFiles);
            loadClassCounts[loadClass] = loadClassCounts.GetValueOrDefault(loadClass) + 1;
            if (loadClass == MobSpawnLoadClass.RenewalDefault) renewalIndices.Add(i);
            else if (loadClass == MobSpawnLoadClass.AthenaOverlay) overlayOnlyIndices.Add(i);
        }
        var effectiveIndices = renewalIndices.Concat(overlayOnlyIndices).Order().ToArray();

        var profilesSource = GenerateMobSpawnLoadProfiles(renewalIndices, effectiveIndices, RegistryClassName, RegistryNamespace, commit);
        await File.WriteAllTextAsync(Path.Combine(outputDir, ProfilesFileName), profilesSource, encoding);

        var validCount = discovered.Count - invalidMapDeclarationCount;
        Console.WriteLine($"Pinned ordinary mob-spawn declarations: {discovered.Count}");
        Console.WriteLine($"Generated:                             {discovered.Count}");
        Console.WriteLine($"Mob definitions resolved:              {discovered.Count} / {discovered.Count}");
        Console.WriteLine($"Valid map dependencies:                {validCount}");
        Console.WriteLine($"Invalid map dependencies:              {invalidMapDeclarationCount}");
        Console.WriteLine($"Generated map/family modules:          {byFile.Length}");
        Console.WriteLine($"Active Renewal source files (config graph): {renewalActiveFiles.Count}");
        Console.WriteLine($"RenewalDefault declarations:           {loadClassCounts.GetValueOrDefault(MobSpawnLoadClass.RenewalDefault)}");
        Console.WriteLine($"AthenaOverlay declarations:            {loadClassCounts.GetValueOrDefault(MobSpawnLoadClass.AthenaOverlay)}");
        Console.WriteLine($"PreRenewalSource declarations:         {loadClassCounts.GetValueOrDefault(MobSpawnLoadClass.PreRenewalSource)}");
        Console.WriteLine($"Disabled declarations:                 {loadClassCounts.GetValueOrDefault(MobSpawnLoadClass.Disabled)}");
        Console.WriteLine($"AthenaIroEffective declarations:       {effectiveIndices.Length}");
        return 0;
    }

    // Hand-authored (not MobDataCompiler-emitted, unlike the per-source-file arrays above) since
    // this is the one map-KEYED aggregation point over every generated array - mirrors
    // MobDataCompiler.GenerateMobRegistry's own hand-rolled dictionary shape, adapted to a
    // string-keyed multi-value (one map may have declarations from several source files, task
    // section 42/43 - merged deterministically, never "last file wins", and never deduplicated by
    // content since two identical-looking declarations at different source locations are distinct
    // entities per pinned rAthena, task section 43).
    private static string GenerateMobSpawnRegistry(IReadOnlyList<string> allArrayExpressions, string className, string worldNamespace, string commit, string sourceDescription)
    {
        var output = new System.Text.StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").AppendLine(sourceDescription)
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using System.Collections.Frozen;")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            .Append("namespace ").Append(worldNamespace).AppendLine(";")
            .AppendLine()
            .AppendLine("internal static class GeneratedMobSpawnRegistry")
            .AppendLine("{")
            .AppendLine("    internal static readonly MobSpawnDefinition[] All =")
            .Append("        [.. ").Append(string.Join(", .. ", allArrayExpressions)).AppendLine("];")
            .AppendLine()
            .AppendLine("    private static readonly FrozenDictionary<string, MobSpawnDefinition[]> ByMap = All")
            .AppendLine("        .GroupBy(spawn => spawn.Map, StringComparer.OrdinalIgnoreCase)")
            .AppendLine("        .ToFrozenDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);")
            .AppendLine()
            .AppendLine("    internal static int Count => All.Length;")
            .AppendLine("    internal static bool TryGetMap(string map, out IReadOnlyList<MobSpawnDefinition> spawns)")
            .AppendLine("    {")
            .AppendLine("        if (ByMap.TryGetValue(map, out var found)) { spawns = found; return true; }")
            .AppendLine("        spawns = []; return false;")
            .AppendLine("    }")
            .AppendLine("    // Returns EVERY represented source declaration for this map, regardless of Renewal")
            .AppendLine("    // source-load profile activation (see GeneratedMobSpawnLoadProfiles.GetForMap for a")
            .AppendLine("    // profile-filtered view - RathenaRenewalDefault/AthenaIroEffective).")
            .AppendLine("    internal static IReadOnlyList<MobSpawnDefinition> GetForMap(string map) => ByMap.TryGetValue(map, out var found) ? found : [];")
            .AppendLine("}");
        return output.ToString();
    }

    // GeneratedMobSpawnLoadProfiles: a profile-neutral MobSpawnDefinition's Renewal source-load
    // classification is deliberately NOT a field on the record itself (ai/world-data.md's "Generated
    // mob spawns" - RathenaScriptConfigGraph). Instead, this emits two filtered VIEWS over
    // GeneratedMobSpawnRegistry.All, indexed BY POSITION into that same array - guaranteeing true CLR
    // reference identity to the SAME canonical instances (task section 22: never a duplicate copy).
    // renewalIndices/effectiveIndices are pre-computed by the caller (GenerateMobSpawnsAsync) from
    // registryAllOrder, which reconstructs GeneratedMobSpawnRegistry.All's own exact flattened
    // element order - both index lists are already ascending/deterministic by construction.
    private static string GenerateMobSpawnLoadProfiles(IReadOnlyList<int> renewalIndices, IReadOnlyList<int> effectiveIndices, string registryClassName, string worldNamespace, string commit)
    {
        var output = new System.Text.StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .AppendLine("// Source: npc/re/scripts_main.conf (pinned Renewal script-config graph) + AthenaOverlaySourceFiles")
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using System.Collections.Frozen;")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            .Append("namespace ").Append(worldNamespace).AppendLine(";")
            .AppendLine()
            .AppendLine("// RathenaRenewalDefault: declarations reachable through the active npc:/import: graph rooted")
            .AppendLine("// at pinned npc/re/scripts_main.conf. AthenaIroEffective: RathenaRenewalDefault plus explicit")
            .AppendLine("// Athena.NET overlay declarations (AthenaOverlaySourceFiles) - the profile runtime registration")
            .AppendLine("// consumes (GeneratedScriptRegistry.Register). See ai/world-data.md for the full model.")
            .AppendLine("internal enum MobSpawnLoadProfile { RathenaRenewalDefault, AthenaIroEffective }")
            .AppendLine()
            .AppendLine("internal static class GeneratedMobSpawnLoadProfiles")
            .AppendLine("{")
            .Append("    internal static readonly MobSpawnDefinition[] RathenaRenewalDefault = [")
            .Append(string.Join(", ", renewalIndices.Select(index => $"{registryClassName}.All[{index}]")))
            .AppendLine("];")
            .Append("    internal static readonly MobSpawnDefinition[] AthenaIroEffective = [")
            .Append(string.Join(", ", effectiveIndices.Select(index => $"{registryClassName}.All[{index}]")))
            .AppendLine("];")
            .AppendLine()
            .AppendLine("    private static readonly FrozenDictionary<string, MobSpawnDefinition[]> RenewalByMap = RathenaRenewalDefault")
            .AppendLine("        .GroupBy(spawn => spawn.Map, StringComparer.OrdinalIgnoreCase)")
            .AppendLine("        .ToFrozenDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);")
            .AppendLine("    private static readonly FrozenDictionary<string, MobSpawnDefinition[]> EffectiveByMap = AthenaIroEffective")
            .AppendLine("        .GroupBy(spawn => spawn.Map, StringComparer.OrdinalIgnoreCase)")
            .AppendLine("        .ToFrozenDictionary(group => group.Key, group => group.ToArray(), StringComparer.OrdinalIgnoreCase);")
            .AppendLine()
            .AppendLine("    internal static IReadOnlyList<MobSpawnDefinition> GetForMap(string map, MobSpawnLoadProfile profile) => profile switch")
            .AppendLine("    {")
            .AppendLine("        MobSpawnLoadProfile.RathenaRenewalDefault => RenewalByMap.TryGetValue(map, out var renewal) ? renewal : [],")
            .AppendLine("        MobSpawnLoadProfile.AthenaIroEffective => EffectiveByMap.TryGetValue(map, out var effective) ? effective : [],")
            .AppendLine("        _ => throw new ArgumentOutOfRangeException(nameof(profile)),")
            .AppendLine("    };")
            .AppendLine("}");
        return output.ToString();
    }

    private static async Task<int> CompileQuestDropAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var root = Path.GetFullPath(options.Required("rathena-root"));
        var questId = uint.Parse(options.Required("quest-id"), CultureInfo.InvariantCulture);
        var commit = options.Required("rathena-commit");

        var questDbYaml = await File.ReadAllTextAsync(Path.Combine(root, "db/re/quest_db.yml"));
        var mobDbYaml = await File.ReadAllTextAsync(Path.Combine(root, "db/re/mob_db.yml"));
        var itemDbYaml = await File.ReadAllTextAsync(Path.Combine(root, "db/re/item_db_etc.yml"));
        var drop = QuestDropDataCompiler.ReadSingleDrop(questDbYaml, questId, mobDbYaml, itemDbYaml);

        var output = Path.GetFullPath(options.Required("output"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output,
            QuestDropDataCompiler.Generate(drop, commit, CanonicalSourceFile(Path.Combine(root, "db/re/quest_db.yml")), 0),
            new System.Text.UTF8Encoding(false));

        Console.WriteLine($"Generated quest {drop.QuestId} drop rule: mob {drop.MobId} -> item {drop.ItemId} x{drop.Count} @ rate {drop.Rate}/10000.");
        return 0;
    }

    private static async Task<int> CompileItemAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var root = Path.GetFullPath(options.Required("rathena-root"));
        var itemId = int.Parse(options.Required("item-id"), CultureInfo.InvariantCulture);
        var itemDbFile = options.Optional("item-db-file") ?? "db/re/item_db_etc.yml";
        var commit = options.Required("rathena-commit");

        var itemDbYaml = await File.ReadAllTextAsync(Path.Combine(root, itemDbFile));
        var item = ItemDataCompiler.ReadItemDefinition(itemDbYaml, itemId);

        var output = Path.GetFullPath(options.Required("output"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output,
            ItemDataCompiler.Generate(item, commit, options.Required("class-name"), options.Required("constant-name"), CanonicalSourceFile(Path.Combine(root, itemDbFile)), 0),
            new System.Text.UTF8Encoding(false));

        Console.WriteLine($"Generated item definition '{item.AegisName}' ({item.Id}), stackable={item.Stackable}.");
        return 0;
    }

    // Offline .gat -> Athena collision artifact compiler (see MapCollisionCompiler/
    // MapCollisionArtifactWriter's own doc comments for the pinned trace and format). The input
    // .gat and the output artifact are BOTH expected to stay local/gitignored for now - see
    // ai/world-data.md's "Map collision data" section for the licensing rationale; this command
    // never reads/writes anywhere inside the committed repository tree by default.
    private static async Task<int> CompileMapCollisionAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var inputPath = options.Required("input");
        var mapName = options.Required("map");
        var outputPath = Path.GetFullPath(options.Required("output"));

        var gatBytes = await File.ReadAllBytesAsync(inputPath);
        var compiled = MapCollisionCompiler.Compile(gatBytes, mapName);
        var artifact = MapCollisionArtifactWriter.Write(compiled);

        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        await File.WriteAllBytesAsync(outputPath, artifact);

        Console.WriteLine($"Compiled map '{mapName}' ({compiled.Width}x{compiled.Height}, {compiled.Cells.Length} cells) into {outputPath}.");
        return 0;
    }

    private static T AssertSingle<T>(IEnumerable<T> values, string description)
    {
        var array = values.ToArray(); return array.Length == 1 ? array[0] : throw new ArgumentException($"Expected one {description}, found {array.Length}.");
    }

    private static string ClassName(string entityId, string trigger) => string.Concat(entityId.Split([':', '_', '-', '#', ' '], StringSplitOptions.RemoveEmptyEntries).Select(part => char.ToUpperInvariant(part[0]) + part[1..])) + trigger + "Script";
    private static string CanonicalSourceFile(string path)
    {
        var normalized = path.Replace('\\', '/'); var legacy = normalized.IndexOf("legacy/rathena/", StringComparison.Ordinal);
        return legacy >= 0 ? normalized[legacy..] : normalized;
    }

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
        Console.Error.WriteLine("WorldDataImporter analyze --rathena-root <folder> --output <folder> [--scope runtime|all] [--domain <maps|mobs|mob-spawns|mvp|items|quests|shops|mapflags|functions|map-world>] [--type <npc|warp|mob|boss|shop|function|mapflag>] [--map <map>] [--source <path-filter>] [--source-context-lines 5]");
        Console.Error.WriteLine("WorldDataImporter convert --source-root <folder> --output <entities-folder> [--source-file <path>] [--map <map>] [--name <name>] [--kind warp]");
        Console.Error.WriteLine("WorldDataImporter convert --source-root <folder> --all-compatible true --output <entities-folder> --report <report.json>");
        Console.Error.WriteLine("WorldDataImporter capabilities --source-root <folder> [--source-root <folder>] --output <report.json>");
        Console.Error.WriteLine("WorldDataImporter compile --source-root <folder> --rathena-commit <sha> --output <World.g.cs> [--source-file <path>] [--map <map>] [--name <name>] [--kind warp]");
        Console.Error.WriteLine("WorldDataImporter compile-script --source-root <folder> --rathena-commit <sha> --output <Npc.cs> --source-file <path> --map <map> --name <name> --kind <npc|warp> [--trigger OnClick|OnTouch]");
        Console.Error.WriteLine("WorldDataImporter compile-actors --source-root <folder> --rathena-commit <sha> --output <Actors.cs> --source-file <path> --map <map> --name <name> [--name <name>]");
        Console.Error.WriteLine("WorldDataImporter compile-navigation --source-root <folder> --output <Navigation.cs> --name <name> [--name <name>] [--namespace <ns>]");
        Console.Error.WriteLine("WorldDataImporter compile-character-data --rathena-root <folder> --rathena-commit <sha> --output <MapServer/Generated directory>");
        Console.Error.WriteLine("WorldDataImporter generate-mobs --rathena-root <folder> [--rathena-commit <sha>] --output <MapServer/Generated/GameData/Mobs directory>");
        Console.Error.WriteLine("WorldDataImporter compile-progression --rathena-root <folder> --rathena-commit <sha> --output <MapServer/Generated/Progression directory> (compatibility alias)");
        Console.Error.WriteLine("WorldDataImporter compile-mob-spawn --rathena-root <folder> --rathena-commit <sha> --mob-id <id> --name <spawn-name> --spawn-file <path> [--exclude-map <map>] --class-name <n> --constant-name <n> --spawn-class-name <n> --spawn-array-name <n> --output-definition <Mob.cs> --output-spawns <MobSpawns.cs>");
        Console.Error.WriteLine("WorldDataImporter generate-mob-spawns --rathena-root <folder> [--rathena-commit <sha>] --output <MapServer/Generated/World directory>");
        Console.Error.WriteLine("WorldDataImporter generate-maps --rathena-root <folder> [--rathena-commit <sha>] --output <MapServer/Generated/World directory>");
        Console.Error.WriteLine("WorldDataImporter generate-warps --rathena-root <folder> [--rathena-commit <sha>] --output <MapServer/Generated/World directory>");
        Console.Error.WriteLine("WorldDataImporter compile-quest-drop --rathena-root <folder> --rathena-commit <sha> --quest-id <id> --output <QuestDrops.cs>");
        Console.Error.WriteLine("WorldDataImporter compile-item --rathena-root <folder> --rathena-commit <sha> --item-id <id> [--item-db-file <path>] --class-name <n> --constant-name <n> --output <Item.cs>");
        Console.Error.WriteLine("WorldDataImporter compile-map-collision --input <local.gat> --map <name> --output <local.athmap>");
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
