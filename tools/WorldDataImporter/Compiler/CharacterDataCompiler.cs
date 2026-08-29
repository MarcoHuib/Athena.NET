using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Athena.WorldCompiler.Generation;

internal sealed record CharacterDataSources(string MmoHeader, string ScriptConstants, string JobExperience, string JobBasePoints, string JobStats, string StatPoints, string SkillDatabase, string SkillTree, string PlayerConfig);
internal sealed record CharacterDataArtifact(string RelativePath, string Source);
internal sealed record CharacterDataCompilation(IReadOnlyList<CharacterDataArtifact> Artifacts, CharacterDataCounts Counts, IReadOnlyList<string> Exclusions);
internal sealed record CharacterDataCounts(int NumericJobIdentitiesDiscovered, int GeneratedJobDefinitions, int JobIdsWithProgression, int UniqueProgressionDefinitions, int CanonicalSkills, int DirectSkillTrees, int EffectiveSkillTrees);

internal static partial class CharacterDataCompiler
{
    private sealed record JobIdentity(ushort Id, string Name, string EnumName)
    {
        // Readable PascalCase C# identifier for this job, computed once and reused for the
        // JobClass enum member and every generated Job_<name> static field. Never re-derived
        // by guessing from the identifier elsewhere - the pinned rAthena canonical name
        // (Name, e.g. "Rune_Knight") stays the single source of truth and is preserved
        // verbatim through JobClassNames.
        internal string CSharpIdentifier { get; } = SanitizeIdentifier(Name);
    }
    // Pinned JobDatabase::parseBodyNode's "exists" flag (src/map/pc.cpp) means these four
    // factors only reset to their rAthena defaults the FIRST time a job_id is seen across
    // job_stats.yml's repeated Jobs blocks; a later block that omits a field leaves the
    // previously-set value untouched rather than resetting it. hp_increase/sp_increase
    // default to 500/100 respectively; hp_factor/sp_factor default to 0.
    private sealed class HpSpFactors
    {
        internal uint HpFactor; internal uint HpIncrease = 500; internal uint SpFactor; internal uint SpIncrease = 100;
    }
    private sealed class ProgressionBuilder(JobIdentity job)
    {
        internal JobIdentity Job { get; } = job;
        internal ushort? MaxBaseLevel, MaxJobLevel;
        internal SortedDictionary<ushort, ulong>? BaseExperience, JobExperience;
        internal SortedDictionary<ushort, ulong> BaseHp = [], BaseSp = [];
        internal readonly HpSpFactors Factors = new();
        internal bool HasJobStatsBlock;
        internal readonly SortedDictionary<ushort, StatBonus> Bonuses = [];
    }
    private readonly record struct StatBonus(int Str, int Agi, int Vit, int Int, int Dex, int Luk)
    {
        internal StatBonus Add(StatBonus value) => new(Str + value.Str, Agi + value.Agi, Vit + value.Vit, Int + value.Int, Dex + value.Dex, Luk + value.Luk);
    }
    private sealed record Progression(JobIdentity Job, ushort MaxBaseLevel, ushort MaxJobLevel, ulong[] BaseExperience, ulong[] JobExperience, uint[] BaseHp, uint[] BaseSp, uint[] StatPoints, uint[] Str, uint[] Agi, uint[] Vit, uint[] Int, uint[] Dex, uint[] Luk, ushort MaxBaseStat, string DataKey);
    private sealed record Skill(ushort Id, string Name, ushort MaxLevel, IReadOnlyList<uint> SpCostByLevel, IReadOnlyList<short> RangeByLevel, bool IsQuest, bool IsWedding, bool IsSpirit, ushort Inf, bool AlterRangeVulture, bool AlterRangeSnakeEye, bool AlterRangeShadowJump, bool AlterRangeRadius, bool AlterRangeResearchTrap);
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
        // Pinned pc_jobid2mapid (src/map/pc.cpp): the exact, fixed set of job identities whose
        // mapid resolves to MAPID_SUMMONER (Summoner, Baby_Summoner) or matches
        // MAPID_SUPER_NOVICE under MAPID_SECONDMASK (Super_Novice, Super_Baby, and the
        // MAPID_THIRDMASK-only-widened Super_Novice_E/Super_Baby_E, whose extra JOBL_THIRD
        // bit falls outside SECONDMASK so they still match). Resolved by canonical name
        // through the same alias table job_stats.yml/job_basepoints.yml use, not by a
        // hand-copied numeric ID list, so a future pinned renumbering cannot silently
        // desync this from the job identity table.
        var summonerJobIds = new[] { "Summoner", "Baby_Summoner" }.Where(aliases.ContainsKey).Select(name => aliases[name].Id).ToHashSet();
        var superNoviceJobIds = new[] { "Super_Novice", "Super_Baby", "Super_Novice_E", "Super_Baby_E" }.Where(aliases.ContainsKey).Select(name => aliases[name].Id).ToHashSet();
        var maxParametersByCategory = ParsePlayerConfigMaxParameters(sources.PlayerConfig);
        var progressions = BuildProgressions(builders.Values, statPoints, summonerJobIds, superNoviceJobIds, maxParametersByCategory);
        var skills = ParseSkills(sources.SkillDatabase);
        var skillsByName = skills.ToDictionary(skill => skill.Name, StringComparer.OrdinalIgnoreCase);
        var directTrees = ParseTrees(sources.SkillTree, aliases, skillsByName);
        var progressionByJob = progressions.ToDictionary(item => item.Job.Id);
        var effectiveTrees = ResolveTrees(directTrees, progressionByJob);

