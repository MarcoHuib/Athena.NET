namespace Athena.WorldCompiler.Generation;

// Explicit, deliberate Athena.NET additions beyond the pinned rAthena Renewal default loadout
// (RathenaScriptConfigGraph.ResolveActiveNpcFiles). Every entry here is a conscious decision to
// activate source content that the pinned config graph itself leaves disabled - never inferred
// from a physical folder/path shape (see ai/world-data.md's "AthenaOverlay" classification). Adding
// an entry here is the ONLY sanctioned way pinned-disabled source content becomes runtime-active in
// the AthenaIroEffective load profile.
internal static class AthenaOverlaySourceFiles
{
    internal static readonly IReadOnlySet<string> Files = new HashSet<string>(StringComparer.Ordinal)
    {
        // Athena.NET tutorial content (Academy: iz_int/int_land families) - pinned-disabled at
        // npc/re/scripts_monsters.conf:5 ("//npc: npc/re/mobs/academy.txt"). Already generated and
        // registered into the runtime world today (see ai/world-data.md's "World Definition Model");
        // this entry makes that pre-existing intentional deviation explicit and test-covered rather
        // than an unrecorded side effect of filesystem-wide scanning.
        "npc/re/mobs/academy.txt",
    };

    // Athena.NET additions to the Renewal-active WARP set (AthenaIroEffective warp profile). Empty by
    // design: every Academy/tutorial door lives in npc/re/warps/cities/izlude.txt, which the pinned
    // Renewal graph already loads. A pinned-disabled warp file becomes runtime-active only by an
    // explicit entry here, never because it happens to sit earlier in a folder scan.
    internal static readonly IReadOnlySet<string> WarpFiles = new HashSet<string>(StringComparer.Ordinal);
}
