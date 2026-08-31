using System.IO.Compression;
using System.Collections.Concurrent;
using Athena.Net.MapServer.Generated.World;

namespace Athena.Net.MapServer.World;

public sealed record GeneratedMapDefinition(
    string Name,
    int Width,
    int Height,
    string CompressedCellsBase64,
    string SourceLayer,
    WorldSourceInfo Source)
{
    public MapCollisionMap CreateCollisionMap()
    {
        var compressed = Convert.FromBase64String(CompressedCellsBase64);
        using var input = new MemoryStream(compressed);
        using var zlib = new ZLibStream(input, CompressionMode.Decompress);
        using var output = new MemoryStream(checked(Width * Height));
        zlib.CopyTo(output);
        var raw = output.ToArray();
        if (raw.Length != checked(Width * Height))
            throw new InvalidDataException($"Generated map '{Name}' decoded to {raw.Length} cells; expected {Width * Height}.");
        var cells = raw.Select((value, index) => value switch
        {
            0 or 2 or 4 or 6 => MapCellFlags.Walkable | MapCellFlags.Shootable,
            1 => MapCellFlags.None,
            3 => MapCellFlags.Walkable | MapCellFlags.Shootable | MapCellFlags.Water,
            5 => MapCellFlags.Shootable,
            _ => throw new InvalidDataException($"Generated map '{Name}' cell {index} has unrecognized GAT type {value}."),
        }).ToArray();
        return new MapCollisionMap(Name, Width, Height, cells);
    }
}

public sealed class GeneratedMapCollisionProvider : IMapCollisionProvider
{
    public static GeneratedMapCollisionProvider Instance { get; } = new();
    private readonly ConcurrentDictionary<string, MapCollisionMap> _loaded = new(StringComparer.OrdinalIgnoreCase);

    private GeneratedMapCollisionProvider() { }

    public bool TryGetMap(string mapName, out MapCollisionMap map)
    {
        if (!GeneratedMapRegistry.TryGet(mapName, out var definition)) { map = null!; return false; }
        map = _loaded.GetOrAdd(definition.Name, _ => definition.CreateCollisionMap());
        return true;
    }
}
