namespace Athena.Net.MapServer.World;

// Immutable, source-backed item data (pinned rAthena db/re/item_db_etc.yml and
// siblings). Only the fields this vertical slice's inventory-add path needs.
public sealed record ItemDefinition(int Id, string AegisName, string Name, bool Stackable, WorldSourceInfo Source);
