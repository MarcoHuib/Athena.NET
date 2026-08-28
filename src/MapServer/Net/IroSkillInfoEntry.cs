using Athena.Net.MapServer.Generated.Skills;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Wire-facing projection of one 0x0B32 (ZC_SKILLINFO_LIST3) entry. Deliberately a separate type
// from CharacterSkillState (domain truth) - see ai/iro-2026-wire.md and ai/map-server.md for the
// full evidence trace behind each field's value:
//   - SkillId, CurrentLevel, Upgradable: Verified (capture-consistent, and CurrentLevel/Upgradable
//     additionally confirmed Reference-backed via pinned clif_skillinfoblock's exact upFlag
//     formula: CurrentLevel < MaxLevel, independent of remaining skill points).
//   - SpCost, Range: Verified layout (stock-iRO capture proves offsets/widths) + Reference-backed
//     value semantics (pinned clif.cpp:5737-5738 proves both use the character's CURRENT
//     persisted level, matching the one captured level-0/zero-cost NV_BASIC entry exactly). Range
//     may itself be a raw negative/special GeneratedSkillDefinition.Range value passed through
//     unresolved - see From()'s own doc comment.
//   - Flags, SecondaryLevel: Unknown beyond the single captured all-zero observation - never
//     silently assumed to be a proven constant for every skill.
// Range is kept SIGNED here (mirroring GeneratedSkillDefinition.Range - never coerced to an
// unsigned type that would corrupt a negative pinned source value like -1). The wire serializer
// writes it as the raw 16-bit bit pattern the captured uint16 field actually holds (a C
// int16/uint16 reinterpretation, exactly what pinned SKILLDATA.range2 - itself a uint16 in a
// packed C struct - would produce for a negative int16 value) - see IroSkillInfoListPackets.
internal readonly record struct IroSkillInfoEntry(
    ushort SkillId,
    int Flags,
    ushort CurrentLevel,
    ushort SpCost,
    short Range,
    bool Upgradable,
    ushort SecondaryLevel)
{
    // Projects one domain CharacterSkillState (already filtered to ClientVisible by the caller -
    // this method does not itself filter) into its wire entry, resolving SpCost/Range from
    // GeneratedSkillDefinition at the character's CURRENT level per the traced pinned behavior.
    // Flags/SecondaryLevel are passed through as the literal captured-observation default (0) -
    // an explicit, disclosed placeholder, not a silently-assumed constant.
    //
    // Range resolution note: GeneratedSkillDefinition.Range may be negative/special (pinned
    // skill_get_range2 resolves such values using LIVE character/equipment state - equipped
    // weapon range, active Vulture's Eye/Snake Eye bonuses, Shadow Jump/Radius overrides). This
    // slice does not perform that live resolution (the necessary equipment-range context is a
    // separate, larger integration than this PR's scope) - the raw generated value is passed
    // through unresolved, an explicit disclosed gap, never silently reinterpreted as if it were
    // the final displayed range.
    internal static IroSkillInfoEntry From(CharacterSkillState state, GeneratedSkillDefinition canonical)
    {
        var spCost = state.CurrentLevel > 0 && state.CurrentLevel <= canonical.SpCostByLevel.Count
            ? canonical.SpCostByLevel[state.CurrentLevel - 1]
            : 0u;
        return new IroSkillInfoEntry(
            state.SkillId,
            Flags: 0,
            state.CurrentLevel,
            (ushort)Math.Min(spCost, ushort.MaxValue),
            canonical.Range,
            state.Upgradeable,
            SecondaryLevel: 0);
    }
}
