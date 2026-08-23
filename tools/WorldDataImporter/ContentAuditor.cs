using System.Text.RegularExpressions;

internal sealed record AuditCategory(string Category, int Count);
internal sealed record TopLevelAudit(int Total, IReadOnlyList<AuditCategory> Categories);
internal sealed record EmbeddedBehaviorAudit(int OnTouch, int OnTouchVariant, int OnClickEquivalent, int OnInit, int TimerOrEvent);
internal sealed record DatabaseBoundaryAudit(string Classification, string Location, string Note);
internal sealed record ConversionAuditReport(int SchemaVersion, string SourceRepository, string SourceCommit, int FilesAnalyzed, TopLevelAudit TopLevel, EmbeddedBehaviorAudit EmbeddedBehavior, IReadOnlyList<DatabaseBoundaryAudit> DatabaseOrOtherConverters, string OverlapNote);

internal static partial class ContentAuditor
{
    public static ConversionAuditReport Audit(IEnumerable<string> roots)
    {
        var files = roots.Select(Path.GetFullPath).SelectMany(root => Directory.EnumerateFiles(root, "*.txt", SearchOption.AllDirectories))
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        var onTouch = 0; var onTouchVariant = 0; var onClick = 0; var onInit = 0; var events = 0;
        foreach (var file in files)
        {
            var scriptDepth = 0;
            foreach (var raw in File.ReadLines(file))
            {
                var line = raw.TrimStart('\uFEFF');
                if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith("//", StringComparison.Ordinal)) continue;
                if (scriptDepth == 0 && line.Contains('\t'))
                {
                    var columns = line.Split('\t');
                    if (columns.Length >= 2)
                    {
                        var directive = columns[1].Trim();
                        Increment(counts, columns[0].Trim() == "function" && directive == "script" ? "function" : Classify(directive, columns.Length > 3 ? columns[3] : ""));
                    }
                }
                var trimmed = line.Trim();
                if (OnTouchRegex().IsMatch(trimmed)) onTouch++;
                if (OnTouchVariantRegex().IsMatch(trimmed)) onTouchVariant++;
                if (trimmed.StartsWith("OnInit:", StringComparison.Ordinal)) onInit++;
                if (EventRegex().IsMatch(trimmed)) events++;
                if (trimmed.StartsWith("OnClick:", StringComparison.Ordinal)) onClick++;
                scriptDepth += line.Count(character => character == '{') - line.Count(character => character == '}');
                if (scriptDepth < 0) scriptDepth = 0;
            }
        }
        var categories = counts.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new AuditCategory(pair.Key, pair.Value)).ToArray();
        return new(1, "rAthena", "6e6bca69b8a2ee03cd744cbc7a78a054a6f376ca", files.Length,
            new(categories.Sum(item => item.Count), categories), new(onTouch, onTouchVariant, Math.Max(onClick, counts.GetValueOrDefault("script:NPC")), onInit, events),
            [new("Mob database definitions", "legacy/rathena/db", "Stats and species data are database-oriented; NPC-tree monster directives are placements/spawns."),
             new("Item/skill/quest databases", "legacy/rathena/db", "Require dedicated database converters, not world-entity conversion.")],
            "WARPNPC is a subset of script declarations; duplicate is a source mechanism whose target may be any content kind. Embedded event labels overlap their containing script declarations.");
    }

    private static string Classify(string directive, string arguments)
    {
        if (directive == "warp") return "warp";
        if (directive == "script") return arguments.Contains("WARPNPC", StringComparison.Ordinal) ? "script:WARPNPC" : "script:NPC";
        if (directive.StartsWith("duplicate(", StringComparison.Ordinal)) return "duplicate";
        if (directive is "shop" or "cashshop" or "pointshop" or "marketshop") return $"shop:{directive}";
        if (directive is "monster" or "boss_monster") return $"spawn:{directive}";
        if (directive == "mapflag") return "mapflag";
        if (directive == "function" || directive.StartsWith("function ", StringComparison.Ordinal)) return "function";
        return "unknown/unclassified";
    }
    private static void Increment(Dictionary<string, int> counts, string key) => counts[key] = counts.GetValueOrDefault(key) + 1;
    [GeneratedRegex(@"^OnTouch:\s*$")] private static partial Regex OnTouchRegex();
    [GeneratedRegex(@"^OnTouch_[^:]*:\s*$")] private static partial Regex OnTouchVariantRegex();
    [GeneratedRegex(@"^On(?:Timer\d+|PCDieEvent|PCLoginEvent|PCLogoutEvent|NPCKillEvent|Clock\d+|Minute\d+|Hour\d+|Day\d+):")]
    private static partial Regex EventRegex();
}
