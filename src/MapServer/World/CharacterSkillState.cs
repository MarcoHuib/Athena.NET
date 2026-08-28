using Athena.Net.MapServer.Generated.Skills;

namespace Athena.Net.MapServer.World;

// Pinned e_skill_flag (legacy/rathena/src/common/mmo.hpp:383-391). Athena's CharSkill.Flag column
// stores this value directly (byte). SKILL_FLAG_NONE (-1) is a pinned in-memory sentinel that is
// never actually persisted to a real row - CharSkillFlag has no member for it because a loaded row
// always carries one of the concrete flags below.
public enum CharSkillFlag : byte
{
    Permanent = 0,
    Temporary = 1,
    Plagiarized = 2,
    PermGranted = 3,
    TmpCombo = 4,
}

// One persisted CharSkill row. CharServer is the sole durable owner; MapServer never invents or
// reassigns SkillId/Level/Flag. Deliberately holds NO job-tree/eligibility knowledge - see
// CharacterSkillSnapshot's own doc comment for why persisted rows and current-job eligibility are
// kept as two separate, separately-tested concerns. Flag is a DIFFERENT concept from a skill's
// wire-facing `inf` (see IroSkillInfoEntry) - never conflate the two.
public sealed record CharacterLearnedSkill(ushort SkillId, byte Level, CharSkillFlag Flag);

// Every persisted CharSkill row for one character. This snapshot holds structurally valid
// persisted facts only - whether a given SkillId is even a member of the character's CURRENT
// JobClass's effective tree is a separate question, answered per-call by
// CharacterSkillService.CalculateEffectiveState, never baked into this type. A persisted skill
// from a previous job (job-changing is out of scope for this slice, but the data model must not
// assume every persisted row belongs to the current tree forever) is not an error here - it
// simply will not appear in a later CalculateEffectiveState call for a tree that no longer
// declares it. Missing row for a given SkillId means level 0; this snapshot never materializes a
// row merely to represent "not learned".
public sealed record CharacterSkillSnapshot(IReadOnlyList<CharacterLearnedSkill> Learned)
{
    public static readonly CharacterSkillSnapshot Empty = new([]);

    // Builds a snapshot from freshly loaded persisted rows, enforcing STRUCTURAL validity only
    // (never current-job eligibility - see this type's own doc comment). A row is structurally
    // invalid, and this throws, if: its SkillId is not a canonical GeneratedSkillRegistry id; its
    // Level is zero (a learned row must never persist level 0 - "no row" already means level 0);
    // or the same SkillId appears more than once (CharSkill's composite primary key makes a real
    // duplicate impossible from a correct load, so this indicates a caller/query error, not a
    // legitimate data state). Whether a persisted level exceeds a MaxLevel is a JOB-TREE-relative
    // question (the canonical GeneratedSkillDefinition carries no MaxLevel of its own - only a
    // job's GeneratedSkillTreeEntry does, and different jobs may clamp the same skill's max level
    // differently) and is therefore intentionally NOT checked here - CharacterSkillService.
    // CalculateEffectiveState performs that check against the character's current effective tree
    // and reports it as a surfaced inconsistency, not a load-time exception, per the corrected
    // job-eligibility/structural-validity separation (see this type's own doc comment). This
    // method surfaces corrupted data loudly instead of silently repairing or dropping rows - see
    // ai/map-server.md for the invariant-violation policy this mirrors from the inventory/
    // equipment domain.
    public static CharacterSkillSnapshot FromLogin(IReadOnlyList<(ushort SkillId, byte Level, CharSkillFlag Flag)> rows)
    {
        var seen = new HashSet<ushort>();
        var learned = new List<CharacterLearnedSkill>(rows.Count);
        foreach (var row in rows)
        {
            if (!seen.Add(row.SkillId))
                throw new InvalidOperationException($"Persisted skill invariant violation: duplicate CharSkill row for SkillId={row.SkillId}.");
            if (row.Level == 0)
                throw new InvalidOperationException($"Persisted skill invariant violation: CharSkill row for SkillId={row.SkillId} has Level=0; a learned row must never persist level 0.");
            if (!GeneratedSkillRegistry.All.Any(s => s.SkillId == row.SkillId))
                throw new InvalidOperationException($"Persisted skill invariant violation: SkillId={row.SkillId} is not a canonical generated skill.");
            learned.Add(new CharacterLearnedSkill(row.SkillId, row.Level, row.Flag));
        }
        return new CharacterSkillSnapshot(learned);
    }

