using Athena.Net.MapServer.Generated.Skills;
using Athena.Net.MapServer.World;

namespace Athena.Net.MapServer.Net;

// Wire-facing projection of one 0x0B32 (ZC_SKILLINFO_LIST3) entry. Deliberately a separate type
// from CharacterSkillState (domain truth) - see ai/iro-2026-wire.md and ai/map-server.md for the
// full evidence trace behind each field's value:
//   - SkillId: Verified (stock-iRO capture).
//   - Inf: Verified layout (capture) + Reference-backed value semantics (pinned
//     clif_skillinfoblock, clif.cpp:5714: `data.inf = skill_get_inf(skill.id);`, sourced from
//     skill_db.yml's TargetType field - see GeneratedSkillDefinition.Inf's own doc comment).
//   - CurrentLevel, Upgradable: Verified (capture-consistent) + Reference-backed (pinned
//     clif_skillinfoblock's exact upFlag formula: `flag == SKILL_FLAG_PERMANENT && level <
//     skill_tree_get_max(...)`, independent of remaining skill points - requires the ACTUAL
//     persisted CharSkill.Flag, not an assumed-permanent default, except for a never-yet-learned
//     row which pinned pc_calc_skilltree's own reset-then-grant sequence proves defaults to
//     Permanent).
//   - SpCost: Verified layout (capture) + Reference-backed value semantics (pinned clif.cpp:5737
//     proves the CURRENT persisted level is used, matching the captured level-0/zero-cost
//     NV_BASIC entry exactly).
//   - Range: Verified layout (capture) + Reference-backed runtime resolution (pinned
//     skill_get_range2, skill.cpp:324-365: absolute-value fallback for a negative source value by
//     default). One verified stock-iRO capture divergence (NV_BASIC's captured range=1 against
//     pinned source's computed range=0) is documented and resolved separately in
//     IroWireCompatibility with explicit provenance - never silently absorbed into this
//     projection's ordinary resolution path.
//   - SecondaryLevel: Verified+Reference-backed - pinned clif.cpp:5732 (`data.level2 =
//     skill.lv;`, gated on the SAME PACKETVER_RE_NUM>=20190807 branch that defines the 0x0b32
//     packet ID itself, i.e. unconditionally true for any project targeting 0x0B32) proves this
//     is simply a duplicate of the raw stored level - NOT a distinct "checked skill" concept (that
//     distinct concept exists only for the unrelated ZC_ADD_SKILL/0x0b31 packet's own level2 via
//     pc_checkskill). The single captured level-0 NV_BASIC entry (secondaryLevel=0) is consistent
//     with, and now fully explained by, this Reference-backed identity-with-level rule.
// Range is kept SIGNED at the domain-resolution boundary (mirroring GeneratedSkillDefinition.Range
// - never coerced to an unsigned type that would corrupt a negative pinned source value like -1)
// but IroSkillRangeResolver/IroWireCompatibility always produce a final non-negative value before
// this record is constructed for a real skill; the field stays `short` only to preserve the same
// signed representation end-to-end and to make an accidental negative value visible in a debugger/
// test rather than silently reinterpreted.
internal readonly record struct IroSkillInfoEntry(
    ushort SkillId,
    ushort Inf,
    ushort CurrentLevel,
    ushort SpCost,
    short Range,
    bool Upgradable,
    ushort SecondaryLevel)
{
    // Projects one domain CharacterSkillState (already filtered to ClientVisible by the caller -
    // this method does not itself filter) into its wire entry. SpCost is resolved from
    // GeneratedSkillDefinition at the character's CURRENT level; Range is resolved through
    // IroSkillRangeResolver then IroWireCompatibility's verified-divergence override; Inf is
    // copied directly from generated data; SecondaryLevel mirrors CurrentLevel exactly (see this
    // type's own doc comment for why that is the correct pinned behavior, not a placeholder).
    internal static IroSkillInfoEntry From(CharacterSkillState state, GeneratedSkillDefinition canonical)
    {
        var spCost = state.CurrentLevel > 0 && state.CurrentLevel <= canonical.SpCostByLevel.Count
            ? canonical.SpCostByLevel[state.CurrentLevel - 1]
            : 0u;
        var resolvedRange = IroSkillRangeResolver.Resolve(canonical);
        var finalRange = IroWireCompatibility.ResolveVerifiedRangeOverride(state.SkillId, resolvedRange);
        return new IroSkillInfoEntry(
            state.SkillId,
            canonical.Inf,
            state.CurrentLevel,
            (ushort)Math.Min(spCost, ushort.MaxValue),
            finalRange,
            state.Upgradeable,
            SecondaryLevel: state.CurrentLevel);
    }
}
