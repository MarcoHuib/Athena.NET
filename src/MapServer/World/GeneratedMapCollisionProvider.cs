using System.Collections.Concurrent;
using Athena.Net.MapServer.Generated.World;

namespace Athena.Net.MapServer.World;

public sealed class GeneratedMapCollisionProvider : IMapCollisionProvider, IDisposable
{
    private readonly IReadOnlyDictionary<string, GeneratedMapDefinition> _definitions;
    private readonly AthenaMapPackReader _reader;
    private readonly ConcurrentDictionary<int, MapCollisionMap> _loaded = new();
    internal GeneratedMapCollisionProvider(IEnumerable<GeneratedMapDefinition> definitions, AthenaMapPackReader reader) { _definitions = definitions.ToDictionary(map => map.Name, StringComparer.OrdinalIgnoreCase); _reader = reader; }
    public static GeneratedMapCollisionProvider OpenProduction() => Open(Path.Combine(AppContext.BaseDirectory, "MapData", "AthenaMaps.bin"));

    // Same composition as OpenProduction, against an explicitly supplied map-pack path. Lets test
    // contexts (where the 53 MiB generated pack is intentionally not copied into build/test output
    // - see MapServer.csproj and ai/world-data.md) point directly at the checked-in source asset
    // under src/MapServer/Generated/Assets/Maps/AthenaMaps.bin instead of duplicating this
    // composition. Production behavior is unchanged: OpenProduction still resolves exactly
    // AppContext.BaseDirectory/MapData/AthenaMaps.bin.
    public static GeneratedMapCollisionProvider Open(string mapPackPath) => new(GeneratedMapRegistry.All, new(mapPackPath, GeneratedMapRegistry.Count));
    public bool TryGetMap(string mapName, out MapCollisionMap map)
    {
        if (!_definitions.TryGetValue(MapName.NormalizeWorld(mapName), out var definition)) { map = null!; return false; }
        map = _loaded.GetOrAdd(definition.AssetId, _ => _reader.ReadMap(definition)); return true;
    }
    public void Dispose() => _reader.Dispose();
}
