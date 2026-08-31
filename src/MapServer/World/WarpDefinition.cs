namespace Athena.Net.MapServer.World;

public sealed record WarpDefinition(
    string Name,
    string SourceMap,
    ushort SourceX,
    ushort SourceY,
    ushort RadiusX,
    ushort RadiusY,
    string DestinationMap,
    ushort DestinationX,
    ushort DestinationY,
    bool HasWarpActor,
    WorldSourceInfo Source,
    IReadOnlyList<WorldActionDefinition>? Actions = null)
{
    public WarpDefinition(string name, string sourceMap, ushort sourceX, ushort sourceY, ushort radiusX, ushort radiusY,
        string destinationMap, ushort destinationX, ushort destinationY, bool hasWarpActor, string sourceFile, int sourceLine,
        IReadOnlyList<WorldActionDefinition>? actions = null)
        : this(name, sourceMap, sourceX, sourceY, radiusX, radiusY, destinationMap, destinationX, destinationY,
            hasWarpActor, new WorldSourceInfo("rAthena", "unknown", sourceFile, sourceLine), actions) { }

    public string SourceFile => Source.File;
    public int SourceLine => Source.Line;
    public IReadOnlyList<WorldActionDefinition> OrderedActions => Actions ?? [new WarpAction(DestinationMap, DestinationX, DestinationY)];
    public bool Matches(string mapName, ushort x, ushort y)
    {
        return string.Equals(SourceMap, mapName, StringComparison.OrdinalIgnoreCase) &&
               x >= (int)SourceX - RadiusX &&
               x <= (int)SourceX + RadiusX &&
               y >= (int)SourceY - RadiusY &&
               y <= (int)SourceY + RadiusY;
    }
}
