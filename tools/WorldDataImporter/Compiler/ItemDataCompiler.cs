using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Athena.WorldCompiler.Generation;

// Compiles one pinned rAthena item_db_etc.yml (or sibling) `- Id:` block.
// Stackability follows item_data::isStackable (itemdb.cpp): every Type except
// Weapon/Armor/PetEgg/PetArmor/ShadowGear is stackable. Etc items (Wood's
// Type) are always stackable, so this compiler does not need to special-case
// Wood - it reads Type generically the same way it reads AegisName/Id.
// Pinned rAthena enum weapon_type (map/pc.hpp:959) - values copied exactly. This is a
// standalone mirror of Athena.Net.MapServer.World.WeaponType: the tool project intentionally
// has no reference to MapServer (MapServer's own Generated/GameData output is produced BY this
// tool, so depending on MapServer here would invert that build order), so this compiler treats
// SubType as this real, strongly-typed enum rather than a raw byte, and stringifies it (via
// nameof) only at generated-source emission time.
internal enum WeaponType : byte
{
    Fist = 0,
    Dagger = 1,
    OneHandSword = 2,
    TwoHandSword = 3,
    OneHandSpear = 4,
    TwoHandSpear = 5,
    OneHandAxe = 6,
    TwoHandAxe = 7,
    Mace = 8,
    TwoHandMace = 9,
    Staff = 10,
    Bow = 11,
    Knuckle = 12,
    Musical = 13,
    Whip = 14,
    Book = 15,
    Katar = 16,
    Revolver = 17,
    Rifle = 18,
    Gatling = 19,
    Shotgun = 20,
    Grenade = 21,
    Huuma = 22,
    TwoHandStaff = 23,
}

internal static class ItemDataCompiler
{
    private static readonly HashSet<string> NonStackableTypes = new(StringComparer.Ordinal) { "Weapon", "Armor", "PetEgg", "PetArmor", "ShadowGear" };

    // Pinned item_db SubType column name -> this compiler's WeaponType. item_db's SubType is
    // parsed as "W_" + SubType against the exact same pinned enum (itemdb.cpp:158-168).
    private static readonly Dictionary<string, WeaponType> WeaponSubTypes = new(StringComparer.Ordinal)
    {
        ["Fist"] = WeaponType.Fist,
        ["Dagger"] = WeaponType.Dagger,
        ["1hSword"] = WeaponType.OneHandSword,
        ["2hSword"] = WeaponType.TwoHandSword,
        ["1hSpear"] = WeaponType.OneHandSpear,
        ["2hSpear"] = WeaponType.TwoHandSpear,
        ["1hAxe"] = WeaponType.OneHandAxe,
        ["2hAxe"] = WeaponType.TwoHandAxe,
        ["Mace"] = WeaponType.Mace,
        ["2hMace"] = WeaponType.TwoHandMace,
        ["Staff"] = WeaponType.Staff,
        ["Bow"] = WeaponType.Bow,
        ["Knuckle"] = WeaponType.Knuckle,
        ["Musical"] = WeaponType.Musical,
        ["Whip"] = WeaponType.Whip,
        ["Book"] = WeaponType.Book,
        ["Katar"] = WeaponType.Katar,
        ["Revolver"] = WeaponType.Revolver,
        ["Rifle"] = WeaponType.Rifle,
        ["Gatling"] = WeaponType.Gatling,
        ["Shotgun"] = WeaponType.Shotgun,
        ["Grenade"] = WeaponType.Grenade,
        ["Huuma"] = WeaponType.Huuma,
        ["2hStaff"] = WeaponType.TwoHandStaff,
    };

    // Pinned item_db `Locations` YAML key -> enum equip_pos bitmask value (mmo.hpp:335-353).
    // Most keys map directly via "EQP_" + key (e.g. "Armor" -> EQP_ARMOR); a handful have an
    // explicit alias registered in pinned script_constants.hpp:911-917 (e.g. "Right_Hand" ->
    // EQP_HAND_R, not a literal EQP_RIGHT_HAND). Extend only as new Locations keys are needed.
    private static readonly Dictionary<string, uint> EquipLocations = new(StringComparer.Ordinal)
    {
        ["Head_Low"] = 0x000001,
        ["Right_Hand"] = 0x000002,
        ["Garment"] = 0x000004,
        ["Right_Accessory"] = 0x000008,
        ["Armor"] = 0x000010,
        ["Left_Hand"] = 0x000020,
        ["Shoes"] = 0x000040,
        ["Left_Accessory"] = 0x000080,
        ["Head_Top"] = 0x000100,
        ["Head_Mid"] = 0x000200,
    };

