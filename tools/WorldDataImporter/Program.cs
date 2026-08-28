using System.Globalization;
using System.Text.Json;
using Athena.WorldCompiler.Generation;
using Athena.WorldCompiler.Lowering;
using Athena.WorldCompiler.Rathena;
using Athena.WorldCompiler.Semantics;

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
                "compile" => await CompileAsync(args[1..]),
                "compile-script" => await CompileScriptAsync(args[1..]),
                "compile-npc-world" => await CompileNpcWorldAsync(args[1..]),
                "compile-actors" => await CompileActorsAsync(args[1..]),
                "compile-navigation" => await CompileNavigationAsync(args[1..]),
                "compile-progression" => await CompileProgressionAsync(args[1..]),
                "compile-mob-spawn" => await CompileMobSpawnAsync(args[1..]),
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
        var source=CSharpWorldEmitter.Emit(lowered,options.Required("rathena-commit"));
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

        var emission = NpcWorldEmitter.Emit(conversion, selection, worldNamespace, scriptsNamespace, options.Required("rathena-commit"), warpConversion, warpSelection);

        Directory.CreateDirectory(outputDir);
        var scriptsDir = Path.Combine(outputDir, "Scripts");
        Directory.CreateDirectory(scriptsDir);
        var encoding = new System.Text.UTF8Encoding(false);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "AcademyWorld.cs"), emission.AcademyWorldSource, encoding);
        await File.WriteAllTextAsync(Path.Combine(outputDir, "AcademyNpcs.cs"), emission.AcademyNpcsSource, encoding);
        if (emission.AcademyWarpTriggersSource is { } warpTriggersSource)
            await File.WriteAllTextAsync(Path.Combine(outputDir, "AcademyWarpTriggers.cs"), warpTriggersSource, encoding);
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
        var root = Path.GetFullPath(options.Required("rathena-root"));
        var generated = ProgressionDataCompiler.Generate(
            await File.ReadAllTextAsync(Path.Combine(root, "db/re/job_exp.yml")),
            await File.ReadAllTextAsync(Path.Combine(root, "db/re/job_basepoints.yml")),
            await File.ReadAllTextAsync(Path.Combine(root, "db/re/job_stats.yml")),
            await File.ReadAllTextAsync(Path.Combine(root, "db/re/statpoint.yml")),
            options.Required("rathena-commit"));
        var output = Path.GetFullPath(options.Required("output"));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        await File.WriteAllTextAsync(output, generated, new System.Text.UTF8Encoding(false));
        Console.WriteLine($"Generated pinned progression registry into {output}.");
        return 0;
    }

    private static async Task<int> CompileMobSpawnAsync(string[] args)
    {
        var options = CliOptions.Parse(args);
        var root = Path.GetFullPath(options.Required("rathena-root"));
        var spawnFile = options.Required("spawn-file");
        var mobName = options.Required("name");
        var mobId = int.Parse(options.Required("mob-id"), CultureInfo.InvariantCulture);
        var commit = options.Required("rathena-commit");

        var mobDbYaml = await File.ReadAllTextAsync(Path.Combine(root, "db/re/mob_db.yml"));
        var mob = MobDataCompiler.ReadMobDefinition(mobDbYaml, mobId);

        var spawnPath = Path.Combine(root, spawnFile);
        var spawnText = await File.ReadAllTextAsync(spawnPath);
        var excludedMaps = options.All("exclude-map").ToHashSet(StringComparer.Ordinal);
        var spawns = MobDataCompiler.ReadMobSpawns(spawnText, CanonicalSourceFile(spawnPath), mobName, excludedMaps);
        var mismatched = spawns.Where(spawn => spawn.MobId != mobId).ToArray();
        if (mismatched.Length > 0) throw new ArgumentException($"Spawn declaration for '{mobName}' at line {mismatched[0].SourceLine} uses mob id {mismatched[0].MobId}, expected {mobId}.");

        var definitionOutput = Path.GetFullPath(options.Required("output-definition"));
        Directory.CreateDirectory(Path.GetDirectoryName(definitionOutput)!);
        await File.WriteAllTextAsync(definitionOutput,
            MobDataCompiler.GenerateMobDefinition(mob, commit, options.Required("class-name"), options.Required("constant-name"), CanonicalSourceFile(Path.Combine(root, "db/re/mob_db.yml")), 0),
            new System.Text.UTF8Encoding(false));

        var spawnOutput = Path.GetFullPath(options.Required("output-spawns"));
        Directory.CreateDirectory(Path.GetDirectoryName(spawnOutput)!);
        var mobExpression = $"{options.Required("class-name")}.{options.Required("constant-name")}";
        await File.WriteAllTextAsync(spawnOutput,
            MobDataCompiler.GenerateMobSpawns(spawns, mobExpression, commit, options.Required("spawn-class-name"), options.Required("spawn-array-name")),
            new System.Text.UTF8Encoding(false));

        Console.WriteLine($"Generated mob definition '{mob.AegisName}' ({mob.Id}) and {spawns.Count} spawn declarations.");
        return 0;
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
        Console.Error.WriteLine("WorldDataImporter convert --source-root <folder> --output <entities-folder> [--source-file <path>] [--map <map>] [--name <name>] [--kind warp]");
        Console.Error.WriteLine("WorldDataImporter convert --source-root <folder> --all-compatible true --output <entities-folder> --report <report.json>");
        Console.Error.WriteLine("WorldDataImporter capabilities --source-root <folder> [--source-root <folder>] --output <report.json>");
        Console.Error.WriteLine("WorldDataImporter compile --source-root <folder> --rathena-commit <sha> --output <World.g.cs> [--source-file <path>] [--map <map>] [--name <name>] [--kind warp]");
        Console.Error.WriteLine("WorldDataImporter compile-script --source-root <folder> --rathena-commit <sha> --output <Npc.cs> --source-file <path> --map <map> --name <name> --kind <npc|warp> [--trigger OnClick|OnTouch]");
        Console.Error.WriteLine("WorldDataImporter compile-actors --source-root <folder> --rathena-commit <sha> --output <Actors.cs> --source-file <path> --map <map> --name <name> [--name <name>]");
        Console.Error.WriteLine("WorldDataImporter compile-navigation --source-root <folder> --output <Navigation.cs> --name <name> [--name <name>] [--namespace <ns>]");
        Console.Error.WriteLine("WorldDataImporter compile-progression --rathena-root <folder> --output <Progression.cs>");
        Console.Error.WriteLine("WorldDataImporter compile-mob-spawn --rathena-root <folder> --rathena-commit <sha> --mob-id <id> --name <spawn-name> --spawn-file <path> [--exclude-map <map>] --class-name <n> --constant-name <n> --spawn-class-name <n> --spawn-array-name <n> --output-definition <Mob.cs> --output-spawns <MobSpawns.cs>");
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
