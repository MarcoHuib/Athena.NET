using System.Globalization;
using System.Text.RegularExpressions;
using Athena.Rathena.Data;
using Athena.WorldCompiler;
using Athena.WorldCompiler.Generation;

internal enum DomainCompatibilityStatus { FullyCompatible, PartiallyCompatible, Unsupported, NotYetAnalyzed, NotApplicable }
internal sealed record DomainComponent(string Name, DomainCompatibilityStatus Status, IReadOnlyList<string>? Blockers = null);
internal sealed record DomainEntity(string Domain, string Id, string Name, string SourceFile, int SourceLine,
    DomainCompatibilityStatus Status, IReadOnlyList<DomainComponent> Components,
    IReadOnlyList<string> Dependencies, IReadOnlyList<string> Blockers, string? Map = null);
internal sealed record DomainSummary(string Domain, int Total, int FullyCompatible, int PartiallyCompatible, int Unsupported, int NotYetAnalyzed, int NotApplicable);

internal static class RepositoryDomainAnalyzers
{
    public static IReadOnlyList<DomainEntity> Analyze(string root, IReadOnlySet<string>? selectedDomains)
    {
        var maps = AnalyzeMaps(root); var mapNames = maps.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var mobs = AnalyzeMobs(root); var mobIds = mobs.Select(item => ParseNumericId(item.Id)).Where(item => item.HasValue).Select(item => item!.Value).ToHashSet();
        var items = AnalyzeItems(root); var itemIds = items.Select(item => ParseNumericId(item.Id)).Where(item => item.HasValue).Select(item => item!.Value).ToHashSet();
        var entities = new List<DomainEntity>();
        Add("maps", maps); Add("mobs", mobs); Add("mvp", AnalyzeMvp(mobs)); Add("items", items);
        Add("mob-spawns", AnalyzeMobSpawns(root, mapNames, mobIds));
        Add("quests", AnalyzeQuests(root));
        Add("shops", AnalyzeShops(root, itemIds));
        Add("mapflags", AnalyzeMapFlags(root, mapNames));
        Add("functions", AnalyzeFunctions(root));
        return entities.OrderBy(item => item.Domain, StringComparer.Ordinal).ThenBy(item => item.SourceFile, StringComparer.Ordinal)
            .ThenBy(item => item.SourceLine).ThenBy(item => item.Id, StringComparer.Ordinal).ToArray();

        void Add(string domain, IEnumerable<DomainEntity> values)
        {
            if (selectedDomains is null || selectedDomains.Count == 0 || selectedDomains.Contains(domain)) entities.AddRange(values);
        }
    }

    public static IReadOnlyList<DomainSummary> Summaries(IEnumerable<DomainEntity> entities) => entities
        .GroupBy(item => item.Domain, StringComparer.Ordinal).OrderBy(item => item.Key, StringComparer.Ordinal)
        .Select(group => new DomainSummary(group.Key, group.Count(),
            group.Count(item => item.Status == DomainCompatibilityStatus.FullyCompatible),
            group.Count(item => item.Status == DomainCompatibilityStatus.PartiallyCompatible),
            group.Count(item => item.Status == DomainCompatibilityStatus.Unsupported),
            group.Count(item => item.Status == DomainCompatibilityStatus.NotYetAnalyzed),
            group.Count(item => item.Status == DomainCompatibilityStatus.NotApplicable))).ToArray();

    private static IReadOnlyList<DomainEntity> AnalyzeMaps(string root)
    {
        var path = FirstExisting(Path.Combine(root, "db/import/map_cache.dat"), Path.Combine(root, "db/re/map_cache.dat"), Path.Combine(root, "db/map_cache.dat"));
        if (path is null) return [];
        try
        {
            return RathenaMapCacheFormat.ReadAll(File.ReadAllBytes(path)).Select(entry =>
                Entity("maps", $"map:{entry.Name}", entry.Name, root, path, 0, DomainCompatibilityStatus.FullyCompatible,
                    [new("Geometry", DomainCompatibilityStatus.FullyCompatible)], [], [])).ToArray();
        }
        catch (Exception)
        {
            return [Entity("maps", "map:map-cache", "map_cache.dat", root, path, 0, DomainCompatibilityStatus.Unsupported,
                [new("Geometry", DomainCompatibilityStatus.Unsupported, ["map:collision-cache"])], [], ["map:collision-cache"] )];
        }
    }

