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
    string SourceFile,
    int SourceLine,
    IReadOnlyList<WorldActionDefinition>? Actions = null)
{
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
