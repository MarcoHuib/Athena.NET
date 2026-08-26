using Athena.Net.CharServer.Db.Entities;

namespace Athena.Net.CharServer.Db;

// The ONE authoritative definition of a character's stable server-side inventory
// SlotIndex ordering. Pinned rAthena builds sd->inventory.u.items_inventory[] by
// loading a character's rows in a stable order (client_index() then adds a fixed
// +2 wire offset - clif.cpp:122). Real rAthena's own `inventory` table has no
// persisted slot/position column either (sql-files/main.sql) - the server-side
// array index is derived purely from load order, not stored state. Athena mirrors
// that: a row's own primary key already reflects (and never changes) its relative
// insertion order among this character's rows, so ordering by Id reproduces the
// same stable 0-based array position a real load pass would assign.
//
// Equipped and unequipped rows share this SAME namespace - equip state is not a
// slot-partitioning concern (mirrors pc.cpp: equipped items still occupy their own
// inventory array index; only sd->equip_index[] separately tracks which array
// index is equipped in which EQI_* slot). Every caller that needs to resolve or
// compute a SlotIndex (inventory-list read, inventory-add slot derivation, equip
// update by slot) MUST go through this one ordering, never redefine it locally -
// see MapServerSession.HandleInventoryListGetAsync/HandleInventoryAddRequestAsync/
// HandleInventoryEquipUpdateAsync, all of which now call this.
internal static class CharInventoryOrdering
{
    public static IOrderedQueryable<CharInventory> InStableSlotOrder(this IQueryable<CharInventory> rows, uint charId) =>
        rows.Where(i => i.CharId == charId).OrderBy(i => i.Id);
}