    // Attack/WeaponLevel/SubType/Locations/AliasName are general item_db_equip.yml columns (see
    // the file's own header comment: "WeaponLevel  Weapon level. (Default: 1 for Weapons)"),
    // read only for Type: Weapon/Armor rows - matching pinned status.cpp:3940's own
    // `sd->inventory_data[index]->type == IT_WEAPON` gate before reading atk/weapon_level.
    // ClientViewId is read for every item, not just equip-capable ones - it is a general item_db
    // concept (client_nameid(), clif.cpp:144-151). Grants is read only for Type: Usable rows
    // whose Script block matches the narrow getitem-only shape this compiler recognizes (see
    // TryParseGetItemScript) - null for every other row, including a Usable row with no Script
    // or with a script this compiler cannot represent (that case throws instead, see below).
    internal sealed record ItemDefinitionData(int Id, string AegisName, string Name, string Type, bool Stackable, int ClientViewId, int? Attack, int? WeaponLevel, WeaponType? WeaponType, int? Range, uint? EquipLocation, IReadOnlyList<(int ItemId, uint Amount)>? Grants);

    internal static ItemDefinitionData ReadItemDefinition(string itemDbYaml, int itemId)
    {
        var block = FindBlockById(itemDbYaml, itemId);

        var aegisName = RequiredScalar(block, "AegisName");
        var name = RequiredScalar(block, "Name");
        var type = RequiredScalar(block, "Type");
        var isWeapon = type == "Weapon";
        var isArmor = type == "Armor";

        // Pinned map_session_data::update_look / client_nameid() (pc.cpp:623-647,
        // clif.cpp:144-151): the client-facing item identity is the AliasName-resolved
        // view_id if the item_db row declares one, else the item's own nameid - applies to
        // every item, not just weapons. Verified against stock-iRO capture
        // (kill-poring-heal-jobup, frame 210): Knife 1201 has no AliasName, so its wire value
        // is 1201, matching this fallback.
        var clientViewId = OptionalScalar(block, "AliasName") is { } aliasName
            ? FindBlockByAegisName(itemDbYaml, aliasName).Id
            : itemId;

        // WeaponLevel defaults to 1 when the pinned YAML omits it for a weapon row (file header
        // comment: "Default: 1 for Weapons"), matching pinned rAthena's own item_db loader default.
        var attack = isWeapon ? int.Parse(RequiredScalar(block, "Attack"), CultureInfo.InvariantCulture) : (int?)null;
        var weaponLevel = isWeapon ? (OptionalScalar(block, "WeaponLevel") is { } wl ? int.Parse(wl, CultureInfo.InvariantCulture) : 1) : (int?)null;
        // Pinned Range column (file header: "Weapon's attack range. (Default: 0)") - read
        // generically for every Type: Weapon row, never special-cased per item id.
        var range = isWeapon ? (OptionalScalar(block, "Range") is { } rangeText ? int.Parse(rangeText, CultureInfo.InvariantCulture) : 0) : (int?)null;
        WeaponType? weaponType = null;
        if (isWeapon)
        {
            var subType = RequiredScalar(block, "SubType");
            if (!WeaponSubTypes.TryGetValue(subType, out var resolved))
                throw new NotSupportedException($"Item SubType '{subType}' has no modeled WeaponType entry yet.");
            weaponType = resolved;
        }

        uint? equipLocation = null;
        if (isWeapon || isArmor)
        {
            equipLocation = ReadLocations(block);
        }

        // Only Type: Usable rows are examined for a getitem-shaped Script - Healing/DelayConsume/
        // etc. rows never carry Grants (their Script, if any, is a different unmodeled effect
        // entirely and must not be misread as a container).
        var grants = type == "Usable" ? TryParseGetItemScript(block) : null;

        return new ItemDefinitionData(itemId, aegisName, name, type, !NonStackableTypes.Contains(type), clientViewId, attack, weaponLevel, weaponType, range, equipLocation, grants);
    }

