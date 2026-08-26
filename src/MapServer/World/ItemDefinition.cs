namespace Athena.Net.MapServer.World;

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
// Attack, WeaponLevel), not tutorial-specific - any weapon item uses this type.
// WeaponType is a plain data field (pinned item_db `Type` sub-value / View.WeaponType),
// not a subclass - combat logic that needs to branch on weapon type does so on the
// field's value, mirroring pinned `sd->status.weapon` (status.hpp), not on C# type.
public sealed record WeaponItemDefinition(
    int Id,
    string AegisName,
    string Name,
    bool Stackable,
    int Attack,
    int WeaponLevel,
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
