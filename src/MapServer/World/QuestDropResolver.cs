namespace Athena.Net.MapServer.World;

// Result of evaluating a monster's death against generated quest-drop rules
// for one active character. Empty when no rule matched or the roll failed.
public readonly record struct QuestDropOutcome(int ItemId, int Count);

// Generic, data-driven quest-drop resolver over generated QuestDropRule data
// (pinned quest_db.yml `Drops:` blocks - see QuestDropRule doc comment).
// Deliberately NOT quest-21008-specific: it consults whatever quest state and
// generated rules it is given, the same way it would for any other quest with
// a Drops rule added later. Mirrors quest_update_objective's own separation
// (quest.cpp:757-838): the kill-count Targets loop and the dropitem loop are
// two independent mechanisms in the pinned source, and this resolver
// implements only the second because quest 21008 has no Targets.
public sealed class QuestDropResolver(IReadOnlyList<QuestDropRule> rules, Func<double> randomSource)
{
    public QuestDropResolver(IReadOnlyList<QuestDropRule> rules) : this(rules, System.Random.Shared.NextDouble) { }

    // `questStatus` is a synchronous, already-resolved per-quest-ID lookup - NOT an
    // ICharacterQuestPersistence or any I/O-performing delegate. Athena has no runtime concept of
    // "the character's whole set of active quest IDs" anywhere (every real quest check in
    // MapClientSession is single-quest-ID-scoped via ICharacterQuestPersistence.GetQuestStateAsync),
    // so this resolver stays pure/synchronous and lets the CALLER obtain each rule's QuestId status
    // beforehand (e.g. by awaiting GetQuestStateAsync once per distinct QuestId appearing in `rules`
    // and building a small local dictionary/closure) rather than inventing a materialized
    // "all active quests" concept or coupling this calculation to CharServer persistence I/O.
    //
    // quest_update_objective (quest.cpp:761-763) iterates sd->quest_log and explicitly `continue`s
    // past Q_COMPLETE entries; it never consults a quest the character doesn't have in their log at
    // all. So an Absent or Completed quest simply never matches here - only Active does.
    public IReadOnlyList<QuestDropOutcome> ResolveDrops(Func<uint, CharacterQuestStatus> questStatus, int killedMobId)
    {
        List<QuestDropOutcome>? outcomes = null;
        foreach (var rule in rules)
        {
            if (questStatus(rule.QuestId) != CharacterQuestStatus.Active) continue;
            if (rule.MobId != 0 && rule.MobId != killedMobId) continue; // quest.cpp:811, mob_id==0 means "any monster".
            // quest.cpp:813 `if (it->rate < 10000 && !rnd_chance<uint16>(it->rate, 10000)) continue;`
            // rnd_chance(rate, 10000) succeeds with probability rate/10000; a rate of
            // exactly 10000 never rolls at all (guaranteed), matching the `rate < 10000`
            // guard on the roll itself.
            if (rule.Rate < 10000 && randomSource() * 10000 >= rule.Rate) continue;
            (outcomes ??= []).Add(new QuestDropOutcome(rule.ItemId, rule.Count));
        }
        return outcomes ?? (IReadOnlyList<QuestDropOutcome>)[];
    }
}