    public byte CurrentLevel(ushort skillId)
    {
        foreach (var skill in Learned)
            if (skill.SkillId == skillId) return skill.Level;
        return 0;
    }

    // Pinned pc_calc_skilltree resets every non-special skill to SKILL_FLAG_PERMANENT before the
    // tree-walk grant, and a freshly-granted zero-level tree skill inherits that reset value
    // without any further flag write (pc.cpp:2642-2650, 2732) - so a skill with NO persisted row
    // (never yet learned) is treated as Permanent here, matching pinned behavior exactly, not an
    // arbitrary default.
    public CharSkillFlag Flag(ushort skillId)
    {
        foreach (var skill in Learned)
            if (skill.SkillId == skillId) return skill.Flag;
        return CharSkillFlag.Permanent;
    }

    // Applies a confirmed-persisted skill-level mutation. Used only after CharServer has already
    // committed the change (see CharacterGameplayStateSession.LearnSkillAsync) - never
    // optimistically before persistence succeeds. A newly inserted row is always Permanent
    // (CharServer's own TryApplySkillLearn only ever inserts SKILL_FLAG_PERMANENT rows for this
    // PR's ordinary point-spend path); an existing row's Flag is preserved unchanged, matching
    // CharServer's own "never rewrite an existing row's Flag on increment" contract.
    public CharacterSkillSnapshot WithLearnedSkill(ushort skillId, byte newLevel)
    {
        var index = Learned.ToList().FindIndex(s => s.SkillId == skillId);
        if (index < 0) return new CharacterSkillSnapshot([.. Learned, new CharacterLearnedSkill(skillId, newLevel, CharSkillFlag.Permanent)]);
        var updated = Learned.ToList();
        updated[index] = updated[index] with { Level = newLevel };
        return new CharacterSkillSnapshot(updated);
    }
}

// One skill as it currently applies to the character's CURRENT JobClass's effective tree.
// Deliberately keeps these concepts separate rather than collapsing them into one boolean -
// pinned rAthena's own runtime model (pc_calc_skilltree/pc_check_skilltree populating
// sd->status.skill[], clif_skillinfoblock reading only from that cache; see ai/map-server.md and
// ai/iro-2026-wire.md for the traced call sites) proves these are independent facts:
//   - EffectiveTreeMembership: this SkillId is a member of GeneratedSkillTreeDefinition.
//     EffectiveSkills for the current JobClass. Necessary but NOT sufficient for visibility -
//     pinned pc_calc_skilltree filters tree membership further by BaseLevel/JobLevel/
//     prerequisites/acquisition-gate below before a skill's id ever enters the character's
//     runtime skill array.
//   - RequirementsSatisfied: BaseLevel/JobLevel/prerequisites are currently met, independent of
//     acquisition-gate state or whether skill points remain.
//   - ClientVisible: pinned pc_calc_skilltree's actual gate for entering sd->status.skill[] (and
//     therefore for appearing in ZC_SKILLINFO_LIST3 / 0x0B32 at all) - true when EITHER the skill
//     is already learned (CurrentLevel > 0, matching pinned "already known skills survive the
//     IsQuest/IsWedding/IsSpirit gate check"), OR (RequirementsSatisfied AND every one of the
//     THREE separately-evaluated acquisition gates below is satisfied). Pinned source evaluates
//     IsQuest/IsWedding/IsSpirit as three conditionally-gated facts, not one collapsed boolean -
//     see CharacterSkillService.CalculateEffectiveState for the exact per-gate conditions
//     (quest_skill_learn config, permanently false for Wedding, live SC_SPIRIT status for
//     Spirit). A skill that fails this is never eligible to appear in 0x0B32, full stop.
//   - Upgradeable: pinned clif_skillinfoblock's exact upFlag condition - `skill.flag ==
//     SKILL_FLAG_PERMANENT && CurrentLevel < MaxLevel`. Requires the ACTUAL persisted
//     CharSkill.Flag (Permanent for an unlearned/zero-level skill, matching pinned
//     pc_calc_skilltree's reset-then-grant sequence - see CharacterSkillSnapshot.Flag's own doc
//     comment). Deliberately NOT gated on SkillPoints > 0 or on RequirementsSatisfied here -
//     pinned source computes this purely from flag+level-vs-max; the client itself disables its
//     "+" button when the player has no points, and ValidateUpgrade separately re-checks
//     SkillPoints/requirements server-side for the actual spend request.
// Wire-facing fields for 0x0B32 (SP cost, range, inf) are resolved separately by a packet
// projection layer from GeneratedSkillDefinition plus this state - this record deliberately stays
// a pure domain projection, never a copy of the wire entry.
public sealed record CharacterSkillState(
    ushort SkillId,
    byte CurrentLevel,
    ushort MaxLevel,
    bool EffectiveTreeMembership,
    bool RequirementsSatisfied,
    bool ClientVisible,
    bool Upgradeable);

