using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Athena.WorldCompiler.Generation;

internal sealed record CharacterDataSources(string MmoHeader, string ScriptConstants, string JobExperience, string JobBasePoints, string JobStats, string StatPoints, string SkillDatabase, string SkillTree);
internal sealed record CharacterDataArtifact(string RelativePath, string Source);
internal sealed record CharacterDataCompilation(IReadOnlyList<CharacterDataArtifact> Artifacts, CharacterDataCounts Counts, IReadOnlyList<string> Exclusions);
internal sealed record CharacterDataCounts(int NumericJobIdentitiesDiscovered, int GeneratedJobDefinitions, int JobIdsWithProgression, int UniqueProgressionDefinitions, int CanonicalSkills, int DirectSkillTrees, int EffectiveSkillTrees);

internal static partial class CharacterDataCompiler
{
    private sealed record JobIdentity(ushort Id, string Name, string EnumName);
    private sealed class ProgressionBuilder(JobIdentity job)
    {
        internal JobIdentity Job { get; } = job;
        internal ushort? MaxBaseLevel, MaxJobLevel;
        internal SortedDictionary<ushort, ulong>? BaseExperience, JobExperience, BaseHp, BaseSp;
        internal readonly SortedDictionary<ushort, StatBonus> Bonuses = [];
    }
    private readonly record struct StatBonus(int Str, int Agi, int Vit, int Int, int Dex, int Luk)
    {
        internal StatBonus Add(StatBonus value) => new(Str + value.Str, Agi + value.Agi, Vit + value.Vit, Int + value.Int, Dex + value.Dex, Luk + value.Luk);
    }
    private sealed record Progression(JobIdentity Job, ushort MaxBaseLevel, ushort MaxJobLevel, ulong[] BaseExperience, ulong[] JobExperience, uint[] BaseHp, uint[] BaseSp, uint[] StatPoints, uint[] Str, uint[] Agi, uint[] Vit, uint[] Int, uint[] Dex, uint[] Luk, string DataKey);
    private sealed record Skill(ushort Id, string Name, ushort MaxLevel);
    private sealed record Requirement(ushort SkillId, ushort Level);
    private sealed record TreeEntry(ushort SkillId, ushort MaxLevel, ushort BaseLevel, ushort JobLevel, IReadOnlyList<Requirement> Requirements, bool Exclude);
    private sealed record DirectTree(JobIdentity Job, IReadOnlyList<JobIdentity> Parents, IReadOnlyList<TreeEntry> Entries);
    private sealed record EffectiveTree(DirectTree Direct, IReadOnlyList<TreeEntry> Entries);

    internal static CharacterDataCompilation Compile(CharacterDataSources sources, string commit)
    {
        if (string.IsNullOrWhiteSpace(commit)) throw new ArgumentException("Missing rAthena commit metadata.");
        var identities = ParseJobs(sources.MmoHeader, sources.ScriptConstants);
        var aliases = BuildJobLookup(identities, sources.ScriptConstants);
        var builders = identities.ToDictionary(job => job.Id, job => new ProgressionBuilder(job));
        ApplyJobDatabase(sources.JobExperience, "db/re/job_exp.yml", aliases, builders, includeExperience: true, includeBasePoints: false, includeBonuses: false);
        ApplyJobDatabase(sources.JobBasePoints, "db/re/job_basepoints.yml", aliases, builders, includeExperience: false, includeBasePoints: true, includeBonuses: false);
        ApplyJobDatabase(sources.JobStats, "db/re/job_stats.yml", aliases, builders, includeExperience: false, includeBasePoints: false, includeBonuses: true);
        var statPoints = ParseStatPoints(sources.StatPoints);
        var progressions = BuildProgressions(builders.Values, statPoints);
        var skills = ParseSkills(sources.SkillDatabase);
        var skillsByName = skills.ToDictionary(skill => skill.Name, StringComparer.OrdinalIgnoreCase);
        var directTrees = ParseTrees(sources.SkillTree, aliases, skillsByName);
        var progressionByJob = progressions.ToDictionary(item => item.Job.Id);
        var effectiveTrees = ResolveTrees(directTrees, progressionByJob);

        var includedIds = progressions.Select(item => item.Job.Id).Concat(directTrees.Select(item => item.Job.Id)).ToHashSet();
        var jobs = identities.Where(job => includedIds.Contains(job.Id)).OrderBy(job => job.Id).ToArray();
        var exclusions = identities.Where(job => !includedIds.Contains(job.Id)).OrderBy(job => job.Id).Select(job => $"{job.Id} {job.Name}: no complete progression definition and no Renewal skill-tree declaration.").ToArray();
        ValidateCrossRegistry(jobs, progressions, skills, directTrees, effectiveTrees);

        var artifacts = new List<CharacterDataArtifact>
        {
            new("Jobs/GeneratedJobRegistry.cs", EmitJobs(jobs, commit)),
            new("Progression/GeneratedProgressionData.cs", EmitProgressions(progressions, commit)),
            new("Progression/GeneratedProgressionRegistry.cs", EmitProgressionRegistry(progressions, commit)),
            new("Skills/GeneratedSkillRegistry.cs", EmitSkills(skills, commit)),
            new("Skills/GeneratedSkillTrees.cs", EmitTrees(effectiveTrees, commit)),
            new("Skills/GeneratedSkillTreeRegistry.cs", EmitTreeRegistry(effectiveTrees, commit)),
        };
        var uniqueProgressions = progressions.Select(item => item.DataKey).Distinct(StringComparer.Ordinal).Count();
        return new(artifacts, new(identities.Count, jobs.Length, progressions.Count, uniqueProgressions, skills.Count, directTrees.Count, effectiveTrees.Count), exclusions);
    }