    private static IReadOnlyList<DomainEntity> AnalyzeMobs(string root)
    {
        var path = Path.Combine(root, "db/re/mob_db.yml"); if (!File.Exists(path)) return [];
        var yaml = File.ReadAllText(path); var result = new List<DomainEntity>();
        foreach (var block in YamlBlocks(yaml))
        {
            var id = BlockId(block.Text); if (id is null) continue;
            var name = Scalar(block.Text, "AegisName") ?? id.Value.ToString(CultureInfo.InvariantCulture);
            var blockers = new HashSet<string>(StringComparer.Ordinal);
            try
            {
                var mob = MobDataCompiler.ReadMobDefinition(yaml, id.Value);
                _ = MobDataCompiler.GenerateMobDefinition(mob, "analysis", "CompatibilityProbe", "Mob", Relative(root, path), block.Line);
                foreach (var key in TopLevelKeys(block.Text).Except(MobSupportedKeys, StringComparer.Ordinal)) blockers.Add("mob-field:" + Kebab(key));
                foreach (var mode in NestedBooleanKeys(block.Text, "Modes").Except(MobSupportedModes, StringComparer.Ordinal)) blockers.Add("mob-mode:" + Kebab(mode));
                var status = blockers.Count == 0 ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.PartiallyCompatible;
                result.Add(Entity("mobs", $"mob:{id}", name, root, path, block.Line, status,
                    [new("StaticData", DomainCompatibilityStatus.FullyCompatible), new("RuntimeBehavior", status, blockers.ToArray())], [], blockers));
            }
            catch (Exception exception)
            {
                blockers.Add("mob-definition:" + ExceptionCapability(exception));
                result.Add(Entity("mobs", $"mob:{id}", name, root, path, block.Line, DomainCompatibilityStatus.Unsupported,
                    [new("StaticData", DomainCompatibilityStatus.Unsupported, blockers.ToArray())], [], blockers));
            }
        }
        return result;
    }

    private static IReadOnlyList<DomainEntity> AnalyzeMvp(IReadOnlyList<DomainEntity> mobs) => mobs
        .Where(item => item.Blockers.Any(blocker => blocker.StartsWith("mob-field:mvp", StringComparison.Ordinal) || blocker == "mob-mode:mvp"))
        .Select(item => item with { Domain = "mvp", Id = item.Id.Replace("mob:", "mvp:", StringComparison.Ordinal), Status = DomainCompatibilityStatus.PartiallyCompatible,
            Components = item.Components.Concat([new DomainComponent("MvpBehavior", DomainCompatibilityStatus.Unsupported, ["mvp:runtime-behavior"])]).ToArray(),
            Blockers = item.Blockers.Append("mvp:runtime-behavior").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray() }).ToArray();

