using Athena.Net.MapServer.Generated.World.Izlude.Academy;
using Athena.Net.MapServer.Generated.World.Izlude.IzludeCity;
using Athena.Net.MapServer.Generated.World;
using Athena.Net.MapServer.Generated.World.Prontera;
using Athena.Net.MapServer.Generated.World.PrtFild08;

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
        // Izlude/prt_fild08d/Prontera travel-corridor content (ai/world-data.md,
        // izlude-prontera-travel-trace.txt): route-critical warps live in each area's own
        // GeneratedWarps.All (composed directly by WorldMapRegistry/MapServerWorld.Build, not
        // through this NPC/warp-trigger builder), while these Register calls add the low-cost
        // static NPC presence (Sailor, Guide, Resting Adventurer, Karian) compiled alongside them.
        IzludeCityWorld.Register(builder);
        IzludeGuideWorld.Register(builder);
        PronteraCityWorld.Register(builder);
        PronteraKarianWorld.Register(builder);
        PrtFild08World.Register(builder);
        // The Athena.NET EFFECTIVE Renewal source-load profile (RathenaRenewalDefault + the
        // explicit Athena overlay, e.g. Academy - ai/world-data.md's "Generated mob spawns"
        // section) - NOT GeneratedMobSpawnRegistry.All. All 10,068 pinned ordinary `monster` spawn
        // declarations remain represented in GeneratedMobSpawnRegistry.All for repository-wide
        // source coverage/research/analysis, but registering every one of them into the runtime
        // world would activate pre-Renewal-only and pinned-disabled (e.g. old event) content the
        // real rAthena Renewal script-config graph never actually loads. GeneratedMobSpawnLoadProfiles
        // .AthenaIroEffective is the deterministic, generation-time-computed subset that matches the
        // real pinned npc/re/scripts_main.conf graph plus Athena's explicit overlay allow-list
        // (AthenaOverlaySourceFiles) - it references the SAME canonical MobSpawnDefinition instances
        // GeneratedMobSpawnRegistry.All holds (by index, never a duplicate copy). Formerly two
        // hand-picked slices (AcademyMobSpawns.GPoringSpawns for int_land*, PrtFild08MobSpawns.All
        // for the prt_fild08 family) were registered here separately; both are a strict subset of
        // this profile's own int_land*/prt_fild08* entries. MapServerHostingScope.ServedMaps (and
        // MapServerWorld.Build's servedMaps filter) remains the SEPARATE runtime-instantiation
        // decision - registering every profile-active declaration here does not activate every map;
        // see that filter for why "definition availability" and "runtime instantiation" stay
        // independent.
        foreach (var spawn in GeneratedMobSpawnLoadProfiles.AthenaIroEffective) builder.AddMobSpawn(spawn);
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