    private static IReadOnlyList<JobIdentity> ParseJobs(string header, string constants)
    {
        var match = JobEnumRegex().Match(header);
        if (!match.Success) throw new ArgumentException("src/common/mmo.hpp does not contain enum e_job.");
        var values = new Dictionary<string, ushort>(StringComparer.Ordinal);
        var current = -1;
        foreach (var raw in match.Groups[1].Value.Split(','))
        {
            var text = Regex.Replace(raw, "//.*", "").Trim();
            if (text.Length == 0) continue;
            var entry = JobEntryRegex().Match(text);
            if (!entry.Success) throw new ArgumentException($"Unsupported e_job entry '{text}'.");
            if (entry.Groups[2].Success) current = int.Parse(entry.Groups[2].Value, CultureInfo.InvariantCulture); else current++;
            if (current is < 0 or > ushort.MaxValue) throw new ArgumentException($"Job value for {entry.Groups[1].Value} is outside ushort range.");
            values.Add(entry.Groups[1].Value, (ushort)current);
        }
        var exported = ExportedJobRegex().Matches(constants).Select(match => match.Groups[1].Value).ToHashSet(StringComparer.Ordinal);
        return values.Where(pair => exported.Contains(pair.Key)).Select(pair => new JobIdentity(pair.Value, CanonicalJobName(pair.Key[4..]), pair.Key)).OrderBy(job => job.Id).ToArray();
    }

    private static Dictionary<string, JobIdentity> BuildJobLookup(IReadOnlyList<JobIdentity> jobs, string constants)
    {
        var result = new Dictionary<string, JobIdentity>(StringComparer.OrdinalIgnoreCase);
        foreach (var job in jobs)
        {
            AddJobAlias(result, job.Name, job);
            AddJobAlias(result, job.EnumName[4..], job);
        }
        foreach (Match alias in JobAliasRegex().Matches(constants))
        {
            var target = jobs.SingleOrDefault(job => job.EnumName == alias.Groups[2].Value) ?? throw new ArgumentException($"Job alias target {alias.Groups[2].Value} is unknown.");
            AddJobAlias(result, alias.Groups[1].Value, target);
        }
        return result;
    }

    private static void AddJobAlias(Dictionary<string, JobIdentity> lookup, string name, JobIdentity job)
    {
        if (lookup.TryGetValue(name, out var existing) && existing.Id != job.Id) throw new ArgumentException($"Duplicate job alias '{name}'.");
        lookup[name] = job;
    }

