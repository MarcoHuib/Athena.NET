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
public abstract record ItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    WorldSourceInfo Source);

// Weapon fields are general item_db columns (legacy/rathena/db/re/item_db_equip.yml:
// Attack, WeaponLevel, SubType, AliasName), not tutorial-specific - any weapon item uses this
// type. WeaponType is a plain data field (pinned item_db `SubType`, parsed as "W_" + SubType
// against enum weapon_type - itemdb.cpp:158-168), not a subclass - combat/appearance logic that
// needs to branch on weapon type does so on the field's value, mirroring pinned
// `sd->status.weapon`, not on C# type.
//
// WeaponViewId is the SEPARATE client-facing appearance value (ZC_SPRITE_CHANGE/0x01D7's
// LOOK_WEAPON val) - pinned map_session_data::update_look (pc.cpp:623-647): the equipped
// item's `AliasName`-resolved view_id if the item_db row declares one, else the item's own
// nameid (Id). Most items (including the starter Knife) have no AliasName, so this equals Id -
// but it is a distinct concept from WeaponType and must never be derived from it or conflated
// with it (verified stock-iRO capture: Knife's LOOK_WEAPON val=1201=Id, not 1=W_DAGGER).
public sealed record WeaponItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    int Attack,
    int WeaponLevel,
    WeaponType WeaponType,
    int WeaponViewId,
    WorldSourceInfo Source)
    : ItemDefinition(Id, AegisName, Name, Stackable, Source);

// Non-weapon, non-armor items (pinned Type: Etc, Usable, etc.). No combat-relevant
// fields yet - extend with new fields only when a traced use case needs them.
public sealed record EtcItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    WorldSourceInfo Source)
    : ItemDefinition(Id, AegisName, Name, Stackable, Source);
