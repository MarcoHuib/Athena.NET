using System.Text.Json;

namespace Athena.Net.MapServer.World;

public sealed class WorldMapRegistry
{
    private readonly IReadOnlyList<WarpDefinition> _warps;
    private readonly IReadOnlyList<WarpActor> _warpActors;
    private readonly int _dynamicWarpActorCount;

    public WorldMapRegistry(
        IEnumerable<WarpDefinition> warps,
        IEnumerable<WarpActorDefinition>? dynamicWarpActors = null)
    {
        _warps = warps.ToArray();
        var allocator = new WorldActorIdAllocator();
        var dynamicActors = (dynamicWarpActors ?? Array.Empty<WarpActorDefinition>()).ToArray();
        _dynamicWarpActorCount = dynamicActors.Length;
        var actorDefinitions = _warps
            .Where(warp => warp.HasWarpActor && warp.RadiusX <= byte.MaxValue && warp.RadiusY <= byte.MaxValue)
            .Select(warp => new WarpActorDefinition(
                warp.Name,
                warp.SourceMap,
                warp.SourceX,
                warp.SourceY,
                (byte)warp.RadiusX,
                (byte)warp.RadiusY))
            .Concat(dynamicActors);
        _warpActors = actorDefinitions
            .Select(actor => new WarpActor(
                allocator.Allocate(),
                actor.Name.Length > 24 ? actor.Name[..24] : actor.Name,
                actor.MapName,
                actor.X,
                actor.Y,
                actor.RadiusX,
                actor.RadiusY))
            .ToArray();
    }

    public static WorldMapRegistry Tutorial { get; } = LoadGenerated();

    public int MapCount => _warps.Select(warp => warp.SourceMap).Distinct(StringComparer.OrdinalIgnoreCase).Count();
    public int StaticWarpCount => _warps.Count;
    public int DynamicWarpActorCount => _dynamicWarpActorCount;

    public IEnumerable<WarpActor> GetVisibleWarpActors(
        string mapName,
        ushort x,
        ushort y,
        ushort range = 14)
    {
        return _warpActors.Where(actor =>
            string.Equals(actor.MapName, mapName, StringComparison.OrdinalIgnoreCase) &&
            Math.Abs((int)actor.X - x) <= range &&
            Math.Abs((int)actor.Y - y) <= range);
    }

    public bool TryFindWarp(string mapName, ushort x, ushort y, out WarpDefinition warp)
    {
        warp = _warps.FirstOrDefault(candidate => candidate.Matches(mapName, x, y))!;
        return warp is not null;
    }

    public bool TryFindFirstWarpAlongRoute(
        string mapName,
        ushort fromX,
        ushort fromY,
        ushort toX,
        ushort toY,
        out WarpIntersection intersection)
    {
        foreach (var (x, y) in GridLineTraversal.Enumerate(fromX, fromY, toX, toY))
        {
            if (TryFindWarp(mapName, x, y, out var warp))
            {
                intersection = new WarpIntersection(warp, x, y);
                return true;
            }
        }

        intersection = default;
        return false;
    }

    private static WorldMapRegistry LoadGenerated()
    {
        var path = Path.Combine(AppContext.BaseDirectory, "data", "world", "warps.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var warps = new List<WarpDefinition>();
        var dynamicWarpActors = new List<WarpActorDefinition>();
        foreach (var item in document.RootElement.GetProperty("StaticWarps").EnumerateArray())
        {
            warps.Add(new WarpDefinition(
                item.GetProperty("Name").GetString()!,
                item.GetProperty("SourceMap").GetString()!,
                item.GetProperty("CenterX").GetUInt16(),
                item.GetProperty("CenterY").GetUInt16(),
                item.GetProperty("RadiusX").GetUInt16(),
                item.GetProperty("RadiusY").GetUInt16(),
                item.GetProperty("DestinationMap").GetString()!,
                item.GetProperty("DestinationX").GetUInt16(),
                item.GetProperty("DestinationY").GetUInt16(),
                item.GetProperty("HasWarpActor").GetBoolean(),
                item.GetProperty("SourceFile").GetString()!,
                item.GetProperty("SourceLine").GetInt32()));
        }

        foreach (var item in document.RootElement.GetProperty("DynamicWarps").EnumerateArray())
        {
            if (item.GetProperty("Name").ValueKind != JsonValueKind.String ||
                item.GetProperty("SourceMap").ValueKind != JsonValueKind.String ||
                item.GetProperty("CenterX").ValueKind != JsonValueKind.Number ||
                item.GetProperty("CenterY").ValueKind != JsonValueKind.Number ||
                item.GetProperty("Radius").ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var radius = item.GetProperty("Radius");
            var radiusX = radius.GetProperty("X").GetUInt16();
            var radiusY = radius.GetProperty("Y").GetUInt16();
            if (radiusX <= byte.MaxValue && radiusY <= byte.MaxValue)
            {
                dynamicWarpActors.Add(new WarpActorDefinition(
                    item.GetProperty("Name").GetString()!,
                    item.GetProperty("SourceMap").GetString()!,
                    item.GetProperty("CenterX").GetUInt16(),
                    item.GetProperty("CenterY").GetUInt16(),
                    (byte)radiusX,
                    (byte)radiusY));
            }
        }

        return new WorldMapRegistry(warps, dynamicWarpActors);
    }
}

public readonly record struct WarpIntersection(WarpDefinition Warp, ushort X, ushort Y);
public sealed record WarpActorDefinition(
    string Name,
    string MapName,
    ushort X,
    ushort Y,
    byte RadiusX,
    byte RadiusY);