        var includedIds = progressions.Select(item => item.Job.Id).Concat(directTrees.Select(item => item.Job.Id)).ToHashSet();
        var jobs = identities.Where(job => includedIds.Contains(job.Id)).OrderBy(job => job.Id).ToArray();
        var exclusions = identities.Where(job => !includedIds.Contains(job.Id)).OrderBy(job => job.Id).Select(job => $"{job.Id} {job.Name}: no complete progression definition and no Renewal skill-tree declaration.").ToArray();
        ValidateCrossRegistry(jobs, progressions, skills, directTrees, effectiveTrees);
        ValidateIdentifierUniqueness(jobs);

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
                    ApplyLevels(node, "BaseHp", "Hp", values => { foreach (var pair in values) target.BaseHp[pair.Key] = pair.Value; }, context);
                    ApplyLevels(node, "BaseSp", "Sp", values => { foreach (var pair in values) target.BaseSp[pair.Key] = pair.Value; }, context);
                }
                if (includeBonuses)
                {
                    // Pinned JobDatabase::parseBodyNode (src/map/pc.cpp): a field present in this
                    // block overwrites the running value; an absent field only resets to the
                    // rAthena default the first time this job_id is ever seen ("exists" flag).
                    var firstBlockForJob = !target.HasJobStatsBlock;
                    target.HasJobStatsBlock = true;
                    ApplyFactor(node, "HpFactor", firstBlockForJob, value => target.Factors.HpFactor = value, () => target.Factors.HpFactor = 0, context);
                    ApplyFactor(node, "HpIncrease", firstBlockForJob, value => target.Factors.HpIncrease = value, () => target.Factors.HpIncrease = 500, context);
                    ApplyFactor(node, "SpFactor", firstBlockForJob, value => target.Factors.SpFactor = value, () => target.Factors.SpFactor = 0, context);
                    ApplyFactor(node, "SpIncrease", firstBlockForJob, value => target.Factors.SpIncrease = value, () => target.Factors.SpIncrease = 100, context);
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
    private static void ApplyFactor(SimpleYamlMap map, string key, bool firstBlockForJob, Action<uint> apply, Action applyDefault, string context)
    {
        if (map.Optional(key) is { } node) apply(checked((uint)node.ULong($"{context}.{key}")));
        else if (firstBlockForJob) applyDefault();
    }
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

    // Pinned pc_jobid2mapid (src/map/pc.cpp) - the exact fixed set of numeric job IDs whose
    // calc_basehp/calc_basesp mapid category triggers an additional adjustment beyond the
    // plain HpFactor/HpIncrease/SpFactor/SpIncrease formula. Renewal never compiles the
    // Ninja/Gunslinger branch (#ifndef RENEWAL guards only the HP side; even SP's branch is
    // moot here because this project only ever loads db/re/* Renewal data, never those two
    // mapid categories' pre-Renewal SP override path is exercised against non-Renewal data);
    // Summoner (+50% HP and SP) and Super Novice (level 99/150 HP bonus) are the two
    // adjustments that DO apply in Renewal and are implemented below.
    private static IReadOnlyList<Progression> BuildProgressions(IEnumerable<ProgressionBuilder> builders, uint[] globalStatPoints, IReadOnlyCollection<ushort> summonerJobIds, IReadOnlyCollection<ushort> superNoviceJobIds, IReadOnlyDictionary<JobParameterCategory, ushort> maxParametersByCategory)
    {
        var result = new List<Progression>();
        foreach (var builder in builders.OrderBy(item => item.Job.Id))
        {
            if (builder.MaxBaseLevel is null || builder.MaxJobLevel is null || builder.BaseExperience is null || builder.JobExperience is null) continue;
            var maxBase = builder.MaxBaseLevel.Value; var maxJob = builder.MaxJobLevel.Value;
            var baseExp = Complete(builder.BaseExperience, maxBase, builder.Job.Name, "BaseExp");
            var jobExp = Complete(builder.JobExperience, maxJob, builder.Job.Name, "JobExp");
            var mapidCategory = summonerJobIds.Contains(builder.Job.Id) ? MapidCategory.Summoner : superNoviceJobIds.Contains(builder.Job.Id) ? MapidCategory.SuperNovice : MapidCategory.None;
            var hp = ResolveBaseHpSp(builder.BaseHp, maxBase, builder.Factors, mapidCategory, isHp: true);
            var sp = ResolveBaseHpSp(builder.BaseSp, maxBase, builder.Factors, mapidCategory, isHp: false);
            if (globalStatPoints.Length <= maxBase) throw new ArgumentException($"db/re/statpoint.yml does not cover {builder.Job.Name} max base level {maxBase}.");
            var statPoints = globalStatPoints[..(maxBase + 1)];
            var stats = Enumerable.Range(0, 6).Select(_ => new uint[maxJob + 1]).ToArray();
            var cumulative = new StatBonus();
            for (ushort level = 1; level <= maxJob; level++)
            {
                cumulative = cumulative.Add(builder.Bonuses.GetValueOrDefault(level));
                stats[0][level] = checked((uint)cumulative.Str); stats[1][level] = checked((uint)cumulative.Agi); stats[2][level] = checked((uint)cumulative.Vit); stats[3][level] = checked((uint)cumulative.Int); stats[4][level] = checked((uint)cumulative.Dex); stats[5][level] = checked((uint)cumulative.Luk);
            }
            var maxBaseStat = JobParameterCategoryMaxStat(ResolveJobParameterCategory(builder.Job.Id), maxParametersByCategory);
            var key = HashKey(maxBase, maxJob, baseExp, jobExp, hp, sp, statPoints, stats, maxBaseStat);
            result.Add(new(builder.Job, maxBase, maxJob, baseExp, jobExp, hp, sp, statPoints, stats[0], stats[1], stats[2], stats[3], stats[4], stats[5], maxBaseStat, key));
        }
        return result;
    }

    private static ulong[] Complete(SortedDictionary<ushort, ulong> values, ushort max, string job, string section)
    {
        var result = new ulong[max + 1];
        for (ushort level = 1; level <= max; level++) result[level] = values.TryGetValue(level, out var value) && value > 0 ? value : throw new ArgumentException($"{job} {section} is missing level {level}.");
        return result;
    }
    private enum MapidCategory { None, Summoner, SuperNovice }

    // Pinned pc_maxparameter's job-category classification (src/map/pc.cpp:14335-14407),
    // driven entirely by pc_jobid2mapid's JOBL_BABY/JOBL_THIRD/JOBL_UPPER/JOBL_FOURTH bits and
    // the three special-cased mapid ranges (Summoner, Kagerou/Oboro/Rebellion "Extended",
    // pc_is_trait_job's primary-4th/upper-expanded-2nd "Fourth"). This project does not model
    // rAthena's full uint64 JOBL_*/MAPID_* bitmask machinery at runtime (it has no other
    // consumer), so the classification is ported once, offline, as this fixed table keyed by
    // the exact pinned numeric Job ID (src/common/mmo.hpp e_job) - never inferred from a job's
    // display/enum name (e.g. a "Baby_"/"_High"/"2" naming heuristic), because name patterns
    // are not the actual source authority and could silently diverge from pc_jobid2mapid's
    // real switch. An id with no entry here means pc_jobid2mapid has no case for it (falls to
    // its `default: return -1`) and must fail generation loudly rather than default to Normal.
    //
    // The "2" gender/appearance-variant job IDs (Knight2, RuneKnightT2, DragonKnight2, ...)
    // are NOT reachable through pc_jobid2mapid's switch at all - pinned job_name (pc.cpp)
    // confirms they render the identical job name as their base id and job_stats.yml's Jobs
    // blocks always flag them alongside their base (e.g. "Knight: true" / "Knight2: true" in
    // the very same block, db/re/job_stats.yml:368-369), so they share their base job's every
    // stat rule including MaxStats. Each is therefore keyed here to its base id's category,
    // not given an independent pc_jobid2mapid resolution that does not exist in pinned source.
    private enum JobParameterCategory { Normal, Trans, Third, ThirdTrans, Baby, BabyThird, Extended, Fourth, Summoner }

    // conf/battle/player.conf's own key naming: each JobParameterCategory maps to exactly one
    // max_*_parameter key. This is a fixed structural mapping (which config key backs which
    // category), not a configuration VALUE - the values themselves are parsed from the pinned
    // conf source at compile time by ParsePlayerConfigMaxParameters, never hardcoded here. See
    // conf/battle/player.conf:104-121 for the key block this mirrors.
    private static readonly IReadOnlyDictionary<JobParameterCategory, string> JobParameterCategoryConfigKey = new Dictionary<JobParameterCategory, string>
    {
        [JobParameterCategory.Normal] = "max_parameter",
        [JobParameterCategory.Trans] = "max_trans_parameter",
        [JobParameterCategory.Third] = "max_third_parameter",
        [JobParameterCategory.ThirdTrans] = "max_third_trans_parameter",
        [JobParameterCategory.Baby] = "max_baby_parameter",
        [JobParameterCategory.BabyThird] = "max_baby_third_parameter",
        [JobParameterCategory.Extended] = "max_extended_parameter",
        [JobParameterCategory.Fourth] = "max_fourth_parameter",
        [JobParameterCategory.Summoner] = "max_summoner_parameter",
    };

    // Parses ONLY the max_*_parameter keys JobParameterCategoryConfigKey requires out of pinned
    // conf/battle/player.conf's plain "key: value" / "// comment" line format (see that file's
    // own header comment for the format). This is the actual configuration VALUE source - the
    // effective runtime config as shipped, not src/map/battle.cpp's compiled-in fallback
    // defaults, which this file overrides at load time. Every required key must be present
    // exactly once with a valid positive ushort value; a missing, duplicated, malformed, zero,
    // or out-of-ushort-range value fails generation loudly rather than silently defaulting -
    // this table is config VALUES, not classification logic, so a config drift must be visible
    // immediately rather than papered over.
    private static IReadOnlyDictionary<JobParameterCategory, ushort> ParsePlayerConfigMaxParameters(string playerConf)
    {
        var required = JobParameterCategoryConfigKey.Values.ToHashSet(StringComparer.Ordinal);
        var found = new Dictionary<string, ushort>(StringComparer.Ordinal);
        foreach (var rawLine in playerConf.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal)) continue;
            var commentIndex = line.IndexOf("//", StringComparison.Ordinal);
            if (commentIndex >= 0) line = line[..commentIndex].TrimEnd();
            var separatorIndex = line.IndexOf(':');
            if (separatorIndex < 0) continue;
            var key = line[..separatorIndex].Trim();
            if (!required.Contains(key)) continue;
            var value = line[(separatorIndex + 1)..].Trim();
            if (!ushort.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) || parsed == 0)
                throw new ArgumentException($"conf/battle/player.conf key '{key}' has a malformed, zero, or out-of-ushort-range value '{value}'.");
            if (!found.TryAdd(key, parsed))
                throw new ArgumentException($"conf/battle/player.conf declares key '{key}' more than once.");
        }
        var missing = required.Where(key => !found.ContainsKey(key)).OrderBy(key => key, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0) throw new ArgumentException($"conf/battle/player.conf is missing required key(s): {string.Join(", ", missing)}.");
        return JobParameterCategoryConfigKey.ToDictionary(pair => pair.Key, pair => found[pair.Value]);
    }

    private static ushort JobParameterCategoryMaxStat(JobParameterCategory category, IReadOnlyDictionary<JobParameterCategory, ushort> parsedMaxParameters) =>
        parsedMaxParameters.TryGetValue(category, out var value) ? value : throw new NotSupportedException($"Unhandled job parameter category {category}.");

    private static JobParameterCategory ResolveJobParameterCategory(ushort jobId) => jobId switch
    {
        // Novice And 1-1 Jobs / 2-1 Jobs / 2-2 Jobs (MAPID_FIRSTMASK..MAPID_SECONDMASK, no
        // JOBL_BABY/THIRD/UPPER bit, not Summoner/Kagerou/Oboro/Rebellion) => max_parameter.
        0 or 1 or 2 or 3 or 4 or 5 or 6 or 7 or 8 or 9 or 10 or 11 or 12 or 13 or 14 or 15 or 16 or 17 or 18 or 19 or 20 or 21 or 23 or 24 or 25
            => JobParameterCategory.Normal,
        // Trans Novice/1-1/2-1/2-2 (JOBL_UPPER, no JOBL_THIRD) => max_trans_parameter.
        >= 4001 and <= 4022 => JobParameterCategory.Trans,
        // Baby Novice/1-1/2-1/2-2 (JOBL_BABY, no JOBL_THIRD) => max_baby_parameter.
        >= 4023 and <= 4045 => JobParameterCategory.Baby,
        // Taekwon/StarGladiator/StarGladiator2/SoulLinker/Gangsi/DeathKnight/DarkCollector -
        // ordinary MAPID_FIRSTMASK/SECONDMASK/2-2 ids with none of the special bits/ranges
        // above => max_parameter.
        >= 4046 and <= 4052 => JobParameterCategory.Normal,
        // Rune_Knight..Guillotine_Cross (3-1, JOBL_THIRD only - pc_is_primary_third's first
        // MAPID_THIRDMASK range) => max_third_parameter.
        >= 4054 and <= 4059 => JobParameterCategory.Third,
        // Rune_Knight_T..Guillotine_Cross_T (3-1 trans: JOBL_THIRD|JOBL_UPPER) =>
        // max_third_trans_parameter.
        >= 4060 and <= 4065 => JobParameterCategory.ThirdTrans,
        // Royal_Guard..Shadow_Chaser (3-2, JOBL_THIRD only - pc_is_primary_third's second
        // MAPID_THIRDMASK range) => max_third_parameter.
        >= 4066 and <= 4072 => JobParameterCategory.Third,
        // Royal_Guard_T..Shadow_Chaser_T (3-2 trans) => max_third_trans_parameter.
        >= 4073 and <= 4079 => JobParameterCategory.ThirdTrans,
        // Rune_Knight2/Royal_Guard2/Ranger2/Mechanic2 (3rd non-trans "2" variant; base id
        // shares job_stats.yml Jobs-block membership with the plain 3rd id - see this
        // classifier's own doc comment) => max_third_parameter.
        4080 or 4082 or 4084 or 4086 => JobParameterCategory.Third,
        // Rune_Knight_T2/Royal_Guard_T2/Ranger_T2/Mechanic_T2 ("2" variant of the 3rd-trans
        // id) => max_third_trans_parameter.
        4081 or 4083 or 4085 or 4087 => JobParameterCategory.ThirdTrans,
        // Baby_Rune_Knight..Baby_Shadow_Chaser / Baby_Rune_Knight2../Super_Baby_E (JOBL_BABY|
        // JOBL_THIRD) => max_baby_third_parameter.
        >= 4096 and <= 4112 => JobParameterCategory.BabyThird,
        // Super_Novice_E (JOBL_THIRD only, MAPID_SUPER_NOVICE_E) => max_third_parameter.
        4190 => JobParameterCategory.Third,
        // Super_Baby_E (JOBL_BABY|JOBL_THIRD) => max_baby_third_parameter.
        4191 => JobParameterCategory.BabyThird,
        // Kagerou/Oboro (MAPID_SECONDMASK == MAPID_KAGEROUOBORO special case) =>
        // max_extended_parameter.
        4211 or 4212 => JobParameterCategory.Extended,
        // Rebellion (MAPID_SECONDMASK == MAPID_REBELLION special case) =>
        // max_extended_parameter.
        4215 => JobParameterCategory.Extended,
        // Summoner (MAPID_FIRSTMASK == MAPID_SUMMONER special case, checked before the baby
        // branch only matters when JOBL_BABY is also set - see Baby_Summoner below) =>
        // max_summoner_parameter.
        4218 => JobParameterCategory.Summoner,
        // Baby_Summoner: JOBL_BABY is checked FIRST in pinned pc.cpp:14340-14350 ("Always
        // check babies first"), so this is max_baby_parameter, NOT max_summoner_parameter,
        // even though its mapid also matches MAPID_SUMMONER under MAPID_FIRSTMASK.
        4220 => JobParameterCategory.Baby,
        // Baby_Ninja/Baby_Kagerou/Baby_Oboro/Baby_Taekwon/Baby_StarGladiator/
        // Baby_SoulLinker/Baby_Gunslinger/Baby_Rebellion (JOBL_BABY; the underlying
        // Kagerou/Oboro/Rebellion Extended special-case only applies without JOBL_BABY, same
        // "babies first" rule as Baby_Summoner) => max_baby_parameter.
        >= 4222 and <= 4229 => JobParameterCategory.Baby,
        // Baby_StarGladiator2 (JOBL_BABY) => max_baby_parameter.
        4238 => JobParameterCategory.Baby,
        // Star_Emperor/Soul_Reaper (3-1/3-2, JOBL_THIRD only) => max_third_parameter.
        4239 or 4240 => JobParameterCategory.Third,
        // Baby_Star_Emperor/Baby_Soul_Reaper (JOBL_BABY|JOBL_THIRD) =>
        // max_baby_third_parameter.
        4241 or 4242 => JobParameterCategory.BabyThird,
        // Star_Emperor2 ("2" variant of Star_Emperor) => max_third_parameter.
        4243 => JobParameterCategory.Third,
        // Baby_Star_Emperor2 ("2" variant of Baby_Star_Emperor) => max_baby_third_parameter.
        4244 => JobParameterCategory.BabyThird,
        // Dragon_Knight..Trouvere (4-1/4-2, pc_is_primary_fourth's two MAPID_FOURTHMASK
        // ranges) => max_fourth_parameter.
        >= 4252 and <= 4264 => JobParameterCategory.Fourth,
        // Windhawk2/Meister2/DragonKnight2/ImperialGuard2 ("2" variants of 4-1/4-2 ids) =>
        // max_fourth_parameter.
        >= 4278 and <= 4281 => JobParameterCategory.Fourth,
        // Sky_Emperor/Soul_Ascetic (pc_is_upper_expanded_second's FOURTHMASK==SKY_EMPEROR/
        // SOUL_ASCETIC case) => max_fourth_parameter.
        4302 or 4303 => JobParameterCategory.Fourth,
        // Shinkiro/Shiranui/Night_Watch (pc_is_upper_expanded_second's
        // THIRDMASK==SHINKIROSHIRANUI/NIGHT_WATCH case, NOT primary-4th) =>
        // max_extended_parameter.
        4304 or 4305 or 4306 => JobParameterCategory.Extended,
        // Hyper_Novice (pc_is_upper_expanded_second's FOURTHMASK==HYPER_NOVICE case) =>
        // max_fourth_parameter.
        4307 => JobParameterCategory.Fourth,
        // Spirit_Handler (pc_is_upper_expanded_second's SECONDMASK==SPIRIT_HANDLER case) =>
        // max_fourth_parameter (pc_is_trait_job = primary_fourth || upper_expanded_second).
        4308 => JobParameterCategory.Fourth,
        // Sky_Emperor2 ("2" variant of Sky_Emperor) => max_fourth_parameter.
        4316 => JobParameterCategory.Fourth,
        _ => throw new ArgumentException($"Job id {jobId} has no pinned pc_jobid2mapid classification for a Status Point cap. Extend ResolveJobParameterCategory with its exact pc_jobid2mapid case before generating progression data for this job."),
    };

    // Matches pinned JobDatabase::loadingFinished (src/map/pc.cpp): for every base level,
    // an explicit table row wins; a level the table never set (job->base_hp[j] == 0) is
    // resolved through calc_basehp/calc_basesp instead. A table row for a level beyond
    // maxBase is silently discarded, mirroring the pinned parse loop's
    // "if (level > job->max_base_level) continue;".
    private static uint[] ResolveBaseHpSp(SortedDictionary<ushort, ulong> tableRows, ushort maxBase, HpSpFactors factors, MapidCategory mapid, bool isHp)
    {
        var result = new uint[maxBase + 1];
        for (ushort level = 1; level <= maxBase; level++)
        {
            var fromTable = tableRows.TryGetValue(level, out var value) ? checked((uint)value) : 0;
            result[level] = fromTable != 0 ? fromTable : isHp ? CalcBaseHp(level, factors, mapid) : CalcBaseSp(level, factors, mapid);
        }
        return result;
    }

    // Pinned JobDatabase::calc_basehp (src/map/pc.cpp). The Ninja/Gunslinger branch is
    // guarded by #ifndef RENEWAL in pinned source and is never compiled for Renewal builds;
    // this project only ever loads db/re/* Renewal data, so that branch is intentionally
    // omitted here, not merely unimplemented.
    private static uint CalcBaseHp(ushort level, HpSpFactors factors, MapidCategory mapid)
    {
        double baseHp = 35.0;
        baseHp += Math.Floor(level * (factors.HpIncrease / 100.0));
        for (var i = 2; i <= level; i++) baseHp += Math.Floor(factors.HpFactor / 100.0 * i + 0.5);
        if (mapid == MapidCategory.Summoner) baseHp += Math.Floor(baseHp / 2 + 0.5);
        else if (mapid == MapidCategory.SuperNovice)
        {
            if (level >= 99) baseHp += 2000.0;
            if (level >= 150) baseHp += 2000.0;
        }
        return checked((uint)baseHp);
    }

    // Pinned JobDatabase::calc_basesp (src/map/pc.cpp). Unlike calc_basehp's HP branch, the
    // Ninja/Gunslinger SP adjustment carries NO #ifndef RENEWAL guard, so pinned rAthena DOES
    // apply it in Renewal. It is deliberately NOT implemented here: Ninja/Baby_Ninja and
    // Gunslinger/Baby_Gunslinger each declare their own explicit BaseSp rows for every base
    // level through their max_base_level in db/re/job_basepoints.yml (verified: these are
    // the four job_stats.yml Jobs-block members whose base-points block is fully dense, not
    // one of the 24 sparse fourth-job entries), so job->base_sp[j] is never 0 for them and
    // calc_basesp's branch is never reached by pinned loadingFinished's `if (base_sp[j]==0)`
    // gate for these jobs. If a future job_basepoints.yml revision ever leaves a Ninja/
    // Gunslinger base level's BaseSp row unset, this omission becomes load-bearing and must
    // be revisited; ResolveBaseHpSp has no way to detect that condition today.
    private static uint CalcBaseSp(ushort level, HpSpFactors factors, MapidCategory mapid)
    {
        double baseSp = 10.0;
        baseSp += Math.Floor(level * (factors.SpIncrease / 100.0));
        for (var i = 2; i <= level; i++) baseSp += Math.Floor(factors.SpFactor / 100.0 * i + 0.5);
        if (mapid == MapidCategory.Summoner) baseSp += Math.Floor(baseSp / 2 + 0.5);
        return checked((uint)baseSp);
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

    // db/re/skill_db.yml's Requires.SpCost is genuinely level-dependent: either a bare scalar
    // (applies at every level, e.g. SM_MAGNUM's flat SpCost: 30) or an explicit per-level
    // "- Level: N / Amount: X" sequence (e.g. SM_BASH has 10 distinct per-level costs). A skill
    // with no Requires block at all (e.g. NV_BASIC) has no SP cost. This scanner reproduces that
    // shape exactly rather than collapsing it into one number; see ai/world-data.md.
    //
    // Pinned db/re/skill_db.yml's Flags block marks skills with conditional-acquisition source
    // facts: IsQuest (e.g. NV_FIRSTAID, NV_TRICKDEAD), IsWedding (WE_* family), IsSpirit (skills
    // that only appear while an active SC_SPIRIT status is present - pc_calc_skilltree/
    // pc_check_skilltree, pc.cpp:2735-2740/2862-2867), and skill_get_range2's range-altering flags
    // (skill.cpp:324-365 - see IroSkillRangeResolver). All are scanned narrowly as source facts
    // only via the exact recognized name switch in ParseSkills below - an unrecognized Flags entry
    // is silently ignored (this project does not model every skill_db.yml Flags key, only the
    // ones actually consumed by 0x0B32/skill-tree gating); CharacterSkillService/
    // IroSkillRangeResolver (not this compiler) decide current runtime behavior from these facts
    // plus server config/character state - see ai/world-data.md. IsGuild is deliberately NOT
    // tracked here: confirmed absent from the player skill-tree tree-walk gate in pinned source.

    // Pinned skill_get_inf's source: skill_db.yml's TargetType field, mapped to the e_skill_inf
    // bitmask via the pinned "INF_" + TargetType + "_SKILL" constant-name convention (skill.cpp,
    // SkillDatabase::parseBodyNode). Every TargetType value actually present in the pinned skill
    // database is covered; absent TargetType defaults to 0 (INF_PASSIVE_SKILL), matching pinned
    // source's zero-initialized struct default.
    private static readonly IReadOnlyDictionary<string, ushort> TargetTypeToInf = new Dictionary<string, ushort>(StringComparer.Ordinal)
    {
        ["Attack"] = 0x01,  // INF_ATTACK_SKILL
        ["Ground"] = 0x02,  // INF_GROUND_SKILL
        ["Self"] = 0x04,    // INF_SELF_SKILL
        ["Support"] = 0x10, // INF_SUPPORT_SKILL
        ["Trap"] = 0x20,    // INF_TRAP_SKILL
    };

    private static IReadOnlyList<Skill> ParseSkills(string yaml)
    {
        var result = new List<Skill>();
        ushort? id = null; string? name = null; ushort maxLevel = 0; ushort inf = 0;
        var isQuest = false; var isWedding = false; var isSpirit = false;
        var alterRangeVulture = false; var alterRangeSnakeEye = false; var alterRangeShadowJump = false; var alterRangeRadius = false; var alterRangeResearchTrap = false;
        uint? spCostScalar = null; SortedDictionary<ushort, uint>? spCostByLevel = null; ushort? pendingSpCostLevel = null;
        short? rangeScalar = null; SortedDictionary<ushort, short>? rangeByLevel = null; ushort? pendingRangeLevel = null;
        var inRequires = false; var inSpCostList = false; var inFlags = false; var inRangeList = false;
        void Finish()
        {
            if (id is null) return;
            if (name is null) throw new ArgumentException($"db/re/skill_db.yml skill {id} has no Name.");
            IReadOnlyList<uint> spCost;
            if (spCostByLevel is { Count: > 0 })
            {
                if (spCostByLevel.Keys.Min() != 1 || spCostByLevel.Keys.Max() != spCostByLevel.Count)
                    throw new ArgumentException($"db/re/skill_db.yml skill {id} ('{name}') has a non-contiguous SpCost level list.");
                spCost = [.. spCostByLevel.Values];
            }
            else if (spCostScalar is { } scalar) spCost = Enumerable.Repeat(scalar, maxLevel).ToArray();
            else spCost = [];
            IReadOnlyList<short> range;
            if (rangeByLevel is { Count: > 0 })
            {
                if (rangeByLevel.Keys.Min() != 1 || rangeByLevel.Keys.Max() != rangeByLevel.Count)
                    throw new ArgumentException($"db/re/skill_db.yml skill {id} ('{name}') has a non-contiguous Range level list.");
                range = [.. rangeByLevel.Values];
            }
            else if (rangeScalar is { } rangeValue) range = Enumerable.Repeat(rangeValue, maxLevel).ToArray();
            else range = [];
            result.Add(new(id.Value, name, maxLevel, spCost, range, isQuest, isWedding, isSpirit, inf, alterRangeVulture, alterRangeSnakeEye, alterRangeShadowJump, alterRangeRadius, alterRangeResearchTrap));
            id = null; name = null; maxLevel = 0; inf = 0; isQuest = false; isWedding = false; isSpirit = false;
            alterRangeVulture = false; alterRangeSnakeEye = false; alterRangeShadowJump = false; alterRangeRadius = false; alterRangeResearchTrap = false;
            spCostScalar = null; spCostByLevel = null; pendingSpCostLevel = null;
            rangeScalar = null; rangeByLevel = null; pendingRangeLevel = null;
            inRequires = false; inSpCostList = false; inFlags = false; inRangeList = false;
        }
        foreach (var line in yaml.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n'))
        {
            var match = SkillIdRegex().Match(line);
            if (match.Success) { Finish(); id = ushort.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            if (id is null) continue;
            match = SkillNameRegex().Match(line); if (match.Success) { name = match.Groups[1].Value; continue; }
            match = SkillMaxLevelRegex().Match(line); if (match.Success) { maxLevel = ushort.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            match = SkillTargetTypeRegex().Match(line);
            if (match.Success)
            {
                if (!TargetTypeToInf.TryGetValue(match.Groups[1].Value, out inf))
                    throw new ArgumentException($"db/re/skill_db.yml skill {id} ('{name}') has unrecognized TargetType '{match.Groups[1].Value}'.");
                continue;
            }
            match = SkillRangeScalarRegex().Match(line); if (match.Success) { rangeScalar = short.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); inRangeList = false; continue; }
            match = SkillRangeListRegex().Match(line); if (match.Success) { inRangeList = true; rangeByLevel = []; continue; }
            match = SkillFlagsRegex().Match(line); if (match.Success) { inFlags = true; inRequires = false; inSpCostList = false; inRangeList = false; continue; }
            match = SkillRequiresRegex().Match(line); if (match.Success) { inRequires = true; inFlags = false; inSpCostList = false; inRangeList = false; continue; }
            if (inRangeList)
            {
                // Range's per-level list is a TOP-LEVEL field (4sp), so any other top-level field
                // (Requires/Flags/Hit/etc.) ends it - reuse the same "other field at this indent"
                // sentinel already proven for Requires/Flags below.
                if (SkillOtherFieldAtRequiresIndentRegex().IsMatch(line)) { inRangeList = false; }
                else
                {
                    match = SkillRangeLevelRegex().Match(line); if (match.Success) { pendingRangeLevel = ushort.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
                    match = SkillRangeSizeRegex().Match(line);
                    if (match.Success && pendingRangeLevel is { } rangeLevel) { (rangeByLevel ??= [])[rangeLevel] = short.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); pendingRangeLevel = null; }
                    continue;
                }
            }
            if (inFlags)
            {
                if (SkillOtherFieldAtRequiresIndentRegex().IsMatch(line)) { inFlags = false; }
                else
                {
                    match = SkillFlagEntryRegex().Match(line);
                    if (match.Success && bool.Parse(match.Groups[2].Value))
                    {
                        switch (match.Groups[1].Value)
                        {
                            case "IsQuest": isQuest = true; break;
                            case "IsWedding": isWedding = true; break;
                            case "IsSpirit": isSpirit = true; break;
                            case "AlterRangeVulture": alterRangeVulture = true; break;
                            case "AlterRangeSnakeEye": alterRangeSnakeEye = true; break;
                            case "AlterRangeShadowJump": alterRangeShadowJump = true; break;
                            case "AlterRangeRadius": alterRangeRadius = true; break;
                            case "AlterRangeResearchTrap": alterRangeResearchTrap = true; break;
                        }
                    }
                    continue;
                }
            }
            if (!inRequires) continue;
            if (SkillOtherFieldAtRequiresIndentRegex().IsMatch(line)) { inRequires = false; inSpCostList = false; continue; }
            match = SkillSpCostScalarRegex().Match(line); if (match.Success) { spCostScalar = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); inSpCostList = false; continue; }
            if (SkillSpCostListRegex().IsMatch(line)) { inSpCostList = true; spCostByLevel = []; continue; }
            if (!inSpCostList) continue;
            match = SkillSpCostLevelRegex().Match(line); if (match.Success) { pendingSpCostLevel = ushort.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); continue; }
            match = SkillSpCostAmountRegex().Match(line);
            if (match.Success && pendingSpCostLevel is { } level) { (spCostByLevel ??= [])[level] = uint.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); pendingSpCostLevel = null; }
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

    // Every C# reserved keyword (not contextual keywords, which are fine as identifiers).
    // None of the 175 pinned rAthena job names collide with one today, but a future job
    // added upstream might, so this is escaped defensively with '@' rather than assumed away.
    private static readonly HashSet<string> CSharpKeywords =
    [
        "abstract","as","base","bool","break","byte","case","catch","char","checked","class","const","continue","decimal","default","delegate","do","double","else","enum","event","explicit","extern","false","finally","fixed","float","for","foreach","goto","if","implicit","in","int","interface","internal","is","lock","long","namespace","new","null","object","operator","out","override","params","private","protected","public","readonly","ref","return","sbyte","sealed","short","sizeof","stackalloc","static","string","struct","switch","this","throw","true","try","typeof","uint","ulong","unchecked","unsafe","ushort","using","virtual","void","volatile","while",
    ];

    // Strips the underscore separators CanonicalJobName introduces, producing a readable
    // PascalCase C# identifier (e.g. "Rune_Knight_T2" -> "RuneKnightT2"). CanonicalJobName
    // already upper-cases each underscore-delimited segment's first letter, so concatenation
    // alone yields PascalCase without re-deriving casing here.
    private static string SanitizeIdentifier(string canonicalName)
    {
        var identifier = canonicalName.Replace("_", "", StringComparison.Ordinal);
        if (identifier.Length == 0 || char.IsDigit(identifier[0])) identifier = "Job" + identifier;
        return CSharpKeywords.Contains(identifier) ? "@" + identifier : identifier;
    }

    // Fails generation loudly rather than silently merging two source job identities that
    // sanitize to the same C# identifier - see SanitizeIdentifier.
    private static void ValidateIdentifierUniqueness(IReadOnlyList<JobIdentity> jobs)
    {
        var duplicate = jobs.GroupBy(job => job.CSharpIdentifier, StringComparer.Ordinal).FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null) throw new ArgumentException($"Jobs {string.Join(", ", duplicate.Select(job => $"{job.Name} ({job.Id})"))} all sanitize to the same C# identifier '{duplicate.Key}'.");
    }
    private static string Header(string commit, params string[] sources) => "// <auto-generated>\n// Generated by Athena.WorldCompiler.\n// Sources:\n" + string.Concat(sources.Select(source => $"//   legacy/rathena/{source}\n")) + $"// rAthena commit: {commit}\n// Do not edit this file directly.\n// </auto-generated>\n";
    private static string Array<T>(IEnumerable<T> values) where T : IFormattable => "[" + string.Join(", ", values.Select(value => value.ToString(null, CultureInfo.InvariantCulture))) + "]";

    private static string EmitJobs(IReadOnlyList<JobIdentity> jobs, string commit)
    {
        var output = new StringBuilder(Header(commit, "src/common/mmo.hpp", "src/map/script_constants.hpp")).AppendLine("namespace Athena.Net.MapServer.Generated.Jobs;").AppendLine();
        output.AppendLine("// Numeric values are the exact pinned rAthena/client job-class IDs - never renumbered here.");
        output.AppendLine("// Public: flows through the public CharacterProgressionDefinition.JobClass domain field.");
        output.AppendLine("public enum JobClass : ushort").AppendLine("{");
        foreach (var job in jobs) output.Append("    ").Append(job.CSharpIdentifier).Append(" = ").Append(job.Id).AppendLine(",");
        output.AppendLine("}").AppendLine();
        output.AppendLine("// Preserves each job's canonical pinned rAthena source name (e.g. \"Rune_Knight\") for");
        output.AppendLine("// display/logging/lookup - never re-derived by guessing from the enum member name.");
        output.AppendLine("internal static class JobClassNames").AppendLine("{");
        output.AppendLine("    private static readonly IReadOnlyDictionary<JobClass, string> ByJobClass = new Dictionary<JobClass, string>").AppendLine("    {");
        foreach (var job in jobs) output.Append("        [JobClass.").Append(job.CSharpIdentifier).Append("] = \"").Append(job.Name).AppendLine("\",");
        output.AppendLine("    };");
        output.AppendLine("    internal static string GetRathenaName(JobClass jobClass) => ByJobClass.TryGetValue(jobClass, out var value) ? value : throw new NotSupportedException($\"Job class {jobClass} is not generated.\");");
        output.AppendLine("    internal static bool IsDefined(ushort jobClass) => ByJobClass.ContainsKey((JobClass)jobClass);");
        return output.AppendLine("}").ToString();
    }

    private static string EmitProgressions(IReadOnlyList<Progression> progressions, string commit)
    {
        var output = new StringBuilder(Header(commit, "db/re/job_exp.yml", "db/re/job_basepoints.yml", "db/re/job_stats.yml", "db/re/statpoint.yml", "conf/battle/player.conf")).AppendLine("using Athena.Net.MapServer.Generated.Jobs;").AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Progression;").AppendLine("internal static class GeneratedProgressionData").AppendLine("{");
        foreach (var item in progressions) output.Append("    internal static readonly CharacterProgressionDefinition ").Append(item.Job.CSharpIdentifier).Append(" = new(JobClass.").Append(item.Job.CSharpIdentifier).Append(", ").Append(item.MaxBaseLevel).Append(", ").Append(item.MaxJobLevel).Append(", ").Append(Array(item.BaseExperience)).Append(", ").Append(Array(item.JobExperience)).Append(", ").Append(Array(item.BaseHp)).Append(", ").Append(Array(item.BaseSp)).Append(", ").Append(Array(item.StatPoints)).Append(", ").Append(Array(item.Str)).Append(", ").Append(Array(item.Agi)).Append(", ").Append(Array(item.Vit)).Append(", ").Append(Array(item.Int)).Append(", ").Append(Array(item.Dex)).Append(", ").Append(Array(item.Luk)).Append(", ").Append(item.MaxBaseStat).AppendLine(");");
        return output.AppendLine("}").ToString();
    }

    private static string EmitProgressionRegistry(IReadOnlyList<Progression> progressions, string commit)
    {
        var output = new StringBuilder(Header(commit, "db/re/job_exp.yml", "db/re/job_basepoints.yml", "db/re/job_stats.yml", "db/re/statpoint.yml", "conf/battle/player.conf")).AppendLine("using Athena.Net.MapServer.Generated.Jobs;").AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Progression;").AppendLine("internal static class GeneratedProgressionRegistry").AppendLine("{").AppendLine("    private static readonly IReadOnlyDictionary<JobClass, CharacterProgressionDefinition> ByJobClass = new Dictionary<JobClass, CharacterProgressionDefinition>").AppendLine("    {");
        foreach (var item in progressions) output.Append("        [JobClass.").Append(item.Job.CSharpIdentifier).Append("] = GeneratedProgressionData.").Append(item.Job.CSharpIdentifier).AppendLine(",");
        output.AppendLine("    };");
        output.AppendLine("    internal static IEnumerable<CharacterProgressionDefinition> All => ByJobClass.Values;");
        output.AppendLine("    internal static CharacterProgressionDefinition Get(JobClass jobClass) => ByJobClass.TryGetValue(jobClass, out var value) ? value : throw new NotSupportedException($\"Progression data for job class {jobClass} is not generated.\");");
        output.AppendLine("    internal static CharacterProgressionDefinition Get(ushort jobClass) => Get((JobClass)jobClass);");
        return output.AppendLine("}").ToString();
    }

    private static string EmitSkills(IReadOnlyList<Skill> skills, string commit)
    {
        var output = new StringBuilder(Header(commit, "db/re/skill_db.yml")).AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Skills;").AppendLine("internal static class GeneratedSkillRegistry").AppendLine("{").AppendLine("    private static readonly GeneratedSkillDefinition[] Definitions =").AppendLine("    [");
        foreach (var skill in skills)
        {
            var spCost = string.Join(", ", skill.SpCostByLevel);
            var range = string.Join(", ", skill.RangeByLevel);
            var acquisition = $"new({(skill.IsQuest ? "true" : "false")}, {(skill.IsWedding ? "true" : "false")}, {(skill.IsSpirit ? "true" : "false")})";
            var rangeFlags = $"new({(skill.AlterRangeVulture ? "true" : "false")}, {(skill.AlterRangeSnakeEye ? "true" : "false")}, {(skill.AlterRangeShadowJump ? "true" : "false")}, {(skill.AlterRangeRadius ? "true" : "false")}, {(skill.AlterRangeResearchTrap ? "true" : "false")})";
            output.Append("        new(").Append(skill.Id).Append(", \"").Append(skill.Name).Append("\", [").Append(spCost).Append("], [").Append(range).Append("], ").Append(acquisition).Append(", ").Append(skill.Inf).Append(", ").Append(rangeFlags).AppendLine("),");
        }
        return output.AppendLine("    ];").AppendLine("    private static readonly IReadOnlyDictionary<ushort, GeneratedSkillDefinition> ById = Definitions.ToDictionary(value => value.SkillId);").AppendLine("    private static readonly IReadOnlyDictionary<string, GeneratedSkillDefinition> ByName = Definitions.ToDictionary(value => value.Name, StringComparer.Ordinal);").AppendLine("    internal static IReadOnlyList<GeneratedSkillDefinition> All => Definitions;").AppendLine("    internal static GeneratedSkillDefinition GetById(ushort skillId) => ById.TryGetValue(skillId, out var value) ? value : throw new NotSupportedException($\"Skill ID {skillId} is not generated.\");").AppendLine("    internal static GeneratedSkillDefinition GetByName(string name) => ByName.TryGetValue(name, out var value) ? value : throw new NotSupportedException($\"Skill '{name}' is not generated.\");").AppendLine("}").ToString();
    }

    private static string EmitTrees(IReadOnlyList<EffectiveTree> trees, string commit)
    {
        var output = new StringBuilder(Header(commit, "db/re/skill_tree.yml", "db/re/skill_db.yml", "src/common/mmo.hpp")).AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Skills;").AppendLine("internal static class GeneratedSkillTrees").AppendLine("{");
        foreach (var tree in trees)
        {
            output.Append("    internal static readonly GeneratedSkillTreeDefinition ").Append(tree.Direct.Job.CSharpIdentifier).Append(" = new(").Append(tree.Direct.Job.Id).Append(", ").Append(Array(tree.Direct.Parents.Select(parent => parent.Id))).AppendLine(",");
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
        var output = new StringBuilder(Header(commit, "db/re/skill_tree.yml", "db/re/skill_db.yml", "src/common/mmo.hpp")).AppendLine("using Athena.Net.MapServer.Generated.Jobs;").AppendLine("using Athena.Net.MapServer.World;").AppendLine("namespace Athena.Net.MapServer.Generated.Skills;").AppendLine("internal static class GeneratedSkillTreeRegistry").AppendLine("{").AppendLine("    private static readonly IReadOnlyDictionary<JobClass, GeneratedSkillTreeDefinition> ByJobClass = new Dictionary<JobClass, GeneratedSkillTreeDefinition>").AppendLine("    {");
        foreach (var tree in trees) output.Append("        [JobClass.").Append(tree.Direct.Job.CSharpIdentifier).Append("] = GeneratedSkillTrees.").Append(tree.Direct.Job.CSharpIdentifier).AppendLine(",");
        output.AppendLine("    };");
        output.AppendLine("    internal static IEnumerable<GeneratedSkillTreeDefinition> All => ByJobClass.Values;");
        output.AppendLine("    internal static GeneratedSkillTreeDefinition Get(JobClass jobClass) => ByJobClass.TryGetValue(jobClass, out var value) ? value : throw new NotSupportedException($\"Skill tree for job class {jobClass} is not generated.\");");
        output.AppendLine("    internal static GeneratedSkillTreeDefinition Get(ushort jobClass) => Get((JobClass)jobClass);");
        return output.AppendLine("}").ToString();
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
    [GeneratedRegex(@"^    Range: (-?\d+)$")]
    private static partial Regex SkillRangeScalarRegex();
    [GeneratedRegex(@"^    Range:$")]
    private static partial Regex SkillRangeListRegex();
    [GeneratedRegex(@"^      - Level: (\d+)$")]
    private static partial Regex SkillRangeLevelRegex();
    [GeneratedRegex(@"^        Size: (-?\d+)$")]
    private static partial Regex SkillRangeSizeRegex();
    [GeneratedRegex(@"^    TargetType: (\w+)$")]
    private static partial Regex SkillTargetTypeRegex();
    [GeneratedRegex(@"^    Requires:$")]
    private static partial Regex SkillRequiresRegex();
    [GeneratedRegex(@"^    Flags:$")]
    private static partial Regex SkillFlagsRegex();
    [GeneratedRegex(@"^      (\w+): (true|false)$")]
    private static partial Regex SkillFlagEntryRegex();
    [GeneratedRegex(@"^    [A-Za-z][A-Za-z0-9]*:")]
    private static partial Regex SkillOtherFieldAtRequiresIndentRegex();
    [GeneratedRegex(@"^      SpCost: (\d+)$")]
    private static partial Regex SkillSpCostScalarRegex();
    [GeneratedRegex(@"^      SpCost:$")]
    private static partial Regex SkillSpCostListRegex();
    [GeneratedRegex(@"^        - Level: (\d+)$")]
    private static partial Regex SkillSpCostLevelRegex();
    [GeneratedRegex(@"^          Amount: (\d+)$")]
    private static partial Regex SkillSpCostAmountRegex();
}
