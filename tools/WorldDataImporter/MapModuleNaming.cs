namespace Athena.WorldCompiler.Generation;

internal readonly record struct MapModulePlacement(string FolderPath, string ClassName, string Namespace, string ArrayName);

internal static class MapModuleNaming
{
    private static readonly IReadOnlyDictionary<string, MapModulePlacement> Families =
        new Dictionary<string, MapModulePlacement>(StringComparer.Ordinal)
        {
            ["prt_fild08"] = new("PrtFild08", "PrtFild08", "Athena.Net.MapServer.Generated.World.PrtFild08", "PrtFild08"),
            ["prt_fild08a"] = new("PrtFild08", "PrtFild08", "Athena.Net.MapServer.Generated.World.PrtFild08", "PrtFild08A"),
            ["prt_fild08b"] = new("PrtFild08", "PrtFild08", "Athena.Net.MapServer.Generated.World.PrtFild08", "PrtFild08B"),
            ["prt_fild08c"] = new("PrtFild08", "PrtFild08", "Athena.Net.MapServer.Generated.World.PrtFild08", "PrtFild08C"),
            ["prt_fild08d"] = new("PrtFild08", "PrtFild08", "Athena.Net.MapServer.Generated.World.PrtFild08", "PrtFild08D"),
            ["int_land"] = new("Izlude/Academy", "Academy", "Athena.Net.MapServer.Generated.World.Izlude.Academy", "IntLand"),
            ["int_land01"] = new("Izlude/Academy", "Academy", "Athena.Net.MapServer.Generated.World.Izlude.Academy", "IntLand01"),
            ["int_land02"] = new("Izlude/Academy", "Academy", "Athena.Net.MapServer.Generated.World.Izlude.Academy", "IntLand02"),
            ["int_land03"] = new("Izlude/Academy", "Academy", "Athena.Net.MapServer.Generated.World.Izlude.Academy", "IntLand03"),
            ["int_land04"] = new("Izlude/Academy", "Academy", "Athena.Net.MapServer.Generated.World.Izlude.Academy", "IntLand04"),
        };

    public static MapModulePlacement Resolve(string map, ISet<string> usedFolders)
    {
        if (Families.TryGetValue(map, out var family)) return family;
        var token = PascalCase(map);
        var folder = token;
        var suffix = 2;
        while (!usedFolders.Add(folder)) folder = $"{token}_{suffix++}";
        return new(folder, folder, $"Athena.Net.MapServer.Generated.World.{folder}", folder);
    }

    public static bool TryGetFamily(string map, out MapModulePlacement placement) => Families.TryGetValue(map, out placement);
    public static MapModulePlacement ResolveWarp(string map, ISet<string> usedFolders)
    {
        if (map is "iz_int" or "iz_int01" or "iz_int02" or "iz_int03" or "iz_int04")
            return new("Izlude/Academy", "Academy", "Athena.Net.MapServer.Generated.World.Izlude.Academy", "IzInt");
        if (map == "izlude_d")
            return new("Izlude/IzludeCity", "IzludeCity", "Athena.Net.MapServer.Generated.World.Izlude.IzludeCity", "IzludeD");
        return Resolve(map, usedFolders);
    }
    public static string PascalCase(string map)
    {
        var parts = map.Split(map.Where(character => !char.IsAsciiLetterOrDigit(character)).Distinct().ToArray(), StringSplitOptions.RemoveEmptyEntries);
        var value = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..]));
        if (value.Length == 0) throw new ArgumentException($"Map token '{map}' cannot form a C# identifier.");
        return char.IsAsciiDigit(value[0]) ? "Map" + value : value;
    }
}
