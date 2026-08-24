namespace Athena.Net.MapServer.World.GeneratedScripts;

public static partial class GeneratedScriptRegistry
{
    private static readonly IReadOnlyDictionary<string, GeneratedScriptRegistration> Registrations =
        CreateRegistrations().ToDictionary(registration => Key(registration.Entity.Id, registration.Trigger), StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<WorldEntityDefinition> Entities { get; } = Registrations.Values
        .Select(registration => registration.Entity)
        .DistinctBy(entity => entity.Id, StringComparer.OrdinalIgnoreCase)
        .OrderBy(entity => entity.Id, StringComparer.Ordinal)
        .ToArray();

    public static bool ContainsEntity(string entityId) => Entities.Any(entity => string.Equals(entity.Id, entityId, StringComparison.OrdinalIgnoreCase));

    public static bool TryCreate(string entityId, string trigger, out IGeneratedNpcScript script)
    {
        if (Registrations.TryGetValue(Key(entityId, trigger), out var registration))
        {
            script = registration.Factory();
            return true;
        }

        script = null!;
        return false;
    }

    private static string Key(string entityId, string trigger) => $"{entityId}:{trigger}";
    private static partial IReadOnlyList<GeneratedScriptRegistration> CreateRegistrations();
}
