namespace Athena.Net.MapServer.World;

// Pinned rAthena enum weapon_type (map/pc.hpp:959) - values match exactly. This is the
// combat-relevant weapon classification (sd->status.weapon, battle_calc_base_damage's
// weapon-roll term, size_fix_db lookups, etc.) - NOT the ZC_SPRITE_CHANGE (0x01D7) client
// appearance value. Those are genuinely different pinned concepts that happen to share no
// numeric relationship: verified stock-iRO capture (kill-poring-heal-jobup, frame 210, post-
// 0x007D) shows LOOK_WEAPON val=0x000004B1=1201 for the Knife (W_DAGGER=1), proving the wire
// value is NOT the weapon_type enum. See WeaponItemDefinition.WeaponViewId for the appearance
// value, derived via map_session_data::update_look (pc.cpp:623-647).
public enum WeaponType : byte
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

// Immutable, source-backed item data (pinned rAthena db/re/item_db_etc.yml,
// item_db_equip.yml, and siblings). Concrete type is selected from the pinned
// `Type` column at generation time, so invalid states (a weapon with no
// Attack, a non-weapon with an Attack) are unrepresentable - no nullable
// weapon-only fields on a shared base.
//
// ClientViewId is the client-facing item identity used on EVERY item-bearing wire packet
// (ZC_SPRITE_CHANGE/0x01D7's LOOK_WEAPON val, and ITID in the normal/equip inventory-list
// packets, 0x0B09/0x0B39) - pinned client_nameid() (clif.cpp:144-151) / map_session_data::
// update_look (pc.cpp:623-647): the item's `AliasName`-resolved view_id if the item_db row
// declares one, else the item's own nameid (Id). This is a general item_db concept (every
// item type can have an AliasName), not weapon-specific - most items (including the starter
// Knife and armor) have no AliasName, so this equals Id. Verified stock-iRO capture
// (kill-poring-heal-jobup, frame 210): Knife 1201's LOOK_WEAPON val=1201=Id, NOT its
// weapon_type enum value - ClientViewId and WeaponType must never be conflated.
public abstract record ItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    int ClientViewId,
    WorldSourceInfo Source);

// Shared by every equip-capable concrete ItemDefinition (weapons, armor, and any future
// equip-slot item type). EquipLocation is the item_db's possible-equip-location bitmask
// (pinned `enum equip_pos`, item_db `Locations` column, e.g. EQP_HAND_R=2, EQP_ARMOR=16) -
// pinned pc_equippoint()/pc_equippoint_sub() (pc.cpp:1490-1495) return exactly this value,
// and it feeds EQUIPITEM_INFO.location in the equip-list packet (clif_item_equip,
// clif.cpp:2946). Deliberately a small interface, not deep inheritance, so equip-list packet
// code can do `item is IEquippableItemDefinition equippable` without caring whether the
// concrete type is a weapon or armor.
public interface IEquippableItemDefinition
{
    uint EquipLocation { get; }
}

// Weapon fields are general item_db columns (legacy/rathena/db/re/item_db_equip.yml:
// Attack, WeaponLevel, SubType, Locations), not tutorial-specific - any weapon item uses this
// type. WeaponType is a plain data field (pinned item_db `SubType`, parsed as "W_" + SubType
// against enum weapon_type - itemdb.cpp:158-168), not a subclass - combat/appearance logic that
// needs to branch on weapon type does so on the field's value, mirroring pinned
// `sd->status.weapon`, not on C# type. See ItemDefinition.ClientViewId for the SEPARATE
// client-facing appearance/identity value - never derive one from the other.
// Range is the item_db_equip.yml `Range` column (file header: "Weapon's attack range. (Default:
// 0)") - the pinned RAW per-item value, before status_calc_pc_'s own floor-at-1 clamp
// (status.cpp:4216: "if(base_status->rhw.range < 1) base_status->rhw.range = 1;"). Consumers that
// need the AUTHORITATIVE effective basic-attack range (what status_get_range/rhw.range actually
// resolves to for combat) must apply that floor themselves - see BasicAttackRangeResolver - rather
// than assuming every weapon's raw Range is already >=1. Read generically from the pinned Range
// column for every Type: Weapon row (see ItemDataCompiler.ReadItemDefinition) - never
// special-cased per item id.
public sealed record WeaponItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    int ClientViewId,
    int Attack,
    int WeaponLevel,
    WeaponType WeaponType,
    int Range,
    uint EquipLocation,
    WorldSourceInfo Source)
    : ItemDefinition(Id, AegisName, Name, Stackable, ClientViewId, Source), IEquippableItemDefinition;

