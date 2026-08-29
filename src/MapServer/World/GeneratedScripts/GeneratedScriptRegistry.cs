using Athena.Net.MapServer.Generated.World.Izlude.Academy;

namespace Athena.Net.MapServer.World.GeneratedScripts;

public static partial class GeneratedScriptRegistry
{
    private static readonly WorldRegistryBuildResult Result = BuildRegistry();

    public static NpcScriptRegistry Registry => Result.Scripts;

    // Includes actor-only entities (e.g. Captain Carocc/Lumin) directly - they never depend on having
    // a script registration to be present here, since WorldRegistryBuilder tracks entities independently
    // of whether AddNpc's definition has any behaviors.
    public static IReadOnlyList<WorldEntityDefinition> Entities => Result.Entities;

    // Same underlying WorldRegistryBuildResult WorldMapRegistry.Tutorial's Entities/Registry
    // already come from - MapServerWorld.Build() applies Register(builder) itself (see below)
    // so the composed live world's MonsterRegistry spawns from the identical generated data
    // WorldMapRegistry.Tutorial itself would use, rather than reading AcademyMobSpawns
    // directly and bypassing the builder.
    public static IReadOnlyList<MobSpawnDefinition> MobSpawns => Result.MobSpawns;

    public static bool ContainsEntity(string entityId) => Entities.Any(entity => string.Equals(entity.Id, entityId, StringComparison.OrdinalIgnoreCase));

    public static bool TryCreate(string entityId, string trigger, out INpcScript script)
    {
        return Registry.TryCreate(entityId, trigger, out script);
    }

    // Applies every generated registration (Academy world + generated mob spawns) onto an
    // externally supplied builder. This is the one place generated content is composed, reused
    // both by this class's own static Result (a private builder, for every existing caller of
    // Entities/Registry/MobSpawns/Tutorial) and by MapServerWorld.Build's live composed world,
    // which additionally applies Customs.World.CustomWorldRegistry.Register on the SAME builder
    // instance when customs are enabled - see ai/map-server.md's "Handwritten custom world
    // content" section. GeneratedScriptRegistry itself stays entirely config-independent: it
    // never knows whether customs will also be applied to the builder it's handed.
    public static void Register(WorldRegistryBuilder builder)
    {
        AcademyWorld.Register(builder);
        // AcademyMobSpawns.GPoringSpawns registration is composed here (not inside AcademyWorld.cs)
        // because AcademyWorld.cs is `compile-npc-world` output, verified byte-for-byte reproducible
        // against the pinned source by WorldDataImporter.Tests.CompilerTests.
        // RealAcademyWorld_GenerationIsDeterministicAndMatchesCompiledAcademyTree. That emitter only
        // knows NPC/warp source parsing today; extending it to also emit mob-spawn registrations was
        // judged disproportionate for this one loop, so it lives here instead, alongside this file's
        // other hand-authored composition logic.
        foreach (var spawn in AcademyMobSpawns.GPoringSpawns) builder.AddMobSpawn(spawn);
    }

    private static WorldRegistryBuildResult BuildRegistry()
    {
        var builder = new WorldRegistryBuilder();
        Register(builder);
        return builder.Build();
    }
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
