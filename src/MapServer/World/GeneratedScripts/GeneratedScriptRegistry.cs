namespace Athena.Net.MapServer.World.GeneratedScripts;

public static partial class GeneratedScriptRegistry
{
    public static NpcScriptRegistry Registry { get; } = new NpcScriptRegistryBuilder().AddGenerated(CreateRegistrations()).Build();

    public static IReadOnlyList<WorldEntityDefinition> Entities => Registry.Entities;

    public static bool ContainsEntity(string entityId) => Entities.Any(entity => string.Equals(entity.Id, entityId, StringComparison.OrdinalIgnoreCase));

    public static bool TryCreate(string entityId, string trigger, out INpcScript script)
    {
        return Registry.TryCreate(entityId, trigger, out script);
    }

    private static IReadOnlyList<GeneratedScriptRegistration> CreateRegistrations() =>
    [
        WarpIntLand04IntroToIzludeDOnTouchScriptRegistration.Create(),
        WarpIzInt03ShipOut03OnTouchScriptRegistration.Create(),
        WarpIzInt01ShipOut01OnTouchScriptRegistration.Create(),
        WarpIzInt02ShipOut02OnTouchScriptRegistration.Create(),
        WarpIzInt04ShipOut04OnTouchScriptRegistration.Create(),
        NpcIzIntWoundedSwordsmanIntroNpc02IzIntOnClickScriptRegistration.Create(),
        NpcIzInt03WoundedSwordsmanIntroNpc01IzInt03OnClickScriptRegistration.Create(),
        NpcIzInt03WoundedSwordsmanIntroNpc02IzInt03OnClickScriptRegistration.Create(),
        NpcIzInt01WoundedSwordsmanIntroNpc01IzInt01OnClickScriptRegistration.Create(),
        NpcIzInt01WoundedSwordsmanIntroNpc02IzInt01OnClickScriptRegistration.Create(),
        NpcIzInt02WoundedSwordsmanIntroNpc01IzInt02OnClickScriptRegistration.Create(),
        NpcIzInt02WoundedSwordsmanIntroNpc02IzInt02OnClickScriptRegistration.Create(),
        NpcIzInt04WoundedSwordsmanIntroNpc01IzInt04OnClickScriptRegistration.Create(),
        NpcIzInt04WoundedSwordsmanIntroNpc02IzInt04OnClickScriptRegistration.Create(),
    ];
}

public sealed class NpcScriptRegistry
{
    private readonly IReadOnlyDictionary<string, GeneratedScriptRegistration> _registrations;
    internal NpcScriptRegistry(IReadOnlyDictionary<string, GeneratedScriptRegistration> registrations)
    {
        _registrations = registrations;
        Entities = registrations.Values.Select(value => value.Entity).DistinctBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase).OrderBy(entity => entity.Id, StringComparer.Ordinal).ToArray();
    }
    public IReadOnlyList<WorldEntityDefinition> Entities { get; }
    public bool TryCreate(string entityId, string trigger, out INpcScript script)
    {
        if (_registrations.TryGetValue(Key(entityId, trigger), out var registration)) { script = registration.Factory(); return true; }
        script = null!; return false;
    }
    internal static string Key(string entityId, string trigger) => $"{entityId}:{trigger}";
}

public sealed class NpcScriptRegistryBuilder
{
    private readonly Dictionary<string, GeneratedScriptRegistration> _registrations = new(StringComparer.OrdinalIgnoreCase);
    public NpcScriptRegistryBuilder AddGenerated(IEnumerable<GeneratedScriptRegistration> registrations) { foreach (var registration in registrations) Add(registration, false); return this; }
    public NpcScriptRegistryBuilder AddCustom(GeneratedScriptRegistration registration, bool explicitlyOverrideGenerated = false) { Add(registration, explicitlyOverrideGenerated); return this; }
    public NpcScriptRegistry Build() => new(_registrations.ToDictionary());
    private void Add(GeneratedScriptRegistration registration, bool replace)
    {
        var key = NpcScriptRegistry.Key(registration.Entity.Id, registration.Trigger);
        if (!replace && _registrations.ContainsKey(key)) throw new InvalidOperationException($"Duplicate NPC script registration '{key}'. Use the explicit override option to replace generated content.");
        _registrations[key] = registration;
    }
}