// Armor-slot equipment (pinned Type: Armor - EQP_ARMOR/EQP_HEAD_*/EQP_SHOES/etc via item_db
// Locations). No combat-relevant fields yet (Defense/ArmorLevel unmodeled) - extend only when a
// traced use case needs them, mirroring WeaponItemDefinition's own extension history.
public sealed record ArmorItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    int ClientViewId,
    uint EquipLocation,
    WorldSourceInfo Source)
    : ItemDefinition(Id, AegisName, Name, Stackable, ClientViewId, Source), IEquippableItemDefinition;

// Pinned Type: Etc items only (e.g. Wood, quest materials). Never a catch-all for other
// unmodeled types - see ItemDataCompiler.ResolveConcreteTypeName's explicit discriminator.
// No combat-relevant fields yet - extend only when a traced use case needs them.
public sealed record EtcItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    int ClientViewId,
    WorldSourceInfo Source)
    : ItemDefinition(Id, AegisName, Name, Stackable, ClientViewId, Source);

// One pinned `getitem <id>,<amount>;` statement from a Type: Usable item's item_db Script
// block (script.cpp BUILDIN_FUNC(getitem)) - the ONLY script shape ItemDataCompiler currently
// recognizes for a container/item-group-opening usable (see its own doc comment). Not a
// general script AST node - this project has no script interpreter; a script that contains
// anything other than a sequence of these fails generation loudly rather than being partially
// represented.
public sealed record ItemGrantDefinition(int ItemId, uint Amount);

// Pinned Type: Usable items (consumables, container/item-group openers like First Aid Box,
// etc.). Grants is empty for an ordinary usable with no item_db Script (or one this compiler's
// getitem-only recognizer does not apply to) - a non-empty Grants list is exactly and only the
// source-derived representation of a `getitem` sequence (see ItemGrantDefinition). This
// project has no general item-effect/status/skill modeling yet - only the getitem-container
// case is represented, matching the narrow traced use case (First Aid Box 23484). A distinct
// type from EtcItemDefinition even where Grants is empty, matching the no-catch-all
// discriminator convention: Usable and Etc are different pinned item_types (mmo.hpp:223-238).
public sealed record UsableItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    int ClientViewId,
    WorldSourceInfo Source,
    IReadOnlyList<ItemGrantDefinition>? Grants = null)
    : ItemDefinition(Id, AegisName, Name, Stackable, ClientViewId, Source)
{
    public IReadOnlyList<ItemGrantDefinition> Grants { get; init; } = Grants ?? [];
}

// Pinned Type: Healing items (mmo.hpp IT_HEALING - HP/SP-restoring consumables, e.g. potions).
// Fields intentionally match UsableItemDefinition's own current shallow-extension state: only
// the common source-backed identity fields are modeled. This type exists purely so pinned
// Type: Healing rows are representable as authoritative inventory data (they can be granted by
// a container's getitem script, or otherwise exist in a character's inventory) - their actual
// healing effect (itemheal) is explicitly OUT OF SCOPE and unimplemented; using a healing item
// is a separate future vertical slice. Never collapsed into UsableItemDefinition or
// EtcItemDefinition - Healing is a distinct pinned item_type and must remain distinguishable by
// C# type, matching every other concrete ItemDefinition in this file.
public sealed record HealingItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    int ClientViewId,
    WorldSourceInfo Source)
    : ItemDefinition(Id, AegisName, Name, Stackable, ClientViewId, Source);

// Pinned Type: DelayConsume items (mmo.hpp IT_DELAYCONSUME - consumption is deferred until the
// item's associated skill/effect actually completes, e.g. Novice Magnifier's itemskill call).
// Same shallow-extension rationale as HealingItemDefinition: modeled only so pinned
// Type: DelayConsume rows are representable as authoritative inventory data; the actual
// delay-consume/itemskill behavior is explicitly OUT OF SCOPE and unimplemented.
public sealed record DelayConsumeItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    int ClientViewId,
    WorldSourceInfo Source)
    : ItemDefinition(Id, AegisName, Name, Stackable, ClientViewId, Source);
