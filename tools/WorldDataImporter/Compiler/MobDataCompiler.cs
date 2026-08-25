using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Athena.WorldCompiler.Generation;

// Compiles pinned rAthena mob_db.yml Id blocks plus the fixed-column
// "map,x,y monster Name MobId,Count,Delay" declaration format used by
// npc/re/mobs/*.txt. Neither is a general rAthena mob-DB/script parser: it
// reads only the scalar fields this vertical slice needs, the same way
// ProgressionDataCompiler reads only the progression-table scalars it needs
// out of job_exp.yml/job_basepoints.yml rather than a general YAML library.
internal static class MobDataCompiler
{
    internal sealed record MobDefinitionData(
        int Id, string AegisName, string Name, int Level, uint Hp,
        int Attack, int Attack2, int Defense, int MagicDefense,
        int Str, int Agi, int Vit, int Int, int Dex, int Luk,
        int AttackRange, int WalkSpeed, int AttackDelay,
        long BaseExp, long JobExp);

    internal sealed record MobSpawnData(string Map, int MobId, int Count, int RespawnDelayMs, string SourceFile, int SourceLine);

    // Parses one `- Id: <n>` block out of mob_db.yml up to (not including) the
    // next top-level `- Id:` line. Defaults for fields absent from the pinned
    // block match mob.cpp's spawn_data default-constructor (mob_summon /
    // MobDatabase::create, ~line 4946-4963) which runs BEFORE the YAML loader
    // conditionally overwrites individual fields - NOT a blanket "0":
    // Level=1, Str/Agi/Vit/Int/Dex/Luk=1, WalkSpeed=DEFAULT_WALK_SPEED(150).
    // Hp/BaseExp/JobExp/Attack/Attack2/Defense/MagicDefense/AttackRange/
    // AttackDelay all genuinely default to 0/unset in that same constructor.
    internal static MobDefinitionData ReadMobDefinition(string mobDbYaml, int mobId)
    {
        var marker = $"  - Id: {mobId.ToString(CultureInfo.InvariantCulture)}\n";
        var start = mobDbYaml.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new ArgumentException($"Mob Id {mobId} was not found in the pinned mob_db.yml.");
        var next = mobDbYaml.IndexOf("\n  - Id: ", start + marker.Length, StringComparison.Ordinal);
        var block = next >= 0 ? mobDbYaml[start..(next + 1)] : mobDbYaml[start..];

        return new MobDefinitionData(
            mobId,
            RequiredScalar(block, "AegisName"),
            RequiredScalar(block, "Name"),
            (int)OptionalInt(block, "Level", 1),
            (uint)OptionalInt(block, "Hp", 0),
            (int)OptionalInt(block, "Attack", 0),
            (int)OptionalInt(block, "Attack2", 0),
            (int)OptionalInt(block, "Defense", 0),
            (int)OptionalInt(block, "MagicDefense", 0),
            (int)OptionalInt(block, "Str", 1),
            (int)OptionalInt(block, "Agi", 1),
            (int)OptionalInt(block, "Vit", 1),
            (int)OptionalInt(block, "Int", 1),
            (int)OptionalInt(block, "Dex", 1),
            (int)OptionalInt(block, "Luk", 1),
            (int)OptionalInt(block, "AttackRange", 0),
            (int)OptionalInt(block, "WalkSpeed", 150),
            (int)OptionalInt(block, "AttackDelay", 0),
            OptionalInt(block, "BaseExp", 0),
            OptionalInt(block, "JobExp", 0));
    }

