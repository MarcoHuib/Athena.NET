namespace Athena.Net.MapServer.World.GeneratedScripts;

public readonly record struct QuestId(uint Value)
{
    public static implicit operator QuestId(uint value) => new(value);
}

public interface INpcScript
{
    Task ExecuteAsync(ScriptContext context, CancellationToken cancellationToken);
}

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