    private static void ApplyJobDatabase(string yaml, string sourceName, IReadOnlyDictionary<string, JobIdentity> jobs, IReadOnlyDictionary<ushort, ProgressionBuilder> builders, bool includeExperience, bool includeBasePoints, bool includeBonuses)
    {
        var root = SimpleYaml.Parse(yaml, sourceName);
        var body = root.Required("Body", sourceName).Sequence($"{sourceName} Body");
        foreach (var (node, blockIndex) in body.Items.Select((node, index) => (node.Map($"{sourceName} Body[{index}]"), index)))
        {
            var context = $"{sourceName} Body[{blockIndex}]";
            var jobMap = node.Required("Jobs", context).Map($"{context}.Jobs");
            var targets = new List<ProgressionBuilder>();
            foreach (var pair in jobMap.Items.Where(pair => pair.Value.Bool($"{context}.Jobs.{pair.Key}")))
            {
                if (!jobs.TryGetValue(pair.Key, out var identity)) throw new ArgumentException($"{sourceName}: unknown job '{pair.Key}'.");
                targets.Add(builders[identity.Id]);
            }
            foreach (var target in targets)
            {
                if (includeExperience)
                {
                    ApplyScalar(node, "MaxBaseLevel", value => target.MaxBaseLevel = value, context);
                    ApplyScalar(node, "MaxJobLevel", value => target.MaxJobLevel = value, context);
                    ApplyLevels(node, "BaseExp", "Exp", values => target.BaseExperience = values, context);
                    ApplyLevels(node, "JobExp", "Exp", values => target.JobExperience = values, context);
                }
                if (includeBasePoints)
                {
                    ApplyLevels(node, "BaseHp", "Hp", values => target.BaseHp = values, context);
                    ApplyLevels(node, "BaseSp", "Sp", values => target.BaseSp = values, context);
                }
                if (includeBonuses && node.Optional("BonusStats") is { } bonuses)
                {
                    foreach (var bonusNode in bonuses.Sequence($"{context}.BonusStats").Items)
                    {
                        var map = bonusNode.Map($"{context}.BonusStats entry");
                        var level = map.Required("Level", context).UShort($"{context}.BonusStats.Level");
                        var value = new StatBonus(ReadOptionalInt(map, "Str"), ReadOptionalInt(map, "Agi"), ReadOptionalInt(map, "Vit"), ReadOptionalInt(map, "Int"), ReadOptionalInt(map, "Dex"), ReadOptionalInt(map, "Luk"));
                        target.Bonuses[level] = target.Bonuses.GetValueOrDefault(level).Add(value);
                    }
                }
            }
        }
    }

    private static void ApplyScalar(SimpleYamlMap map, string key, Action<ushort> apply, string context) { if (map.Optional(key) is { } node) apply(node.UShort($"{context}.{key}")); }
    private static void ApplyLevels(SimpleYamlMap map, string section, string valueName, Action<SortedDictionary<ushort, ulong>> apply, string context)
    {
        if (map.Optional(section) is not { } sectionNode) return;
        var values = new SortedDictionary<ushort, ulong>();
        foreach (var item in sectionNode.Sequence($"{context}.{section}").Items)
        {
            var row = item.Map($"{context}.{section} row");
            var level = row.Required("Level", context).UShort($"{context}.{section}.Level");
            var value = row.Required(valueName, context).ULong($"{context}.{section}.{valueName}");
            // JobDatabase writes rows into the indexed table in source order, so a
            // repeated level in the pinned file is an explicit last-row-wins overlay.
            values[level] = value;
        }
        apply(values);
    }

    private static uint[] ParseStatPoints(string yaml)
    {
        var root = SimpleYaml.Parse(yaml, "db/re/statpoint.yml");
        var rows = root.Required("Body", "db/re/statpoint.yml").Sequence("db/re/statpoint.yml Body");
        var values = new SortedDictionary<ushort, uint>();
        foreach (var item in rows.Items)
        {
            var map = item.Map("db/re/statpoint.yml row");
            var level = map.Required("Level", "statpoint row").UShort("statpoint Level");
            var points = checked((uint)map.Required("Points", "statpoint row").ULong("statpoint Points"));
            if (!values.TryAdd(level, points)) throw new ArgumentException($"db/re/statpoint.yml has duplicate level {level}.");
        }
        var maximum = values.Keys.Max();
        var result = new uint[maximum + 1];
        for (ushort level = 1; level <= maximum; level++) result[level] = values.TryGetValue(level, out var value) ? value : throw new ArgumentException($"db/re/statpoint.yml is missing level {level}.");
        return result;
    }

