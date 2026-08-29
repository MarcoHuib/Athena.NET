namespace Athena.Net.MapServer.World.GeneratedScripts;

public readonly record struct QuestId(uint Value)
{
    public static implicit operator QuestId(uint value) => new(value);
}

public interface INpcScript
{
    Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken);
}

// Thrown by ScriptContext's own wrapper methods (never by generated script code, and never by
// INpcScriptHost implementations directly) when a host mutation command that CAN fail - e.g. a
// delitem/getitem CharServer persistence failure - reports failure, so a generated script's
// remaining statement sequence stops instead of silently continuing into further rewards/dialogue
// after a failed authoritative mutation (AGENTS.md's "do not report success to the client before
// required persistence succeeds"). Caught generically at the one script-dispatch call site
// (MapClientSession.ExecuteGeneratedScriptAsync) alongside every other unexpected script
// exception; this is not itself a bug/crash condition, so it is logged distinctly there. Never
// triggers a rollback of already-applied earlier statements in the same script (this project's
// documented "no distributed idempotency" stance - see ai/world-data.md's "Inventory persistence
// guarantees" section) and is not a general script-failure framework - only commands that
// genuinely have a fallible authoritative persistence step throw it.
public sealed class ScriptMutationFailedException(string message) : Exception(message);

public interface INpcScriptHost
{
    Task MesAsync(uint actorId, string text, CancellationToken cancellationToken);
    Task NextAsync(uint actorId, CancellationToken cancellationToken);
    Task<int> SelectAsync(uint actorId, IReadOnlyList<string> options, CancellationToken cancellationToken);
    Task CloseAsync(uint actorId, CancellationToken cancellationToken);
    Task Close2Async(uint actorId, CancellationToken cancellationToken);
    Task<CharacterQuestStatus> GetQuestStateAsync(QuestId questId, CancellationToken cancellationToken);
    Task SetQuestAsync(QuestId questId, CancellationToken cancellationToken);
    Task CompleteQuestAsync(QuestId questId, CancellationToken cancellationToken);
    Task WarpAsync(string map, ushort x, ushort y, CancellationToken cancellationToken);
    Task SetSavePointAsync(string map, ushort x, ushort y, CancellationToken cancellationToken);
    Task CutinAsync(string image, byte position, CancellationToken cancellationToken);
    Task NpcTalkAsync(uint actorId, string text, CancellationToken cancellationToken);
    Task SetNpcCloakAsync(string entityIdOrName, bool cloaked, CancellationToken cancellationToken);
    Task NavigateToAsync(string map, ushort x, ushort y, CancellationToken cancellationToken);
    Task GrantExperienceAsync(long baseExperience, long jobExperience, CancellationToken cancellationToken);
    Task HealAsync(int hp, int sp, CancellationToken cancellationToken);
    Task SpecialEffectAsync(int effectId, CancellationToken cancellationToken);
    Task SkillEffectAsync(int skillId, int level, CancellationToken cancellationToken);
    Task StartStatusAsync(int statusId, int durationMilliseconds, int val1, CancellationToken cancellationToken);
    string GetActiveCharacterName();
    // Read-only snapshot of the authenticated session's already in-memory CharacterGameplayState
    // (levels, EXP, HP/SP, stat/skill points, job class) - no additional CharServer/persistence
    // query. Added for the Athena Test NPC's "Show Character State" diagnostic option (see
    // ai/map-server.md's "Handwritten custom world content" section); general-purpose, so any
    // future generated or custom script needing a read-only state snapshot can reuse it instead
    // of a script-specific accessor.
    CharacterGameplayState GetGameplayState();
    Task<uint> CountItemAsync(int itemId, CancellationToken cancellationToken);
    Task<bool> DeleteItemAsync(int itemId, uint amount, CancellationToken cancellationToken);
    Task<bool> GetItemAsync(int itemId, uint amount, CancellationToken cancellationToken);
}

// Pinned rAthena numeric constants referenced by generated script identifiers
// (e.g. `specialeffect2 EF_HEAL2;`, `sc_start SC_BLESSING,240000,10;`). Values are the
// exact ordinal of each pinned enum entry - see the source references on each field.
// Only constants actually reached by a currently generated script are added; this is
// not a transcription of the complete pinned enums.
public static class RathenaConstants
{
    // legacy/rathena/src/map/script.hpp enum e_special_effects (EF_NONE = -1 origin).
    public const int EF_HEAL2 = 313;

    // legacy/rathena/src/map/status.hpp enum sc_type (SC_STONE = 0 origin).
    public const int SC_BLESSING = 30;
    public const int SC_INCREASEAGI = 32;
}

public sealed class ScriptContext
{
    private readonly INpcScriptHost _host;

    public ScriptContext(INpcScriptHost host, string entityId, uint actorId, string executingNpcName, string? baseNpcName)
    {
        _host = host;
        EntityId = entityId;
        ActorId = actorId;
        ExecutingNpcName = executingNpcName;
        BaseNpcName = baseNpcName;
    }

    public string EntityId { get; }
    public uint ActorId { get; }
    public string ExecutingNpcName { get; }
    public string? BaseNpcName { get; }

