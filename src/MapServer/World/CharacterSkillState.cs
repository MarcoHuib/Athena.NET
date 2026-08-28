using Athena.Net.MapServer.Generated.Skills;

namespace Athena.Net.MapServer.World;

// One persisted CharSkill row. CharServer is the sole durable owner; MapServer never invents or
// reassigns SkillId/Level. Deliberately holds NO job-tree/eligibility knowledge - see
// CharacterSkillSnapshot's own doc comment for why persisted rows and current-job eligibility are
// kept as two separate, separately-tested concerns.
public sealed record CharacterLearnedSkill(ushort SkillId, byte Level);

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
    public static CharacterSkillSnapshot FromLogin(IReadOnlyList<(ushort SkillId, byte Level)> rows)
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
            learned.Add(new CharacterLearnedSkill(row.SkillId, row.Level));
        }
        return new CharacterSkillSnapshot(learned);
    }

    public byte CurrentLevel(ushort skillId)
    {
        foreach (var skill in Learned)
            if (skill.SkillId == skillId) return skill.Level;
        return 0;
    }

    // Applies a confirmed-persisted skill-level mutation. Used only after CharServer has already
    // committed the change (see CharacterGameplayStateSession.LearnSkillAsync) - never
    // optimistically before persistence succeeds.
    public CharacterSkillSnapshot WithLearnedSkill(ushort skillId, byte newLevel)
    {
        var index = Learned.ToList().FindIndex(s => s.SkillId == skillId);
        if (index < 0) return new CharacterSkillSnapshot([.. Learned, new CharacterLearnedSkill(skillId, newLevel)]);
        var updated = Learned.ToList();
        updated[index] = updated[index] with { Level = newLevel };
        return new CharacterSkillSnapshot(updated);
    }
}

// One skill as it currently applies to the character's CURRENT JobClass's effective tree.
// Deliberately keeps four distinct concepts separate rather than collapsing them into one
// boolean - pinned rAthena's own runtime model (pc_calc_skilltree/pc_check_skilltree populating
// sd->status.skill[], clif_skillinfoblock reading only from that cache; see ai/map-server.md and
// ai/iro-2026-wire.md for the traced call sites) proves these are independent facts:
//   - EffectiveTreeMembership: this SkillId is a member of GeneratedSkillTreeDefinition.
//     EffectiveSkills for the current JobClass. Necessary but NOT sufficient for visibility -
//     pinned pc_calc_skilltree filters tree membership further by BaseLevel/JobLevel/
//     prerequisites/NormallyLearnable before a skill's id ever enters the character's runtime
//     skill array.
//   - NormallyLearnable: source-backed (GeneratedSkillDefinition.NormallyLearnable, from
//     skill_db.yml Flags.IsQuest/IsWedding/IsGuild) - whether this skill is EVER acquired through
//     ordinary skill-point spending, independent of current requirements or level.
//   - RequirementsSatisfied: BaseLevel/JobLevel/prerequisites are currently met, independent of
//     whether the skill is normally learnable or whether skill points remain.
//   - ClientVisible: pinned pc_calc_skilltree's actual gate for entering sd->status.skill[] (and
//     therefore for appearing in ZC_SKILLINFO_LIST3 / 0x0B32 at all) - true when EITHER the skill
//     is already learned (CurrentLevel > 0, matching pinned "already known skills survive the
//     IsQuest/IsWedding gate check"), OR (RequirementsSatisfied && NormallyLearnable). A skill
//     that fails this is never eligible to appear in 0x0B32, full stop.
//   - Upgradeable: pinned clif_skillinfoblock's exact upFlag condition -
//     CurrentLevel < MaxLevel for a normal (non-temporary/non-plagiarized) skill. Deliberately
//     NOT gated on SkillPoints > 0 or on RequirementsSatisfied here - pinned source computes this
//     purely from level-vs-max; the client itself disables its "+" button when the player has no
//     points, and ValidateUpgrade separately re-checks SkillPoints/requirements server-side for
//     the actual spend request. Collapsing SkillPoints into this field would make a
//     zero-skill-point character's already-eligible skills silently report Upgradeable=false,
//     which is not what pinned source's upFlag actually encodes.
// Wire-facing fields for 0x0B32 (SP cost, range, flags) are resolved separately by a packet
// projection layer from GeneratedSkillDefinition plus this state - this record deliberately stays
// a pure domain projection, never a copy of the wire entry.
public sealed record CharacterSkillState(
    ushort SkillId,
    byte CurrentLevel,
    ushort MaxLevel,
    bool EffectiveTreeMembership,
    bool NormallyLearnable,
    bool RequirementsSatisfied,
    bool ClientVisible,
    bool Upgradeable);

// Why a requested upgrade may not proceed - the server derives which reason applies; a client (or
// any caller) never supplies or trusts a target level or point balance directly.
public enum SkillUpgradeRejectionReason
{
    UnknownSkill,
    NotInEffectiveTree,
    NotNormallyLearnable,
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
            var normallyLearnable = GeneratedSkillRegistry.GetById(entry.SkillId).NormallyLearnable;
            var requirementsSatisfied = RequirementsSatisfied(gameplay, skills, entry);
            // Pinned pc_calc_skilltree: already-known skills (CurrentLevel > 0) always survive
            // into the runtime array regardless of the IsQuest/IsWedding gate re-check; a
            // not-yet-learned skill needs both its ordinary requirements AND normal-learnability.
            var clientVisible = currentLevel > 0 || (requirementsSatisfied && normallyLearnable);
            var upgradeable = currentLevel < entry.MaxLevel;
            result.Add(new CharacterSkillState(entry.SkillId, currentLevel, entry.MaxLevel, EffectiveTreeMembership: true, normallyLearnable, requirementsSatisfied, clientVisible, upgradeable));
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

        // Pinned pc_calc_skilltree never grants a normal skill point toward an IsQuest/IsWedding/
        // IsGuild skill through the ordinary tree-walk path, regardless of whether its other
        // requirements are met - see GeneratedSkillDefinition.NormallyLearnable's doc comment.
        if (!GeneratedSkillRegistry.GetById(requestedSkillId).NormallyLearnable)
            return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.NotNormallyLearnable);

        if (gameplay.SkillPoints == 0) return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.NoSkillPoints);

        var currentLevel = skills.CurrentLevel(requestedSkillId);
        if (currentLevel >= entry.MaxLevel) return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.MaxLevelReached);

        if (entry.BaseLevel > 0 && gameplay.BaseLevel < entry.BaseLevel) return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.BaseLevelNotMet);
        if (entry.JobLevel > 0 && gameplay.JobLevel < entry.JobLevel) return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.JobLevelNotMet);

        foreach (var prerequisite in entry.Prerequisites)
            if (skills.CurrentLevel(prerequisite.SkillId) < prerequisite.Level)
                return SkillUpgradeValidationResult.Rejected(SkillUpgradeRejectionReason.PrerequisiteNotMet);

        return SkillUpgradeValidationResult.Valid((byte)(currentLevel + 1), gameplay.SkillPoints - 1, entry.MaxLevel);
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