    private static IReadOnlyList<Progression> BuildProgressions(IEnumerable<ProgressionBuilder> builders, uint[] globalStatPoints)
    {
        var result = new List<Progression>();
        foreach (var builder in builders.OrderBy(item => item.Job.Id))
        {
            if (builder.MaxBaseLevel is null || builder.MaxJobLevel is null || builder.BaseExperience is null || builder.JobExperience is null || builder.BaseHp is null || builder.BaseSp is null) continue;
            var maxBase = builder.MaxBaseLevel.Value; var maxJob = builder.MaxJobLevel.Value;
            var baseExp = Complete(builder.BaseExperience, maxBase, builder.Job.Name, "BaseExp");
            var jobExp = Complete(builder.JobExperience, maxJob, builder.Job.Name, "JobExp");
            var hp = CompleteUInt(builder.BaseHp, maxBase, builder.Job.Name, "BaseHp");
            var sp = CompleteUInt(builder.BaseSp, maxBase, builder.Job.Name, "BaseSp");
            if (globalStatPoints.Length <= maxBase) throw new ArgumentException($"db/re/statpoint.yml does not cover {builder.Job.Name} max base level {maxBase}.");
            var statPoints = globalStatPoints[..(maxBase + 1)];
            var stats = Enumerable.Range(0, 6).Select(_ => new uint[maxJob + 1]).ToArray();
            var cumulative = new StatBonus();
            for (ushort level = 1; level <= maxJob; level++)
            {
                cumulative = cumulative.Add(builder.Bonuses.GetValueOrDefault(level));
                stats[0][level] = checked((uint)cumulative.Str); stats[1][level] = checked((uint)cumulative.Agi); stats[2][level] = checked((uint)cumulative.Vit); stats[3][level] = checked((uint)cumulative.Int); stats[4][level] = checked((uint)cumulative.Dex); stats[5][level] = checked((uint)cumulative.Luk);
            }
            var key = HashKey(maxBase, maxJob, baseExp, jobExp, hp, sp, statPoints, stats);
            result.Add(new(builder.Job, maxBase, maxJob, baseExp, jobExp, hp, sp, statPoints, stats[0], stats[1], stats[2], stats[3], stats[4], stats[5], key));
        }
        return result;
    }

    private static ulong[] Complete(SortedDictionary<ushort, ulong> values, ushort max, string job, string section)
    {
        var result = new ulong[max + 1];
        for (ushort level = 1; level <= max; level++) result[level] = values.TryGetValue(level, out var value) && value > 0 ? value : throw new ArgumentException($"{job} {section} is missing level {level}.");
        return result;
    }
    private static uint[] CompleteUInt(SortedDictionary<ushort, ulong> values, ushort max, string job, string section)
    {
        // Pinned JobDatabase initializes HP/SP arrays to zero and several advanced
        // classes intentionally provide rows only from their legal change level.
        var result = new uint[max + 1];
        foreach (var pair in values.Where(pair => pair.Key <= max)) result[pair.Key] = checked((uint)pair.Value);
        if (result.All(value => value == 0)) throw new ArgumentException($"{job} {section} has no rows through max level {max}.");
        return result;
    }
    private static string HashKey(params object[] values) => string.Join('|', values.Select(StableValue));
    private static string StableValue(object? value) => value switch
    {
        null => "null",
        string text => text,
        System.Collections.IEnumerable sequence => "[" + string.Join(',', sequence.Cast<object?>().Select(StableValue)) + "]",
        IFormattable formattable => formattable.ToString(null, CultureInfo.InvariantCulture),
        _ => value.ToString() ?? string.Empty,
    };

    private static IReadOnlyList<Skill> ParseSkills(string yaml)
    {
        var result = new List<Skill>();
        ushort? id = null; string? name = null; ushort maxLevel = 0;
        void Finish()
        {
            if (id is null) return;
            if (name is null) throw new ArgumentException($"db/re/skill_db.yml skill {id} has no Name.");
            result.Add(new(id.Value, name, maxLevel)); id = null; name = null; maxLevel = 0;
        }
        foreach (var line in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = SkillIdRegex().Match(line);
            if (match.Success) { Finish(); id = ushort.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            if (id is null) continue;
            match = SkillNameRegex().Match(line); if (match.Success) { name = match.Groups[1].Value; continue; }
            match = SkillMaxLevelRegex().Match(line); if (match.Success) maxLevel = ushort.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture);
        }
        Finish();
        var duplicateId = result.GroupBy(skill => skill.Id).FirstOrDefault(group => group.Count() > 1); if (duplicateId is not null) throw new ArgumentException($"db/re/skill_db.yml has duplicate skill ID {duplicateId.Key}.");
        var duplicateName = result.GroupBy(skill => skill.Name, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1); if (duplicateName is not null) throw new ArgumentException($"db/re/skill_db.yml has duplicate skill name '{duplicateName.Key}'.");
        return result.OrderBy(skill => skill.Id).ToArray();
    }

