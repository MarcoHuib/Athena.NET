namespace Athena.Net.Shared.MapIdentity;

// The ONE map-identity policy shared by CharServer and MapServer (compiled into each assembly from
// this single file; see the <Compile Include="../Shared/..."> link in both .csproj files).
//
// Pinned rAthena hosts the intro/Academy route as PARALLEL "channel" copies of the same physical
// maps (F_IzludeChannel, `start_point: iz_int,iz_int01..04`, `izlude_a..d`, `prt_fild08a..d`...).
// Athena.NET does not keep channels: every alias below has the same geometry as its canonical map, and
// players are routed onto the canonical map so all of them share one World simulation, one actor-id
// space and one visibility domain. The table is EXPLICIT - no suffix stripping, no pattern rules - so a
// map is only ever folded onto another when it is listed here (PvP maps, `1@...` instances, `new_1-1..5-1`,
// seasonal/event maps and any other identical-geometry map are deliberately absent).
//
// Source representation is not runtime activation: the pinned/generated data for the aliases stays in
// the repository; only hosting and routing collapse onto the canonical maps.
internal static class CanonicalMapPolicy
{
    private static readonly IReadOnlyDictionary<string, string> Aliases = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["izlude_a"] = "izlude", ["izlude_b"] = "izlude", ["izlude_c"] = "izlude", ["izlude_d"] = "izlude",
        ["prt_fild08a"] = "prt_fild08", ["prt_fild08b"] = "prt_fild08", ["prt_fild08c"] = "prt_fild08", ["prt_fild08d"] = "prt_fild08",
        ["iz_int01"] = "iz_int", ["iz_int02"] = "iz_int", ["iz_int03"] = "iz_int", ["iz_int04"] = "iz_int",
        ["int_land01"] = "int_land", ["int_land02"] = "int_land", ["int_land03"] = "int_land", ["int_land04"] = "int_land",
        ["iz_ac01_a"] = "iz_ac01", ["iz_ac01_b"] = "iz_ac01", ["iz_ac01_c"] = "iz_ac01", ["iz_ac01_d"] = "iz_ac01",
        ["iz_ac02_a"] = "iz_ac02", ["iz_ac02_b"] = "iz_ac02", ["iz_ac02_c"] = "iz_ac02", ["iz_ac02_d"] = "iz_ac02",
    };

    // alias -> canonical, for tests/diagnostics. 24 entries.
    internal static IReadOnlyDictionary<string, string> AliasTable => Aliases;

    // The six canonical maps the aliases fold onto.
    internal static IReadOnlySet<string> CanonicalMaps { get; } = new HashSet<string>(Aliases.Values, StringComparer.OrdinalIgnoreCase);

    internal static bool IsAlias(string? map) => TryGetCanonical(map, out _);

    // True only for a listed alias; `canonical` is the lower-case canonical map name. A `.gat` suffix is
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
}
