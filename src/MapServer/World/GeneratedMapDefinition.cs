namespace Athena.Net.MapServer.World;

public enum MapSourceLayer : byte { Base, Renewal, Import }

public sealed record GeneratedMapDefinition(
    int AssetId,
    string Name,
    int Width,
    int Height,
    MapSourceLayer SourceLayer,
    WorldSourceInfo Source);
