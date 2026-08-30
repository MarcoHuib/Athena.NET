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
        int AttackRange, int WalkSpeed, int AttackDelay, int AttackMotion, int DamageMotion,
        long BaseExp, long JobExp, MobModeData Mode,
        string? JapaneseName, uint Sp, long MvpExp, int Resistance, int MagicResistance,
        int SkillRange, int ChaseRange, MobSizeData Size, MobRaceData Race, MobElementData Element,
        int ElementLevel, int ClientAttackMotion, int DamageTaken, int GroupId, string? Title,
        MobClassData Class);

    // Mirrors Athena.Net.MapServer.World.MobSize exactly (same numeric values/names) - see that
    // enum's own doc comment for the pinned e_size trace and why SZ_ALL/SZ_MAX are excluded.
    internal enum MobSizeData { Small = 0, Medium = 1, Big = 2 }

    // Mirrors Athena.Net.MapServer.World.MobRace exactly (same numeric values/names) - see that
    // enum's own doc comment for the pinned e_race trace and why RC_NONE_/RC_ALL/RC_MAX are excluded.
    internal enum MobRaceData
    {
        Formless = 0, Undead = 1, Brute = 2, Plant = 3, Insect = 4, Fish = 5, Demon = 6,
        DemiHuman = 7, Angel = 8, Dragon = 9, PlayerHuman = 10, PlayerDoram = 11,
    }

    // Mirrors Athena.Net.MapServer.World.MobElement exactly (same numeric values/names) - see that
    // enum's own doc comment for the pinned e_element trace and why the sentinel/wildcard members
    // are excluded.
    internal enum MobElementData
    {
        Neutral = 0, Water = 1, Earth = 2, Fire = 3, Wind = 4, Poison = 5, Holy = 6, Dark = 7,
        Ghost = 8, Undead = 9,
    }

    // Mirrors Athena.Net.MapServer.World.MobClass exactly (same numeric values/names, including the
    // pinned enum's own gap at 3) - see that enum's own doc comment for the pinned e_mob_class trace.
    internal enum MobClassData { Normal = 0, Boss = 1, Guardian = 2, Battlefield = 4, Event = 5 }

    // Mirrors Athena.Net.MapServer.World.MobMode exactly (same bit values/names) - kept as a
    // separate type per this project's existing WorldDataImporter/MapServer decoupling rule (see
    // e.g. CompiledMapCellFlags's own doc comment for the same pattern): WorldDataImporter has no
    // project reference to MapServer.
    [Flags]
    internal enum MobModeData
    {
        None = 0,
        CanMove = 0x0000001,
        NoRandomWalk = 0x0000020,
        CanAttack = 0x0000080,
        ChangeTargetMelee = 0x0001000,
        ChangeTargetChase = 0x0002000,
    }

    // Pinned e_aegis_monstertype (legacy/rathena/src/map/mob.hpp:151-182) - the COMPLETE pinned
    // Ai-preset-name -> raw e_mode bitmask table, reproduced in full even though this project's
    // MobModeData only exposes two of the bits any given preset may set, so a future mob using a
    // different Ai preset is decoded correctly rather than needing this table extended piecemeal.
    private static readonly Dictionary<string, int> AiPresets = new(StringComparer.Ordinal)
    {
        ["01"] = 0x81, ["02"] = 0x83, ["03"] = 0x1089, ["04"] = 0x3885, ["05"] = 0x2085,
        ["06"] = 0, ["07"] = 0x108B, ["08"] = 0x7085, ["09"] = 0x3095, ["10"] = 0x84,
        ["11"] = 0x84, ["12"] = 0x2085, ["13"] = 0x308D, ["17"] = 0x91, ["19"] = 0x3095,
        ["20"] = 0x3295, ["21"] = 0x3695, ["24"] = 0xA1, ["25"] = 0x1, ["26"] = 0xB695,
        ["27"] = 0x8084, ["ABR_PASSIVE"] = 0x21, ["ABR_OFFENSIVE"] = 0xA5,
    };

    // Pinned MD_* bit values this project's MobModeData currently models (mmo.hpp:242-272) - used
    // only to mask the raw Ai-preset+Modes: bitmask down to the bits Athena's generated model
    // actually exposes; the full pinned e_mode has many more bits (MD_AGGRESSIVE, MD_LOOTER,
    // MD_ASSIST, MD_MVP, etc.) that are correctly computed as part of the raw mask below but
    // deliberately not surfaced in MobModeData yet (see that enum's own doc comment).
    private const int ModeBitCanMove = 0x0000001;
    private const int ModeBitNoRandomWalk = 0x0000020;
    private const int ModeBitCanAttack = 0x0000080;
    private const int ModeBitChangeTargetMelee = 0x0001000;
    private const int ModeBitChangeTargetChase = 0x0002000;

    internal sealed record MobSpawnData(string Map, int MobId, int Count, int RespawnDelayMs, string SourceFile, int SourceLine, short X, short Y, short Xs, short Ys);

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
            // AttackMotion (amotion) and DamageMotion (dmotion) are pinned mob_db.yml scalars
            // distinct from AttackDelay (adelay) - amotion is the attacker's attack-animation
            // timing (clif_damage's own srcSpeed for a mob attacker), dmotion is this mob's OWN
            // hit-reaction/walk-delay timing when IT is the target (clif_damage's own dstSpeed when
            // a player attacks this mob) - see MobBasicAttackCalculator/IroMonsterCombatPackets
            // call sites for where each is actually used. Never conflated with AttackDelay, which
            // controls attack CADENCE (NextAttackAt), not animation/hit-reaction timing. Same
            // mob.cpp:4946-4963 default-constructor rationale as AttackDelay's own comment: both
            // genuinely default to 0/unset when the pinned block omits them.
            (int)OptionalInt(block, "AttackMotion", 0),
            (int)OptionalInt(block, "DamageMotion", 0),
            OptionalInt(block, "BaseExp", 0),
            OptionalInt(block, "JobExp", 0),
            ReadMode(block),
            OptionalScalar(block, "JapaneseName"),
            (uint)OptionalInt(block, "Sp", 1),
            OptionalInt(block, "MvpExp", 0),
            (int)OptionalInt(block, "Resistance", 0),
            (int)OptionalInt(block, "MagicResistance", 0),
            (int)OptionalInt(block, "SkillRange", 0),
            (int)OptionalInt(block, "ChaseRange", 0),
            ReadSize(block),
            ReadRace(block),
            ReadElement(block),
            (int)OptionalInt(block, "ElementLevel", 1),
            // ClientAttackMotion has no fixed default: pinned MobDatabase::parseBodyNode resolves an
            // absent field to THIS SAME mob's own resolved AttackMotion value the first time a mob_id
            // is seen (mob.cpp:5391-5397) - see MobDefinition.ClientAttackMotion's own doc comment.
            (int)OptionalInt(block, "ClientAttackMotion", OptionalInt(block, "AttackMotion", 0)),
            (int)OptionalInt(block, "DamageTaken", 100),
            (int)OptionalInt(block, "GroupId", 0),
            OptionalScalar(block, "Title"),
            ReadClass(block));
    }

    // Pinned MobDatabase::parseBodyNode's Size:/Race:/Element:/Class: resolution (mob.cpp:5244-5487):
    // build "<Prefix>_" + the pinned string value, case-insensitively look it up against the fixed
    // pinned constant table (script_get_constant -> search_str uses strcasecmp, matching real data
    // such as "Player_Doram"/"Demihuman"), and fall back to the documented default when absent,
    // unrecognized, or out of the type's valid range - exactly like every other unrecognized-Ai/
    // unrecognized-mode fallback already in this file, never a thrown error for a single bad
    // enum-shaped field.
    private static MobSizeData ReadSize(string block)
    {
        var value = OptionalScalar(block, "Size");
        if (value is null) return MobSizeData.Small;
        return value.Trim() switch
        {
            var v when string.Equals(v, "Small", StringComparison.OrdinalIgnoreCase) => MobSizeData.Small,
            var v when string.Equals(v, "Medium", StringComparison.OrdinalIgnoreCase) => MobSizeData.Medium,
            var v when string.Equals(v, "Large", StringComparison.OrdinalIgnoreCase) => MobSizeData.Big, // Pinned "Size_Large" constant name maps to SZ_BIG.
            _ => MobSizeData.Small,
        };
    }

    private static readonly Dictionary<string, MobRaceData> RaceNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Formless"] = MobRaceData.Formless, ["Undead"] = MobRaceData.Undead, ["Brute"] = MobRaceData.Brute,
        ["Plant"] = MobRaceData.Plant, ["Insect"] = MobRaceData.Insect, ["Fish"] = MobRaceData.Fish,
        ["Demon"] = MobRaceData.Demon, ["Demihuman"] = MobRaceData.DemiHuman, ["Angel"] = MobRaceData.Angel,
        ["Dragon"] = MobRaceData.Dragon, ["Player_Human"] = MobRaceData.PlayerHuman, ["Player_Doram"] = MobRaceData.PlayerDoram,
    };

    private static MobRaceData ReadRace(string block)
    {
        var value = OptionalScalar(block, "Race");
        if (value is null) return MobRaceData.Formless;
        return RaceNames.TryGetValue(value.Trim(), out var race) ? race : MobRaceData.Formless;
    }

    private static readonly Dictionary<string, MobElementData> ElementNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Neutral"] = MobElementData.Neutral, ["Water"] = MobElementData.Water, ["Earth"] = MobElementData.Earth,
        ["Fire"] = MobElementData.Fire, ["Wind"] = MobElementData.Wind, ["Poison"] = MobElementData.Poison,
        ["Holy"] = MobElementData.Holy, ["Dark"] = MobElementData.Dark, ["Ghost"] = MobElementData.Ghost,
        ["Undead"] = MobElementData.Undead,
    };

    private static MobElementData ReadElement(string block)
    {
        var value = OptionalScalar(block, "Element");
        if (value is null) return MobElementData.Neutral;
        return ElementNames.TryGetValue(value.Trim(), out var element) ? element : MobElementData.Neutral;
    }

    private static readonly Dictionary<string, MobClassData> ClassNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Normal"] = MobClassData.Normal, ["Boss"] = MobClassData.Boss, ["Guardian"] = MobClassData.Guardian,
        ["Battlefield"] = MobClassData.Battlefield, ["Event"] = MobClassData.Event,
    };

    private static MobClassData ReadClass(string block)
    {
        var value = OptionalScalar(block, "Class");
        if (value is null) return MobClassData.Normal;
        return ClassNames.TryGetValue(value.Trim(), out var mobClass) ? mobClass : MobClassData.Normal;
    }

    // Reproduces pinned MobDatabase::parseBodyNode's mode resolution exactly (mob.cpp:5446-5519):
    // the pinned `Ai:` field resolves to one of the MONSTER_TYPE_NN preset bitmasks (the mob's
    // BASE status.mode), then each pinned `Modes:` entry individually ORs (true) or AND-NOTs
    // (false) its own bit on top of that preset - never treating the Modes: block as the complete
    // mode by itself. A block with no `Ai:` field defaults to the same MONSTER_TYPE_06=0 pinned
    // uses when the YAML field is entirely absent (mob.cpp default-constructs status.mode to 0
    // before any conditional field ever runs).
    private static MobModeData ReadMode(string block)
    {
        var raw = 0;

        var aiMatch = ScalarRegex("Ai").Match(block);
        if (aiMatch.Success)
        {
            var ai = aiMatch.Groups[1].Value.Trim();
            raw = AiPresets.TryGetValue(ai, out var preset) ? preset : 0; // Unknown Ai defaults to MONSTER_TYPE_06=0, matching pinned invalidWarning fallback.
        }

        var modesMatch = ModesBlockRegex().Match(block);
        if (modesMatch.Success)
        {
            foreach (Match entry in ModeEntryRegex().Matches(modesMatch.Groups[1].Value))
            {
                var name = entry.Groups["name"].Value;
                var active = string.Equals(entry.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
                if (!ModeBitsByName.TryGetValue(name, out var bit)) continue; // Unmodeled bit (e.g. FixedItemDrop) - correctly ignored, not an error.
                raw = active ? raw | bit : raw & ~bit;
            }
        }

        var mode = MobModeData.None;
        if ((raw & ModeBitCanMove) != 0) mode |= MobModeData.CanMove;
        if ((raw & ModeBitNoRandomWalk) != 0) mode |= MobModeData.NoRandomWalk;
        if ((raw & ModeBitCanAttack) != 0) mode |= MobModeData.CanAttack;
        if ((raw & ModeBitChangeTargetMelee) != 0) mode |= MobModeData.ChangeTargetMelee;
        if ((raw & ModeBitChangeTargetChase) != 0) mode |= MobModeData.ChangeTargetChase;
        return mode;
    }

    // Only the mode names this project's MobModeData models are recognized here - every other
    // pinned MD_* name (Aggressive, Looter, Assist, FixedItemDrop, Detector, ...) is silently
    // skipped by ReadMode above, matching how a real Modes: block legitimately sets many bits this
    // project's generated model does not yet expose (e.g. G_PORING's own Modes: FixedItemDrop).
    private static readonly Dictionary<string, int> ModeBitsByName = new(StringComparer.Ordinal)
    {
        ["CanMove"] = ModeBitCanMove,
        ["NoRandomWalk"] = ModeBitNoRandomWalk,
        ["CanAttack"] = ModeBitCanAttack,
        ["ChangeTargetMelee"] = ModeBitChangeTargetMelee,
        ["ChangeTargetChase"] = ModeBitChangeTargetChase,
    };

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
    // `excludedMaps` lets a caller drop rows for a map instance this MapServer
    // build genuinely does not serve at all. It must NEVER be used merely
    // because a row is a `duplicate(...)` family's generic/base template map
    // (e.g. `int_land` for the int_land/int_land01..04 G_PORING family) - an
    // earlier invocation of this command excluded `int_land` on the
    // (by-then-stale) assumption that the compiled Academy world never
    // registered anything there, mirroring a similar, since-corrected
    // over-exclusion in compile-npc-world's --exclude-placement usage (see
    // ai/world-data.md's "Runtime architecture" section and
    // WorldMapRegistryFamilyTests/PoringRandomSpawnIntegrationTests for the
    // regression coverage this caused).
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
            var x = short.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
            var y = short.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
            var xsGroup = match.Groups["xs"];
            var ysGroup = match.Groups["ys"];
            var xs = xsGroup.Success ? short.Parse(xsGroup.Value, CultureInfo.InvariantCulture) : (short)0;
            var ys = ysGroup.Success ? short.Parse(ysGroup.Value, CultureInfo.InvariantCulture) : (short)0;
            results.Add(new MobSpawnData(
                map,
                int.Parse(match.Groups["mobid"].Value, CultureInfo.InvariantCulture),
                count,
                delay,
                sourceFile,
                i + 1,
                x, y, xs, ys));
        }
        if (results.Count == 0) throw new ArgumentException($"No '{mobName}' monster spawn declarations were found in the pinned source.");
        return results;
    }

    internal static string GenerateMobDefinition(MobDefinitionData mob, string commit, string className, string constantName, string sourceFile, int sourceLine) =>
        GenerateMobDefinitions([(mob, constantName)], commit, className, sourceFile, sourceLine);

    // One class, N mob constants - the same "one class, many constants" shape GeneratedItems and
    // the existing single-mob GeneratedMobs class already use (see GeneratedItems.cs). A single
    // mob is just the N=1 case, kept byte-identical to the original single-mob emission (verified
    // by CompilerTests) rather than a separate code path.
    internal static string GenerateMobDefinitions(IReadOnlyList<(MobDefinitionData Mob, string ConstantName)> mobs, string commit, string className, string sourceFile, int sourceLine)
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
            //
            // `partial`: large mob coverage is organized as multiple deterministic generated files
            // (one per pinned-source-derived category, e.g. GeneratedMobs.Monsters.cs today, a
            // future GeneratedMobs.Mvps.cs once real MVP-flagged mobs are generated) that all
            // contribute to this ONE class - consumers always reference GeneratedMobs.<Name>
            // regardless of which file actually declares it; file layout is purely organizational.
            .AppendLine("namespace Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine()
            .Append("internal static partial class ").AppendLine(className)
            .AppendLine("{");
        // One physical source line per MobDefinition (ascending MobId within this call's mob set):
        // these files are generated data that will eventually hold thousands of entries, where the
        // prior ~25-line-per-mob layout made large datasets hard to scan/grep/diff. Named arguments
        // are kept in full (never collapsed to positional/helper arguments) - only the line-break
        // formatting changes, not the emitted field set or values.
        foreach (var (mob, constantName) in mobs.OrderBy(item => item.Mob.Id))
        {
            output
                .Append("    internal static readonly MobDefinition ").Append(constantName).Append(" = new(Id: ").Append(mob.Id)
                .Append(", AegisName: \"").Append(mob.AegisName).Append("\", Name: \"").Append(mob.Name)
                .Append("\", Level: ").Append(mob.Level).Append(", MaxHp: ").Append(mob.Hp)
                .Append(", Attack: ").Append(mob.Attack).Append(", Attack2: ").Append(mob.Attack2)
                .Append(", Defense: ").Append(mob.Defense).Append(", MagicDefense: ").Append(mob.MagicDefense)
                .Append(", Str: ").Append(mob.Str).Append(", Agi: ").Append(mob.Agi)
                .Append(", Vit: ").Append(mob.Vit).Append(", Int: ").Append(mob.Int)
                .Append(", Dex: ").Append(mob.Dex).Append(", Luk: ").Append(mob.Luk)
                .Append(", AttackRange: ").Append(mob.AttackRange).Append(", WalkSpeed: ").Append(mob.WalkSpeed)
                .Append(", AttackDelay: ").Append(mob.AttackDelay).Append(", AttackMotion: ").Append(mob.AttackMotion)
                .Append(", DamageMotion: ").Append(mob.DamageMotion).Append(", BaseExp: ").Append(mob.BaseExp)
                .Append(", JobExp: ").Append(mob.JobExp).Append(", Mode: ").Append(FormatMode(mob.Mode))
                .Append(", Source: new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(sourceFile).Append("\", ").Append(sourceLine).Append(')')
                .Append(", JapaneseName: ").Append(FormatNullableString(mob.JapaneseName))
                .Append(", MaxSp: ").Append(mob.Sp).Append(", MvpExp: ").Append(mob.MvpExp)
                .Append(", Resistance: ").Append(mob.Resistance).Append(", MagicResistance: ").Append(mob.MagicResistance)
                .Append(", SkillRange: ").Append(mob.SkillRange).Append(", ChaseRange: ").Append(mob.ChaseRange)
                .Append(", Size: MobSize.").Append(mob.Size).Append(", Race: MobRace.").Append(mob.Race)
                .Append(", Element: MobElement.").Append(mob.Element).Append(", ElementLevel: ").Append(mob.ElementLevel)
                .Append(", ClientAttackMotion: ").Append(mob.ClientAttackMotion).Append(", DamageTaken: ").Append(mob.DamageTaken)
                .Append(", GroupId: ").Append(mob.GroupId).Append(", Title: ").Append(FormatNullableString(mob.Title))
                .Append(", Class: MobClass.").Append(mob.Class).AppendLine(");");
        }
        output.AppendLine("}");
        return output.ToString();
    }

    internal static string GenerateMobSpawns(IReadOnlyList<MobSpawnData> spawns, string mobDefinitionExpression, string commit, string className, string arrayName, string worldNamespace = "Athena.Net.MapServer.Generated.World.Izlude.Academy") =>
        GenerateMobSpawnGroups([(spawns, mobDefinitionExpression, arrayName)], commit, className, worldNamespace);

    // Map-centric shape: ONE `MobSpawnDefinition[] All` array per file, where each entry may
    // reference a DIFFERENT mob's global GeneratedMobs.* constant (a single real map commonly hosts
    // several different mobs) - distinct from GenerateMobSpawnGroups' "one array per mob, several
    // maps mixed into it" shape used by the Academy slice. `entries` is (Spawn, MobDefinitionExpression)
    // pairs, already filtered/ordered by the caller to belong to exactly one map.
    internal static string GenerateMobSpawnsForMap(IReadOnlyList<(MobSpawnData Spawn, string MobDefinitionExpression)> entries, string commit, string className, string worldNamespace)
    {
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(entries[0].Spawn.SourceFile).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            .Append("namespace ").Append(worldNamespace).AppendLine(";")
            .AppendLine()
            .Append("internal static class ").AppendLine(className)
            .AppendLine("{")
            .AppendLine("    internal static readonly MobSpawnDefinition[] All =")
            .AppendLine("    [");
        foreach (var (spawn, mobDefinitionExpression) in entries)
        {
            output.Append("        new(").Append(mobDefinitionExpression).Append(", \"").Append(spawn.Map).Append("\", ")
                .Append(spawn.Count).Append(", ").Append(spawn.RespawnDelayMs)
                .Append(", new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(spawn.SourceFile).Append("\", ").Append(spawn.SourceLine).Append(')')
                .Append(", X: ").Append(spawn.X).Append(", Y: ").Append(spawn.Y).Append(", Xs: ").Append(spawn.Xs).Append(", Ys: ").Append(spawn.Ys).AppendLine("),");
        }
        output.AppendLine("    ];").AppendLine("}");
        return output.ToString();
    }

    // Consolidated DUPLICATE-FAMILY shape (e.g. pinned prt_fild08/a/b/c/d): one class, one
    // `MobSpawnDefinition[]` array per CONCRETE map (each entry keeps its own exact map string and
    // source provenance - the five maps are never collapsed into one runtime/template identity),
    // plus one composed `All` array that concatenates every per-map array (never a duplicated
    // re-listing of the same entries) for callers that want the family's complete population.
    // `mapEntries` is (ArrayName, Entries) pairs in caller-supplied order - e.g.
    // [("PrtFild08", prt_fild08 rows), ("PrtFild08A", prt_fild08a rows), ...]. ArrayName is a
    // fully caller-supplied identifier, deliberately kept separate from PascalCaseMapName's own
    // map-NAME casing convention: a family's per-map array name (e.g. "PrtFild08A") uses a
    // different capitalization from that same map's own file/type name elsewhere (e.g.
    // "PrtFild08a"), and is not itself derived by this function.
    internal static string GenerateMobSpawnFamily(IReadOnlyList<(string ArrayName, IReadOnlyList<(MobSpawnData Spawn, string MobDefinitionExpression)> Entries)> mapEntries, string commit, string className, string worldNamespace)
    {
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(mapEntries[0].Entries[0].Spawn.SourceFile).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            .Append("namespace ").Append(worldNamespace).AppendLine(";")
            .AppendLine()
            .Append("internal static class ").AppendLine(className)
            .AppendLine("{");
        var arrayNames = new List<string>();
        foreach (var (arrayName, entries) in mapEntries)
        {
            arrayNames.Add(arrayName);
            output
                .Append("    internal static readonly MobSpawnDefinition[] ").Append(arrayName).AppendLine(" =")
                .AppendLine("    [");
            foreach (var (spawn, mobDefinitionExpression) in entries)
            {
                output.Append("        new(").Append(mobDefinitionExpression).Append(", \"").Append(spawn.Map).Append("\", ")
                    .Append(spawn.Count).Append(", ").Append(spawn.RespawnDelayMs)
                    .Append(", new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(spawn.SourceFile).Append("\", ").Append(spawn.SourceLine).Append(')')
                    .Append(", X: ").Append(spawn.X).Append(", Y: ").Append(spawn.Y).Append(", Xs: ").Append(spawn.Xs).Append(", Ys: ").Append(spawn.Ys).AppendLine("),");
            }
            output.AppendLine("    ];");
        }
        output
            .AppendLine("    internal static readonly MobSpawnDefinition[] All =")
            .Append("        [.. ").Append(string.Join(", .. ", arrayNames)).AppendLine("];")
            .AppendLine("}");
        return output.ToString();
    }

    // One class, N named MobSpawnDefinition[] arrays (one per mob) - mirrors GenerateMobDefinitions'
    // "one class, many constants" shape. A single mob/array is the N=1 case, kept byte-identical to
    // the original single-array emission (verified by CompilerTests).
    internal static string GenerateMobSpawnGroups(IReadOnlyList<(IReadOnlyList<MobSpawnData> Spawns, string MobDefinitionExpression, string ArrayName)> groups, string commit, string className, string worldNamespace = "Athena.Net.MapServer.Generated.World.Izlude.Academy")
    {
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(groups[0].Spawns[0].SourceFile).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            // World/placement data - lives under whichever area namespace the caller supplies
            // (defaulting to Generated.World.Izlude.Academy for existing invocations), unlike the
            // MobDefinition it references (global game data, see GenerateMobDefinition above).
            .Append("namespace ").Append(worldNamespace).AppendLine(";")
            .AppendLine()
            .Append("internal static class ").AppendLine(className)
            .AppendLine("{");
        foreach (var (spawns, mobDefinitionExpression, arrayName) in groups)
        {
            output
                .Append("    internal static readonly MobSpawnDefinition[] ").Append(arrayName).AppendLine(" =")
                .AppendLine("    [");
            foreach (var spawn in spawns)
            {
                output.Append("        new(").Append(mobDefinitionExpression).Append(", \"").Append(spawn.Map).Append("\", ")
                    .Append(spawn.Count).Append(", ").Append(spawn.RespawnDelayMs)
                    .Append(", new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(spawn.SourceFile).Append("\", ").Append(spawn.SourceLine).Append(')')
                    .Append(", X: ").Append(spawn.X).Append(", Y: ").Append(spawn.Y).Append(", Xs: ").Append(spawn.Xs).Append(", Ys: ").Append(spawn.Ys).AppendLine("),");
            }
            output.AppendLine("    ];");
        }
        output.AppendLine("}");
        return output.ToString();
    }

    // Emits a C# MobMode expression matching the generated definition's flags exactly - "None"
    // when no modeled bit is set, otherwise a `|`-joined list of the modeled MobMode member names.
    private static string FormatMode(MobModeData mode)
    {
        if (mode == MobModeData.None) return "MobMode.None";
        var parts = new List<string>();
        if (mode.HasFlag(MobModeData.CanMove)) parts.Add("MobMode.CanMove");
        if (mode.HasFlag(MobModeData.NoRandomWalk)) parts.Add("MobMode.NoRandomWalk");
        if (mode.HasFlag(MobModeData.CanAttack)) parts.Add("MobMode.CanAttack");
        if (mode.HasFlag(MobModeData.ChangeTargetMelee)) parts.Add("MobMode.ChangeTargetMelee");
        if (mode.HasFlag(MobModeData.ChangeTargetChase)) parts.Add("MobMode.ChangeTargetChase");
        return string.Join(" | ", parts);
    }

    // Emits a C# null literal or an escaped string literal - JapaneseName/Title are genuinely
    // Optional pinned fields (mob_db.yml doc comment) unlike AegisName/Name, which pinned source
    // treats as always-present per-block identifiers.
    private static string FormatNullableString(string? value) =>
        value is null ? "null" : "\"" + EscapeForCSharpString(value) + "\"";

    private static string EscapeForCSharpString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string RequiredScalar(string block, string field)
    {
        var match = ScalarRegex(field).Match(block);
        if (!match.Success) throw new ArgumentException($"Pinned mob_db.yml block has no '{field}' field.");
        return match.Groups[1].Value;
    }

    // Unlike RequiredScalar, an absent field is a legitimate "use the documented default" case, not
    // an error - mirrors OptionalInt's own absent-field semantics for string-shaped fields
    // (JapaneseName, Title, and the Size/Race/Element/Class enum-shaped scalars).
    private static string? OptionalScalar(string block, string field)
    {
        var match = ScalarRegex(field).Match(block);
        if (!match.Success) return null;
        var raw = match.Groups[1].Value;
        // Real pinned Title: values are YAML-double-quoted whenever the string contains characters
        // the YAML scanner would otherwise treat specially (e.g. "<Red Pepper>" - the angle brackets
        // require quoting). Size/Race/Element/Class/JapaneseName are always bare unquoted words in
        // every real pinned occurrence, so this only ever fires for a genuinely quoted scalar - a
        // one-layer strip of a matching leading/trailing '"' pair, mirroring how a real YAML parser
        // would unwrap this exact simple case (no embedded-quote escape handling is needed since
        // rAthena's own values never contain an embedded '"').
        return raw.Length >= 2 && raw[0] == '"' && raw[^1] == '"' ? raw[1..^1] : raw;
    }

    // A field entirely absent from the block takes the constructor default
    // documented at each call site above, not a blanket zero.
    private static long OptionalInt(string block, string field, long defaultValue)
    {
        var match = ScalarRegex(field).Match(block);
        return match.Success ? long.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture) : defaultValue;
    }

    private static readonly Regex SpawnLine = new(@"^(?<map>[A-Za-z0-9_]+),(?<x>-?\d+),(?<y>-?\d+)(?:,(?<xs>\d+),(?<ys>\d+))?\t+monster\t+(?<name>[^\t]+)\t+(?<mobid>\d+),(?<count>\d+)(?:,(?<delay1>\d+))?(?:,(?<delay2>\d+))?", RegexOptions.None);
    private static Regex SpawnLineRegex() => SpawnLine;

    private static Regex ScalarRegex(string field) => new($@"^    {Regex.Escape(field)}: (.+)$", RegexOptions.Multiline);

    // Captures the raw text of a `    Modes:\n      Name: value\n      ...` block: every
    // subsequent 6-space-indented line, stopping at the first line that is NOT indented that
    // deeply (the next top-level `    Field:` entry or the next `  - Id:` block).
    private static readonly Regex ModesBlock = new(@"^    Modes:\n((?:      .+\n?)*)", RegexOptions.Multiline);
    private static Regex ModesBlockRegex() => ModesBlock;

    private static readonly Regex ModeEntry = new(@"^\s*(?<name>\w+):\s*(?<value>true|false)\s*$", RegexOptions.Multiline);
    private static Regex ModeEntryRegex() => ModeEntry;
}