    private static IReadOnlyList<DomainEntity> AnalyzeItems(string root)
    {
        var files = Directory.Exists(Path.Combine(root, "db/re")) ? Directory.EnumerateFiles(Path.Combine(root, "db/re"), "item_db*.yml").Order(StringComparer.Ordinal).ToArray() : [];
        var result = new List<DomainEntity>();
        foreach (var path in files)
        {
            var yaml = File.ReadAllText(path);
            foreach (var block in YamlBlocks(yaml))
            {
                var id = BlockId(block.Text); if (id is null) continue;
                var name = Scalar(block.Text, "AegisName") ?? id.Value.ToString(CultureInfo.InvariantCulture); var blockers = new HashSet<string>(StringComparer.Ordinal);
                try
                {
                    var item = ItemDataCompiler.ReadItemDefinition(yaml, id.Value);
                    _ = ItemDataCompiler.Generate(item, "analysis", "CompatibilityProbe", "Item", Relative(root, path), block.Line);
                    foreach (var key in TopLevelKeys(block.Text).Except(ItemSupportedKeys, StringComparer.Ordinal))
                        if (key is not "Script" and not "EquipScript" and not "UnEquipScript") blockers.Add("item-field:" + Kebab(key));
                    if (HasBlock(block.Text, "Script") && item.Grants is null) blockers.Add("item-script:use");
                    if (HasBlock(block.Text, "EquipScript")) blockers.Add("item-script:equip");
                    if (HasBlock(block.Text, "UnEquipScript")) blockers.Add("item-script:unequip");
                    var dependencies = item.Grants?.Select(grant => $"item:{grant.ItemId}").ToArray() ?? [];
                    var status = blockers.Count == 0 ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.PartiallyCompatible;
                    result.Add(Entity("items", $"item:{id}", name, root, path, block.Line, status,
                        [new("StaticData", DomainCompatibilityStatus.FullyCompatible), new("RuntimeBehavior", status, blockers.Where(x => x.StartsWith("item-script:", StringComparison.Ordinal)).ToArray())], dependencies, blockers));
                }
                catch (Exception exception)
                {
                    blockers.Add("item-definition:" + ExceptionCapability(exception));
                    result.Add(Entity("items", $"item:{id}", name, root, path, block.Line, DomainCompatibilityStatus.Unsupported,
                        [new("StaticData", DomainCompatibilityStatus.Unsupported, blockers.ToArray())], [], blockers));
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<DomainEntity> AnalyzeQuests(string root)
    {
        var path = Path.Combine(root, "db/re/quest_db.yml"); var mobPath = Path.Combine(root, "db/re/mob_db.yml");
        var itemPath = Path.Combine(root, "db/re/item_db_etc.yml"); if (!File.Exists(path) || !File.Exists(mobPath) || !File.Exists(itemPath)) return [];
        var yaml = File.ReadAllText(path); var mobs = File.ReadAllText(mobPath); var items = File.ReadAllText(itemPath); var result = new List<DomainEntity>();
        foreach (var block in YamlBlocks(yaml))
        {
            var id = BlockId(block.Text); if (id is null) continue; var blockers = new List<string>(); var dependencies = new List<string>();
            try
            {
                var quest = QuestDropDataCompiler.ReadSingleDrop(yaml, (uint)id.Value, mobs, items);
                _ = QuestDropDataCompiler.Generate(quest, "analysis", Relative(root, path), block.Line);
                dependencies.Add($"mob:{quest.MobId}"); dependencies.Add($"item:{quest.ItemId}"); blockers.Add("quest:playability-not-evaluated");
                result.Add(Entity("quests", $"quest:{id}", quest.Title, root, path, block.Line, DomainCompatibilityStatus.PartiallyCompatible,
                    [new("Definition", DomainCompatibilityStatus.FullyCompatible), new("RuntimeReadiness", DomainCompatibilityStatus.NotYetAnalyzed, blockers)], dependencies, blockers));
            }
            catch (Exception)
            {
                blockers.Add(HasBlock(block.Text, "Targets") ? "quest:targets" : HasBlock(block.Text, "Drops") ? "quest:drops" : "quest:definition");
                result.Add(Entity("quests", $"quest:{id}", Scalar(block.Text, "Title") ?? id.Value.ToString(CultureInfo.InvariantCulture), root, path, block.Line, DomainCompatibilityStatus.Unsupported,
                    [new("Definition", DomainCompatibilityStatus.Unsupported, blockers)], dependencies, blockers));
            }
        }
        return result;
    }

    private static IReadOnlyList<DomainEntity> AnalyzeMobSpawns(string root, IReadOnlySet<string> maps, IReadOnlySet<int> mobs)
    {
        var npc = Path.Combine(root, "npc"); if (!Directory.Exists(npc)) return [];
        var result = new List<DomainEntity>();
        foreach (var path in Directory.EnumerateFiles(npc, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var text = File.ReadAllText(path); var names = Regex.Matches(text, @"(?m)^[^/\r\n]+\t(?:monster|boss_monster)\t(?<name>[^\t]+)\t")
                .Select(match => match.Groups["name"].Value).Distinct(StringComparer.Ordinal).ToArray();
            foreach (var name in names)
            {
                IReadOnlyList<MobDataCompiler.MobSpawnData> spawns; try { spawns = MobDataCompiler.ReadMobSpawns(text, Relative(root, path), name); } catch { continue; }
                foreach (var spawn in spawns)
                {
                    var blockers = new List<string>(); if (!maps.Contains(spawn.Map)) blockers.Add("dependency:map"); if (!mobs.Contains(spawn.MobId)) blockers.Add("dependency:mob");
                    var status = blockers.Count == 0 ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.Unsupported;
                    result.Add(Entity("mob-spawns", $"mob-spawn:{Relative(root, path)}:{spawn.SourceLine}", name, root, path, spawn.SourceLine, status,
                        [new("Spawn", status, blockers)], [$"map:{spawn.Map}", $"mob:{spawn.MobId}"], blockers, spawn.Map));
                }
            }
        }
        return result;
    }

    private static IReadOnlyList<DomainEntity> AnalyzeShops(string root, IReadOnlySet<int> itemIds)
    {
        return PositionedDeclarations(root).Where(item => item.Directive.Contains("shop", StringComparison.OrdinalIgnoreCase)).Select(item =>
        {
            var dependencies = Regex.Matches(item.Arguments, @"(?:^|,)(\d+):").Select(match => "item:" + match.Groups[1].Value).Distinct().Order(StringComparer.Ordinal).ToArray();
            var blockers = new[] { "shop:runtime" }.Concat(dependencies.Where(dep => int.TryParse(dep[5..], out var id) && !itemIds.Contains(id)).Select(_ => "dependency:item")).Distinct().ToArray();
            return Entity("shops", $"shop:{item.Source.File}:{item.Source.Line}", item.Name, root, Resolve(root, item.Source.File), item.Source.Line, DomainCompatibilityStatus.NotYetAnalyzed,
                [new("Runtime", DomainCompatibilityStatus.NotYetAnalyzed, blockers)], dependencies, blockers, item.Map);
        }).ToArray();
    }

    private static IReadOnlyList<DomainEntity> AnalyzeMapFlags(string root, IReadOnlySet<string> maps)
    {
        var npc = Path.Combine(root, "npc"); if (!Directory.Exists(npc)) return [];
        var result = new List<DomainEntity>();
        foreach (var path in Directory.EnumerateFiles(npc, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        foreach (var (line, index) in File.ReadLines(path).Select((line, index) => (line, index)))
        {
            var columns = line.Trim().Split('\t'); if (columns.Length < 3 || columns[1] != "mapflag") continue;
            var map = columns[0]; var flag = columns[2].Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]; var blockers = new List<string> { "mapflag:" + Kebab(flag) };
            if (!maps.Contains(map)) blockers.Add("dependency:map");
            result.Add(Entity("mapflags", $"mapflag:{Relative(root, path)}:{index + 1}", flag, root, path, index + 1, DomainCompatibilityStatus.Unsupported,
                [new("Runtime", DomainCompatibilityStatus.Unsupported, blockers)], [$"map:{map}"], blockers, map));
        }
        return result;
    }

    private static IReadOnlyList<DomainEntity> AnalyzeFunctions(string root)
    {
        var npc = Path.Combine(root, "npc"); if (!Directory.Exists(npc)) return [];
        var result = new List<DomainEntity>();
        foreach (var path in Directory.EnumerateFiles(npc, "*.txt", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
        {
            var lines = File.ReadAllLines(path);
            for (var index = 0; index < lines.Length; index++)
            {
                var match = Regex.Match(lines[index], @"^function\s+(?:script\s+)?(?<name>\S+)\s*\{"); if (!match.Success) continue;
                var declarationLine = index + 1;
                var body = new List<string>(); var depth = lines[index].Count(character => character == '{') - lines[index].Count(character => character == '}');
                while (depth > 0 && ++index < lines.Length) { body.Add(lines[index]); depth += lines[index].Count(c => c == '{') - lines[index].Count(c => c == '}'); }
                var source = new WorldSourceInfo("rAthena", "pinned", Relative(root, path), declarationLine);
                var (syntax, semantics) = RathenaEventCompiler.Parse(string.Join('\n', body), source);
                var compilation = RathenaEventCompiler.Compile(syntax, semantics, "OnClick");
                var bodyBlockers = compilation.Diagnostics.Where(item => item.Severity == "Error").Select(item => CompatibilityDiagnosticNormalizer.Normalize(item, syntax).CapabilityId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var blockers = bodyBlockers.Append("function:runtime").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                result.Add(Entity("functions", $"function:{match.Groups["name"].Value}", match.Groups["name"].Value, root, path, declarationLine, DomainCompatibilityStatus.Unsupported,
                    [new("Body", bodyBlockers.Length == 0 ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.Unsupported, bodyBlockers), new("Runtime", DomainCompatibilityStatus.Unsupported, ["function:runtime"])], [], blockers));
            }
        }
        return result;
    }

    private static IEnumerable<RathenaDeclaration> PositionedDeclarations(string root)
    {
        var npc = Path.Combine(root, "npc"); return Directory.Exists(npc) ? RathenaSourceParser.Parse([npc]) : [];
    }

    private static DomainEntity Entity(string domain, string id, string name, string root, string path, int line, DomainCompatibilityStatus status,
        IReadOnlyList<DomainComponent> components, IEnumerable<string> dependencies, IEnumerable<string> blockers, string? map = null) =>
        new(domain, id, name, Relative(root, path), line, status, components,
            dependencies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), blockers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), map);

    private static readonly string[] MobSupportedKeys = ["Id", "AegisName", "Name", "Level", "Hp", "Attack", "Attack2", "Defense", "MagicDefense", "Str", "Agi", "Vit", "Int", "Dex", "Luk", "AttackRange", "WalkSpeed", "AttackDelay", "AttackMotion", "DamageMotion", "BaseExp", "JobExp", "Ai", "Modes"];
    private static readonly string[] MobSupportedModes = ["CanMove", "NoRandomWalk", "CanAttack", "ChangeTargetMelee", "ChangeTargetChase"];
    private static readonly string[] ItemSupportedKeys = ["Id", "AegisName", "Name", "Type", "AliasName", "Attack", "WeaponLevel", "SubType", "Range", "Locations", "Script", "EquipScript", "UnEquipScript"];

    private sealed record YamlBlock(string Text, int Line);
    private static IEnumerable<YamlBlock> YamlBlocks(string yaml)
    {
        var matches = Regex.Matches(yaml, @"(?m)^  - Id: \d+\s*$");
        for (var i = 0; i < matches.Count; i++)
        {
            var start = matches[i].Index; var end = i + 1 < matches.Count ? matches[i + 1].Index : yaml.Length;
            yield return new(yaml[start..end], 1 + yaml[..start].Count(character => character == '\n'));
        }
    }
    private static int? BlockId(string block) => int.TryParse(Scalar(block, "Id"), NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : null;
    private static string? Scalar(string block, string key) { var match = Regex.Match(block, $@"(?m)^    {Regex.Escape(key)}:\s*(.+)$|^  - {Regex.Escape(key)}:\s*(.+)$"); return match.Success ? match.Groups.Cast<Group>().Skip(1).First(group => group.Success).Value.Trim() : null; }
    private static IEnumerable<string> TopLevelKeys(string block) => Regex.Matches(block, @"(?m)^    (?<key>[A-Za-z][A-Za-z0-9_]*):").Select(match => match.Groups["key"].Value);
    private static IEnumerable<string> NestedBooleanKeys(string block, string section) { var match = Regex.Match(block, $@"(?ms)^    {section}:\s*\n(?<body>(?:      .*\n?)*)"); return match.Success ? Regex.Matches(match.Groups["body"].Value, @"(?m)^      (?<key>\S+):").Select(item => item.Groups["key"].Value) : []; }
    private static bool HasBlock(string block, string key) => Regex.IsMatch(block, $@"(?m)^    {Regex.Escape(key)}:");
    private static int? ParseNumericId(string id) => int.TryParse(id[(id.IndexOf(':') + 1)..], out var value) ? value : null;
    private static string ExceptionCapability(Exception exception) => Kebab(exception.GetType().Name.Replace("Exception", "", StringComparison.Ordinal));
    private static string Kebab(string value) => Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").Replace('_', '-').ToLowerInvariant();
    private static string? FirstExisting(params string[] paths) => paths.FirstOrDefault(File.Exists);
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Resolve(string root, string source) => File.Exists(source) ? source : Path.Combine(root, source.Replace("legacy/rathena/", "", StringComparison.Ordinal));
}