    // Recognizes ONLY the narrow script shape this project models: a Script block consisting
    // entirely of one or more `getitem <constant item id>,<constant amount>;` statements
    // (pinned script.cpp BUILDIN_FUNC(getitem), item_db.yml Script column) - a container/
    // item-group-opening usable, e.g. First Aid Box. Returns null (no Grants) when the row has
    // no Script block at all, OR when its Script's first statement is not `getitem` at all -
    // that is simply a different, currently-unmodeled item effect (a heal/skill/status/etc.
    // script), not a container this compiler misrepresented; leaving Grants empty is the
    // correct "not implemented" representation for that case, matching how this compiler
    // already leaves Healing's itemheal effect entirely unmodeled.
    //
    // Once a script commits to looking like a container (its first statement IS getitem), every
    // remaining statement MUST also be a constant getitem - a mixed script (getitem followed by
    // anything else) throws rather than silently parsing only the getitem lines and dropping
    // the rest, because at that point representing only the getitem prefix WOULD misstate the
    // item's real combined effect. This project has no general rAthena script interpreter.
    private static IReadOnlyList<(int ItemId, uint Amount)>? TryParseGetItemScript(string block)
    {
        var scriptMatch = Regex.Match(block, @"^    Script: \|\n((?:      .*\n?)+)", RegexOptions.Multiline);
        if (!scriptMatch.Success) return null;

        var lines = scriptMatch.Groups[1].Value.Split('\n').Select(l => l.Trim()).Where(l => l.Length > 0).ToList();
        if (lines.Count == 0) return null;
        if (!Regex.IsMatch(lines[0], @"^getitem\s+\d+\s*,\s*\d+\s*;$")) return null;

        var grants = new List<(int ItemId, uint Amount)>();
        foreach (var line in lines)
        {
            var getItemMatch = Regex.Match(line, @"^getitem\s+(\d+)\s*,\s*(\d+)\s*;$");
            if (!getItemMatch.Success)
            {
                throw new NotSupportedException(
                    $"Item Script line '{line}' is not a recognized constant getitem statement; this compiler does not implement a general script interpreter.");
            }

            grants.Add((
                int.Parse(getItemMatch.Groups[1].Value, CultureInfo.InvariantCulture),
                uint.Parse(getItemMatch.Groups[2].Value, CultureInfo.InvariantCulture)));
        }

        return grants;
    }

    // Pinned item_db Locations block (itemdb.cpp:446-475): a set of `KeyName: true` entries at
    // 6-space indent under a `    Locations:` header, OR'd together into the item's possible
    // equip-position bitmask.
    private static uint ReadLocations(string block)
    {
        var locationsMatch = Regex.Match(block, @"^    Locations:\n((?:      \S+: true\n?)+)", RegexOptions.Multiline);
        if (!locationsMatch.Success) throw new ArgumentException("Pinned item block has no 'Locations' field.");

        uint equip = 0;
        foreach (Match entry in Regex.Matches(locationsMatch.Groups[1].Value, @"^      (\S+): true$", RegexOptions.Multiline))
        {
            var key = entry.Groups[1].Value;
            if (!EquipLocations.TryGetValue(key, out var value))
                throw new NotSupportedException($"Item Locations key '{key}' has no modeled equip_pos entry yet.");
            equip |= value;
        }
        return equip;
    }

    private static string FindBlockById(string itemDbYaml, int itemId)
    {
        var marker = $"  - Id: {itemId.ToString(CultureInfo.InvariantCulture)}\n";
        var start = itemDbYaml.IndexOf(marker, StringComparison.Ordinal);
        if (start < 0) throw new ArgumentException($"Item Id {itemId} was not found in the pinned item database.");
        return ExtractBlock(itemDbYaml, start);
    }

    // Mirrors pinned item_db.search_aegisname() (itemdb.cpp): resolves an AliasName column
    // (an AegisName reference to another item in the same pinned document) to that item's Id.
    private static (string Block, int Id) FindBlockByAegisName(string itemDbYaml, string aegisName)
    {
        var marker = $"\n    AegisName: {aegisName}\n";
        var nameIndex = itemDbYaml.IndexOf(marker, StringComparison.Ordinal);
        if (nameIndex < 0) throw new ArgumentException($"AliasName '{aegisName}' was not found in the pinned item database.");
        var idMarkerStart = itemDbYaml.LastIndexOf("\n  - Id: ", nameIndex, StringComparison.Ordinal) + 1;
        var idStart = idMarkerStart + "  - Id: ".Length;
        var idEnd = itemDbYaml.IndexOf('\n', idStart);
        var id = int.Parse(itemDbYaml[idStart..idEnd], CultureInfo.InvariantCulture);
        return (ExtractBlock(itemDbYaml, idMarkerStart), id);
    }

    private static string ExtractBlock(string itemDbYaml, int start)
    {
        var next = itemDbYaml.IndexOf("\n  - Id: ", start + 1, StringComparison.Ordinal);
        return next >= 0 ? itemDbYaml[start..(next + 1)] : itemDbYaml[start..];
    }

