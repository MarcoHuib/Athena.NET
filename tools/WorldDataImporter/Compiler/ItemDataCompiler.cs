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

    // Attack/WeaponLevel/SubType/AliasName are general item_db_equip.yml columns (see the
    // file's own header comment: "WeaponLevel  Weapon level. (Default: 1 for Weapons)"), read
    // only for Type: Weapon rows - matching pinned status.cpp:3940's own
    // `sd->inventory_data[index]->type == IT_WEAPON` gate before reading atk/weapon_level.
    internal sealed record ItemDefinitionData(int Id, string AegisName, string Name, string Type, bool Stackable, int? Attack, int? WeaponLevel, WeaponType? WeaponType, int? WeaponViewId);

    internal static ItemDefinitionData ReadItemDefinition(string itemDbYaml, int itemId)
    {
        var block = FindBlockById(itemDbYaml, itemId);

        var aegisName = RequiredScalar(block, "AegisName");
        var name = RequiredScalar(block, "Name");
        var type = RequiredScalar(block, "Type");
        var isWeapon = type == "Weapon";
        // WeaponLevel defaults to 1 when the pinned YAML omits it for a weapon row (file header
        // comment: "Default: 1 for Weapons"), matching pinned rAthena's own item_db loader default.
        var attack = isWeapon ? int.Parse(RequiredScalar(block, "Attack"), CultureInfo.InvariantCulture) : (int?)null;
        var weaponLevel = isWeapon ? (OptionalScalar(block, "WeaponLevel") is { } wl ? int.Parse(wl, CultureInfo.InvariantCulture) : 1) : (int?)null;
        WeaponType? weaponType = null;
        int? weaponViewId = null;
        if (isWeapon)
        {
            var subType = RequiredScalar(block, "SubType");
            if (!WeaponSubTypes.TryGetValue(subType, out var resolved))
                throw new NotSupportedException($"Item SubType '{subType}' has no modeled WeaponType entry yet.");
            weaponType = resolved;

            // Pinned map_session_data::update_look (pc.cpp:623-647): the client-facing
            // LOOK_WEAPON value is the equipped item's AliasName-resolved view_id if the
            // item_db row declares one, else the item's own nameid - never the weapon_type
            // enum. Verified against stock-iRO capture (kill-poring-heal-jobup, frame 210):
            // Knife 1201 has no AliasName, so its wire value is 1201, matching this fallback.
            weaponViewId = OptionalScalar(block, "AliasName") is { } aliasName
                ? FindBlockByAegisName(itemDbYaml, aliasName).Id
                : itemId;
        }
        return new ItemDefinitionData(itemId, aegisName, name, type, !NonStackableTypes.Contains(type), attack, weaponLevel, weaponType, weaponViewId);
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
    // concrete ItemDefinition subtype. Any other pinned Type (Usable, Healing, Armor, Card,
    // Ammo, Cash, ShadowGear, PetEgg, PetArmor, ...) must fail generation loudly rather than
    // silently collapsing into EtcItemDefinition - an unmodeled type is not "the same as Etc",
    // it is simply not supported yet.
    private static string ResolveConcreteTypeName(string type) => type switch
    {
        "Weapon" => "WeaponItemDefinition",
        "Etc" => "EtcItemDefinition",
        _ => throw new NotSupportedException($"Item Type '{type}' has no modeled ItemDefinition subtype yet."),
    };

    internal static string Generate(ItemDefinitionData item, string commit, string className, string constantName, string sourceFile, int sourceLine)
    {
        var typeName = ResolveConcreteTypeName(item.Type);
        var isWeapon = item.Type == "Weapon";

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
            .Append("        AegisName: \"").Append(item.AegisName).AppendLine("\",")
            .Append("        Name: \"").Append(item.Name).AppendLine("\",")
            .Append("        Stackable: ").Append(item.Stackable ? "true" : "false").AppendLine(",");

        if (isWeapon)
        {
            builder
                .Append("        Attack: ").Append(item.Attack!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(",")
                .Append("        WeaponLevel: ").Append(item.WeaponLevel!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(",")
                .Append("        WeaponType: WeaponType.").Append(item.WeaponType!.Value).AppendLine(",")
                .Append("        WeaponViewId: ").Append(item.WeaponViewId!.Value.ToString(CultureInfo.InvariantCulture)).AppendLine(",");
        }

        builder
            .Append("        Source: new WorldSourceInfo(\"rAthena\", \"").Append(commit).Append("\", \"").Append(sourceFile).Append("\", ").Append(sourceLine).AppendLine("));")
            .AppendLine("}");
        return builder.ToString();
    }

    private static string RequiredScalar(string block, string field)
    {
        var match = Regex.Match(block, $@"^    {Regex.Escape(field)}: (.+)$", RegexOptions.Multiline);
        if (!match.Success) throw new ArgumentException($"Pinned item block has no '{field}' field.");
        return match.Groups[1].Value;
    }

    private static string? OptionalScalar(string block, string field)
    {
        var match = Regex.Match(block, $@"^    {Regex.Escape(field)}: (.+)$", RegexOptions.Multiline);
        return match.Success ? match.Groups[1].Value : null;
    }
}