// Why a requested upgrade may not proceed - the server derives which reason applies; a client (or
// any caller) never supplies or trusts a target level or point balance directly.
public enum SkillUpgradeRejectionReason
{
    UnknownSkill,
    NotInEffectiveTree,
    // Covers all three source-backed acquisition gates (IsQuest without quest_skill_learn,
    // IsWedding, IsSpirit without an active SC_SPIRIT status) - see
    // CharacterSkillService.ValidateUpgrade for which specific gate applied.
    NotNormallyLearnable,
    NotPermanentSkill,
    NoSkillPoints,
    MaxLevelReached,
    BaseLevelNotMet,
    JobLevelNotMet,
    PrerequisiteNotMet,
}

// Immutable outcome of CharacterSkillService.ValidateUpgrade. Exactly one of Valid/Rejected is
// meaningful, discriminated by IsValid - never both, and callers must check IsValid before
// reading NewSkillLevel/NewSkillPoints/MaxLevel.
public readonly record struct SkillUpgradeValidationResult(
    bool IsValid,
    SkillUpgradeRejectionReason? RejectionReason,
    byte NewSkillLevel,
    uint NewSkillPoints,
    ushort MaxLevel)
{
    public static SkillUpgradeValidationResult Valid(byte newSkillLevel, uint newSkillPoints, ushort maxLevel) =>
        new(true, null, newSkillLevel, newSkillPoints, maxLevel);

    public static SkillUpgradeValidationResult Rejected(SkillUpgradeRejectionReason reason) =>
        new(false, reason, 0, 0, 0);
}

// Static/pure domain rules for skill state and skill-point spending. No constructor, no stored
// session references, no I/O - persistence/session orchestration lives strictly above this layer
// (CharacterGameplayStateSession.LearnSkillAsync), exactly the way CharacterProgressionService
// keeps its own pure Calculate separate from CharacterGameplayStateSession.MutateAsync. This
// service must remain generic: it never branches on a specific JobClass or SkillId - see
// AGENTS.md and ai/map-server.md for why (a future job/skill must work without a runtime change
// here).
public static class CharacterSkillService
{
    // Pinned battle_config default (conf/battle/player.conf: "quest_skill_learn: no") - Athena has
    // no server-config system for this yet, so it is conservatively fixed at the pinned default
    // (off) rather than invented as configurable. When Athena gains real server config, this
    // should become a real config read, not a hardcoded constant.
    private const bool QuestSkillLearnEnabled = false;