    // Explicit discriminator: only pinned Types this compiler has actually modeled map to a
    // concrete ItemDefinition subtype. Any other pinned Type (Healing, Card, Ammo, Cash,
    // ShadowGear, PetEgg, PetArmor, ...) must fail generation loudly rather than silently
    // collapsing into EtcItemDefinition - an unmodeled type is not "the same as Etc", it is
    // simply not supported yet.
    private static string ResolveConcreteTypeName(string type) => type switch
    {
        "Weapon" => "WeaponItemDefinition",
        "Armor" => "ArmorItemDefinition",
        "Etc" => "EtcItemDefinition",
        "Usable" => "UsableItemDefinition",
        "Healing" => "HealingItemDefinition",
        "DelayConsume" => "DelayConsumeItemDefinition",
        _ => throw new NotSupportedException($"Item Type '{type}' has no modeled ItemDefinition subtype yet."),
    };

    internal static string Generate(ItemDefinitionData item, string commit, string className, string constantName, string sourceFile, int sourceLine)
    {
        var typeName = ResolveConcreteTypeName(item.Type);
        var isWeapon = item.Type == "Weapon";
        var isArmor = item.Type == "Armor";

        var builder = new StringBuilder()
            .AppendLine("// <auto-generated>")
            .AppendLine("// Generated by Athena.WorldCompiler.")
            .Append("// Source: ").Append(sourceFile).Append(':').Append(sourceLine.ToString(CultureInfo.InvariantCulture)).AppendLine()
            .Append("// rAthena commit: ").AppendLine(commit)
            .AppendLine("// Do not edit this file directly.")
            .AppendLine("// </auto-generated>")
            .AppendLine("using Athena.Net.MapServer.World;")
            .AppendLine()
            // Global game data ("what is item <id>") - referenceable from any world slice, not
            // world/placement data, so this does not live under Generated.World.Izlude.Academy.
            .AppendLine("namespace Athena.Net.MapServer.Generated.GameData.Items;")
            .AppendLine()
            .Append("internal static class ").AppendLine(className)
            .AppendLine("{")
            .Append("    internal static readonly ").Append(typeName).Append(' ').Append(constantName).AppendLine(" = new(")
            .Append("        Id: ").Append(item.Id).AppendLine(",")
            .Append("        AegisName: \"").Append(EscapeForCSharpString(item.AegisName)).AppendLine("\",")
            .Append("        Name: \"").Append(EscapeForCSharpString(item.Name)).AppendLine("\",")
            .Append("        Stackable: ").Append(item.Stackable ? "true" : "false").AppendLine(",")
            .Append("        ClientViewId: ").Append(item.ClientViewId.ToString(CultureInfo.InvariantCulture)).AppendLine(",");

        if (isWeapon)
        {
            builder
                .Append("        Attack: ").Append(item.Attack!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(",")
                .Append("        WeaponLevel: ").Append(item.WeaponLevel!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(",")
                .Append("        WeaponType: WeaponType.").Append(item.WeaponType!.Value).AppendLine(",")
                .Append("        Range: ").Append(item.Range!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(",")
                .Append("        EquipLocation: 0x").Append(item.EquipLocation!.Value.ToString("X6", CultureInfo.InvariantCulture)).AppendLine(",");
        }
        else if (isArmor)
        {
            builder
                .Append("        EquipLocation: 0x").Append(item.EquipLocation!.Value.ToString("X6", CultureInfo.InvariantCulture)).AppendLine(",");
        }

        builder
            .Append("        Source: new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(sourceFile).Append("\", ").Append(sourceLine).Append(')');

        if (item.Grants is { Count: > 0 } grants)
        {
            builder.AppendLine(",").Append("        Grants: [");
            builder.Append(string.Join(", ", grants.Select(g => $"new ItemGrantDefinition({g.ItemId}, {g.Amount})")));
            builder.Append(']');
        }

        builder.AppendLine(");").AppendLine("}");
        return builder.ToString();
    }

    private static string RequiredScalar(string block, string field)
    {
        var match = Regex.Match(block, $@"^    {Regex.Escape(field)}: (.+)$", RegexOptions.Multiline);
        if (!match.Success) throw new ArgumentException($"Pinned item block has no '{field}' field.");
        return UnquoteYamlScalar(match.Groups[1].Value);
    }

    // A pinned item_db YAML scalar is double-quoted only when its raw text would otherwise be
    // ambiguous to the YAML parser (e.g. Center_Potion_B's Name: "[Not For Sale] ..." - a
    // leading '[' would otherwise start a flow sequence). Strip that YAML-level quoting so
    // downstream C# string-literal emission (see EscapeForCSharpString) works from the real
    // display text, not the raw YAML token.
    private static string UnquoteYamlScalar(string value) =>
        value.Length >= 2 && value[0] == '"' && value[^1] == '"' ? value[1..^1] : value;

    private static string EscapeForCSharpString(string value) => value.Replace("\\", "\\\\").Replace("\"", "\\\"");

    private static string? OptionalScalar(string block, string field)
    {
        var match = Regex.Match(block, $@"^    {Regex.Escape(field)}: (.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }
}