    private static IReadOnlyList<DirectTree> ParseTrees(string yaml, IReadOnlyDictionary<string, JobIdentity> jobs, IReadOnlyDictionary<string, Skill> skills)
    {
        var root = SimpleYaml.Parse(yaml, "db/re/skill_tree.yml");
        var body = root.Required("Body", "skill_tree").Sequence("skill_tree Body");
        var result = new List<DirectTree>();
        foreach (var node in body.Items)
        {
            var map = node.Map("skill tree entry");
            var jobName = map.Required("Job", "skill tree entry").Scalar("skill tree Job");
            if (!jobs.TryGetValue(jobName, out var job)) throw new ArgumentException($"Skill tree job '{jobName}' is unknown.");
            var parents = new List<JobIdentity>();
            if (map.Optional("Inherit") is { } inheritNode)
                foreach (var pair in inheritNode.Map($"skill tree {jobName}.Inherit").Items.Where(pair => pair.Value.Bool($"{jobName}.Inherit.{pair.Key}")))
                    parents.Add(jobs.TryGetValue(pair.Key, out var parent) ? parent : throw new ArgumentException($"Skill tree job '{jobName}' inherits unknown job '{pair.Key}'."));
            var entries = new List<TreeEntry>();
            if (map.Optional("Tree") is { } treeNode)
                foreach (var entryNode in treeNode.Sequence($"skill tree {jobName}.Tree").Items)
                {
                    var entry = entryNode.Map($"skill tree {jobName} entry");
                    var skillName = entry.Required("Name", jobName).Scalar($"{jobName} skill Name");
                    if (!skills.TryGetValue(skillName, out var skill)) throw new ArgumentException($"Skill '{skillName}' in job '{jobName}' is unknown.");
                    var max = entry.Required("MaxLevel", skillName).UShort($"{jobName}.{skillName}.MaxLevel");
                    if (max > skill.MaxLevel) max = skill.MaxLevel;
                    var requirements = new List<Requirement>();
                    if (entry.Optional("Requires") is { } requires)
                        foreach (var requiredNode in requires.Sequence($"{jobName}.{skillName}.Requires").Items)
                        {
                            var required = requiredNode.Map($"{jobName}.{skillName} prerequisite");
                            var requiredName = required.Required("Name", skillName).Scalar($"{skillName} prerequisite Name");
                            if (!skills.TryGetValue(requiredName, out var requiredSkill)) throw new ArgumentException($"Skill '{skillName}' in job '{jobName}' requires unknown skill '{requiredName}'.");
                            var level = required.Required("Level", skillName).UShort($"{skillName} prerequisite Level");
                            if (level > requiredSkill.MaxLevel) level = requiredSkill.MaxLevel;
                            if (level > 0) requirements.Add(new(requiredSkill.Id, level));
                        }
                    entries.Add(new(skill.Id, max, ReadOptionalUShort(entry, "BaseLevel"), ReadOptionalUShort(entry, "JobLevel"), requirements.OrderBy(item => item.SkillId).ToArray(), entry.Optional("Exclude")?.Bool($"{jobName}.{skillName}.Exclude") ?? false));
                }
            if (result.Any(item => item.Job.Id == job.Id)) throw new ArgumentException($"Duplicate skill tree for job '{jobName}'.");
            result.Add(new(job, parents, entries));
        }
        return result.OrderBy(tree => tree.Job.Id).ToArray();
    }