    // Parses the fixed rAthena spawn-declaration format:
    //   <map>,<x>,<y>[,<xs>,<ys>]\tmonster\t<Name>\t<MobId>,<Count>[,<Delay1>[,<Delay2>]]
    // Only lines whose Name matches `mobName` (rAthena's second field, purely
    // cosmetic/for grep-ability - the numeric MobId is authoritative) are
    // returned. `x,y` of `0,0` with no `xs,ys` means rAthena's mob_spawn
    // (mob.cpp:1117) treats the declared center as unusable (xs+ys<1) and
    // instead does a map-wide randomized candidate search bounded by
    // battle_config.map_edge_size, checked against real GAT walkability
    // (map.cpp:1798 map_search_freecell) - it is NOT literal coordinate (0,0)
    // and NOT "random within a small radius around (0,0)". See
    // IMobSpawnCellSelector for how Athena's runtime handles this given it has
    // no GAT/collision data source at all (a genuine data gap, not a
    // deliberately-skipped feature).
    // `excludedMaps` lets a caller drop rows for a map instance the runtime
    // doesn't serve (e.g. base `int_land`, which the compiled Academy world
    // never registers - Captain Carocc/Lumin placements already exclude it
    // the same way), mirroring compile-npc-world's --exclude-placement.
    internal static IReadOnlyList<MobSpawnData> ReadMobSpawns(string spawnScriptText, string sourceFile, string mobName, IReadOnlySet<string>? excludedMaps = null)
    {
        var results = new List<MobSpawnData>();
        var lines = spawnScriptText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var match = SpawnLineRegex().Match(lines[i]);
            if (!match.Success) continue;
            if (!string.Equals(match.Groups["name"].Value, mobName, StringComparison.Ordinal)) continue;
            var map = match.Groups["map"].Value;
            if (excludedMaps is not null && excludedMaps.Contains(map)) continue;
            var count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
            var delayGroup = match.Groups["delay1"];
            var delay = delayGroup.Success ? int.Parse(delayGroup.Value, CultureInfo.InvariantCulture) : 0;
            results.Add(new MobSpawnData(
                map,
                int.Parse(match.Groups["mobid"].Value, CultureInfo.InvariantCulture),
                count,
                delay,
                sourceFile,
                i + 1));
        }
        if (results.Count == 0) throw new ArgumentException($"No '{mobName}' monster spawn declarations were found in the pinned source.");
        return results;
    }

    internal static string GenerateMobDefinition(MobDefinitionData mob, string commit, string className, string constantName, string sourceFile, int sourceLine)
    {
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(sourceFile).Append(':').Append(sourceLine.ToString(CultureInfo.InvariantCulture)).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            // Global game data ("what is mob <id>") - not world/placement data, so this does NOT
            // live under Generated.World.Izlude.Academy even though only the Academy slice
            // currently generates a mob. MobSpawnDefinition (map/count/respawn) is the
            // world-scoped counterpart and stays under Generated.World.Izlude.Academy.
            .AppendLine("namespace Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine()
            .Append("internal static class ").AppendLine(className)
            .AppendLine("{")
            .Append("    internal static readonly MobDefinition ").Append(constantName).AppendLine(" = new(")
            .Append("        Id: ").Append(mob.Id).AppendLine(",")
            .Append("        AegisName: \"").Append(mob.AegisName).AppendLine("\",")
            .Append("        Name: \"").Append(mob.Name).AppendLine("\",")
            .Append("        Level: ").Append(mob.Level).AppendLine(",")
            .Append("        MaxHp: ").Append(mob.Hp).AppendLine(",")
            .Append("        Attack: ").Append(mob.Attack).AppendLine(",")
            .Append("        Attack2: ").Append(mob.Attack2).AppendLine(",")
            .Append("        Defense: ").Append(mob.Defense).AppendLine(",")
            .Append("        MagicDefense: ").Append(mob.MagicDefense).AppendLine(",")
            .Append("        Str: ").Append(mob.Str).AppendLine(",")
            .Append("        Agi: ").Append(mob.Agi).AppendLine(",")
            .Append("        Vit: ").Append(mob.Vit).AppendLine(",")
            .Append("        Int: ").Append(mob.Int).AppendLine(",")
            .Append("        Dex: ").Append(mob.Dex).AppendLine(",")
            .Append("        Luk: ").Append(mob.Luk).AppendLine(",")
            .Append("        AttackRange: ").Append(mob.AttackRange).AppendLine(",")
            .Append("        WalkSpeed: ").Append(mob.WalkSpeed).AppendLine(",")
            .Append("        AttackDelay: ").Append(mob.AttackDelay).AppendLine(",")
            .Append("        BaseExp: ").Append(mob.BaseExp).AppendLine(",")
            .Append("        JobExp: ").Append(mob.JobExp).AppendLine(",")
            .Append("        Source: new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(sourceFile).Append("\", ").Append(sourceLine).AppendLine("));")
            .AppendLine("}");
        return output.ToString();
    }

    internal static string GenerateMobSpawns(IReadOnlyList<MobSpawnData> spawns, string mobDefinitionExpression, string commit, string className, string arrayName)
    {
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(spawns[0].SourceFile).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            // World/placement data - correctly stays under Generated.World.Izlude.Academy, unlike
            // the MobDefinition it references (global game data, see GenerateMobDefinition above).
            .AppendLine("namespace Athena.Net.MapServer.Generated.World.Izlude.Academy;")
            .AppendLine()
            .Append("internal static class ").AppendLine(className)
            .AppendLine("{")
            .Append("    internal static readonly MobSpawnDefinition[] ").Append(arrayName).AppendLine(" =")
            .AppendLine("    [");
        foreach (var spawn in spawns)
        {
            output.Append("        new(").Append(mobDefinitionExpression).Append(", \"").Append(spawn.Map).Append("\", ")
                .Append(spawn.Count).Append(", ").Append(spawn.RespawnDelayMs)
                .Append(", new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(spawn.SourceFile).Append("\", ").Append(spawn.SourceLine).AppendLine(")),");
        }
        output.AppendLine("    ];").AppendLine("}");
        return output.ToString();
    }

    private static string RequiredScalar(string block, string field)
    {
        var match = ScalarRegex(field).Match(block);
        if (!match.Success) throw new ArgumentException($"Pinned mob_db.yml block has no '{field}' field.");
        return match.Groups[1].Value;
    }

    // A field entirely absent from the block takes the constructor default
    // documented at each call site above, not a blanket zero.
    private static long OptionalInt(string block, string field, long defaultValue)
    {
        var match = ScalarRegex(field).Match(block);
        return match.Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : defaultValue;
    }

    private static readonly Regex SpawnLine = new(@"^(?<map>[A-Za-z0-9_]+),(?<x>-?\d+),(?<y>-?\d+)(?:,\d+,\d+)?\t+monster\t+(?<name>[^\t]+)\t+(?<mobid>\d+),(?<count>\d+)(?:,(?<delay1>\d+))?(?:,(?<delay2>\d+))?", RegexOptions.None);
    private static Regex SpawnLineRegex() => SpawnLine;

    private static Regex ScalarRegex(string field) => new($@"^    {Regex.Escape(field)}: (.+)$", RegexOptions.Multiline);
}
