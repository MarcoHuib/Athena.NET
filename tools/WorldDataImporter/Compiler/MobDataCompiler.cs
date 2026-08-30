using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Athena.WorldCompiler.Generation;

// Compiles pinned rAthena mob_db.yml Id blocks plus the fixed-column
// "map,x,y monster Name MobId,Count,Delay" declaration format used by
// npc/re/mobs/*.txt. Neither is a general rAthena mob-DB/script parser: both
// use a narrow, purpose-built line/block scanner (ScalarRegex and friends)
// rather than a general YAML library, the same approach ProgressionDataCompiler
// uses for job_exp.yml/job_basepoints.yml. Despite that narrow PARSING
// technique, ReadMobDefinition's actual FIELD COVERAGE is intentionally
// lossless/near-complete for mob_db.yml's documented schema (every meaningful
// top-level scalar plus the Modes:/RaceGroups:/Drops:/MvpDrops: list-shaped
// blocks - see ai/world-data.md's "Mob static-data schema coverage" section
// and the PinnedMobDbSchema_* tests in MobDataCompilerTests.cs) - it is not
// merely reading "the few scalars an early vertical slice needed".
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
        MobClassData Class,
        IReadOnlyList<MobRaceGroupEntryData> RaceGroups, IReadOnlyList<MobDropEntryData> Drops,
        IReadOnlyList<MobDropEntryData> MvpDrops,
        // The pinned mob_db.yml 1-based line number of this mob's OWN "  - Id: <n>" declaration
        // line, computed from the block's string offset - real provenance rather than a shared/
        // caller-supplied placeholder. 0 only for synthetic in-memory fixtures that never came from
        // a real file offset (e.g. unit-test YAML snippets built directly as MobDefinitionData).
        int SourceLine = 0);

    // Mirrors Athena.Net.MapServer.World.MobRaceGroupEntry exactly - see that record's own doc
    // comment for why RaceGroups is a pinned-name list rather than a fixed C# enum.
    internal sealed record MobRaceGroupEntryData(string Name, bool Value);

    // Mirrors Athena.Net.MapServer.World.MobDropEntry exactly - see that record's own doc comment.
    internal sealed record MobDropEntryData(string Item, int Rate, bool StealProtected, string? RandomOptionGroup);

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

    // Mirrors Athena.Net.MapServer.World.MobModeResolver.ClassDerivedBits exactly - pinned
    // MobDatabase::loadingFinished()'s class-derived mode-bit resolution (mob.cpp:5536-5551),
    // applied to every mob's SOURCE mode (Ai preset + Modes: overrides) to compute the pinned-
    // accurate EFFECTIVE mode a real rAthena server actually holds/runs combat against. See
    // MobModeResolver's own doc comment in WorldEntityDefinition.cs for the SourceMode-vs-
    // EffectiveMode rationale this mirrors.
    internal static MobModeData ResolveEffectiveMode(MobModeData sourceMode, MobClassData mobClass) => sourceMode | mobClass switch
    {
        MobClassData.Boss => MobModeData.Detector | MobModeData.StatusImmune | MobModeData.KnockBackImmune,
        MobClassData.Guardian => MobModeData.StatusImmune,
        MobClassData.Battlefield => MobModeData.StatusImmune | MobModeData.SkillImmune,
        MobClassData.Event => MobModeData.FixedItemDrop,
        _ => MobModeData.None,
    };

    // Mirrors Athena.Net.MapServer.World.MobMode exactly (same bit values/names, the complete
    // pinned MD_* bitmask) - kept as a separate type per this project's existing
    // WorldDataImporter/MapServer decoupling rule (see e.g. CompiledMapCellFlags's own doc comment
    // for the same pattern): WorldDataImporter has no project reference to MapServer.
    [Flags]
    internal enum MobModeData : uint
    {
        None = 0,
        CanMove = 0x0000001,
        Looter = 0x0000002,
        Aggressive = 0x0000004,
        Assist = 0x0000008,
        CastSensorIdle = 0x0000010,
        NoRandomWalk = 0x0000020,
        NoCast = 0x0000040,
        CanAttack = 0x0000080,
        CastSensorChase = 0x0000200,
        ChangeChase = 0x0000400,
        Angry = 0x0000800,
        ChangeTargetMelee = 0x0001000,
        ChangeTargetChase = 0x0002000,
        TargetWeak = 0x0004000,
        RandomTarget = 0x0008000,
        IgnoreMelee = 0x0010000,
        IgnoreMagic = 0x0020000,
        IgnoreRanged = 0x0040000,
        Mvp = 0x0080000,
        IgnoreMisc = 0x0100000,
        KnockBackImmune = 0x0200000,
        TeleportBlock = 0x0400000,
        FixedItemDrop = 0x1000000,
        Detector = 0x2000000,
        StatusImmune = 0x4000000,
        SkillImmune = 0x8000000,
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

    // DeathEvent/Size/Ai mirror pinned npc_parse_mob's `w4` format's remaining optional positions
    // (`<mobid>,<count>,<delay1>,<delay2>,<event>,<size>,<ai>`) - preserved losslessly as source
    // data even though no death-event/size-override/AI-override runtime exists in this project.
    // `null` means the field was omitted from the source line; a present DeathEvent (including the
    // pinned tree's own inert literal "0" placeholder values, see ReadMobSpawns) is stored verbatim.
    // Size reuses this file's own MobSizeData (mirrors Athena.Net.MapServer.World.MobSize exactly -
    // see MobSizeData's own doc comment) rather than a raw int, for the same reason mob.Size does
    // above: a present-but-out-of-[0,2]-range source value should fail generation loudly instead of
    // being silently cast into a meaningless enum member.
    internal sealed record MobSpawnData(string Map, int MobId, int Count, int RespawnDelay, int RespawnRandomDelay, string SourceFile, int SourceLine, short X, short Y, short Xs, short Ys, string? DeathEvent = null, MobSizeData? Size = null, int? Ai = null);

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
        return ParseMobBlock(block, mobId, CountLines(mobDbYaml, start));
    }

    // Enumerates EVERY "  - Id: <n>" block in the pinned mob_db.yml, in source (file) order - the
    // source of truth for "generate all pinned mobs" (this task's primary objective), as opposed to
    // ReadMobDefinition's single-mob lookup used by the pre-existing manually-curated
    // compile-mob-definitions command. A single forward scan (no repeated IndexOf-from-start per
    // mob) so this stays linear in file size for 2,675+ real entries.
    internal static IReadOnlyList<MobDefinitionData> ReadAllMobDefinitions(string mobDbYaml)
    {
        var results = new List<MobDefinitionData>();
        const string marker = "\n  - Id: ";
        var searchFrom = 0;
        var line = 1; // 1-based line number at `searchFrom`.
        while (true)
        {
            var start = mobDbYaml.IndexOf(marker, searchFrom, StringComparison.Ordinal);
            if (start < 0) break;
            for (var i = searchFrom; i < start; i++) if (mobDbYaml[i] == '\n') line++;
            start += 1; // Skip the leading '\n' shared with the previous block's terminator.
            line++; // The line the '- Id:' declaration itself starts on.
            var idStart = start + "  - Id: ".Length;
            var idEnd = mobDbYaml.IndexOf('\n', idStart);
            if (idEnd < 0) throw new ArgumentException("Pinned mob_db.yml has a truncated '- Id:' block at the end of the file.");
            var id = int.Parse(mobDbYaml[idStart..idEnd].Trim(), CultureInfo.InvariantCulture);
            var next = mobDbYaml.IndexOf(marker, idEnd, StringComparison.Ordinal);
            var block = next >= 0 ? mobDbYaml[start..(next + 1)] : mobDbYaml[start..];
            results.Add(ParseMobBlock(block, id, line));
            searchFrom = idEnd;
        }
        return results;
    }

    private static MobDefinitionData ParseMobBlock(string block, int mobId, int sourceLine)
    {
        var name = RequiredScalar(block, "Name");

        return new MobDefinitionData(
            mobId,
            RequiredScalar(block, "AegisName"),
            name,
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
            // Pinned MobDatabase::parseBodyNode (mob.cpp:5028-5040): JapaneseName present -> use it;
            // absent on a mob_id seen for the first time (`!exists`) -> falls back to this SAME
            // block's own resolved Name (mob->jname = mob->name), never left null/blank. This
            // project reads only the base db/re/mob_db.yml with no db/import overlay layering, so
            // every parsed mob is effectively "seen for the first time" - the `exists`-true branch
            // (which would instead leave a PRIOR jname untouched) never applies here.
            OptionalScalar(block, "JapaneseName") ?? name,
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
            ReadClass(block),
            ReadRaceGroups(block),
            ReadDrops(block, "Drops"),
            ReadDrops(block, "MvpDrops"),
            sourceLine);
    }

    internal sealed record GeneratedMobSymbol(MobDefinitionData Mob, string Symbol);

    internal static IReadOnlyList<GeneratedMobSymbol> CreateGeneratedSymbols(IReadOnlyList<MobDefinitionData> mobs)
    {
        var duplicateIds = mobs.GroupBy(mob => mob.Id).Where(group => group.Count() > 1).OrderBy(group => group.Key).ToArray();
        if (duplicateIds.Length > 0)
            throw new ArgumentException($"Pinned mob_db.yml contains duplicate effective mob Id(s): {string.Join(", ", duplicateIds.Select(group => group.Key))}.");

        var used = new HashSet<string>(StringComparer.Ordinal);
        var results = new List<GeneratedMobSymbol>(mobs.Count);
        foreach (var mob in mobs.OrderBy(mob => mob.Id))
        {
            var baseSymbol = SanitizeMobSymbol(mob.AegisName);
            var symbol = baseSymbol;
            if (!used.Add(symbol))
            {
                symbol = $"{baseSymbol}_{mob.Id.ToString(CultureInfo.InvariantCulture)}";
                if (!used.Add(symbol))
                    throw new ArgumentException($"Mob symbol collision could not be disambiguated for Id {mob.Id} ('{mob.AegisName}').");
            }
            results.Add(new GeneratedMobSymbol(mob, symbol));
        }
        return results;
    }

    internal static string SanitizeMobSymbol(string aegisName)
    {
        var parts = Regex.Matches(aegisName, @"[A-Za-z0-9]+")
            .Select(match => match.Value)
            .Where(part => part.Length > 0)
            .ToArray();
        var symbol = string.Concat(parts.Select(part => char.ToUpperInvariant(part[0]) + part[1..].ToLowerInvariant()));
        if (symbol.Length == 0) symbol = "Mob";
        if (!SyntaxFactsLikeIdentifierStart(symbol[0])) symbol = "Mob" + symbol;
        return CSharpKeywords.Contains(symbol) ? symbol + "Mob" : symbol;
    }

    private static bool SyntaxFactsLikeIdentifierStart(char value) => value == '_' || char.IsLetter(value);

    private static readonly HashSet<string> CSharpKeywords = new(StringComparer.Ordinal)
    {
        "Abstract", "As", "Base", "Bool", "Break", "Byte", "Case", "Catch", "Char", "Checked",
        "Class", "Const", "Continue", "Decimal", "Default", "Delegate", "Do", "Double", "Else",
        "Enum", "Event", "Explicit", "Extern", "False", "Finally", "Fixed", "Float", "For", "Foreach",
        "Goto", "If", "Implicit", "In", "Int", "Interface", "Internal", "Is", "Lock", "Long", "Namespace",
        "New", "Null", "Object", "Operator", "Out", "Override", "Params", "Private", "Protected", "Public",
        "Readonly", "Ref", "Return", "Sbyte", "Sealed", "Short", "Sizeof", "Stackalloc", "Static", "String",
        "Struct", "Switch", "This", "Throw", "True", "Try", "Typeof", "Uint", "Ulong", "Unchecked", "Unsafe",
        "Ushort", "Using", "Virtual", "Void", "Volatile", "While", "Record", "Required", "File"
    };

    // Reproduces pinned MobDatabase::parseBodyNode's RaceGroups: resolution (mob.cpp:5291-5317):
    // each entry is a bare pinned key name (never re-prefixed/validated against a fixed bound here -
    // see MobRaceGroupEntry's own doc comment for why this project does not hardcode the pinned
    // RC2_* table) paired with its own explicit true/false toggle value, in pinned source order.
    private static IReadOnlyList<MobRaceGroupEntryData> ReadRaceGroups(string block)
    {
        var match = RaceGroupsBlockRegex().Match(block);
        if (!match.Success) return [];
        var entries = new List<MobRaceGroupEntryData>();
        foreach (Match entry in ModeEntryRegex().Matches(match.Groups[1].Value))
        {
            var active = string.Equals(entry.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
            entries.Add(new MobRaceGroupEntryData(entry.Groups["name"].Value, active));
        }
        return entries;
    }

    // Pinned MAX_MOB_DROP/MAX_MVP_DROP (mob.hpp:27/31) - the exact bound MobDatabase::parseDropNode
    // enforces per section (an Index >= max, or an append once the effective list already holds
    // max entries, is skipped/warned rather than accepted).
    private const int MaxMobDrop = 10;
    private const int MaxMvpDrop = 3;

    // Reproduces pinned MobDatabase::parseDropNode (mob.cpp:4844-4923) EXACTLY, including its
    // stateful Index: overwrite/append/skip semantics - shared verbatim by both `Drops:` and
    // `MvpDrops:` (parseDropNode is one function called for both sections with a different `max`).
    // Index is NOT merely a db/import overlay mechanism this project can ignore: real pinned
    // db/re/mob_db.yml itself uses `Index:` on essentially every drop entry (1,301 real occurrences)
    // - e.g. the REAL Poring/1002 declares `Index: 0` through `Index: 7` on its own 8 base-file
    // drop entries. Declarations are processed SEQUENTIALLY, each one mutating the SAME growing
    // effective list the next declaration is evaluated against (not independently parsed and then
    // reordered) - exactly mirroring pinned source's own `for (dropit : node) { ... }` loop over
    // the SAME `drops` vector:
    //   - no `Index:`           -> append (if the effective list has not yet reached `max`)
    //   - `Index == count`      -> append at that (implicitly correct) next slot
    //   - `Index < count`       -> OVERWRITE the entry already at that slot in place (does not move it)
    //   - `Index > count`       -> skip (a "gap" - pinned source's own explicit `// TODO: warning` case)
    //   - `Index >= max`        -> skip (invalid, out of the section's own bound)
    private static IReadOnlyList<MobDropEntryData> ReadDrops(string block, string sectionName)
    {
        var max = sectionName == "MvpDrops" ? MaxMvpDrop : MaxMobDrop;
        var blockMatch = DropsBlockRegex(sectionName).Match(block);
        if (!blockMatch.Success) return [];
        var entries = new List<MobDropEntryData>();
        foreach (Match entry in DropEntryRegex().Matches(blockMatch.Groups[1].Value))
        {
            var item = entry.Groups["item"].Value;
            var rest = entry.Groups["rest"].Value;
            var rate = int.Parse(RequiredScalarIn(rest, "Rate"), CultureInfo.InvariantCulture);
            var stealValue = OptionalScalarIn(rest, "StealProtected");
            var steal = stealValue is not null && string.Equals(stealValue, "true", StringComparison.OrdinalIgnoreCase);
            var group = OptionalScalarIn(rest, "RandomOptionGroup");
            var drop = new MobDropEntryData(item, rate, steal, group);

            var indexValue = OptionalScalarIn(rest, "Index");
            if (indexValue is null)
            {
                if (entries.Count < max) entries.Add(drop); // else: skipped, matching pinned "Maximum of %d monster %s met, skipping.".
                continue;
            }

            var index = int.Parse(indexValue, CultureInfo.InvariantCulture);
            if (index >= max) continue; // Skipped, matching pinned "Invalid monster %s index %hu ... skipping.".
            if (index == entries.Count) entries.Add(drop); // Append at the next slot.
            else if (index < entries.Count) entries[index] = drop; // Overwrite in place - does not move the entry.
            // else (index > entries.Count): a genuine gap - skipped, matching pinned's own "TODO: warning" case.
        }
        return entries;
    }

    // Field lookups scoped to one already-extracted drop-entry's own continuation text (`rest`
    // above), distinct from block-scoped ScalarRegex (which anchors on 4-space top-level
    // indentation) - a drop entry's own fields are indented deeper (8 spaces: 6 for the list item
    // plus 2 more for its own sub-fields).
    private static readonly Regex DropEntryField = new(@"^\s*(?<field>\w+):\s*(?<value>.+?)\s*$", RegexOptions.Multiline);

    private static string RequiredScalarIn(string text, string field)
    {
        foreach (Match match in DropEntryField.Matches(text))
        {
            if (string.Equals(match.Groups["field"].Value, field, StringComparison.Ordinal)) return match.Groups["value"].Value;
        }
        throw new ArgumentException($"Pinned mob_db.yml drop entry has no '{field}' field.");
    }

    private static string? OptionalScalarIn(string text, string field)
    {
        foreach (Match match in DropEntryField.Matches(text))
        {
            if (string.Equals(match.Groups["field"].Value, field, StringComparison.Ordinal)) return match.Groups["value"].Value;
        }
        return null;
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
    // before any conditional field ever runs). Every named MD_* bit is preserved (ModeBitsByName
    // below covers the complete pinned bitmask, matching MobModeData exactly) - an unrecognized
    // mode NAME (never a valid pinned MD_* name, only a genuinely unknown/future one) is skipped
    // exactly like pinned source's own "Unknown monster mode %s, skipping" invalidWarning path, not
    // silently normalized into an unrelated bit.
    private static MobModeData ReadMode(string block)
    {
        var mode = MobModeData.None;

        var aiMatch = ScalarRegex("Ai").Match(block);
        if (aiMatch.Success)
        {
            var ai = aiMatch.Groups[1].Value.Trim();
            // Unknown Ai defaults to MONSTER_TYPE_06=0, matching pinned invalidWarning fallback.
            mode = AiPresets.TryGetValue(ai, out var preset) ? (MobModeData)preset : MobModeData.None;
        }

        var modesMatch = ModesBlockRegex().Match(block);
        if (modesMatch.Success)
        {
            foreach (Match entry in ModeEntryRegex().Matches(modesMatch.Groups[1].Value))
            {
                var name = entry.Groups["name"].Value;
                var active = string.Equals(entry.Groups["value"].Value, "true", StringComparison.OrdinalIgnoreCase);
                if (!ModeBitsByName.TryGetValue(name, out var bit)) continue; // Genuinely unrecognized mode name - pinned source's own "skip" fallback.
                mode = active ? mode | bit : mode & ~bit;
            }
        }

        return mode;
    }

    // The complete pinned MD_* name -> bit table (doc/mob_db_mode_list.txt), matching MobModeData's
    // full bitmask exactly - every valid pinned Modes: entry name is recognized and its bit
    // retained, independent of whether MapServer's runtime executes that bit yet (see MobModeData's
    // own doc comment and RepositoryDomainAnalyzers' ModeData/ModeRuntime split).
    private static readonly Dictionary<string, MobModeData> ModeBitsByName = new(StringComparer.Ordinal)
    {
        ["CanMove"] = MobModeData.CanMove,
        ["Looter"] = MobModeData.Looter,
        ["Aggressive"] = MobModeData.Aggressive,
        ["Assist"] = MobModeData.Assist,
        ["CastSensorIdle"] = MobModeData.CastSensorIdle,
        ["NoRandomWalk"] = MobModeData.NoRandomWalk,
        ["NoCast"] = MobModeData.NoCast,
        ["CanAttack"] = MobModeData.CanAttack,
        ["CastSensorChase"] = MobModeData.CastSensorChase,
        ["ChangeChase"] = MobModeData.ChangeChase,
        ["Angry"] = MobModeData.Angry,
        ["ChangeTargetMelee"] = MobModeData.ChangeTargetMelee,
        ["ChangeTargetChase"] = MobModeData.ChangeTargetChase,
        ["TargetWeak"] = MobModeData.TargetWeak,
        ["RandomTarget"] = MobModeData.RandomTarget,
        ["IgnoreMelee"] = MobModeData.IgnoreMelee,
        ["IgnoreMagic"] = MobModeData.IgnoreMagic,
        ["IgnoreRanged"] = MobModeData.IgnoreRanged,
        ["Mvp"] = MobModeData.Mvp,
        ["IgnoreMisc"] = MobModeData.IgnoreMisc,
        ["KnockBackImmune"] = MobModeData.KnockBackImmune,
        ["TeleportBlock"] = MobModeData.TeleportBlock,
        ["FixedItemDrop"] = MobModeData.FixedItemDrop,
        ["Detector"] = MobModeData.Detector,
        ["StatusImmune"] = MobModeData.StatusImmune,
        ["SkillImmune"] = MobModeData.SkillImmune,
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
            if (!TryParseSpawnLine(lines[i], sourceFile, i + 1, out var spawn, out var name)) continue;
            if (!string.Equals(name, mobName, StringComparison.Ordinal)) continue;
            if (excludedMaps is not null && excludedMaps.Contains(spawn.Map)) continue;
            results.Add(spawn);
        }
        if (results.Count == 0) throw new ArgumentException($"No '{mobName}' monster spawn declarations were found in the pinned source.");
        return results;
    }

    // Every ordinary `monster` declaration in the file, regardless of mob name/id - the
    // generate-mob-spawns CLI command's own scan needs every declaration, not one name at a time
    // like ReadMobSpawns (which single-mob callers such as compile-mob-spawn still use). Shares the
    // SAME TryParseSpawnLine helper as ReadMobSpawns so there remains exactly one spawn-line parser
    // (task's "strong preference: one shared parser" - RepositoryDomainAnalyzers.AnalyzeMobSpawns
    // already calls ReadMobSpawns per-name; a future cleanup could switch it to this all-at-once
    // form too, but that is not required for parser unification since both paths already bottom out
    // in TryParseSpawnLine).
    internal static IReadOnlyList<MobSpawnData> ReadAllMobSpawns(string spawnScriptText, string sourceFile)
    {
        var results = new List<MobSpawnData>();
        var lines = spawnScriptText.Split('\n');
        for (var i = 0; i < lines.Length; i++)
            if (TryParseSpawnLine(lines[i], sourceFile, i + 1, out var spawn, out _))
                results.Add(spawn);
        return results;
    }

    // Pinned npc_parse_mob (npc.cpp:5218): delay1 defaults to 5000 when the 3rd `w4` field is
    // absent (the local `int32 delay = 5000` initializer, never overwritten by sscanf when that
    // field is missing) - NOT 0. delay2 defaults to 0 (spawn_data is memset to 0 before parsing).
    // DeathEvent/Size/Ai follow the SAME omitted-means-null rule as Xs/Ys above; a present
    // DeathEvent has its optional surrounding quotes (the pinned tree's own convention for real
    // event labels - see SpawnLine's own doc comment) stripped so callers always see one logical
    // string regardless of source quoting style.
    private static bool TryParseSpawnLine(string line, string sourceFile, int sourceLine, out MobSpawnData spawn, out string name)
    {
        var match = SpawnLineRegex().Match(line);
        if (!match.Success) { spawn = null!; name = string.Empty; return false; }
        name = match.Groups["name"].Value;
        var map = match.Groups["map"].Value;
        var count = int.Parse(match.Groups["count"].Value, CultureInfo.InvariantCulture);
        var delay1Group = match.Groups["delay1"];
        var delay1 = delay1Group.Success ? int.Parse(delay1Group.Value, CultureInfo.InvariantCulture) : 5000;
        var delay2Group = match.Groups["delay2"];
        var delay2 = delay2Group.Success ? int.Parse(delay2Group.Value, CultureInfo.InvariantCulture) : 0;
        var x = short.Parse(match.Groups["x"].Value, CultureInfo.InvariantCulture);
        var y = short.Parse(match.Groups["y"].Value, CultureInfo.InvariantCulture);
        var xsGroup = match.Groups["xs"];
        var ysGroup = match.Groups["ys"];
        var xs = xsGroup.Success ? short.Parse(xsGroup.Value, CultureInfo.InvariantCulture) : (short)0;
        var ys = ysGroup.Success ? short.Parse(ysGroup.Value, CultureInfo.InvariantCulture) : (short)0;
        var eventGroup = match.Groups["event"];
        var deathEvent = eventGroup.Success ? StripQuotes(eventGroup.Value) : null;
        var sizeGroup = match.Groups["size"];
        var size = sizeGroup.Success ? ParseSize(sizeGroup.Value, sourceFile, sourceLine) : (MobSizeData?)null;
        var aiGroup = match.Groups["ai"];
        var ai = aiGroup.Success ? int.Parse(aiGroup.Value, CultureInfo.InvariantCulture) : (int?)null;
        spawn = new MobSpawnData(
            map,
            int.Parse(match.Groups["mobid"].Value, CultureInfo.InvariantCulture),
            count,
            delay1,
            delay2,
            sourceFile,
            sourceLine,
            x, y, xs, ys,
            deathEvent, size, ai);
        return true;
    }

    private static string StripQuotes(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    // Fail closed (task section 21): a present spawn-line size field outside pinned SZ_SMALL(0)..
    // SZ_BIG(2) is a genuine unsupported-syntax case, not silently coerced into a meaningless enum
    // member. No real pinned ordinary-monster declaration exercises this today (verified
    // exhaustively - zero rows have a 6th `w4` field at all), but a future pinned revision that adds
    // one must be caught here rather than corrupting generated output.
    private static MobSizeData ParseSize(string raw, string sourceFile, int sourceLine)
    {
        var value = int.Parse(raw, CultureInfo.InvariantCulture);
        if (!Enum.IsDefined(typeof(MobSizeData), value))
            throw new ArgumentException($"Unsupported mob spawn size {value} at {sourceFile}:{sourceLine}; expected 0 (Small), 1 (Medium), or 2 (Big).");
        return (MobSizeData)value;
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
                .Append(", Source: new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(sourceFile).Append("\", ").Append(mob.SourceLine == 0 ? sourceLine : mob.SourceLine).Append(')')
                .Append(", JapaneseName: ").Append(FormatNullableString(mob.JapaneseName))
                .Append(", MaxSp: ").Append(mob.Sp).Append(", MvpExp: ").Append(mob.MvpExp)
                .Append(", Resistance: ").Append(mob.Resistance).Append(", MagicResistance: ").Append(mob.MagicResistance)
                .Append(", SkillRange: ").Append(mob.SkillRange).Append(", ChaseRange: ").Append(mob.ChaseRange)
                .Append(", Size: MobSize.").Append(mob.Size).Append(", Race: MobRace.").Append(mob.Race)
                .Append(", Element: MobElement.").Append(mob.Element).Append(", ElementLevel: ").Append(mob.ElementLevel)
                .Append(", ClientAttackMotion: ").Append(mob.ClientAttackMotion).Append(", DamageTaken: ").Append(mob.DamageTaken)
                .Append(", GroupId: ").Append(mob.GroupId).Append(", Title: ").Append(FormatNullableString(mob.Title))
                .Append(", Class: MobClass.").Append(mob.Class)
                .Append(", RaceGroups: ").Append(FormatRaceGroups(mob.RaceGroups))
                .Append(", Drops: ").Append(FormatDrops(mob.Drops))
                .Append(", MvpDrops: ").Append(FormatDrops(mob.MvpDrops)).AppendLine(");");
        }
        output.AppendLine("}");
        return output.ToString();
    }

    internal static string GenerateMobRegistry(IReadOnlyList<GeneratedMobSymbol> mobs, string commit, string sourceFile)
    {
        var ordered = mobs.OrderBy(item => item.Mob.Id).ToArray();
        var output = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(sourceFile).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            .AppendLine("namespace Athena.Net.MapServer.Generated.GameData.Mobs;")
            .AppendLine()
            .AppendLine("internal static class GeneratedMobRegistry")
            .AppendLine("{")
            .AppendLine("    private static readonly IReadOnlyDictionary<int, MobDefinition> ById = new Dictionary<int, MobDefinition>")
            .AppendLine("    {");
        foreach (var item in ordered)
            output.Append("        [").Append(item.Mob.Id).Append("] = GeneratedMobs.").Append(item.Symbol).AppendLine(",");
        output
            .AppendLine("    };")
            .AppendLine()
            .AppendLine("    internal static int Count => ById.Count;")
            .AppendLine("    internal static IEnumerable<int> Ids => ById.Keys;")
            .AppendLine("    internal static IEnumerable<MobDefinition> All => ById.Values;")
            .AppendLine("    internal static bool TryGet(int mobId, out MobDefinition mob) => ById.TryGetValue(mobId, out mob!);")
            .AppendLine("    internal static MobDefinition Get(int mobId) => ById.TryGetValue(mobId, out var mob)")
            .AppendLine("        ? mob")
            .AppendLine("        : throw new KeyNotFoundException($\"Unknown generated mob Id {mobId}.\");")
            .AppendLine("}");
        return output.ToString();
    }

    internal static bool IsOwnedGeneratedMobFile(string path, string className, string category)
    {
        var name = Path.GetFileName(path);
        if (!(name == $"{className}.Registry.cs" ||
              (name.StartsWith($"{className}.{category}.", StringComparison.Ordinal) && name.EndsWith(".cs", StringComparison.Ordinal))))
            return false;
        using var reader = new StreamReader(path);
        return string.Equals(reader.ReadLine(), "// <auto-generated>", StringComparison.Ordinal) &&
               string.Equals(reader.ReadLine(), "// Generated by Athena.WorldCompiler.", StringComparison.Ordinal);
    }

    // Same safe-stale-cleanup contract as IsOwnedGeneratedMobFile (task section 36: delete only
    // files this generator owns, by filename-prefix PLUS header validation) - adapted for
    // generate-mob-spawns' own one-file-per-source-file shape (`<className>.<Suffix>.cs` for every
    // pinned NPC source file, `<className>.Registry.cs` for the map-keyed aggregation), where
    // "category" has no meaning (there is only ever one category: mob spawns).
    internal static bool IsOwnedGeneratedMobSpawnFile(string path, string className)
    {
        var name = Path.GetFileName(path);
        if (!(name == $"{className}.Registry.cs" ||
              (name.StartsWith($"{className}.", StringComparison.Ordinal) && name.EndsWith(".cs", StringComparison.Ordinal))))
            return false;
        using var reader = new StreamReader(path);
        return string.Equals(reader.ReadLine(), "// <auto-generated>", StringComparison.Ordinal) &&
               string.Equals(reader.ReadLine(), "// Generated by Athena.WorldCompiler.", StringComparison.Ordinal);
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
        foreach (var (spawn, mobDefinitionExpression) in entries) AppendSpawnEntry(output, spawn, mobDefinitionExpression, commit);
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
            foreach (var (spawn, mobDefinitionExpression) in entries) AppendSpawnEntry(output, spawn, mobDefinitionExpression, commit);
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
            foreach (var spawn in spawns) AppendSpawnEntry(output, spawn, mobDefinitionExpression, commit);
            output.AppendLine("    ];");
        }
        output.AppendLine("}");
        return output.ToString();
    }

    // Shared `new(...)` emission for one MobSpawnDefinition, reused by every GenerateMobSpawn*
    // shape above so the emitted argument list/formatting can never drift between them. Only emits
    // DeathEvent/Size/Ai as named arguments when the source actually supplied a value - keeps the
    // overwhelming majority (declarations with none of these three fields) exactly as compact as
    // before this branch added them.
    private static void AppendSpawnEntry(StringBuilder output, MobSpawnData spawn, string mobDefinitionExpression, string commit)
    {
        output.Append("        new(").Append(mobDefinitionExpression).Append(", \"").Append(spawn.Map).Append("\", ")
            .Append(spawn.Count).Append(", ").Append(spawn.RespawnDelay).Append(", ").Append(spawn.RespawnRandomDelay)
            .Append(", new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(spawn.SourceFile).Append("\", ").Append(spawn.SourceLine).Append(')')
            .Append(", X: ").Append(spawn.X).Append(", Y: ").Append(spawn.Y).Append(", Xs: ").Append(spawn.Xs).Append(", Ys: ").Append(spawn.Ys);
        if (spawn.DeathEvent is not null) output.Append(", DeathEvent: \"").Append(spawn.DeathEvent.Replace("\"", "\\\"")).Append('"');
        if (spawn.Size is not null) output.Append(", Size: MobSize.").Append(spawn.Size);
        if (spawn.Ai is not null) output.Append(", Ai: ").Append(spawn.Ai);
        output.AppendLine("),");
    }

    // Emits a C# MobMode expression matching the generated definition's flags exactly - "None"
    // when no bit is set, otherwise a `|`-joined list of every SET MobMode member name (the
    // complete pinned bitmask, not only the runtime-executed subset - see MobModeData's own doc
    // comment).
    private static readonly (MobModeData Bit, string Name)[] ModeBitOrder =
    [
        (MobModeData.CanMove, "CanMove"), (MobModeData.Looter, "Looter"), (MobModeData.Aggressive, "Aggressive"),
        (MobModeData.Assist, "Assist"), (MobModeData.CastSensorIdle, "CastSensorIdle"), (MobModeData.NoRandomWalk, "NoRandomWalk"),
        (MobModeData.NoCast, "NoCast"), (MobModeData.CanAttack, "CanAttack"), (MobModeData.CastSensorChase, "CastSensorChase"),
        (MobModeData.ChangeChase, "ChangeChase"), (MobModeData.Angry, "Angry"), (MobModeData.ChangeTargetMelee, "ChangeTargetMelee"),
        (MobModeData.ChangeTargetChase, "ChangeTargetChase"), (MobModeData.TargetWeak, "TargetWeak"), (MobModeData.RandomTarget, "RandomTarget"),
        (MobModeData.IgnoreMelee, "IgnoreMelee"), (MobModeData.IgnoreMagic, "IgnoreMagic"), (MobModeData.IgnoreRanged, "IgnoreRanged"),
        (MobModeData.Mvp, "Mvp"), (MobModeData.IgnoreMisc, "IgnoreMisc"), (MobModeData.KnockBackImmune, "KnockBackImmune"),
        (MobModeData.TeleportBlock, "TeleportBlock"), (MobModeData.FixedItemDrop, "FixedItemDrop"), (MobModeData.Detector, "Detector"),
        (MobModeData.StatusImmune, "StatusImmune"), (MobModeData.SkillImmune, "SkillImmune"),
    ];

    private static string FormatMode(MobModeData mode)
    {
        if (mode == MobModeData.None) return "MobMode.None";
        var parts = ModeBitOrder.Where(entry => mode.HasFlag(entry.Bit)).Select(entry => "MobMode." + entry.Name).ToArray();
        return string.Join(" | ", parts);
    }

    // Emits a C# null literal or an escaped string literal - JapaneseName/Title are genuinely
    // Optional pinned fields (mob_db.yml doc comment) unlike AegisName/Name, which pinned source
    // treats as always-present per-block identifiers.
    private static string FormatNullableString(string? value) =>
        value is null ? "null" : "\"" + EscapeForCSharpString(value) + "\"";

    // Emits a C# collection-expression literal for RaceGroups - "null" when the pinned block has no
    // RaceGroups: section at all (distinct from an explicit empty list, which pinned source cannot
    // actually produce, since an empty `RaceGroups:` header with no entries never appears in real
    // data - but the record's own nullable default matches "field absent" for every other optional
    // block on this record, so this keeps that convention rather than inventing a special case).
    private static string FormatRaceGroups(IReadOnlyList<MobRaceGroupEntryData> entries)
    {
        if (entries.Count == 0) return "null";
        var parts = entries.Select(entry => $"new MobRaceGroupEntry(\"{EscapeForCSharpString(entry.Name)}\", {(entry.Value ? "true" : "false")})");
        return "[" + string.Join(", ", parts) + "]";
    }

    // Emits a C# collection-expression literal for Drops/MvpDrops - "null" when the pinned block
    // has no such section (see FormatRaceGroups' own doc comment for the same "absent vs empty"
    // rationale).
    private static string FormatDrops(IReadOnlyList<MobDropEntryData> entries)
    {
        if (entries.Count == 0) return "null";
        var parts = entries.Select(entry =>
            $"new MobDropEntry(\"{EscapeForCSharpString(entry.Item)}\", {entry.Rate.ToString(CultureInfo.InvariantCulture)}, {(entry.StealProtected ? "true" : "false")}, {FormatNullableString(entry.RandomOptionGroup)})");
        return "[" + string.Join(", ", parts) + "]";
    }

    private static string EscapeForCSharpString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static int CountLines(string text, int exclusiveEnd)
    {
        var line = 1;
        for (var i = 0; i < exclusiveEnd; i++) if (text[i] == '\n') line++;
        return line;
    }

    private static string RequiredScalar(string block, string field)
    {
        var match = ScalarRegex(field).Match(block);
        if (!match.Success) throw new ArgumentException($"Pinned mob_db.yml block has no '{field}' field.");
        return StripTrailingComment(match.Groups[1].Value);
    }

    // Unlike RequiredScalar, an absent field is a legitimate "use the documented default" case, not
    // an error - mirrors OptionalInt's own absent-field semantics for string-shaped fields
    // (JapaneseName, Title, and the Size/Race/Element/Class enum-shaped scalars).
    private static string? OptionalScalar(string block, string field)
    {
        var match = ScalarRegex(field).Match(block);
        if (!match.Success) return null;
        var raw = StripTrailingComment(match.Groups[1].Value);
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
        return match.Success ? long.Parse(StripTrailingComment(match.Groups[1].Value), CultureInfo.InvariantCulture) : defaultValue;
    }

    // Strips a trailing unquoted YAML end-of-line comment (a '#' preceded by whitespace, per
    // standard YAML comment syntax) from an already-captured scalar value. Real pinned data:
    // every `DamageMotion: 1000    # (unknown)` occurrence across db/re/mob_db.yml (48 real mobs,
    // Ids 22192-22239) - ScalarRegex's own `(.+)$` previously captured the comment text verbatim,
    // so `long.Parse("1000    # (unknown)")` threw a FormatException and those 48 mobs were
    // misreported as genuinely unparseable ("mob-definition:format") rather than fully
    // representable. No real pinned scalar value anywhere in this file contains a quoted '#'
    // (verified: only these 48 DamageMotion lines have '#' in their raw captured text at all), so
    // this unconditional strip is safe - it does not special-case DamageMotion, since a future
    // pinned revision could add the same inline-comment convention to any other field.
    private static readonly Regex TrailingComment = new(@"\s+#.*$", RegexOptions.None);
    private static string StripTrailingComment(string raw) => TrailingComment.Replace(raw, string.Empty);

    // `event` mirrors pinned sscanf's `%77[^,]` - it stops at the next comma (never containing one
    // itself), so `[^,\t\r\n]+` is a faithful capture whether or not the source line wraps it in
    // literal quotes (the pinned tree's own convention for real death-event labels, verified: every
    // real event value found in the inventory is `"map::Label"`-quoted; the inert `0`/`1`
    // placeholder values are bare). Quotes, if present, are stripped by the caller so DeathEvent
    // always stores the same logical string regardless of source quoting style. `size`/`ai` are
    // bare non-negative integers per pinned `%11d` - zero real occurrences exist in the pinned
    // ordinary-monster domain today (verified exhaustively), but the groups exist so a future
    // pinned revision that adds one is captured, not silently truncated the way this regex used to
    // drop everything past delay2.
    private static readonly Regex SpawnLine = new(@"^(?<map>[A-Za-z0-9_]+),(?<x>-?\d+),(?<y>-?\d+)(?:,(?<xs>\d+),(?<ys>\d+))?\t+monster\t+(?<name>[^\t]+)\t+(?<mobid>\d+),(?<count>\d+)(?:,(?<delay1>\d+)(?:,(?<delay2>\d+)(?:,(?<event>[^,\t\r\n]+)(?:,(?<size>\d+)(?:,(?<ai>\d+))?)?)?)?)?", RegexOptions.None);
    private static Regex SpawnLineRegex() => SpawnLine;

    private static Regex ScalarRegex(string field) => new($@"^    {Regex.Escape(field)}: (.+)$", RegexOptions.Multiline);

    // Captures the raw text of a `    Modes:\n      Name: value\n      ...` block: every
    // subsequent 6-space-indented line, OR a column-0 `#`-commented-out line, OR a blank line,
    // stopping at the first line that is none of those (the next top-level `    Field:` entry or
    // the next `  - Id:` block). The `#`/blank tolerance matters for real pinned data: e.g.
    // `db/re/mob_db.yml` has 14 real mobs whose `Modes:` block is interrupted by a column-0 `#...`
    // comment line partway through - an earlier version of this regex (6-space-indent-only) treated
    // that comment as ending the block, silently truncating every mode entry AFTER it. The comment/
    // blank lines are captured but harmless - ModeEntryRegex below only matches genuine
    // `Name: true/false` lines, so a captured `#...` line is simply never matched as an entry.
    private static readonly Regex ModesBlock = new(@"^    Modes:\n((?:(?:      .+|#.*|)\n?)*)", RegexOptions.Multiline);
    private static Regex ModesBlockRegex() => ModesBlock;

    private static readonly Regex ModeEntry = new(@"^\s*(?<name>\w+):\s*(?<value>true|false)\s*$", RegexOptions.Multiline);
    private static Regex ModeEntryRegex() => ModeEntry;

    // Same shape/tolerance as ModesBlock (see that field's own doc comment for the real-data
    // rationale) - pinned RaceGroups: entries use the identical `<Name>: <bool>` shape as Modes:
    // entries, so ModeEntryRegex is reused directly rather than duplicating an identical pattern.
    private static readonly Regex RaceGroupsBlock = new(@"^    RaceGroups:\n((?:(?:      .+|#.*|)\n?)*)", RegexOptions.Multiline);
    private static Regex RaceGroupsBlockRegex() => RaceGroupsBlock;

    // Captures the raw text of a `    <sectionName>:\n      - Item: ...\n        Rate: ...\n      - ...`
    // list block: every subsequent 6-space-indented line (each entry's `- Item:` line and its own
    // further-indented `Rate:`/`StealProtected:`/`RandomOptionGroup:`/`Index:` continuation lines
    // all satisfy this depth), OR a column-0 `#`-commented-out line, OR a blank line - same
    // real-data rationale as ModesBlock above (e.g. the REAL pinned Poring/1002 `Drops:` block has
    // a column-0 `#       RandomOptionGroup: 30L` comment between its `Knife_` and `Sticky_Mucus`
    // entries at db/re/mob_db.yml:171 - an earlier indent-only version of this regex silently
    // truncated Poring's drop table down to 2 of its real 8 entries).
    private static Regex DropsBlockRegex(string sectionName) => new($@"^    {Regex.Escape(sectionName)}:\n((?:(?:      .+|#.*|)\n?)*)", RegexOptions.Multiline);

    // One `- Item: <name>` entry followed by its own optional indented Rate:/StealProtected:/
    // RandomOptionGroup: fields, up to the next `- Item:` entry or the end of the captured block.
    private static readonly Regex DropEntry = new(
        @"^\s*-\s*Item:\s*(?<item>\S+)\s*\n(?<rest>(?:(?!\s*-\s*Item:).*\n?)*)",
        RegexOptions.Multiline);
    private static Regex DropEntryRegex() => DropEntry;
}