    // Athena does not yet model SC_SPIRIT (Soul Link) status. This is a TEMPORARY runtime
    // capability gap, not an intrinsic property of IsSpirit skills - see
    // GeneratedSkillDefinition.SkillAcquisitionFlags.IsSpirit's own doc comment. Until a real
    // status-effect model exists, every character is conservatively treated as NOT having an
    // active SC_SPIRIT status, so no not-yet-learned IsSpirit skill becomes ClientVisible or
    // normally learnable - but an ALREADY-learned IsSpirit skill (CurrentLevel > 0) is never
    // hidden merely because of this gap, exactly like the IsQuest/IsWedding "already known
    // survives" rule. Replacing this with a real status check must not change generated data or
    // skill identities - only this one evaluation site.
    private static bool HasActiveSpiritStatus(CharacterGameplayState gameplay) => false;

    // Projects every EFFECTIVE tree entry (not the persisted snapshot's own rows - a persisted
    // skill outside the current tree, e.g. from a prior job, is intentionally excluded from this
    // result rather than treated as an error; see CharacterSkillSnapshot's doc comment) into its
    // current CharacterSkillState. A tree entry whose persisted level exceeds ITS OWN MaxLevel is
    // a genuine data inconsistency (not the prior-job carve-out, since this skill IS still a
    // member of the current tree) and is surfaced via the out parameter rather than thrown -
    // callers decide whether that is fatal for character initialization.
    //
    // Returns EVERY effective tree entry, including ones that are not ClientVisible - callers that
    // need only what a stock-iRO client should actually see (e.g. the 0x0B32 packet projection)
    // must filter on ClientVisible themselves, mirroring pinned pc_calc_skilltree's own two-step
    // shape (compute eligibility, then let clif_skillinfoblock read only the eligible subset) -
    // see this type's own doc comment for the full evidence trace.
    public static IReadOnlyList<CharacterSkillState> CalculateEffectiveState(
        CharacterGameplayState gameplay,
        CharacterSkillSnapshot skills,
        GeneratedSkillTreeDefinition tree,
        out IReadOnlyList<ushort> inconsistentSkillIds)
    {
        var result = new List<CharacterSkillState>(tree.EffectiveSkills.Count);
        var inconsistent = new List<ushort>();
        foreach (var entry in tree.EffectiveSkills)
        {
            var currentLevel = skills.CurrentLevel(entry.SkillId);
            if (currentLevel > entry.MaxLevel)
            {
                inconsistent.Add(entry.SkillId);
                currentLevel = (byte)entry.MaxLevel;
            }
            var requirementsSatisfied = RequirementsSatisfied(gameplay, skills, entry);
            var acquisitionGateSatisfied = AcquisitionGateSatisfied(GeneratedSkillRegistry.GetById(entry.SkillId).Acquisition, gameplay);
            // Pinned pc_calc_skilltree: already-known skills (CurrentLevel > 0) always survive
            // into the runtime array regardless of the IsQuest/IsWedding/IsSpirit gate re-check; a
            // not-yet-learned skill needs both its ordinary requirements AND every acquisition
            // gate satisfied.
            var clientVisible = currentLevel > 0 || (requirementsSatisfied && acquisitionGateSatisfied);
            var flag = skills.Flag(entry.SkillId);
            var upgradeable = flag == CharSkillFlag.Permanent && currentLevel < entry.MaxLevel;
            result.Add(new CharacterSkillState(entry.SkillId, currentLevel, entry.MaxLevel, EffectiveTreeMembership: true, requirementsSatisfied, clientVisible, upgradeable));
        }
        inconsistentSkillIds = inconsistent;
        return result;
    }

    // Validates whether exactly one level may be gained for the requested skill. Never accepts or
    // trusts a caller-supplied target level or point balance - both are always derived internally
    // from gameplay/skills/tree.
    public static SkillUpgradeValidationResult ValidateUpgrade(
        CharacterGameplayState gameplay,
        CharacterSkillSnapshot skills,
        GeneratedSkillTreeDefinition tree,
        ushort requestedSkillId)
    {
        // Checked separately from tree membership below (task section 15 treats "exists in
        // canonical GeneratedSkillRegistry" and "exists in this character's effective tree" as
        // two distinct required checks) even though a well-formed tree can never reference an
        // unknown SkillId (CharacterDataCompiler.ValidateCrossRegistry enforces this at generation
        // time) - this guards against a malformed/forged requestedSkillId that isn't a real skill
        // at all, giving that case its own diagnosable rejection reason instead of collapsing it
        // into NotInEffectiveTree.
        if (!GeneratedSkillRegistry.All.Any(s => s.SkillId == requestedSkillId))
            return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.UnknownSkill);

