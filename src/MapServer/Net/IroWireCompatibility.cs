namespace Athena.Net.MapServer.Net;

// Explicit, narrowly-scoped overrides where a VERIFIED stock-iRO capture provably diverges from
// pinned rAthena generated source data. Per this project's evidence-priority rule (AGENTS.md,
// ai/README.md), a verified capture always wins for client-facing wire behavior - but that must
// never be modeled by silently mutating pinned-source-derived generated data (which stays a
// faithful reproduction of legacy/rathena for every other purpose). This type is the ONE place such
// a divergence is recorded, with explicit provenance, so it can never be mistaken for an ordinary
// generated value or accidentally reused for gameplay logic beyond client wire serialization.
//
// Do NOT add an entry here without a verified official stock-iRO capture proving the exact
// divergence - never to "make a test pass" or to guess at unproven client behavior.
internal static class IroWireCompatibility
{
    // Official stock-iRO map-entry capture (ai/iro-2026-wire.md, ai/map-server.md) proves
    // NV_BASIC's captured 0x0B32 entry has range=1. Pinned db/re/skill_db.yml's NV_BASIC entry has
    // NO Range field at all, which pinned SkillDatabase::parseBodyNode explicitly zero-fills
    // (skill.cpp:14934-14937: `memset(skill->range, 0, sizeof(skill->range));`) - i.e. pinned
    // source unambiguously computes range=0 for this skill, not 1. This is a genuine, currently
    // unexplained divergence between the pinned rAthena snapshot and the live operator's current
    // stock-iRO client/server data - not a modeling bug in this project's compiler or resolver.
    // Per the evidence-priority rule, the verified capture wins for what Athena actually sends to
    // the client; pinned source remains untouched (GeneratedSkillDefinition.Range for NV_BASIC
    // stays 0, exactly matching legacy/rathena) since this is a wire-serialization concern only,
    // never a gameplay-mechanics one.
    // Keyed by SkillId, each entry documented individually with its capture provenance above. This
    // table only ever grows by adding a new verified-divergence entry - it must never be extended
    // speculatively, and no entry may be removed without re-verifying the capture no longer
    // applies (e.g. a future PACKETVER change).
    private static readonly IReadOnlyDictionary<ushort, short> VerifiedRangeOverrides = new Dictionary<ushort, short>
    {
        [1] = 1, // NV_BASIC - see this type's own doc comment.
    };

    internal static short ResolveVerifiedRangeOverride(ushort skillId, short pinnedResolvedRange) =>
        VerifiedRangeOverrides.TryGetValue(skillId, out var overrideValue) ? overrideValue : pinnedResolvedRange;
}
