using System.Globalization;
using System.Text.RegularExpressions;
using Athena.Rathena.Data;
using Athena.WorldCompiler;
using Athena.WorldCompiler.Generation;

internal enum DomainCompatibilityStatus { FullyCompatible, PartiallyCompatible, Unsupported, NotYetAnalyzed, NotApplicable }
// Metric is a minimal, optional structural counter (e.g. "12 of 13 mob spawns on this map are
// FullyCompatible") for components that report COMPLETENESS rather than blockers - currently only
// map-world's MobSpawns/MapFlags components (Priority 4, ai/world-data.md). Blockers must contain
// only genuine semantic blocker/capability IDs; a metric is never a blocker and must never be
// smuggled into that list as a formatted string like "12/13".
internal sealed record DomainMetric(int Compatible, int Total);
internal sealed record DomainComponent(string Name, DomainCompatibilityStatus Status, IReadOnlyList<string>? Blockers = null, DomainMetric? Metric = null);
internal sealed record DomainEntity(string Domain, string Id, string Name, string SourceFile, int SourceLine,
    DomainCompatibilityStatus Status, IReadOnlyList<DomainComponent> Components,
    IReadOnlyList<string> Dependencies, IReadOnlyList<string> Blockers, string? Map = null, string? Provenance = null);
internal sealed record DomainSummary(string Domain, int Total, int FullyCompatible, int PartiallyCompatible, int Unsupported, int NotYetAnalyzed, int NotApplicable);

internal static class RepositoryDomainAnalyzers
{
    public static IReadOnlyList<DomainEntity> Analyze(string root, IReadOnlySet<string>? selectedDomains)
    {
        var maps = AnalyzeMaps(root); var mapNames = maps.Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
        var mobs = AnalyzeMobSkills(root, AnalyzeMobs(root));
        var mobIds = mobs.Select(item => ParseNumericId(item.Id)).Where(item => item.HasValue).Select(item => item!.Value).ToHashSet();
        var items = AnalyzeItems(root); var itemIds = items.Select(item => ParseNumericId(item.Id)).Where(item => item.HasValue).Select(item => item!.Value).ToHashSet();
        var entities = new List<DomainEntity>();
        Add("maps", maps); Add("mobs", mobs); Add("mvp", AnalyzeMvp(root, mobs)); Add("items", items);
        var mobSpawns = AnalyzeMobSpawns(root, mapNames, mobIds); Add("mob-spawns", mobSpawns);
        Add("quests", AnalyzeQuests(root));
        Add("shops", AnalyzeShops(root, itemIds));
        var mapFlags = AnalyzeMapFlags(root, mapNames); Add("mapflags", mapFlags);
        Add("functions", AnalyzeFunctions(root));
        Add("map-world", AnalyzeMapWorld(root, selectedDomains, maps, mobSpawns, mapFlags));
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
        var basePath = Path.Combine(root, "db/map_cache.dat");
        if (!File.Exists(basePath)) return [];
        var renewalPath = Path.Combine(root, "db/re/map_cache.dat");
        var importPath = Path.Combine(root, "db/import/map_cache.dat");
        try
        {
            var baseBytes = File.ReadAllBytes(basePath);
            var renewalBytes = File.Exists(renewalPath) ? File.ReadAllBytes(renewalPath) : null;
            var importBytes = File.Exists(importPath) ? File.ReadAllBytes(importPath) : null;
            var merged = RathenaMapCacheLayers.Merge(baseBytes, renewalBytes, importBytes);
            return merged.Select(resolved =>
                Entity("maps", $"map:{resolved.Entry.Name}", resolved.Entry.Name, root, basePath, 0, DomainCompatibilityStatus.FullyCompatible,
                    [new("Geometry", DomainCompatibilityStatus.FullyCompatible)], [], [], provenance: resolved.Source.ToString())).ToArray();
        }
        catch (MapCacheLayerException exception)
        {
            return [Entity("maps", "map:map-cache", "map_cache.dat", root, basePath, 0, DomainCompatibilityStatus.Unsupported,
                [new("Geometry", DomainCompatibilityStatus.Unsupported, ["map:collision-cache"])], [], ["map:collision-cache:" + Kebab(exception.Layer)] )];
        }
    }