        var entry = tree.EffectiveSkills.FirstOrDefault(e => e.SkillId == requestedSkillId);
        if (entry is null) return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.NotInEffectiveTree);

        var currentLevel = skills.CurrentLevel(requestedSkillId);

        // A skill already learned bypasses the acquisition gate for FURTHER leveling (pinned
        // pc_calc_skilltree's own "already known skills survive the gate re-check" rule applies
        // equally to raising an already-known skill, not just initial visibility) - the gate only
        // blocks the very FIRST point spent on a skill that isn't normally acquirable.
        if (currentLevel == 0 && !AcquisitionGateSatisfied(GeneratedSkillRegistry.GetById(requestedSkillId).Acquisition, gameplay))
            return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.NotNormallyLearnable);

        // Pinned upFlag: only a SKILL_FLAG_PERMANENT skill may be leveled through ordinary point
        // spending. A missing persisted row is treated as Permanent (see
        // CharacterSkillSnapshot.Flag's own doc comment), so this only actually rejects a row this
        // project doesn't otherwise produce yet (Temporary/Plagiarized/PermGranted/TmpCombo).
        if (skills.Flag(requestedSkillId) != CharSkillFlag.Permanent)
            return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.NotPermanentSkill);

        if (gameplay.SkillPoints == 0) return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.NoSkillPoints);

        if (currentLevel >= entry.MaxLevel) return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.MaxLevelReached);

        if (entry.BaseLevel > 0 && gameplay.BaseLevel < entry.BaseLevel) return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.BaseLevelNotMet);
        if (entry.JobLevel > 0 && gameplay.JobLevel < entry.JobLevel) return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.JobLevelNotMet);

        foreach (var prerequisite in entry.Prerequisites)
            if (skills.CurrentLevel(prerequisite.SkillId) < prerequisite.Level)
                return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.PrerequisiteNotMet);

        return SkillUpgradeValidationResult.Valid((byte)(currentLevel + 1), gameplay.SkillPoints - 1, entry.MaxLevel);
    }

    // Evaluates the three source-backed acquisition gates INDEPENDENTLY, per pinned
    // pc_calc_skilltree/pc_check_skilltree (pc.cpp:2735-2740/2862-2867):
    //   !(IsQuest && !quest_skill_learn) && !IsWedding && !(IsSpirit && !hasActiveSpiritStatus)
    // A skill fails this gate if ANY applicable flag's condition fails - this is intentionally NOT
    // collapsed into one precomputed boolean on generated data (see
    // GeneratedSkillDefinition.SkillAcquisitionFlags's own doc comment): the gate depends on
    // server config (quest_skill_learn) and character runtime state (active SC_SPIRIT), neither of
    // which belongs in generated source data.
    private static bool AcquisitionGateSatisfied(SkillAcquisitionFlags flags, CharacterGameplayState gameplay)
    {
        var questGateSatisfied = !flags.IsQuest || QuestSkillLearnEnabled;
        var weddingGateSatisfied = !flags.IsWedding;
        var spiritGateSatisfied = !flags.IsSpirit || HasActiveSpiritStatus(gameplay);
        return questGateSatisfied && weddingGateSatisfied && spiritGateSatisfied;
    }

    private static bool RequirementsSatisfied(
        CharacterGameplayState gameplay,
        CharacterSkillSnapshot skills,
        GeneratedSkillTreeEntry entry)
    {
        if (entry.BaseLevel > 0 && gameplay.BaseLevel < entry.BaseLevel) return false;
        if (entry.JobLevel > 0 && gameplay.JobLevel < entry.JobLevel) return false;
        foreach (var prerequisite in entry.Prerequisites)
            if (skills.CurrentLevel(prerequisite.SkillId) < prerequisite.Level) return false;
        return true;
    }
}
