namespace Athena.Net.Shared.MapIdentity;

// The generic, content-free map canonicalization MECHANISM shared by CharServer and MapServer (compiled into
// each assembly from this single file plus Generated/GeneratedCanonicalMapAliases.cs; see the <Compile
// Include="../Shared/..."> links in both .csproj files). It knows no map names: the explicit alias table is
// generated DATA (WorldDataImporter `generate-canonical-maps`, from tools/WorldDataImporter/canonical-map-
// families.json, validated against the pinned effective map cache).
//
// Pinned rAthena hosts the intro/Academy route as parallel "channel" copies of the same physical maps.
// Athena.NET does not keep channels: players are routed onto the canonical map so all of them share one World
// simulation, one actor-id space and one visibility domain. Lookup is by exact (case-insensitive) table
// membership only - there is no suffix stripping and no name-shape inference, so a map that is not listed in
// the generated table (PvP maps, `1@...` instances, seasonal/event maps, pre-re tutorial maps, any other
// identical-geometry map) can never be folded onto another.
//
// Source representation is not runtime activation: the pinned/generated content for the aliases stays in the
// repository; only hosting and routing collapse onto the canonical maps.
internal static class CanonicalMapPolicy
{
    private static readonly IReadOnlyDictionary<string, string> Aliases = BuildAliasTable(GeneratedCanonicalMapAliases.All);

    // alias -> canonical, for tests/diagnostics.
    internal static IReadOnlyDictionary<string, string> AliasTable => Aliases;

    // The distinct canonical maps the aliases fold onto.
    internal static IReadOnlySet<string> CanonicalMaps { get; } = new HashSet<string>(Aliases.Values, StringComparer.OrdinalIgnoreCase);

    internal static bool IsAlias(string? map) => TryGetCanonical(map, out _);

    // True only for a listed alias; `canonical` is the canonical map name from the table. A `.gat` suffix is
    // tolerated on input (client/world forms) but an alias is never inferred from its shape.
    internal static bool TryGetCanonical(string? map, out string canonical)
    {
        canonical = string.Empty;
        if (string.IsNullOrWhiteSpace(map)) return false;
        var name = map.Trim();
        if (name.EndsWith(".gat", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return Aliases.TryGetValue(name, out canonical!);
    }

    // Canonical map for `map`; anything that is not a listed alias (including null/empty and canonical
    // maps themselves) is returned unchanged.
    internal static string Canonicalize(string map) => TryGetCanonical(map, out var canonical) ? canonical : map;

    // Defence in depth (the generator already validates the same rules): the table must be a flat function,
    // so a hand edit of the generated file cannot introduce a chain, duplicate or self-alias.
    internal static IReadOnlyDictionary<string, string> BuildAliasTable(IEnumerable<(string Alias, string Canonical)> entries)
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, canonical) in entries)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(canonical) || string.Equals(alias, canonical, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"Invalid canonical map alias entry '{alias}' -> '{canonical}'.");
            if (!table.TryAdd(alias, canonical)) throw new InvalidOperationException($"Duplicate canonical map alias '{alias}'.");
        }
        foreach (var canonical in table.Values)
            if (table.ContainsKey(canonical)) throw new InvalidOperationException($"Canonical map '{canonical}' is itself an alias (chained canonicalization is not allowed).");
        return table;
    }
}