    public Task MesAsync(string text, CancellationToken cancellationToken) => _host.MesAsync(ActorId, text, cancellationToken);
    public Task NextAsync(CancellationToken cancellationToken) => _host.NextAsync(ActorId, cancellationToken);
    public Task<int> SelectAsync(IReadOnlyList<string> options, CancellationToken cancellationToken) => _host.SelectAsync(ActorId, options, cancellationToken);
    public Task CloseAsync(CancellationToken cancellationToken) => _host.CloseAsync(ActorId, cancellationToken);
    public Task Close2Async(CancellationToken cancellationToken) => _host.Close2Async(ActorId, cancellationToken);
    public Task<CharacterQuestStatus> GetQuestStateAsync(QuestId questId, CancellationToken cancellationToken) => _host.GetQuestStateAsync(questId, cancellationToken);
    public Task SetQuestAsync(QuestId questId, CancellationToken cancellationToken) => _host.SetQuestAsync(questId, cancellationToken);
    public Task CompleteQuestAsync(QuestId questId, CancellationToken cancellationToken) => _host.CompleteQuestAsync(questId, cancellationToken);
    public Task WarpAsync(string map, ushort x, ushort y, CancellationToken cancellationToken) => _host.WarpAsync(map, x, y, cancellationToken);
    public Task SetSavePointAsync(string map, ushort x, ushort y, CancellationToken cancellationToken) => _host.SetSavePointAsync(map, x, y, cancellationToken);
    public Task CutinAsync(string image, byte position, CancellationToken cancellationToken) => _host.CutinAsync(image, position, cancellationToken);
    public Task NpcTalkAsync(string text, CancellationToken cancellationToken) => _host.NpcTalkAsync(ActorId, text, cancellationToken);
    public Task SetNpcCloakAsync(string? npcName, bool cloaked, CancellationToken cancellationToken) =>
        _host.SetNpcCloakAsync(npcName ?? EntityId, cloaked, cancellationToken);
    public Task NavigateToAsync(string map, ushort x, ushort y, CancellationToken cancellationToken) => _host.NavigateToAsync(map, x, y, cancellationToken);
    public Task GrantExperienceAsync(long baseExperience, long jobExperience, CancellationToken cancellationToken) => _host.GrantExperienceAsync(baseExperience, jobExperience, cancellationToken);
    public Task HealAsync(int hp, int sp, CancellationToken cancellationToken) => _host.HealAsync(hp, sp, cancellationToken);
    public Task SpecialEffectAsync(int effectId, CancellationToken cancellationToken) => _host.SpecialEffectAsync(effectId, cancellationToken);
    public Task SkillEffectAsync(int skillId, int level, CancellationToken cancellationToken) => _host.SkillEffectAsync(skillId, level, cancellationToken);
    public Task StartStatusAsync(int statusId, int durationMilliseconds, int val1, CancellationToken cancellationToken) => _host.StartStatusAsync(statusId, durationMilliseconds, val1, cancellationToken);
    public CharacterGameplayState GetGameplayState() => _host.GetGameplayState();
    public Task<uint> CountItemAsync(int itemId, CancellationToken cancellationToken) => _host.CountItemAsync(itemId, cancellationToken);

    // Both delitem/getitem host methods have a genuinely fallible authoritative persistence step
    // (CharServer consume/add). This is the one seam between generated code and INpcScriptHost, so
    // it is also the one place a `false` result is translated into a thrown
    // ScriptMutationFailedException - the generated script itself stays a bare sequential `await`
    // call (see SailorOnClickScript.cs), matching every other generated statement, while still
    // stopping the remaining sequence on failure via normal exception propagation up through
    // INpcScript.ExecuteAsync to MapClientSession.ExecuteGeneratedScriptAsync's existing catch.
    public async Task DeleteItemAsync(int itemId, uint amount, CancellationToken cancellationToken)
    {
        if (!await _host.DeleteItemAsync(itemId, amount, cancellationToken))
            throw new ScriptMutationFailedException($"delitem {itemId},{amount} failed.");
    }

    public async Task GetItemAsync(int itemId, uint amount, CancellationToken cancellationToken)
    {
        if (!await _host.GetItemAsync(itemId, amount, cancellationToken))
            throw new ScriptMutationFailedException($"getitem {itemId},{amount} failed.");
    }

    public string StrNpcInfo(int type) => type switch
    {
        2 => ExecutingNpcName.TrimStart('#'),
        4 => EntityId.Split(':', 3)[1],
        _ => throw new NotSupportedException($"strnpcinfo({type}) is not available in generated scripts."),
    };

    public string StrCharInfo(int type) => type switch
    {
        0 => _host.GetActiveCharacterName(),
        _ => throw new NotSupportedException($"strcharinfo({type}) is not available in generated scripts."),
    };

    public static string ReplaceString(string value, string search, string replacement) =>
        value.Replace(search, replacement, StringComparison.Ordinal);
}

public sealed record GeneratedScriptRegistration(
    WorldEntityDefinition Entity,
    string Trigger,
    Func<INpcScript> Factory);
