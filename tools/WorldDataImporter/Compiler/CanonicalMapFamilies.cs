using System.Text;
using System.Text.Json;

namespace Athena.WorldCompiler.Generation;

// World/content knowledge about pinned rAthena's parallel "channel" copy maps lives HERE (as explicit data
// in canonical-map-families.json), never in the generic runtime resolver. The generator validates the data
// against the effective map cache and emits GeneratedCanonicalMapAliases.cs, which CharServer and MapServer
// both compile next to the generic Athena.Net.Shared.MapIdentity.CanonicalMapPolicy.
//
// There is deliberately no discovery by name shape: an alias is only ever what the JSON lists. Validation
// makes the list auditable - every name must be a real effective map, every alias must be cell-for-cell
// identical to its canonical map, and the table must be a flat alias -> canonical function (no chains,
// duplicates, self-aliases or wildcards).
internal sealed record CanonicalMapFamily(string Canonical, IReadOnlyList<string> Aliases, string Evidence);

internal static class CanonicalMapFamilies
{
    internal sealed record MapGeometry(int Width, int Height, byte[] Cells);

    internal static IReadOnlyList<CanonicalMapFamily> Parse(string json)
    {
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("families", out var families) || families.ValueKind != JsonValueKind.Array)
            throw new ArgumentException("canonical-map-families: missing 'families' array.");
        var result = new List<CanonicalMapFamily>();
        foreach (var family in families.EnumerateArray())
        {
            var canonical = family.TryGetProperty("canonical", out var c) ? c.GetString() : null;
            var evidence = family.TryGetProperty("evidence", out var e) ? e.GetString() : null;
            if (string.IsNullOrWhiteSpace(canonical)) throw new ArgumentException("canonical-map-families: a family has no 'canonical'.");
            if (string.IsNullOrWhiteSpace(evidence)) throw new ArgumentException($"canonical-map-families: family '{canonical}' has no 'evidence'.");
            if (!family.TryGetProperty("aliases", out var aliases) || aliases.ValueKind != JsonValueKind.Array)
                throw new ArgumentException($"canonical-map-families: family '{canonical}' has no 'aliases' array.");
            result.Add(new CanonicalMapFamily(canonical, aliases.EnumerateArray().Select(a => a.GetString() ?? string.Empty).ToArray(), evidence));
        }
        return result;
    }

    // Structural + geometry validation. `maps` is the effective (layered) map-cache content by name.
    internal static void Validate(IReadOnlyList<CanonicalMapFamily> families, IReadOnlyDictionary<string, MapGeometry> maps)
    {
        if (families.Count == 0) throw new ArgumentException("canonical-map-families: no families declared.");
        var canonicals = new HashSet<string>(StringComparer.Ordinal);
        var aliases = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var family in families)
        {
            RequireLiteralName(family.Canonical);
            if (!canonicals.Add(family.Canonical)) throw new ArgumentException($"canonical-map-families: duplicate canonical '{family.Canonical}'.");
            if (family.Aliases.Count == 0) throw new ArgumentException($"canonical-map-families: family '{family.Canonical}' has no aliases.");
            foreach (var alias in family.Aliases)
            {
                RequireLiteralName(alias);
                if (alias == family.Canonical) throw new ArgumentException($"canonical-map-families: '{alias}' aliases itself.");
                if (!aliases.TryAdd(alias, family.Canonical)) throw new ArgumentException($"canonical-map-families: alias '{alias}' is listed more than once.");
            }
        }
        foreach (var canonical in canonicals)
            if (aliases.ContainsKey(canonical)) throw new ArgumentException($"canonical-map-families: '{canonical}' is both a canonical map and an alias (chains are not allowed).");

        foreach (var family in families)
        {
            if (!maps.TryGetValue(family.Canonical, out var canonicalMap)) throw new ArgumentException($"canonical-map-families: canonical map '{family.Canonical}' is not an effective map.");
            foreach (var alias in family.Aliases)
            {
                if (!maps.TryGetValue(alias, out var aliasMap)) throw new ArgumentException($"canonical-map-families: alias '{alias}' is not an effective map.");
                if (aliasMap.Width != canonicalMap.Width || aliasMap.Height != canonicalMap.Height || !aliasMap.Cells.AsSpan().SequenceEqual(canonicalMap.Cells))
                    throw new ArgumentException($"canonical-map-families: alias '{alias}' does not have identical geometry to '{family.Canonical}'.");
            }
        }
    }

    private static void RequireLiteralName(string name)
    {
        if (string.IsNullOrWhiteSpace(name) || name != name.Trim() || name.Any(ch => ch is '*' or '?' or '/' or '\\' or '.' || char.IsWhiteSpace(ch)))
            throw new ArgumentException($"canonical-map-families: '{name}' is not a literal map name (no wildcards, paths, extensions or whitespace).");
        if (name != name.ToLowerInvariant()) throw new ArgumentException($"canonical-map-families: '{name}' must be lower-case.");
    }

    internal static string Emit(IReadOnlyList<CanonicalMapFamily> families, string commit)
    {
        var b = new StringBuilder()
            .Append("// <auto-generated>\n// Generated by Athena.WorldCompiler canonical-map generator.\n// Source: tools/WorldDataImporter/canonical-map-families.json (validated against the pinned effective map cache)\n// rAthena commit: ").Append(commit)
            .Append("\n// Do not edit this file directly.\n// </auto-generated>\n")
            .Append("namespace Athena.Net.Shared.MapIdentity;\n\n")
            .Append("// Explicit alias -> canonical map table (the ONLY source of canonicalization data; the resolver in\n// CanonicalMapPolicy has no map knowledge of its own).\n")
            .Append("internal static class GeneratedCanonicalMapAliases\n{\n    internal static readonly (string Alias, string Canonical)[] All =\n    [\n");
        foreach (var family in families.OrderBy(f => f.Canonical, StringComparer.Ordinal))
        {
            b.Append("        // ").Append(family.Canonical).Append(": ").Append(family.Evidence).Append('\n');
            foreach (var alias in family.Aliases.Order(StringComparer.Ordinal))
                b.Append("        (\"").Append(alias).Append("\", \"").Append(family.Canonical).Append("\"),\n");
        }
        return b.Append("    ];\n}\n").ToString();
    }
}
