using Athena.Net.CharServer.Db.Entities;

namespace Athena.Net.CharServer.Db;

// The ONE stable, deterministic enumeration order CharServer uses whenever it needs to return a
// character's inventory rows in a consistent sequence (currently only the full inventory-list
// read, for MapServer's initial dense runtime-slot assignment at login). This is NOT a runtime
// inventory "slot" concept - CharServer has no slot concept at all. Each row's own real primary
// key (CharInventory.Id) already reflects its insertion order and never changes, so ordering by
// it is simply the most natural stable order; MapServer is free to (and does) reassign its own
// independent runtime SlotIndex from this sequence and mutate that assignment locally afterward
// (holes on delete, reuse on add) without ever asking CharServer to recompute anything.
internal static class CharInventoryOrdering
{
    public static IOrderedQueryable<CharInventory> InStableOrder(this IQueryable<CharInventory> rows, uint charId) =>
        rows.Where(i => i.CharId == charId).OrderBy(i => i.Id);
}