    // Priority 5 (ai/world-data.md): StaticData and Modes are independent components, exactly like
    // items' StaticData/RuntimeBehavior split - each derives its status ONLY from its own blockers.
    // Fields the generated MobDefinition actually carries (MobDataCompiler.MobDefinitionData) are
    // MobSupportedKeys; anything else present in the pinned block (Size, Race, Element,
    // ElementLevel, Class, SkillRange, ChaseRange, ClientAttackMotion, DamageTaken, MvpExp,
    // MvpDrops, ...) is real source data this project silently drops today and can never be
    // reported FullyCompatible merely because MobDataCompiler.ReadMobDefinition/GenerateMobDefinition
    // didn't throw. Drops is its own DEDICATED component (Unsupported when the mob has a Drops:
    // table at all - there is no drop-table compiler/runtime anywhere in this project outside the
    // single-quest QuestDropDataCompiler slice, which is unrelated general monster drop data) and is
    // therefore excluded from the generic unknown-top-level-field StaticData scan below (Priority 2,
    // ai/world-data.md) - a source `Drops:` block must produce exactly one blocker
    // (`mob-drops:runtime`, on the Drops component), never also a redundant `mob-field:drops`
    // StaticData blocker for the identical construct. Skills starts as NotApplicable here and is
    // populated by AnalyzeMobSkills afterward (a second pass over the differently-formatted
    // mob_skill_db.txt, Priority 6).
    private static IReadOnlyList<DomainEntity> AnalyzeMobs(string root)
    {
        var path = Path.Combine(root, "db/re/mob_db.yml"); if (!File.Exists(path)) return [];
        var yaml = File.ReadAllText(path); var result = new List<DomainEntity>();
        foreach (var block in YamlBlocks(yaml))
        {
            var id = BlockId(block.Text); if (id is null) continue;
            var name = Scalar(block.Text, "AegisName") ?? id.Value.ToString(CultureInfo.InvariantCulture);
            try
            {
                var mob = MobDataCompiler.ReadMobDefinition(yaml, id.Value);
                _ = MobDataCompiler.GenerateMobDefinition(mob, "analysis", "CompatibilityProbe", "Mob", Relative(root, path), block.Line);

                // "Drops" is deliberately excluded here even though it is not in MobSupportedKeys:
                // it has its own dedicated Drops component below (Priority 2, ai/world-data.md), so
                // the generic unknown-top-level-field detector must not ALSO report it as a
                // "mob-field:drops" StaticData blocker - that would double-count the exact same
                // source construct under two unrelated components. Every other unmodeled top-level
                // field still becomes a StaticData gap as before.
                var staticBlockers = TopLevelKeys(block.Text).Except(MobSupportedKeys, StringComparer.Ordinal).Except(["Drops"], StringComparer.Ordinal).Select(key => "mob-field:" + Kebab(key)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var staticStatus = staticBlockers.Length == 0 ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.PartiallyCompatible;

                var modeBlockers = NestedBooleanKeys(block.Text, "Modes").Except(MobSupportedModes, StringComparer.Ordinal).Select(mode => "mob-mode:" + Kebab(mode)).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var modeStatus = modeBlockers.Length == 0 ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.PartiallyCompatible;

                var hasDrops = HasBlock(block.Text, "Drops");
                var dropsStatus = hasDrops ? DomainCompatibilityStatus.Unsupported : DomainCompatibilityStatus.NotApplicable;
                var dropsBlockers = hasDrops ? new[] { "mob-drops:runtime" } : [];

                var components = new DomainComponent[]
                {
                    new("StaticData", staticStatus, staticBlockers),
                    new("Modes", modeStatus, modeBlockers),
                    new("Drops", dropsStatus, dropsBlockers),
                    new("Skills", DomainCompatibilityStatus.NotApplicable, []),
                };
                var allBlockers = staticBlockers.Concat(modeBlockers).Concat(dropsBlockers).ToArray();
                var overall = RollupComponents(components);
                result.Add(Entity("mobs", $"mob:{id}", name, root, path, block.Line, overall, components, [], allBlockers));
            }
            catch (Exception exception)
            {
                var blockers = new[] { "mob-definition:" + ExceptionCapability(exception) };
                var components = new DomainComponent[]
                {
                    new("StaticData", DomainCompatibilityStatus.Unsupported, blockers),
                    new("Modes", DomainCompatibilityStatus.NotApplicable, []),
                    new("Drops", DomainCompatibilityStatus.NotApplicable, []),
                    new("Skills", DomainCompatibilityStatus.NotApplicable, []),
                };
                result.Add(Entity("mobs", $"mob:{id}", name, root, path, block.Line, DomainCompatibilityStatus.Unsupported, components, [], blockers));
            }
        }
        return result;
    }

    // Priority 6 (ai/world-data.md): legacy/rathena/db/re/mob_skill_db.txt is a plain
    // tab/comma-delimited text file (NOT YAML - see the file's own header comment), format:
    // MobID,Dummy,State,SkillID,SkillLv,Rate,CastTime,Delay,Cancelable,Target,ConditionType,
    // ConditionValue,val1..val5,Emotion,Chat. No compiler/runtime anywhere in this project reads
    // this file or executes mob skills at all, so every mob referenced here gets an Unsupported
    // Skills component - a full, honest gap measurement, not a partial score. A mob absent from
    // this file keeps its NotApplicable Skills component from AnalyzeMobs untouched.
    private static IReadOnlyList<DomainEntity> AnalyzeMobSkills(string root, IReadOnlyList<DomainEntity> mobs)
    {
        var path = Path.Combine(root, "db/re/mob_skill_db.txt"); if (!File.Exists(path)) return mobs;
        var skillIdsByMob = new Dictionary<int, HashSet<int>>();
        foreach (var line in File.ReadLines(path))
        {
            var trimmed = line.Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
            var columns = trimmed.Split(',');
            if (columns.Length < 4) continue;
            if (!int.TryParse(columns[0].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var mobId)) continue;
            if (!int.TryParse(columns[3].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out var skillId)) continue;
            (skillIdsByMob.TryGetValue(mobId, out var set) ? set : skillIdsByMob[mobId] = []).Add(skillId);
        }
        if (skillIdsByMob.Count == 0) return mobs;

        return mobs.Select(mob =>
        {
            var numericId = ParseNumericId(mob.Id);
            if (numericId is null || !skillIdsByMob.TryGetValue(numericId.Value, out var skillIds)) return mob;
            var skillBlockers = skillIds.OrderBy(item => item).Select(skillId => "mob-skill:" + skillId.ToString(CultureInfo.InvariantCulture))
                .Append("mob-skill:runtime").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            var skillsComponent = new DomainComponent("Skills", DomainCompatibilityStatus.Unsupported, skillBlockers);
            var components = mob.Components.Where(component => component.Name != "Skills").Append(skillsComponent).ToArray();
            var blockers = mob.Blockers.Concat(skillBlockers).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return mob with { Components = components, Blockers = blockers, Status = RollupComponents(components) };
        }).ToArray();
    }

    // Priority 10 (ai/world-data.md): pinned mob_db.yml marks MVP status via
    // `Class: Boss` + `Modes: Mvp: true` + a dedicated MvpExp/MvpDrops pair (verified against
    // Golden Thief Bug, db/re/mob_db.yml Id 1086) - NOT a "mob-field:mvp*"/"mob-mode:mvp" blocker
    // string. mob_db.yml never declares a top-level field literally named "Mvp" (it only ever
    // appears nested under Modes:, and MobSupportedModes never excluded it either), so the earlier
    // detection (grepping AnalyzeMobs' own blocker strings for that pattern) never actually fired
    // against a real file - dead code inspecting a field name mob_db.yml doesn't use. Detected here
    // directly from the pinned source instead, independent of whatever StaticData/Modes/Drops
    // happens to report for unrelated reasons, so a fully-modeled MVP mob is still correctly
    // classified as MVP.
    private static IReadOnlyList<DomainEntity> AnalyzeMvp(string root, IReadOnlyList<DomainEntity> mobs)
    {
        var path = Path.Combine(root, "db/re/mob_db.yml"); if (!File.Exists(path)) return [];
        var yaml = File.ReadAllText(path);
        var mvpIds = YamlBlocks(yaml).Where(block => IsMvpBlock(block.Text)).Select(block => BlockId(block.Text)).Where(id => id.HasValue).Select(id => id!.Value).ToHashSet();
        if (mvpIds.Count == 0) return [];

        return mobs.Where(mob => ParseNumericId(mob.Id) is { } numericId && mvpIds.Contains(numericId))
            .Select(mob =>
            {
                var components = mob.Components.Append(new DomainComponent("MvpBehavior", DomainCompatibilityStatus.Unsupported, ["mvp:runtime-behavior"])).ToArray();
                var blockers = mob.Blockers.Append("mvp:runtime-behavior").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                return mob with
                {
                    Domain = "mvp", Id = mob.Id.Replace("mob:", "mvp:", StringComparison.Ordinal),
                    Status = DomainCompatibilityStatus.Unsupported, Components = components, Blockers = blockers,
                };
            }).ToArray();
    }

    private static bool IsMvpBlock(string block) =>
        Regex.IsMatch(block, @"(?m)^    Class:\s*Boss\s*$") && Regex.IsMatch(block, @"(?ms)^    Modes:.*?^      Mvp:\s*true\s*$");

    // Shared component-status rollup (mobs/items alike): the overall entity status is
    // FullyCompatible only when every component is FullyCompatible or NotApplicable (a component
    // that doesn't apply can never drag the overall status down); Unsupported when every
    // NON-not-applicable component is Unsupported; otherwise PartiallyCompatible. This is what
    // Priorities 3/5 fix - the overall status is now genuinely derived from every component instead
    // of a hardcoded-compatible StaticData masking a real sibling-component gap.
    private static DomainCompatibilityStatus RollupComponents(IReadOnlyList<DomainComponent> components)
    {
        var relevant = components.Select(component => component.Status).Where(status => status != DomainCompatibilityStatus.NotApplicable).ToArray();
        if (relevant.Length == 0) return DomainCompatibilityStatus.NotApplicable;
        if (relevant.All(status => status == DomainCompatibilityStatus.FullyCompatible)) return DomainCompatibilityStatus.FullyCompatible;
        if (relevant.All(status => status == DomainCompatibilityStatus.Unsupported)) return DomainCompatibilityStatus.Unsupported;
        return DomainCompatibilityStatus.PartiallyCompatible;
    }

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
                var name = Scalar(block.Text, "AegisName") ?? id.Value.ToString(CultureInfo.InvariantCulture);
                try
                {
                    var item = ItemDataCompiler.ReadItemDefinition(yaml, id.Value);
                    _ = ItemDataCompiler.Generate(item, "analysis", "CompatibilityProbe", "Item", Relative(root, path), block.Line);

                    // StaticData and RuntimeBehavior are independent components: a missing/unmodeled
                    // database field (item-field:*) is a StaticData gap and must never taint
                    // RuntimeBehavior, and vice versa - see ai/world-data.md's item static-vs-runtime
                    // compatibility section. Each component's own status is derived only from its own
                    // blockers.
                    var staticBlockers = new HashSet<string>(StringComparer.Ordinal);
                    foreach (var key in TopLevelKeys(block.Text).Except(ItemSupportedKeys, StringComparer.Ordinal))
                        if (key is not "Script" and not "EquipScript" and not "UnEquipScript") staticBlockers.Add("item-field:" + Kebab(key));

                    var runtimeBlockers = new HashSet<string>(StringComparer.Ordinal);
                    var hasScript = HasBlock(block.Text, "Script") || HasBlock(block.Text, "EquipScript") || HasBlock(block.Text, "UnEquipScript");
                    if (HasBlock(block.Text, "Script") && item.Grants is null) runtimeBlockers.Add("item-script:use");
                    if (HasBlock(block.Text, "EquipScript")) runtimeBlockers.Add("item-script:equip");
                    if (HasBlock(block.Text, "UnEquipScript")) runtimeBlockers.Add("item-script:unequip");

                    var staticStatus = staticBlockers.Count == 0 ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.PartiallyCompatible;
                    var runtimeStatus = !hasScript ? DomainCompatibilityStatus.NotApplicable
                        : runtimeBlockers.Count == 0 ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.PartiallyCompatible;
                    var blockers = staticBlockers.Concat(runtimeBlockers).ToArray();
                    var status = staticStatus == DomainCompatibilityStatus.FullyCompatible && runtimeStatus is DomainCompatibilityStatus.FullyCompatible or DomainCompatibilityStatus.NotApplicable
                        ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.PartiallyCompatible;
                    var dependencies = item.Grants?.Select(grant => $"item:{grant.ItemId}").ToArray() ?? [];
                    result.Add(Entity("items", $"item:{id}", name, root, path, block.Line, status,
                        [new("StaticData", staticStatus, staticBlockers.ToArray()), new("RuntimeBehavior", runtimeStatus, runtimeBlockers.ToArray())], dependencies, blockers));
                }
                catch (Exception exception)
                {
                    var blockers = new[] { ItemCapability(exception) };
                    result.Add(Entity("items", $"item:{id}", name, root, path, block.Line, DomainCompatibilityStatus.Unsupported,
                        [new("StaticData", DomainCompatibilityStatus.Unsupported, blockers), new("RuntimeBehavior", DomainCompatibilityStatus.NotYetAnalyzed, [])], [], blockers));
                }
            }
        }
        return result;
    }

    // QuestDropDataCompiler (see its own header comment) is authoritative ONLY for one narrow
    // single-mob/single-item Drops rule - it has no Targets (kill-count objective) support and is
    // not a general quest-definition converter. Priority 7 (ai/world-data.md): a quest's overall
    // "Definition" component must never be claimed FullyCompatible merely because the specialized
    // drop compiler happened to succeed. Components:
    //   - DropRule: exactly what QuestDropDataCompiler converts (FullyCompatible on success,
    //     Unsupported only when the block genuinely has a Drops shape the compiler rejects, e.g. a
    //     malformed/multi-entry Drops block; NotApplicable when the quest has no Drops block at all).
    //   - Targets: Unsupported when the quest has a Targets block (positively known unconvertible -
    //     there is no kill-count runtime), NotApplicable otherwise.
    //   - Definition: the quest-as-a-whole conversion. There is no general quest-definition
    //     converter in this project, so Definition is NotYetAnalyzed unless a component establishes
    //     a genuine, positive fact: Unsupported if Targets is Unsupported (a real, known gap);
    //     PartiallyCompatible if DropRule is FullyCompatible (at least one meaningful component
    //     really does convert) and Targets is not itself Unsupported; otherwise NotYetAnalyzed.
    private static IReadOnlyList<DomainEntity> AnalyzeQuests(string root)
    {
        var path = Path.Combine(root, "db/re/quest_db.yml"); var mobPath = Path.Combine(root, "db/re/mob_db.yml");
        if (!File.Exists(path) || !File.Exists(mobPath)) return [];
        var itemFiles = Directory.Exists(Path.Combine(root, "db/re")) ? Directory.EnumerateFiles(Path.Combine(root, "db/re"), "item_db*.yml").Order(StringComparer.Ordinal).ToArray() : [];
        if (itemFiles.Length == 0) return [];
        var yaml = File.ReadAllText(path); var mobs = File.ReadAllText(mobPath);
        var itemTexts = itemFiles.Select(File.ReadAllText).ToArray();
        var result = new List<DomainEntity>();
        foreach (var block in YamlBlocks(yaml))
        {
            var id = BlockId(block.Text); if (id is null) continue;
            var title = Scalar(block.Text, "Title") ?? id.Value.ToString(CultureInfo.InvariantCulture);
            var hasTargets = HasBlock(block.Text, "Targets"); var hasDrops = HasBlock(block.Text, "Drops");
            var dependencies = new List<string>(); var blockers = new List<string>();
            DomainComponent targetsComponent = hasTargets
                ? new("Targets", DomainCompatibilityStatus.Unsupported, ["quest:targets"])
                : new("Targets", DomainCompatibilityStatus.NotApplicable);
            if (hasTargets) blockers.Add("quest:targets");

            DomainComponent dropRuleComponent;
            if (!hasDrops)
            {
                dropRuleComponent = new("DropRule", DomainCompatibilityStatus.NotApplicable);
            }
            else
            {
                // Resolve item AegisName across ALL pinned db/re/item_db*.yml files (not just
                // item_db_etc.yml) so a quest drop item declared in item_db_equip.yml or another
                // sibling file still resolves - QuestDropDataCompiler.ReadSingleDrop only accepts
                // one item_db document at a time, so try each until one resolves the AegisName.
                Exception? lastFailure = null; QuestDropDataCompiler.QuestDropData? quest = null;
                foreach (var itemText in itemTexts)
                {
                    try { quest = QuestDropDataCompiler.ReadSingleDrop(yaml, (uint)id.Value, mobs, itemText); break; }
                    catch (Exception exception) { lastFailure = exception; }
                }
                if (quest is not null)
                {
                    _ = QuestDropDataCompiler.Generate(quest, "analysis", Relative(root, path), block.Line);
                    dependencies.Add($"mob:{quest.MobId}"); dependencies.Add($"item:{quest.ItemId}");
                    dropRuleComponent = new("DropRule", DomainCompatibilityStatus.FullyCompatible);
                }
                else
                {
                    blockers.Add("quest:drops"); dropRuleComponent = new("DropRule", DomainCompatibilityStatus.Unsupported, ["quest:drops"]);
                    _ = lastFailure; // Genuinely unresolvable (bad Drops shape or unresolvable Mob/Item name across every item_db file) - not classified further; "quest:drops" is the honest, coarse signal.
                }
            }

            blockers.Add("quest:playability-not-evaluated");
            var runtimeComponent = new DomainComponent("RuntimeReadiness", DomainCompatibilityStatus.NotYetAnalyzed, ["quest:playability-not-evaluated"]);
            var definitionStatus = hasTargets ? DomainCompatibilityStatus.Unsupported
                : dropRuleComponent.Status == DomainCompatibilityStatus.FullyCompatible ? DomainCompatibilityStatus.PartiallyCompatible
                : DomainCompatibilityStatus.NotYetAnalyzed;
            var definitionComponent = new DomainComponent("Definition", definitionStatus, definitionStatus == DomainCompatibilityStatus.Unsupported ? ["quest:targets"] : []);
            var overall = hasTargets ? DomainCompatibilityStatus.Unsupported
                : dropRuleComponent.Status == DomainCompatibilityStatus.FullyCompatible ? DomainCompatibilityStatus.PartiallyCompatible
                : DomainCompatibilityStatus.NotYetAnalyzed;
            result.Add(Entity("quests", $"quest:{id}", title, root, path, block.Line, overall,
                [definitionComponent, targetsComponent, dropRuleComponent, runtimeComponent], dependencies, blockers));
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
            if (IsCommentedOrBlank(line)) continue;
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
                if (IsCommentedOrBlank(lines[index])) continue;
                var match = Regex.Match(lines[index], @"^function\s+(?:script\s+)?(?<name>\S+)\s*\{"); if (!match.Success) continue;
                var declarationLine = index + 1;
                var body = new List<string>(); var depth = lines[index].Count(character => character == '{') - lines[index].Count(character => character == '}');
                while (depth > 0 && ++index < lines.Length) { body.Add(lines[index]); depth += lines[index].Count(c => c == '{') - lines[index].Count(c => c == '}'); }
                var source = new WorldSourceInfo("rAthena", "pinned", Relative(root, path), declarationLine);
                var (syntax, semantics) = RathenaEventCompiler.Parse(string.Join('\n', body), source);
                var compilation = RathenaEventCompiler.Compile(syntax, semantics, "OnClick");
                var bodyBlockers = compilation.Diagnostics.Where(item => item.Severity == "Error").Select(item => CompatibilityDiagnosticNormalizer.Normalize(item, syntax).CapabilityId).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                var blockers = bodyBlockers.Append("function:runtime").Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
                // Priority 3 (ai/world-data.md): the entity id must identify THIS source
                // declaration, not merely its display name - pinned rAthena declares multiple
                // distinct `function script <Name> {...}` bodies sharing the same name across
                // different files (e.g. Job_Change, Chk, Catwarp), which previously collapsed onto
                // one "function:<name>" id and silently merged unrelated entities/dependency-graph
                // nodes. Source-qualified with the canonical relative source file and declaration
                // line, both already deterministic per run (Relative(root, path) + a real source
                // line number), so distinct declarations always stay distinct, identical reruns stay
                // stable, and the id remains human-readable for diagnostics.
                var functionId = $"function:{Relative(root, path)}:{declarationLine}:{match.Groups["name"].Value}";
                result.Add(Entity("functions", functionId, match.Groups["name"].Value, root, path, declarationLine, DomainCompatibilityStatus.Unsupported,
                    [new("Body", bodyBlockers.Length == 0 ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.Unsupported, bodyBlockers), new("Runtime", DomainCompatibilityStatus.Unsupported, ["function:runtime"])], [], blockers));
            }
        }
        return result;
    }

    // Priority 15 (ai/world-data.md): a map-level "World status" aggregate distinct from map
    // GEOMETRY compatibility (the "maps" domain's own Geometry component, which is purely about
    // map_cache.dat collision data). This rolls up how much of everything that should exist on a
    // map has actually been evaluated and is compatible - mob spawns and mapflags, the two
    // per-map domains RepositoryDomainAnalyzers itself produces. Warps/NPC placements are produced
    // one layer up by RepositoryCompatibilityAnalyzer (a different entity model, CompatibilityEntity,
    // not DomainEntity) and are therefore intentionally NOT rolled up here - this aggregate is
    // scoped to what this analyzer can see; folding warps/NPCs in as well is a natural future
    // extension at that outer layer, not a fabrication here.
    //
    // Only computed for maps whose relevant domains were actually selected in this run
    // (--domain filtering respected): a map row is only emitted when at least one of
    // mob-spawns/mapflags was selected, and only that/those domain's counts are rolled up - never a
    // fabricated 0/0 for a domain the caller didn't ask to analyze. A map's World status can only be
    // FullyCompatible when every considered domain's own entities for that map are all
    // FullyCompatible/NotApplicable; any NotYetAnalyzed entity anywhere in a considered domain makes
    // the map NotFullyEvaluated (surfaced as DomainCompatibilityStatus.NotYetAnalyzed on the
    // returned entity) rather than FullyCompatible - shops, for example, are always NotYetAnalyzed
    // today, so a map that also had shops folded into this rollup could never be truthfully
    // FullyCompatible; shops are not currently one of this method's considered domains for exactly
    // that reason (mob-spawns/mapflags are the two domains actually rolled up here today).
    private static IReadOnlyList<DomainEntity> AnalyzeMapWorld(string root, IReadOnlySet<string>? selectedDomains,
        IReadOnlyList<DomainEntity> maps, IReadOnlyList<DomainEntity> mobSpawns, IReadOnlyList<DomainEntity> mapFlags)
    {
        bool Selected(string domain) => selectedDomains is null || selectedDomains.Count == 0 || selectedDomains.Contains(domain);
        if (!Selected("maps") || maps.Count == 0) return [];
        var mobSpawnsSelected = Selected("mob-spawns"); var mapFlagsSelected = Selected("mapflags");
        if (!mobSpawnsSelected && !mapFlagsSelected) return [];

        var result = new List<DomainEntity>();
        foreach (var map in maps.Select(item => item.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var components = new List<DomainComponent>();
            var consideredStatuses = new List<DomainCompatibilityStatus>();

            if (mobSpawnsSelected)
            {
                var forMap = mobSpawns.Where(item => item.Map == map).ToArray();
                var (status, full, total) = RollupCounted(forMap.Select(item => item.Status));
                components.Add(new("MobSpawns", status, [], new DomainMetric(full, total)));
                consideredStatuses.Add(status);
            }
            if (mapFlagsSelected)
            {
                var forMap = mapFlags.Where(item => item.Map == map).ToArray();
                var (status, full, total) = RollupCounted(forMap.Select(item => item.Status));
                components.Add(new("MapFlags", status, [], new DomainMetric(full, total)));
                consideredStatuses.Add(status);
            }

            var overall = consideredStatuses.Any(status => status == DomainCompatibilityStatus.NotYetAnalyzed) ? DomainCompatibilityStatus.NotYetAnalyzed
                : consideredStatuses.All(status => status is DomainCompatibilityStatus.FullyCompatible or DomainCompatibilityStatus.NotApplicable) ? DomainCompatibilityStatus.FullyCompatible
                : DomainCompatibilityStatus.PartiallyCompatible;
            result.Add(Entity("map-world", $"map-world:{map}", map, root, Path.Combine(root, "db/map_cache.dat"), 0, overall, components, [], [], map));
        }
        return result;
    }

    // A map with zero entities in a considered domain (e.g. no mob spawns at all on that map)
    // rolls up to NotApplicable, not FullyCompatible-by-vacuous-truth or NotYetAnalyzed - there is
    // simply nothing there for that component to report.
    private static (DomainCompatibilityStatus Status, int Full, int Total) RollupCounted(IEnumerable<DomainCompatibilityStatus> statuses)
    {
        var array = statuses.ToArray();
        var full = array.Count(item => item == DomainCompatibilityStatus.FullyCompatible);
        if (array.Length == 0) return (DomainCompatibilityStatus.NotApplicable, 0, 0);
        if (array.Any(item => item == DomainCompatibilityStatus.NotYetAnalyzed)) return (DomainCompatibilityStatus.NotYetAnalyzed, full, array.Length);
        return (full == array.Length ? DomainCompatibilityStatus.FullyCompatible : DomainCompatibilityStatus.PartiallyCompatible, full, array.Length);
    }

    // Shared guard for every raw-line domain scanner (AnalyzeMapFlags, AnalyzeFunctions) that reads
    // rAthena *.txt content directly with regex/column-splitting rather than through
    // RathenaSourceParser/RathenaEventCompiler (which already skip comments as part of real
    // tokenization). A line is excluded once its content after trimming leading whitespace is empty
    // or begins with a "//" line comment (rAthena's own comment syntax) - matching real pinned data,
    // e.g. npc/custom/etc/penal_servitude.txt's commented-out `//sec_in02	mapflag	pvp` rows, which
    // must never be discovered as active declarations (see ai/world-data.md).
    private static bool IsCommentedOrBlank(string line)
    {
        var trimmed = line.TrimStart();
        return trimmed.Length == 0 || trimmed.StartsWith("//", StringComparison.Ordinal);
    }

    private static IEnumerable<RathenaDeclaration> PositionedDeclarations(string root)
    {
        var npc = Path.Combine(root, "npc"); return Directory.Exists(npc) ? RathenaSourceParser.Parse([npc]) : [];
    }

    private static DomainEntity Entity(string domain, string id, string name, string root, string path, int line, DomainCompatibilityStatus status,
        IReadOnlyList<DomainComponent> components, IEnumerable<string> dependencies, IEnumerable<string> blockers, string? map = null, string? provenance = null) =>
        new(domain, id, name, Relative(root, path), line, status, components,
            dependencies.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), blockers.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray(), map, provenance);

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

    // Classifies ItemDataCompiler.ReadItemDefinition/Generate failures into stable semantic
    // capability IDs instead of collapsing every failure into one generic
    // "item-definition:not-supported" bucket (Priority 4). ItemDataCompiler now throws the typed
    // ItemDefinitionUnsupportedException (see that type's own doc comment) carrying an
    // already-classified CapabilityId (e.g. "item-type:card", "item-subtype:whatever",
    // "item-location:whatever", "item-script:unsupported-shape") for every genuinely unmodeled
    // construct it recognizes - so this reads that id directly rather than parsing prose. Only a
    // truly unclassified failure (a missing required field via ArgumentException, or any other
    // exception type) falls back to the generic exception-type bucket as a last resort.
    private static string ItemCapability(Exception exception) =>
        exception is ItemDefinitionUnsupportedException typed ? typed.CapabilityId : "item-definition:" + ExceptionCapability(exception);
    private static string Kebab(string value) => Regex.Replace(value, "([a-z0-9])([A-Z])", "$1-$2").Replace('_', '-').ToLowerInvariant();
    private static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
    private static string Resolve(string root, string source) => File.Exists(source) ? source : Path.Combine(root, source.Replace("legacy/rathena/", "", StringComparison.Ordinal));
}
