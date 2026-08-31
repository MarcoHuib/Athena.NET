using System.Collections.Concurrent;
using Athena.Net.MapServer.Generated.World;

namespace Athena.Net.MapServer.World;

public sealed class GeneratedMapCollisionProvider : IMapCollisionProvider, IDisposable
{
    private readonly IReadOnlyDictionary<string, GeneratedMapDefinition> _definitions;
    private readonly AthenaMapPackReader _reader;
    private readonly ConcurrentDictionary<int, MapCollisionMap> _loaded = new();
    internal GeneratedMapCollisionProvider(IEnumerable<GeneratedMapDefinition> definitions, AthenaMapPackReader reader) { _definitions = definitions.ToDictionary(map => map.Name, StringComparer.OrdinalIgnoreCase); _reader = reader; }
    public static GeneratedMapCollisionProvider OpenProduction() => new(GeneratedMapRegistry.All, new(Path.Combine(AppContext.BaseDirectory, "MapData", "AthenaMaps.bin"), GeneratedMapRegistry.Count));
    public bool TryGetMap(string mapName, out MapCollisionMap map)
    {
        if (!_definitions.TryGetValue(MapName.NormalizeWorld(mapName), out var definition)) { map = null!; return false; }
        map = _loaded.GetOrAdd(definition.AssetId, _ => _reader.ReadMap(definition)); return true;
    }
    public void Dispose() => _reader.Dispose();
}