    private static IReadOnlyList<EffectiveTree> ResolveTrees(IReadOnlyList<DirectTree> trees, IReadOnlyDictionary<ushort, Progression> progressions)
    {
        var byJob = trees.ToDictionary(tree => tree.Job.Id);
        var visiting = new List<ushort>();
        var visited = new HashSet<ushort>();
        void ValidateGraph(DirectTree tree)
        {
            if (visited.Contains(tree.Job.Id)) return;
            var cycle = visiting.IndexOf(tree.Job.Id);
            if (cycle >= 0) throw new ArgumentException($"Skill-tree inheritance cycle: {string.Join(" -> ", visiting[cycle..].Append(tree.Job.Id).Select(id => byJob[id].Job.Name))}.");
            visiting.Add(tree.Job.Id);
            foreach (var parent in tree.Parents)
            {
                if (!byJob.TryGetValue(parent.Id, out var parentTree)) throw new ArgumentException($"Skill tree job '{tree.Job.Name}' inherits job '{parent.Name}' which has no tree declaration.");
                ValidateGraph(parentTree);
            }
            visiting.RemoveAt(visiting.Count - 1);
            visited.Add(tree.Job.Id);
        }
        foreach (var tree in trees) ValidateGraph(tree);

        var result = new List<EffectiveTree>();
        foreach (var tree in trees)
        {
            var effective = new Dictionary<ushort, TreeEntry>();
            // Matches pinned loadingFinished(): each listed parent contributes only
            // its declared Tree, not the parent's already-populated effective tree.
            // The last inherited tree replaces an earlier inherited duplicate;
            // direct declarations always win over inherited entries.
            foreach (var parent in tree.Parents)
                foreach (var entry in byJob[parent.Id].Entries.Where(entry => !entry.Exclude && !tree.Entries.Any(direct => direct.SkillId == entry.SkillId)))
                    effective[entry.SkillId] = entry;
            foreach (var entry in tree.Entries) effective[entry.SkillId] = entry;
            var maxBase = progressionByJobValue(progressions, tree.Job.Id)?.MaxBaseLevel ?? ushort.MaxValue;
            var maxJob = progressionByJobValue(progressions, tree.Job.Id)?.MaxJobLevel ?? ushort.MaxValue;
            var entries = effective.Values.Where(entry => entry.MaxLevel > 0).Select(entry => entry with { BaseLevel = Math.Min(entry.BaseLevel, maxBase), JobLevel = Math.Min(entry.JobLevel, maxJob) }).OrderBy(entry => entry.SkillId).ToArray();
            result.Add(new(tree, entries));
        }
        return result.OrderBy(tree => tree.Direct.Job.Id).ToArray();
        static Progression? progressionByJobValue(IReadOnlyDictionary<ushort, Progression> values, ushort id) => values.TryGetValue(id, out var value) ? value : null;
    }

    private static void ValidateCrossRegistry(IReadOnlyList<JobIdentity> jobs, IReadOnlyList<Progression> progressions, IReadOnlyList<Skill> skills, IReadOnlyList<DirectTree> directTrees, IReadOnlyList<EffectiveTree> effectiveTrees)
    {
        var jobIds = jobs.Select(job => job.Id).ToHashSet(); var skillIds = skills.Select(skill => skill.Id).ToHashSet();
        if (progressions.Any(item => !jobIds.Contains(item.Job.Id)) || directTrees.Any(item => !jobIds.Contains(item.Job.Id))) throw new ArgumentException("Generated job registries are inconsistent.");
        foreach (var entry in effectiveTrees.SelectMany(tree => tree.Entries).Concat(directTrees.SelectMany(tree => tree.Entries)))
        {
            if (!skillIds.Contains(entry.SkillId) || entry.Requirements.Any(requirement => !skillIds.Contains(requirement.SkillId))) throw new ArgumentException("Generated skill registries are inconsistent.");
        }
    }

    private static ushort ReadOptionalUShort(SimpleYamlMap map, string key) => map.Optional(key)?.UShort(key) ?? 0;
    private static int ReadOptionalInt(SimpleYamlMap map, string key) => map.Optional(key) is { } node && int.TryParse(node.Scalar(key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : map.Optional(key) is null ? 0 : throw new ArgumentException($"{key} must be an integer.");
    private static string CanonicalJobName(string name) => string.Join('_', name.Split('_').Select(part => part.Length == 0 ? part : char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
    private static string Header(string commit, params string[] sources) => "// <auto-generated>\n// Generated by Athena.WorldCompiler.\n// Sources:\n" + string.Concat(sources.Select(source => $"//   legacy/rathena/{source}\n")) + $"// rAthena commit: {commit}\n// Do not edit this file directly.\n// </auto-generated>\n";
    private static string Array<T>(IEnumerable<T> values) where T : IFormattable => "[" + string.Join(", ", values.Select(value => value.ToString(null, CultureInfo.InvariantCulture))) + "]";

    private static string EmitJobs(IReadOnlyList<JobIdentity> jobs, string commit)
    {
        var output = new StringBuilder(Header(commit, "src/common/mmo.hpp", "src/map/script_constants.hpp")).AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Jobs;").AppendLine("internal static class GeneratedJobRegistry").AppendLine("{").AppendLine("    private static readonly IReadOnlyDictionary<ushort, GeneratedJobDefinition> ById = new Dictionary<ushort, GeneratedJobDefinition>").AppendLine("    {");
        foreach (var job in jobs) output.Append("        [").Append(job.Id).Append("] = new(").Append(job.Id).Append(", \"").Append(job.Name).AppendLine("\"),");
        return output.AppendLine("    };").AppendLine("    internal static IEnumerable<GeneratedJobDefinition> All => ById.Values;").AppendLine("    internal static GeneratedJobDefinition Get(ushort jobClass) => ById.TryGetValue(jobClass, out var value) ? value : throw new NotSupportedException($\"Job class {jobClass} is not generated.\");").AppendLine("}").ToString();
    }

    private static string EmitProgressions(IReadOnlyList<Progression> progressions, string commit)
    {
        var output = new StringBuilder(Header(commit, "db/re/job_exp.yml", "db/re/job_basepoints.yml", "db/re/job_stats.yml", "db/re/statpoint.yml")).AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Progression;").AppendLine("internal static class GeneratedProgressionData").AppendLine("{");
        foreach (var item in progressions) output.Append("    internal static readonly CharacterProgressionDefinition Job_").Append(item.Job.Id.ToString("D4", CultureInfo.InvariantCulture)).Append(" = new(").Append(item.Job.Id).Append(", ").Append(item.MaxBaseLevel).Append(", ").Append(item.MaxJobLevel).Append(", ").Append(Array(item.BaseExperience)).Append(", ").Append(Array(item.JobExperience)).Append(", ").Append(Array(item.BaseHp)).Append(", ").Append(Array(item.BaseSp)).Append(", ").Append(Array(item.StatPoints)).Append(", ").Append(Array(item.Str)).Append(", ").Append(Array(item.Agi)).Append(", ").Append(Array(item.Vit)).Append(", ").Append(Array(item.Int)).Append(", ").Append(Array(item.Dex)).Append(", ").Append(Array(item.Luk)).AppendLine(");");
        return output.AppendLine("}").ToString();
    }

    private static string EmitProgressionRegistry(IReadOnlyList<Progression> progressions, string commit)
    {
        var output = new StringBuilder(Header(commit, "db/re/job_exp.yml", "db/re/job_basepoints.yml", "db/re/job_stats.yml", "db/re/statpoint.yml")).AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Progression;").AppendLine("internal static class GeneratedProgressionRegistry").AppendLine("{").AppendLine("    private static readonly IReadOnlyDictionary<ushort, CharacterProgressionDefinition> ByJobClass = new Dictionary<ushort, CharacterProgressionDefinition>").AppendLine("    {");
        foreach (var item in progressions) output.Append("        [").Append(item.Job.Id).Append("] = GeneratedProgressionData.Job_").Append(item.Job.Id.ToString("D4", CultureInfo.InvariantCulture)).AppendLine(",");
        return output.AppendLine("    };").AppendLine("    internal static IEnumerable<CharacterProgressionDefinition> All => ByJobClass.Values;").AppendLine("    internal static CharacterProgressionDefinition Get(ushort jobClass) => ByJobClass.TryGetValue(jobClass, out var value) ? value : throw new NotSupportedException($\"Progression data for job class {jobClass} is not generated.\");").AppendLine("}").ToString();
    }

    private static string EmitSkills(IReadOnlyList<Skill> skills, string commit)
    {
        var output = new StringBuilder(Header(commit, "db/re/skill_db.yml")).AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Skills;").AppendLine("internal static class GeneratedSkillRegistry").AppendLine("{").AppendLine("    private static readonly GeneratedSkillDefinition[] Definitions =").AppendLine("    [");
        foreach (var skill in skills) output.Append("        new(").Append(skill.Id).Append(", \"").Append(skill.Name).AppendLine("\"),");
        return output.AppendLine("    ];").AppendLine("    private static readonly IReadOnlyDictionary<ushort, GeneratedSkillDefinition> ById = Definitions.ToDictionary(value => value.SkillId);").AppendLine("    private static readonly IReadOnlyDictionary<string, GeneratedSkillDefinition> ByName = Definitions.ToDictionary(value => value.Name, StringComparer.Ordinal);").AppendLine("    internal static IReadOnlyList<GeneratedSkillDefinition> All => Definitions;").AppendLine("    internal static GeneratedSkillDefinition GetById(ushort skillId) => ById.TryGetValue(skillId, out var value) ? value : throw new NotSupportedException($\"Skill ID {skillId} is not generated.\");").AppendLine("    internal static GeneratedSkillDefinition GetByName(string name) => ByName.TryGetValue(name, out var value) ? value : throw new NotSupportedException($\"Skill '{name}' is not generated.\");").AppendLine("}").ToString();
    }

    private static string EmitTrees(IReadOnlyList<EffectiveTree> trees, string commit)
    {
        var output = new StringBuilder(Header(commit, "db/re/skill_tree.yml", "db/re/skill_db.yml", "src/common/mmo.hpp")).AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Skills;").AppendLine("internal static class GeneratedSkillTrees").AppendLine("{");
        foreach (var tree in trees)
        {
            output.Append("    internal static readonly GeneratedSkillTreeDefinition Job_").Append(tree.Direct.Job.Id.ToString("D4", CultureInfo.InvariantCulture)).Append(" = new(").Append(tree.Direct.Job.Id).Append(", ").Append(Array(tree.Direct.Parents.Select(parent => parent.Id))).AppendLine(",");
            EmitTreeEntries(output, tree.Direct.Entries, "        "); output.AppendLine(","); EmitTreeEntries(output, tree.Entries, "        "); output.AppendLine(");");
        }
        return output.AppendLine("}").ToString();
    }

    private static void EmitTreeEntries(StringBuilder output, IReadOnlyList<TreeEntry> entries, string indent)
    {
        output.Append(indent).AppendLine("[");
        foreach (var entry in entries.OrderBy(entry => entry.SkillId))
        {
            output.Append(indent).Append("    new(").Append(entry.SkillId).Append(", ").Append(entry.MaxLevel).Append(", ").Append(entry.BaseLevel).Append(", ").Append(entry.JobLevel).Append(", ");
            if (entry.Requirements.Count == 0) output.Append("[]"); else output.Append('[').Append(string.Join(", ", entry.Requirements.Select(requirement => $"new SkillPrerequisite({requirement.SkillId}, {requirement.Level})"))).Append(']');
            output.Append(", ").Append(entry.Exclude ? "true" : "false").AppendLine("),");
        }
        output.Append(indent).Append(']');
    }

    private static string EmitTreeRegistry(IReadOnlyList<EffectiveTree> trees, string commit)
    {
        var output = new StringBuilder(Header(commit, "db/re/skill_tree.yml", "db/re/skill_db.yml", "src/common/mmo.hpp")).AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Skills;").AppendLine("internal static class GeneratedSkillTreeRegistry").AppendLine("{").AppendLine("    private static readonly IReadOnlyDictionary<ushort, GeneratedSkillTreeDefinition> ByJobClass = new Dictionary<ushort, GeneratedSkillTreeDefinition>").AppendLine("    {");
        foreach (var tree in trees) output.Append("        [").Append(tree.Direct.Job.Id).Append("] = GeneratedSkillTrees.Job_").Append(tree.Direct.Job.Id.ToString("D4", CultureInfo.InvariantCulture)).AppendLine(",");
        return output.AppendLine("    };").AppendLine("    internal static IEnumerable<GeneratedSkillTreeDefinition> All => ByJobClass.Values;").AppendLine("    internal static GeneratedSkillTreeDefinition Get(ushort jobClass) => ByJobClass.TryGetValue(jobClass, out var value) ? value : throw new NotSupportedException($\"Skill tree for job class {jobClass} is not generated.\");").AppendLine("}").ToString();
    }

    [GeneratedRegex(@"(?s)enum\s+e_job\s*\{(.*?)\};")]
    private static partial Regex JobEnumRegex();
    [GeneratedRegex(@"^(JOB_[A-Z0-9_]+)(?:\s*=\s*(\d+))?$")]
    private static partial Regex JobEntryRegex();
    [GeneratedRegex(@"export_constant\((JOB_[A-Z0-9_]+)\)")]
    private static partial Regex ExportedJobRegex();
    [GeneratedRegex("export_constant2\\(\"Job_([^\"]+)\",\\s*(JOB_[A-Z0-9_]+)\\)")]
    private static partial Regex JobAliasRegex();
    [GeneratedRegex(@"^  - Id: (\d+)(?:\s+#.*)?$")]
    private static partial Regex SkillIdRegex();
    [GeneratedRegex(@"^    Name: ([A-Za-z0-9_]+)(?:\s+#.*)?$")]
    private static partial Regex SkillNameRegex();
    [GeneratedRegex(@"^    MaxLevel: (\d+)$")]
    private static partial Regex SkillMaxLevelRegex();
}
